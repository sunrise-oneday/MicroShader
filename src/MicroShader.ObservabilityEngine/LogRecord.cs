namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 从环形缓冲里读出的一条日志（"只在 dump / 自检时构造"，热路径绝不产生这个对象）。
/// </summary>
public readonly record struct LogRecord(
    long GlobalSequence,
    long Timestamp,
    LogLevel Level,
    int Category,
    string Message)
{
    public string CategoryName => LogCategories.Name(Category);
}
