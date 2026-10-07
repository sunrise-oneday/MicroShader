using System.Globalization;
using System.Text;

namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 向 dump 贡献一段外部状态（VFS 包映射、最近一次切片失败块、内存指标……）。
/// </summary>
/// <remarks>
/// 用接口而不是直接引用，是为了让 ObservabilityEngine 保持"全系统叶子依赖"：
/// 黑匣子必须比它记录的对象活得久，绝不能反向依赖它们。
/// </remarks>
public interface IDumpContributor
{
    /// <summary>贡献者名字（作为 JSON 里的一级键）。</summary>
    string Name { get; }

    /// <summary>把一段（不含最外层大括号的）JSON 片段追加进 writer。</summary>
    void Contribute(DumpJsonWriter writer);
}

/// <summary>
/// 极简 JSON 写入器：手写转义与写入，不用 System.Text.Json
/// （dump 是冷路径，但 AOT 下序列化器会引入反射/源生成器的额外风险面）。
/// </summary>
public ref struct DumpJsonWriter
{
    private readonly StringBuilder _builder;

    internal DumpJsonWriter(StringBuilder builder)
    {
        _builder = builder;
    }

    public readonly int Length => _builder.Length;

    public void AppendRaw(ReadOnlySpan<char> raw) => _builder.Append(raw);

    public void Property(string name)
    {
        if (_builder.Length > 0 && _builder[^1] != '{' && _builder[^1] != ',') _builder.Append(',');
        WriteString(name);
        _builder.Append(':');
    }

    public void String(string name, ReadOnlySpan<char> value)
    {
        Property(name);
        WriteString(value);
    }

    public void Number(string name, long value)
    {
        Property(name);
        _builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    public void Boolean(string name, bool value)
    {
        Property(name);
        _builder.Append(value ? "true" : "false");
    }

    public void Object(string name, Action<DumpJsonWriter> body)
    {
        Property(name);
        _builder.Append('{');
        DumpJsonWriter inner = new(_builder);
        body(inner);
        _builder.Append('}');
    }

    public void StringValue(ReadOnlySpan<char> value) => WriteString(value);

    /// <summary>JSON 字符串转义（含控制字符与 BMP 外字符的 \uXXXX 代理对形式）。</summary>
    private void WriteString(ReadOnlySpan<char> value)
    {
        _builder.Append('"');
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': _builder.Append("\\\""); break;
                case '\\': _builder.Append("\\\\"); break;
                case '\b': _builder.Append("\\b"); break;
                case '\f': _builder.Append("\\f"); break;
                case '\n': _builder.Append("\\n"); break;
                case '\r': _builder.Append("\\r"); break;
                case '\t': _builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        _builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        _builder.Append(c);
                    }
                    break;
            }
        }
        _builder.Append('"');
    }
}

/// <summary>
/// 崩溃飞行记录仪（规格书模块 I 的 "CrashFlightRecorder"）：
/// 把内存黑匣子的全量日志 + 外部状态快照原子落盘。
/// </summary>
/// <remarks>
/// "落盘目录"：v2.0 清单第 8 条明确从 "Temp/" 改到
/// "%LOCALAPPDATA%/MicroShader/dumps/"（Temp 可能被系统清理、被 CI 忽略）。
/// "原子性"：同卷 "*.tmp" + "File.Move(tmp, final, overwrite: true)"，
/// 否则崩溃/断电会留下半截 JSON 反而误导排查。
/// "兜底"："AppDomain.ProcessExit" 与 "Console.CancelKeyPress" 各挂一个
/// 转储机会。Windows 上的强杀（TerminateProcess）不可捕获 —— 因此关键状态不能只存在内存里，
/// 这是本模块的已知边界。
/// "磁盘满"：不做任何重试，走 <see cref="CriticalFallback"/> 把最关键的错误
/// 用 "window/showMessage" 弹出（由传输层注入回调），绝不留未捕获异常。
/// </remarks>
public static class CrashFlightRecorder
{
    private static readonly List<IDumpContributor> Contributors = [];
    private static readonly DateTime OriginUtc = DateTime.UtcNow;
    private static readonly long OriginTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();

    private static int _dumping;
    private static int _exitHooked;
    private static string _dumpRoot = ResolveDumpRoot();

    /// <summary>dump 落盘根目录。</summary>
    public static string DumpRoot
    {
        get => _dumpRoot;
        set => _dumpRoot = value;
    }

    /// <summary>磁盘写入失败时的降级通道（由 LSP 传输层注入 "window/showMessage"）。</summary>
    public static Action<string>? CriticalFallback { get; set; }

    /// <summary>最近一次失败原因；成功时为 null。</summary>
    public static string? LastFailure { get; private set; }

    /// <summary>最近一次成功落盘路径。</summary>
    public static string? LastDumpPath { get; private set; }

    /// <summary>"%LOCALAPPDATA%/MicroShader/dumps/"；取不到本地应用数据目录时退回临时目录。</summary>
    public static string ResolveDumpRoot()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
        return Path.Combine(local, "MicroShader", "dumps");
    }

    public static void RegisterContributor(IDumpContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        lock (Contributors)
        {
            if (!Contributors.Contains(contributor)) Contributors.Add(contributor);
        }
    }

    public static void UnregisterContributor(IDumpContributor contributor)
    {
        lock (Contributors)
        {
            Contributors.Remove(contributor);
        }
    }

    /// <summary>
    /// 转储内存黑匣子。返回落盘路径；失败返回 "null"（原因见 <see cref="LastFailure"/>）。
    /// 重入调用直接返回 "null"。
    /// </summary>
    public static string? Dump(string reason)
    {
        if (Interlocked.Exchange(ref _dumping, 1) != 0) return null;

        string final = string.Empty;
        string tmp = string.Empty;
        try
        {
            string root = DumpRoot;
            Directory.CreateDirectory(root);

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            final = Path.Combine(root, "microshader_" + stamp + ".json");
            tmp = final + ".tmp";

            string json = BuildJson(reason ?? string.Empty);
            File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tmp, final, overwrite: true);

            LastDumpPath = final;
            LastFailure = null;
            return final;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            TryDelete(tmp);
            LastFailure = ex.Message;
            NotifyCriticalFallback(reason, ex.Message);
            return null;
        }
        finally
        {
            Volatile.Write(ref _dumping, 0);
        }
    }

    /// <summary>挂上进程退出兜底（幂等）。</summary>
    public static void HookProcessExit()
    {
        if (Interlocked.CompareExchange(ref _exitHooked, 1, 0) != 0) return;

        AppDomain.CurrentDomain.ProcessExit += static (_, _) => Dump("ProcessExit");
        Console.CancelKeyPress += static (_, _) => Dump("CancelKeyPress");
    }

    /// <summary>时间戳换算：Stopwatch 原始值 → UTC。</summary>
    public static DateTime ToUtc(long stopwatchTimestamp)
    {
        double seconds = (stopwatchTimestamp - OriginTimestamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        return OriginUtc.AddSeconds(seconds);
    }

    private static void NotifyCriticalFallback(string reason, string failure)
    {
        Action<string>? fallback = CriticalFallback;
        if (fallback is null) return;

        StringBuilder sb = new();
        sb.Append("[MicroShader] 故障转储落盘失败：").Append(failure).Append("；原因=").Append(reason);

        List<LogRecord> records = [];
        Log.Ring.Snapshot(records, maxRecords: 64);
        int shown = 0;
        foreach (LogRecord record in records)
        {
            if (record.Level < LogLevel.Error) continue;
            sb.Append("\n[").Append(LevelName(record.Level)).Append(']').
               Append(record.Message.AsSpan(0, Math.Min(record.Message.Length, 160)));
            if (++shown >= 10) break;
        }

        try
        {
            fallback(sb.ToString());
        }
        catch (InvalidOperationException)
        {
            // 传输层此刻可能已经断了；降级通道本身绝不允许再抛。
        }
        catch (IOException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string BuildJson(string reason)
    {
        StringBuilder sb = new(16 * 1024);
        sb.Append('{');
        DumpJsonWriter writer = new(sb);

        writer.Number("schema", 1);
        writer.String("reason", reason);
        writer.String("timestampUtc", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        using (System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess())
        {
            writer.Number("processId", self.Id);
            writer.Number("workingSetBytes", self.WorkingSet64);
            writer.Number("privateMemoryBytes", self.PrivateMemorySize64);
        }

        writer.Number("totalWritten", Log.Ring.TotalWritten);
        writer.Number("capacity", Log.Ring.Capacity);
        writer.Number("slotBytes", LogRingBuffer.SlotBytes);
        // 黑匣子时间窗口（v2.0 第 5 条）：窗口 = 容量 ÷ 速率。按实测 15.6M 条/秒给出下界。
        writer.Number("windowMicrosecondsAt156Mps", (long)Log.Ring.Capacity * 1_000_000L / 15_600_000L);

        List<LogRecord> records = [];
        Log.Ring.Snapshot(records);

        writer.Number("logCount", records.Count);
        writer.Property("logs");
        sb.Append('[');
        for (int i = 0; i < records.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('{');
            DumpJsonWriter entry = new(sb);
            entry.Number("seq", records[i].GlobalSequence);
            entry.String("timeUtc", ToUtc(records[i].Timestamp).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            entry.String("level", LevelName(records[i].Level));
            entry.String("category", records[i].CategoryName);
            entry.String("message", records[i].Message);
            sb.Append('}');
        }
        sb.Append(']');

        // 原生 stderr 旁证（v2.0 第 6 条）：dxc.exe 的错误 100% 走 stderr 且是 GBK/ANSI，
        // 这里只是「有什么记什么」，不作诊断权威来源。
        string native = StdioAirGapGuard.ReadNativeStderr();
        if (native.Length > 0)
        {
            writer.String("nativeStderr", native);
        }

        IDumpContributor[] contributors;
        lock (Contributors)
        {
            contributors = [.. Contributors];
        }

        if (contributors.Length > 0)
        {
            writer.Property("contributors");
            sb.Append('{');
            for (int i = 0; i < contributors.Length; i++)
            {
                if (i > 0) sb.Append(',');
                DumpJsonWriter outer = new(sb);
                outer.Property(contributors[i].Name);
                try
                {
                    sb.Append('{');
                    contributors[i].Contribute(new DumpJsonWriter(sb));
                    sb.Append('}');
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
                {
                    sb.Append("{\"error\":\"contributor failed\"}");
                }
            }
            sb.Append('}');
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "Trace",
        LogLevel.Debug => "Debug",
        LogLevel.Info => "Info",
        LogLevel.Warning => "Warning",
        LogLevel.Error => "Error",
        _ => "Unknown",
    };
}
