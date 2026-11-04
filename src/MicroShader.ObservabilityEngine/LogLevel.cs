namespace MicroShader.ObservabilityEngine;

/// <summary>日志级别（值即排序权，比较用整数最快）。</summary>
public enum LogLevel : byte
{
    /// <summary>最细粒度；只进环形缓冲，绝不推向 LSP 通道（走 "$/logTrace" 时例外）。</summary>
    Trace = 0,

    Debug = 1,
    Info = 2,
    Warning = 3,
    Error = 4,
}
