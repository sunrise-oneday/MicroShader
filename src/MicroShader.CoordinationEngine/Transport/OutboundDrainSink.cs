using System.Buffers;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading.Channels;

namespace MicroShader.CoordinationEngine.Transport;

/// <summary>一帧待发送的出站数据（池化缓冲，由汇聚线程负责归还）。</summary>
public readonly struct OutboundFrame
{
    public OutboundFrame(byte[]? buffer, int length)
    {
        Buffer = buffer;
        Length = length;
    }

    public byte[]? Buffer { get; }

    public int Length { get; }

    public ReadOnlySpan<byte> Span => Buffer is null ? default : Buffer.AsSpan(0, Length);

    /// <summary>归还池化缓冲。</summary>
    public void Return()
    {
        if (Buffer is not null) ArrayPool<byte>.Shared.Return(Buffer);
    }
}

/// <summary>
/// 输出端单写者汇聚池：所有要发给编辑器的 JSON-RPC 数据包都投进这里，
/// 由"唯一一个"长任务顺序写入标准输出，物理隔绝报文交错。
/// </summary>
/// <remarks>
/// "原子提交"（v2.0 第 6 条）：每帧用一次 "Write" + 一次 "FlushAsync"，
/// 头部与报文体不可能被拆到两次 flush 里。
/// "必须消费 FlushResult"（v2.0 第 7 条）："IsCompleted" 为真即对端已断开 ——
/// 这比「等写 Stdout 抛 ERROR_BROKEN_PIPE(109)」可靠得多（缓冲区写得下时对端已死也不一定抛）。
/// 因此 "IOException" 只作补充信号。
/// "背压策略"：有界队列 + "TryWrite"。队列满意味着对端已经停止读取，
/// 此时"绝不静默丢帧"（丢一个响应会让客户端永远等下去），而是判定为断开并终止会话。
/// "stdout 协议专线"（v2.0 第 15 条）：日志一律走 "window/logMessage"，
/// 绝不直接写这个通道。
/// </remarks>
public sealed class OutboundDrainSink : IAsyncDisposable
{
    /// <summary>默认队列容量：诊断发布被合并、日志被限流，4096 帧足够吸收任何合法突发。</summary>
    public const int DefaultCapacity = 4096;

    private readonly Channel<OutboundFrame> _channel;
    private readonly PipeWriter _writer;
    private readonly PooledBufferWriter _frameBuffer = new(8192);
    private readonly PooledBufferWriter _bodyBuffer = new(8192);
    private readonly object _gate = new();

    private int _disconnected;
    private long _framesWritten;
    private long _bytesWritten;

    public OutboundDrainSink(PipeWriter writer, int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(writer);
        _writer = writer;
        _channel = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>对端是否已断开（FlushResult.IsCompleted、IOException 或队列溢出）。</summary>
    public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

    /// <summary>断开原因（首次触发的那个）。</summary>
    public string? DisconnectReason { get; private set; }

    /// <summary>已成功提交的帧数与字节数。</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public long BytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>断开通知（会话据此终止读循环）。</summary>
    public event Action<string>? Disconnected;

    /// <summary>
    /// 构造并投递一帧。"非线程安全"的构造缓冲被内部串行化，
    /// 因此可以从任意线程调用（出站帧的顺序由入队顺序决定）。
    /// </summary>
    public bool SendNotification(string method, Action<Utf8JsonWriter>? writeParams)
    {
        lock (_gate)
        {
            LspFrame.WriteNotificationBody(_bodyBuffer, method, writeParams);
            LspFrame.WriteFrame(_frameBuffer, _bodyBuffer.WrittenSpan);
            return EnqueueCopy();
        }
    }

    /// <summary>构造并投递一条成功响应。</summary>
    public bool SendResult(in ReadOnlySequence<byte> id, Action<Utf8JsonWriter>? writeResult)
    {
        lock (_gate)
        {
            LspFrame.WriteResultBody(_bodyBuffer, id, writeResult);
            LspFrame.WriteFrame(_frameBuffer, _bodyBuffer.WrittenSpan);
            return EnqueueCopy();
        }
    }

    /// <summary>构造并投递一条错误响应。</summary>
    public bool SendError(in ReadOnlySequence<byte> id, int code, string message)
    {
        lock (_gate)
        {
            LspFrame.WriteErrorBody(_bodyBuffer, id, code, message);
            LspFrame.WriteFrame(_frameBuffer, _bodyBuffer.WrittenSpan);
            return EnqueueCopy();
        }
    }

    /// <summary>单写者排空循环。持久运行，直到取消或对端断开。</summary>
    public async Task DrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    if (IsDisconnected) continue;

                    // 一次 Write + 一次 Flush = 一帧原子提交。
                    _writer.Write(frame.Span);
                    var flush = await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);

                    Interlocked.Increment(ref _framesWritten);
                    Interlocked.Add(ref _bytesWritten, frame.Length);

                    if (flush.IsCompleted)
                    {
                        MarkDisconnected("对端已关闭标准输出（FlushResult.IsCompleted）");
                    }
                }
                catch (IOException ex)
                {
                    MarkDisconnected("写入标准输出失败: " + ex.Message);
                }
                catch (ObjectDisposedException)
                {
                    MarkDisconnected("标准输出管道已被释放");
                }
                catch (Exception ex)
                {
                    MarkDisconnected("写入标准输出时未预期异常: " + ex.GetType().Name + ": " + ex.Message);
                }
                finally
                {
                    frame.Return();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停机路径：把队列里剩下的帧归还掉。
        }
        finally
        {
            while (_channel.Reader.TryRead(out var leftover)) leftover.Return();
        }
    }

    /// <summary>停止接收新帧（进程退出前调用，随后 DrainAsync 会自然结束）。</summary>
    public void CompleteInput() => _channel.Writer.TryComplete();

    public async ValueTask DisposeAsync()
    {
        CompleteInput();
        if (_writer is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        }
        else
        {
            _writer.Complete();
        }

        _frameBuffer.Dispose();
        _bodyBuffer.Dispose();
    }

    /// <summary>把当前整帧拷进池化数组并投递（帧构造缓冲随即被下一帧复用）。</summary>
    private bool EnqueueCopy()
    {
        if (IsDisconnected) return false;

        var span = _frameBuffer.WrittenSpan;
        var rented = ArrayPool<byte>.Shared.Rent(span.Length);
        span.CopyTo(rented);

        if (_channel.Writer.TryWrite(new OutboundFrame(rented, span.Length))) return true;

        // 队列满 = 对端已经停止读取；丢帧比死等更危险（客户端会永远挂着），
        // 因此判定为断开并让会话退出。
        ArrayPool<byte>.Shared.Return(rented);
        MarkDisconnected("出站队列已满，对端停止读取标准输出");
        return false;
    }

    private void MarkDisconnected(string reason)
    {
        if (Interlocked.Exchange(ref _disconnected, 1) != 0) return;
        DisconnectReason = reason;
        Disconnected?.Invoke(reason);
    }
}
