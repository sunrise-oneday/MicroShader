namespace MicroShader.ObservabilityEngine;

/// <summary>日志出站通道（由 LSP 传输层实现，如 "window/logMessage"）。</summary>
public interface ILogSink
{
    void Emit(LogLevel level, int category, ReadOnlySpan<char> message);
}

/// <summary>
/// 可选扩展：支持 "$/logTrace" 的通道。Trace 级"绝不"走 "window/logMessage"
/// （v2.0 清单第 11 条：会污染编辑器 Output 面板），只在通道实现了本接口时才逐条送出。
/// </summary>
public interface ILogTraceSink
{
    void EmitTrace(int category, ReadOnlySpan<char> message);
}

/// <summary>
/// LSP 通道限流汇（规格书模块 I 的 "LspChannelThrottledSink"）：
/// 过滤掉 Trace/Debug，只放行 Info/Warning/Error，并对突发做滑动窗口限流聚合。
/// </summary>
/// <remarks>
/// "过滤规则"：Trace → 仅当内层实现 <see cref="ILogTraceSink"/> 时走 "$/logTrace"；
/// Debug → 只留在环形缓冲里，永不出站；Info/Warning/Error → 受窗口限流。
/// "限流规则"：窗口内放行数达到上限后，后续消息只累加计数不逐条出站；
/// 调用方按 <see cref="PumpInterval"/> 周期调用 <see cref="Pump"/>，把计数聚合成一条
/// 「日志过多，其余 N 条已缓存在内存黑匣子」。"聚合文案由内层截断"，避免超长通知。
/// 本类不是线程安全的出站单写者 —— 它假定调用方（R3 调度线程 / 定时器线程）
/// 是唯一消费者，与环形缓冲的 MPSC 模型一致。
/// </remarks>
public sealed class LspChannelThrottledSink : ILogSink
{
    /// <summary>默认窗口内放行上限（规格书正文：过去 1 秒超 20 条即聚合）。</summary>
    public const int DefaultWindowLimit = 20;

    private readonly ILogSink _inner;
    private readonly int _windowLimit;
    private readonly long _windowTicks;

    private long _windowStart;
    private int _emittedInWindow;
    private int _suppressed;

    public LspChannelThrottledSink(ILogSink inner, int windowLimit = DefaultWindowLimit, TimeSpan? window = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (windowLimit < 1) throw new ArgumentOutOfRangeException(nameof(windowLimit), windowLimit, "窗口放行上限必须 ≥ 1。");

        _inner = inner;
        _windowLimit = windowLimit;
        _windowTicks = (window ?? TimeSpan.FromSeconds(1)).Ticks;
        _windowStart = DateTime.UtcNow.Ticks;
    }

    /// <summary>建议的 Pump 周期（规格书正文：后台定时器每 500ms 轮询一次）。</summary>
    public static TimeSpan PumpInterval => TimeSpan.FromMilliseconds(500);

    /// <summary>窗口内被聚合掉的条数（自检/诊断用）。</summary>
    public int SuppressedCount => _suppressed;

    public void Emit(LogLevel level, int category, ReadOnlySpan<char> message)
    {
        if (level == LogLevel.Trace)
        {
            if (_inner is ILogTraceSink trace) trace.EmitTrace(category, message);
            return;
        }

        if (level == LogLevel.Debug) return;

        DateTime now = DateTime.UtcNow;
        if (now.Ticks - _windowStart >= _windowTicks)
        {
            _windowStart = now.Ticks;
            _emittedInWindow = 0;
        }

        if (_emittedInWindow >= _windowLimit)
        {
            _suppressed++;
            return;
        }

        _emittedInWindow++;
        _inner.Emit(level, category, message);
    }

    /// <summary>聚合本轮被吞掉的日志。返回是否真的吐出了一条聚合消息。</summary>
    public bool Pump()
    {
        if (_suppressed == 0) return false;

        int count = _suppressed;
        _suppressed = 0;
        _windowStart = DateTime.UtcNow.Ticks;
        _emittedInWindow = 0;

        string message = BuildAggregate(count);
        _inner.Emit(LogLevel.Warning, LogCategories.Runtime, message);
        return true;
    }

    private static string BuildAggregate(int suppressed)
    {
        Span<char> buffer = stackalloc char[128];
        const string prefix = "日志过多，其余 ";
        const string suffix = " 条已缓存在内存黑匣子（可用 DumpDiagnosticSnapshot 导出）";
        prefix.AsSpan().CopyTo(buffer);
        int written = prefix.Length;
        if (!suppressed.TryFormat(buffer[written..], out int digits, default, null)) digits = 0;
        written += digits;
        suffix.AsSpan().CopyTo(buffer[written..]);
        written += suffix.Length;
        return buffer[..written].ToString();
    }
}
