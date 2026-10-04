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

    /// <summary>默认参数。</summary>
    public static LspServerOptions Default { get; } = new();
}
