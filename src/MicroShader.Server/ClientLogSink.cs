using MicroShader.CoordinationEngine.Session;
using MicroShader.ObservabilityEngine;

namespace MicroShader.Server;

/// <summary>
/// 把模块 7 的黑匣子日志旁路到 LSP 通道（"window/logMessage"）。
/// </summary>
/// <remarks>
/// 这是 README 模块 8 后续项第 1 条要求的接线。三层各司其职：
/// <list type="number">
/// <item>"本类"：无锁快路径先丢掉 Trace/Debug（不取锁、不分配），再进锁；</item>
/// <item><see cref="LspChannelThrottledSink"/>：滑窗限流（默认每秒 20 条），超出的聚合后由
/// <see cref="Pump"/> 定时吐出 —— 保证不会把编辑器的 Output 面板刷爆；</item>
/// <item><see cref="LspForwardSink"/>：物化文本并交给会话出站。</item>
/// </list>
/// "为什么必须加锁"："LspChannelThrottledSink" 的文档明确声明它「假定调用方是唯一消费者」，
/// 而 "Log.Write" 会被多个分析线程调用。限流汇自带可变窗口状态，不加锁就是数据竞争。
/// "为什么不会拖慢热路径"：Trace/Debug 在取锁之前就返回了；真正进锁的只有 Info 及以上
/// （正常速率下每文档个位数）。环形缓冲是唯一真相源，本汇只是旁路 —— 任何一侧出问题都不影响另一侧。
/// </remarks>
internal sealed class ClientLogSink : ILogSink, IDisposable
{
    private readonly object _gate = new();
    private readonly LspChannelThrottledSink _throttled;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public ClientLogSink(LspServerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _throttled = new LspChannelThrottledSink(new LspForwardSink(session), LspChannelThrottledSink.DefaultWindowLimit);
        _pump = Task.Run(PumpLoopAsync);
    }

    /// <summary>窗口内被聚合掉的条数（排障用）。</summary>
    public int SuppressedCount
    {
        get
        {
            lock (_gate) return _throttled.SuppressedCount;
        }
    }

    public void Emit(LogLevel level, int category, ReadOnlySpan<char> message)
    {
        // 无锁快路径：与 LspChannelThrottledSink 的过滤规则保持一致，绝不把这两级送进锁。
        if (level is LogLevel.Trace or LogLevel.Debug)
        {
            return;
        }

        lock (_gate)
        {
            _throttled.Emit(level, category, message);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 关闭期不关心泵的异常。
        }

        _cts.Dispose();
    }

    private async Task PumpLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(LspChannelThrottledSink.PumpInterval, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                lock (_gate)
                {
                    _throttled.Pump();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 日志泵内部异常不得杀死泵循环
            }
        }
    }
}

/// <summary>限流汇的内层落点：把一条日志物化成 "window/logMessage" 交给会话出站。</summary>
internal sealed class LspForwardSink : ILogSink
{
    private readonly LspServerSession _session;

    public LspForwardSink(LspServerSession session) => _session = session;

    public void Emit(LogLevel level, int category, ReadOnlySpan<char> message)
    {
        // LSP MessageType：1=Error / 2=Warning / 3=Info / 4=Log
        var type = level switch
        {
            LogLevel.Error => 1,
            LogLevel.Warning => 2,
            LogLevel.Info => 3,
            _ => 4,
        };

        // 只有通过限流的少数消息才会走到这里，因此这里的一次性格式化是可接受的。
        var text = string.Concat(LogCategories.Name(category), ": ", message);
        _session.TrySendLogMessage(type, text);
    }
}
