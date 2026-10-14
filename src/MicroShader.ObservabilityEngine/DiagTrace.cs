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

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    _stopped = true;
                    File.AppendAllText(path, "追踪文件超过 " + MaxBytes + " 字节，已停写。" + Environment.NewLine);
                    return;
                }

                File.AppendAllText(path, DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
        }
        catch
        {
            // 追踪失败绝不冒泡：它只服务于排障。
        }
    }

    /// <summary>
    /// 把致命异常写到固定文件（不依赖追踪是否开启）。
    /// 用于 AppDomain.UnhandledException / TaskScheduler.UnobservedTaskException —— Native AOT 下
    /// 后台线程的未处理异常会让进程 FailFast，这是唯一来得及留痕的位置。
    /// </summary>
    public static void Fatal(string kind, Exception? ex)
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
                + (ex?.ToString() ?? "(无异常对象)") + Environment.NewLine;
            File.AppendAllText(Path.Combine(root, "fatal.log"), line);
        }
        catch
        {
            // 兜底失败也只能忍。
        }
    }
}
