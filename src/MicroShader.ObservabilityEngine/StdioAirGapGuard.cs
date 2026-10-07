using System.Runtime.InteropServices;

namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 标准 I/O 物理隔离屏障（规格书模块 I 的 "StdioAirGapGuard"）：
/// 把「第三方库私自 Console.WriteLine / 原生库私自 printf」与 LSP 的 stdio 协议通道彻底隔开。
/// </summary>
/// <remarks>
/// "必须在进程入口第一件事调用"，且"早于任何 Console 访问"：Console 会缓存句柄，
/// 晚做则屏障失效（v2.0 清单第 4 条第 (1) 点，已实测「晚做仍打到原目标」）。
///
/// "三条硬约束"（v2.0 第 4 条）：
/// <list type="number">
///   <item>"先预捕获原始 stdout 流"再动手：<see cref="CapturedStdout"/> 是协议层唯一合法的
///         输出通道，若拿不到它，屏蔽动作会把我们自己的 LSP 通道一起掐死；</item>
///   <item>"绝不无条件把 STDOUT 指向 NUL"：那是 LSP 传输通道。本实现把它做成显式开关
///         "sealNativeStdout"，默认关闭；开启时调用方"必须"改用 <see cref="CapturedStdout"/>；</item>
///   <item>stderr 不走 NUL 而走临时文件（v2.0 第 6 条）：实测 dxc.exe 的编译错误 100% 走 stderr
///         且是 GBK/ANSI 编码，落到文件才能事后并入 dump 排查。注意**诊断文本仍以
///         "IDxcResult::GetErrorBuffer" 为准**，stderr 仅作旁证。</item>
/// </list>
/// 
///
/// "边界"："SetStdHandle" 改的是进程 Win32 句柄表。若某个 DLL 在初始化时
/// 已经缓存了自己的 CRT stderr 文件描述符，它仍可能写到旧目标 —— 这是本屏障的已知边界，
/// 因此 stderr 文件只是「旁证收集」，不能当作诊断的权威来源。
/// </remarks>
public static partial class StdioAirGapGuard
{
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;

    private static Stream? _capturedStdout;
    private static FileStream? _stderrFile;
    private static FileStream? _nulStream;
    private static int _installed;

    /// <summary>是否已安装屏障。</summary>
    public static bool Installed => Volatile.Read(ref _installed) != 0;

    /// <summary>
    /// 预捕获的原始 stdout 字节流。协议层必须写它，"绝不能写 "Console.Out""
    /// （"Console.Out" 已被本屏障换成 <see cref="TextWriter.Null"/>）。
    /// </summary>
    public static Stream? CapturedStdout => _capturedStdout;

    /// <summary>原生 stderr 被重定向到的文件路径（可能为 null）。</summary>
    public static string? StderrRedirectPath => _stderrFile?.Name;

    /// <summary>
    /// 安装屏障。幂等（重复调用直接返回）。
    /// </summary>
    /// <param name="stderrRedirectPath">stderr 落盘路径；null 则自动放到 "%LOCALAPPDATA%/MicroShader/native/"。</param>
    /// <param name="sealNativeStdout">
    /// 是否同时把进程级 STDOUT 句柄指向 NUL，以封住原生库的 printf。
    /// 默认 "false"；启用前必须确认传输层已经持有 <see cref="CapturedStdout"/>。
    /// </param>
    public static void Install(string? stderrRedirectPath = null, bool sealNativeStdout = false)
    {
        if (Interlocked.CompareExchange(ref _installed, 1, 0) != 0) return;

        // ── 步骤 1：预捕获原始 stdout（必须在任何屏蔽动作之前）────────────────────
        _capturedStdout = Console.OpenStandardOutput();

        // ── 步骤 2：托管层的杂散 Console.WriteLine 一律丢弃 ──────────────────────
        Console.SetOut(TextWriter.Null);

        // ── 步骤 3（可选）：把进程级 STDOUT 句柄指向 NUL，封住原生 printf ─────────
        if (sealNativeStdout)
        {
            _nulStream = OpenNulDevice();
            if (_nulStream is not null)
            {
                _ = SetStdHandle(StdOutputHandle, _nulStream.SafeFileHandle.DangerousGetHandle());
            }
        }

        // ── 步骤 4：STD_ERROR_HANDLE → 临时文件（不是 NUL）──────────────────────
        string path = stderrRedirectPath ?? DefaultStderrPath();
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            _stderrFile = new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Create,
                    Access = FileAccess.Write,
                    Share = FileShare.ReadWrite,
                    BufferSize = 0,
                    Options = FileOptions.WriteThrough,
                });
            _ = SetStdHandle(StdErrorHandle, _stderrFile.SafeFileHandle.DangerousGetHandle());
        }
        catch (IOException)
        {
            // 拿不到文件就只保留托管层屏障。日志模块本身绝不允许影响进程启动。
            _stderrFile = null;
        }
        catch (UnauthorizedAccessException)
        {
            _stderrFile = null;
        }
    }

    /// <summary>把已写入的原生 stderr 文本读回来（dump 时并入；失败返回空串）。</summary>
    public static string ReadNativeStderr(int maxChars = 8192)
    {
        FileStream? file = _stderrFile;
        if (file is null) return string.Empty;
        try
        {
            file.Flush();
            string path = file.Name;
            using FileStream read = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long length = read.Length;
            if (length == 0) return string.Empty;

            long start = Math.Max(0, length - maxChars * 4L);
            read.Seek(start, SeekOrigin.Begin);
            byte[] buffer = new byte[length - start];
            int readTotal = read.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return System.Text.Encoding.UTF8.GetString(buffer, 0, readTotal);
        }
        catch (IOException)
        {
            return string.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>关闭屏障持有的文件句柄（进程退出/自检清理用）。</summary>
    public static void Shutdown()
    {
        _stderrFile?.Dispose();
        _stderrFile = null;
        _nulStream?.Dispose();
        _nulStream = null;
    }

    private static string DefaultStderrPath()
    {
        string root = CrashFlightRecorder.ResolveDumpRoot();
        return Path.Combine(root, "native", "microshader_stderr.log");
    }

    private static FileStream? OpenNulDevice()
    {
        try
        {
            return new FileStream("NUL", FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int SetStdHandle(int nStdHandle, IntPtr hHandle);
}
