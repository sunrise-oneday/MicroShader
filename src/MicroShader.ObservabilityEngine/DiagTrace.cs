namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 临时诊断追踪：把「服务端正在做什么」在发生之后立刻落盘，用于定位 FailFast 级崩溃
/// （这类崩溃绕过托管异常管道与环形缓冲，只有已落盘的痕迹留得下来）。
/// </summary>
/// <remarks>
/// 「为什么用哨兵文件而不是环境变量」：服务端由编辑器拉起，改环境变量需要重启编辑器；
/// 哨兵文件存在即生效、删除即关闭，可以在不打扰用户的前提下开关追踪。
/// 追踪本身永不影响主流程：任何异常都被吞掉，单文件超过 4 MB 自动停写。
/// </remarks>
public static class DiagTrace
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private static readonly object Gate = new();
    private static string? _path;
    private static bool _stopped;

    // 字节数在内存里累计：原来每条面包屑都要 File.Exists + new FileInfo().Length 两次 stat，
    // 再走一次 AppendAllText（打开、写、关闭）—— 一条追踪六个系统调用，而热路径（每次 DXC 编译）
    // 也在写。现在只剩打开/写/关闭，配额用内存计数判断。
    //
    // 刻意不长期持有 FileStream：那是每条写一次的最优解，但会让日志文件在进程存活期间一直被占用
    // （排障时最常见的就是另开一个工具 tail 它），省下的两次系统调用换不来这个麻烦。
    private static long _written;

    /// <summary>追踪是否已启用。</summary>
    public static bool Enabled => _path is not null && !_stopped;

    /// <summary>
    /// 启用追踪。path 为 null 时按「显式环境变量 MICROSHADER_TRACE_FILE → 哨兵文件 trace.on」决定。
    /// </summary>
    public static void Init(string? path)
    {
        try
        {
            var chosen = path;
            if (string.IsNullOrWhiteSpace(chosen))
            {
                chosen = Environment.GetEnvironmentVariable("MICROSHADER_TRACE_FILE");
            }

            if (string.IsNullOrWhiteSpace(chosen))
            {
                var root = DumpRoot();
                if (root is null || !File.Exists(Path.Combine(root, "trace.on")))
                {
                    return;
                }

                chosen = Path.Combine(root, "trace.log");
            }

            var dir = Path.GetDirectoryName(chosen);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _path = chosen;

            // 已有文件的大小算进配额：否则重启一次就能把「4 MB 上限」当空气。
            _written = File.Exists(chosen) ? new FileInfo(chosen).Length : 0;
            Mark("===== 新会话 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " =====");
        }
        catch
        {
            _path = null;
        }
    }

    /// <summary>崩溃转储根目录（与 CrashFlightRecorder 同源）。</summary>
    public static string? DumpRoot()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(local) ? null : Path.Combine(local, "MicroShader", "dumps");
    }

    /// <summary>写一条面包屑。失败静默。</summary>
    public static void Mark(string message)
    {
        var path = _path;
        if (path is null || _stopped)
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (_stopped)
                {
                    return;
                }

                var line = DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine;
                var bytes = System.Text.Encoding.UTF8.GetByteCount(line);

                if (_written + bytes > MaxBytes)
                {
                    _stopped = true;
                    AppendLine(path, "追踪文件超过 " + MaxBytes + " 字节，已停写。" + Environment.NewLine);
                    return;
                }

                AppendLine(path, line);
                _written += bytes;
            }
        }
        catch
        {
            // 追踪失败绝不冒泡：它只服务于排障。
        }
    }

    /// <summary>追加一行并立刻交给操作系统（不做 fsync：进程被强杀时 OS 缓存里的内容同样还在）。</summary>
    private static void AppendLine(string path, string line)
    {
        using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.ReadWrite,
                BufferSize = 0,
            });
        var buffer = System.Text.Encoding.UTF8.GetBytes(line);
        stream.Write(buffer, 0, buffer.Length);
    }

    /// <summary>
    /// 把致命异常写到固定文件（不依赖追踪是否开启）。
    /// 用于 AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException —— Native AOT 下
    /// 后台线程的未处理异常会让进程 FailFast，这是唯一来得及留痕的位置。
    /// </summary>
    public static void Fatal(string kind, Exception? ex)
        => FatalText(kind, ex?.ToString() ?? "(无异常对象)");

    /// <summary>
    /// 把一段文字写到固定文件 fatal.log（不依赖追踪是否开启）。
    /// </summary>
    /// <remarks>
    /// 给「没有异常对象、但必须留下证据」的场景用（例如崩溃转储落盘失败时的降级出口）。
    /// 与 Fatal 写同一个文件，排障时只看一处。
    /// </remarks>
    public static void FatalText(string kind, string text)
    {
        try
        {
            var root = DumpRoot();
            if (root is null)
            {
                return;
            }

            Directory.CreateDirectory(root);
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + kind + "] "
                + text + Environment.NewLine;
            File.AppendAllText(Path.Combine(root, "fatal.log"), line);
        }
        catch
        {
            // 兜底失败也只能忍。
        }
    }
}
