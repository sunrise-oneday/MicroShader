namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 全系统日志门面（规格书模块 I 的 "public static class Log"）。
/// </summary>
/// <remarks>
/// 
/// 与原文签名的两处偏离，均为 v2.0 清单第 11 条明确建议的 AOT 友好化：
/// <list type="bullet">
///   <item>分类由 "string category" 改为 "int" 静态常量（<see cref="LogCategories"/>），
///         避免每次调用算字符串哈希或查表；</item>
///   <item>补齐重载以支持 "\$"..."" 零分配插值（<see cref="LogMessageHandler"/>）。</item>
/// </list>
/// 
/// 
/// 静态门面在所有核心子系统之上，因此本模块"零 ProjectReference"：黑匣子必须比它记录的对象活得久。
/// 
/// </remarks>
public static class Log
{
    private static LogRingBuffer _ring = CreateDefaultRing();

    private static LogRingBuffer CreateDefaultRing()
    {
        LogRingBuffer ring = new();
        ring.Sanitizer = BuildDefaultSanitizer();
        return ring;
    }

    /// <summary>总开关。关闭后热路径只剩一次静态字段读 + 分支。</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>低于该级别的记录直接丢弃（默认全收，因为落进环形缓冲几乎不花钱）。</summary>
    public static LogLevel MinimumLevel { get; set; } = LogLevel.Trace;

    /// <summary>当前生效的环形缓冲（dump / 自检需要直接访问）。</summary>
    public static LogRingBuffer Ring => _ring;

    /// <summary>替换环形缓冲（自检隔离用；运行时不调用）。</summary>
    public static void Reset(LogRingBuffer? ring = null)
    {
        _ring = ring ?? new LogRingBuffer();
        _ring.Sanitizer = BuildDefaultSanitizer();
    }

    /// <summary>
    /// 依据当前进程的用户主目录与当前工作目录构造默认脱敏器。
    /// 这两个路径是唯一会出现在我们日志里的绝对用户路径来源。
    /// </summary>
    public static LogSanitizer BuildDefaultSanitizer()
    {
        List<string> roots = [];
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(profile)) roots.Add(profile);
        }
        catch (PlatformNotSupportedException)
        {
            // AOT/受限环境取不到就算了，脱敏降级为「只靠入队截断」。
        }

        try
        {
            string cwd = Environment.CurrentDirectory;
            if (!string.IsNullOrEmpty(cwd)) roots.Add(cwd);
        }
        catch (PlatformNotSupportedException)
        {
        }

        return roots.Count == 0 ? LogSanitizer.Empty : new LogSanitizer(roots);
    }

    /// <summary>
    /// 可选出站汇（模块 8 落地时接 <see cref="LspChannelThrottledSink"/>，把日志以
    /// "window/logMessage" 推给客户端）。为 "null" 时热路径只多一次引用读 + 分支。
    /// </summary>
    /// <remarks>
    /// 环形缓冲是"唯一真相源"，汇是旁路：汇的实现绝不允许抛异常穿透到这里
    /// （否则会把「记一条日志」变成「整条分析链路失败」）。
    /// </remarks>
    public static ILogSink? Sink { get; set; }

    public static void Write(LogLevel level, int category, ReadOnlySpan<char> message)
    {
        if (!Enabled || level < MinimumLevel) return;

        _ring.Write(level, category, message);

        // 旁路出站：先写黑匣子、再尽力送达客户端。汇内部自带限流与级别过滤。
        if (Sink is { } sink)
        {
            sink.Emit(level, category, message);
        }
    }

    public static void Trace(int category, ReadOnlySpan<char> message) => Write(LogLevel.Trace, category, message);

    public static void Debug(int category, ReadOnlySpan<char> message) => Write(LogLevel.Debug, category, message);

    public static void Info(int category, ReadOnlySpan<char> message) => Write(LogLevel.Info, category, message);

    public static void Warn(int category, ReadOnlySpan<char> message) => Write(LogLevel.Warning, category, message);

    public static void Error(int category, ReadOnlySpan<char> message) => Write(LogLevel.Error, category, message);

    public static void Trace(int category, ref LogMessageHandler handler) => Write(LogLevel.Trace, category, handler.Text);

    public static void Debug(int category, ref LogMessageHandler handler) => Write(LogLevel.Debug, category, handler.Text);

    public static void Info(int category, ref LogMessageHandler handler) => Write(LogLevel.Info, category, handler.Text);

    public static void Warn(int category, ref LogMessageHandler handler) => Write(LogLevel.Warning, category, handler.Text);

    public static void Error(int category, ref LogMessageHandler handler) => Write(LogLevel.Error, category, handler.Text);

    /// <summary>
    /// 把内存黑匣子转储到磁盘，返回落盘路径（失败返回 "null"）。
    /// 规格书原文签名："DumpDiagnosticSnapshot(string reason)"。
    /// </summary>
    public static string? DumpDiagnosticSnapshot(string reason) => CrashFlightRecorder.Dump(reason);
}

// 注：默认脱敏器在 Log 的静态字段初始化里装上，刻意不用 [ModuleInitializer]
// —— CA2255 明确禁止库程序集使用它（本工程 TreatWarningsAsErrors，直接编译失败）。
