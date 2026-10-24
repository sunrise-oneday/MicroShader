namespace MicroShader.CoordinationEngine.Session;

/// <summary>服务端可调参数。</summary>
public sealed class LspServerOptions
{
    /// <summary>打字防抖时长。默认 300ms，自动夹取到 [200ms, 500ms]（v2.0 第 11 条）。</summary>
    public TimeSpan? Debounce { get; init; }

    /// <summary>服务端名字（写进 "initialize" 响应的 "serverInfo"）。</summary>
    public string ServerName { get; init; } = "MicroShader";

    /// <summary>服务端版本。</summary>
    public string ServerVersion { get; init; } = "0.1.0";

    /// <summary>退出时等待出站队列排空的超时（超时即放弃，绝不无限等）。</summary>
    public TimeSpan DrainShutdownTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 空闲回收动作：由组合根在判定「会话空闲」之后调用，引擎自己不会调它。
    /// </summary>
    /// <remarks>
    /// 引擎这边只提供状态（会话的 IsIdle 与 SinceLastActivity），什么时候算空闲、
    /// 多久回收一次、整个进程回收几次，全部是宿主策略，放在组合根里。
    /// 服务端的做法是一个 2 秒周期的定时器检查这两项状态，满足门槛才调它。
    /// 为 null 表示组合根不打算做空闲回收。
    /// </remarks>
    public Action? AfterIdle { get; init; }

    /// <summary>默认参数。</summary>
    public static LspServerOptions Default { get; } = new();
}
