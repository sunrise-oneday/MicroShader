using System.Buffers;
using System.IO.Pipelines;
using System.Text;

namespace MicroShader.CoordinationEngine.Transport;

/// <summary>分帧失败类别。"除 <see cref="None"/> 外全部是不可恢复错误。"</summary>
/// <remarks>
/// v2.0 修正清单第 2 条删除了原文的「逐字节滑动窗口重同步」：LSP 是长度前缀协议，
/// payload 内部对分帧层不透明（HLSL 注释、诊断文本、正在编辑的源码本身都可能字面包含
/// "Content-Length:"），按字节搜标记会"重同步到 payload 内部"，
/// 此后帧边界永久错乱且无法自愈。因此这里只做"合法性校验"，绝不猜边界。
/// </remarks>
public enum FramingFailureKind
{
    None = 0,

    /// <summary>对端正常关闭且缓冲已空。</summary>
    EndOfStream,

    /// <summary>头部块超过上限仍未出现分隔符。</summary>
    HeaderTooLarge,

    /// <summary>头部含非 ASCII 字节或结构非法。</summary>
    MalformedHeader,

    /// <summary>头部块里没有 Content-Length。</summary>
    MissingContentLength,

    /// <summary>Content-Length 不是合法的非负十进制整数。</summary>
    InvalidContentLength,

    /// <summary>声明的长度超过 64MB 上限。</summary>
    PayloadTooLarge,

    /// <summary>流在报文体凑齐之前就结束了。</summary>
    TruncatedPayload,
}

/// <summary>一次分帧的结果。</summary>
public readonly struct FrameReadResult
{
    /// <summary>报文体切片；失败或 EOF 时为空。</summary>
    public ReadOnlySequence<byte> Payload { get; init; }

    /// <summary>失败类别。</summary>
    public FramingFailureKind Failure { get; init; }

    /// <summary>头部声明的字节长度（失败时可能为 0）。</summary>
    public int DeclaredLength { get; init; }

    /// <summary>是否成功取到完整的一帧。</summary>
    public bool Succeeded => Failure == FramingFailureKind.None;

    /// <summary>对端是否正常关闭。</summary>
    public bool IsEndOfStream => Failure == FramingFailureKind.EndOfStream;

    /// <summary>是否属于「协议已错乱、必须终止会话」的失败。</summary>
    public bool IsUnrecoverable =>
        Failure is not FramingFailureKind.None and not FramingFailureKind.EndOfStream;
}

/// <summary>
/// 标准输入物理分帧通道：从 <see cref="PipeReader"/> 上切出 LSP 的
/// "Content-Length: N\r\n\r\n" 帧，"零拷贝"地把报文体作为
/// <see cref="ReadOnlySequence{T}"/> 借给上层。
/// </summary>
/// <remarks>
/// "长度单位"："Content-Length" 按 "UTF-8 字节数"计算（不是字符数），
/// 这正是本类工作在字节层的原因（v2.0 修正清单第 1 条）。
/// "头部宽容度"：字段顺序任意、允许未知字段（如 "Content-Type"）、
/// 字段名大小写不敏感、"\r\n" 与裸 "\n" 都接受（第 1 条）。
/// "所有权"：成功返回后，报文体切片只在调用 <see cref="Complete"/> 之前有效。
/// 上层必须在本线程同步处理完再调用（这也是「Stdio 读单线程」的意义所在）。
/// </remarks>
public sealed class StdioFramingChannel
{
    /// <summary>头部块上限：超过即判定协议错乱。</summary>
    public const int MaxHeaderBytes = 8 * 1024;

    /// <summary>报文体上限：超过即拒绝（防止一个畸形头部让我们分配 GB 级缓冲）。</summary>
    public const int MaxPayloadBytes = 64 * 1024 * 1024;

    private readonly PipeReader _reader;
    private SequencePosition _pendingConsumed;
    private bool _hasPending;

    public StdioFramingChannel(PipeReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    /// <summary>异步读取下一帧。</summary>
    public async ValueTask<FrameReadResult> ReadFrameAsync(CancellationToken cancellationToken)
    {
        if (_hasPending)
        {
            throw new InvalidOperationException("上一帧尚未调用 Complete()，报文体切片仍然有效。");
        }

        while (true)
        {
            var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            var buffer = result.Buffer;

            if (!TryFindHeaderEnd(buffer, out var bodyStart))
            {
                if (buffer.Length > MaxHeaderBytes)
                {
                    _reader.AdvanceTo(buffer.Start);
                    return new FrameReadResult { Failure = FramingFailureKind.HeaderTooLarge };
                }

                if (result.IsCompleted)
                {
                    _reader.AdvanceTo(buffer.Start);
                    var kind = buffer.Length == 0
                        ? FramingFailureKind.EndOfStream
                        : FramingFailureKind.TruncatedPayload;
                    return new FrameReadResult { Failure = kind };
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            if (!TryParseContentLength(buffer.Slice(0, bodyStart), out var declared, out var failure))
            {
                _reader.AdvanceTo(buffer.Start);
                return new FrameReadResult { Failure = failure };
            }

            if (declared > MaxPayloadBytes)
            {
                _reader.AdvanceTo(buffer.Start);
                return new FrameReadResult { Failure = FramingFailureKind.PayloadTooLarge, DeclaredLength = declared };
            }

            if (buffer.Length - bodyStart < declared)
            {
                if (result.IsCompleted)
                {
                    _reader.AdvanceTo(buffer.Start);
                    return new FrameReadResult
                    {
                        Failure = FramingFailureKind.TruncatedPayload,
                        DeclaredLength = declared,
                    };
                }

                _reader.AdvanceTo(buffer.Start, buffer.End);
                continue;
            }

            var payload = buffer.Slice(bodyStart, declared);
            _pendingConsumed = buffer.GetPosition(bodyStart + declared);
            _hasPending = true;
            return new FrameReadResult { Payload = payload, DeclaredLength = declared };
        }
    }

    /// <summary>确认上一帧已处理完毕，归还管道缓冲。</summary>
    public void Complete()
    {
        if (!_hasPending) return;
        _hasPending = false;
        _reader.AdvanceTo(_pendingConsumed);
    }

    /// <summary>在不产生帧的情况下放弃当前缓冲游标（会话终止路径）。</summary>
    public void Abandon()
    {
        _hasPending = false;
        _reader.CancelPendingRead();
    }

    /// <summary>在字节序列里定位头部块结束位置（即报文体起点）。</summary>
    private static bool TryFindHeaderEnd(in ReadOnlySequence<byte> buffer, out long bodyStart)
    {
        bodyStart = 0;
        var reader = new SequenceReader<byte>(buffer);
        var limit = Math.Min(buffer.Length, MaxHeaderBytes + 1L);

        for (long i = 0; i < limit; i++)
        {
            if (!reader.TryPeek(i, out var current)) return false;
            if (current != (byte)'\n') continue;

            if (i >= 1 && reader.TryPeek(i - 1, out var prev))
            {
                // 裸 \n\n
                if (prev == (byte)'\n')
                {
                    bodyStart = i + 1;
                    return true;
                }

                // \r\n\r\n
                if (prev == (byte)'\r' && i >= 2 && reader.TryPeek(i - 2, out var before) && before == (byte)'\n')
                {
                    bodyStart = i + 1;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>解析头部块里的 Content-Length。</summary>
    private static bool TryParseContentLength(
        in ReadOnlySequence<byte> header,
        out int declared,
        out FramingFailureKind failure)
    {
        declared = 0;
        failure = FramingFailureKind.None;

        var length = (int)header.Length;
        Span<byte> stack = stackalloc byte[512];
        var rented = length <= stack.Length ? null : ArrayPool<byte>.Shared.Rent(length);
        Span<byte> text = rented is null ? stack : rented;
        try
        {
            header.CopyTo(text);
            var block = text[..length];

            // 头部必须是纯 ASCII（含 BOM / 中文字节即判非法）。
            foreach (var b in block)
            {
                if (b >= 0x80)
                {
                    failure = FramingFailureKind.MalformedHeader;
                    return false;
                }
            }

            var found = false;
            var start = 0;
            while (start < block.Length)
            {
                var lineEnd = block[start..].IndexOf((byte)'\n');
                var line = lineEnd < 0 ? block[start..] : block.Slice(start, lineEnd);
                start = lineEnd < 0 ? block.Length : start + lineEnd + 1;

                if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
                if (line.Length == 0) continue;

                var colon = line.IndexOf((byte)':');
                if (colon <= 0)
                {
                    failure = FramingFailureKind.MalformedHeader;
                    return false;
                }

                var name = line[..colon];
                if (!IsContentLength(name)) continue;

                var value = Trim(line[(colon + 1)..]);
                if (value.Length == 0)
                {
                    failure = FramingFailureKind.InvalidContentLength;
                    return false;
                }

                foreach (var b in value)
                {
                    if (b is < (byte)'0' or > (byte)'9')
                    {
                        failure = FramingFailureKind.InvalidContentLength;
                        return false;
                    }
                }

                // 手工累加而不是 int.TryParse：避免把 "0000000000000012" 这类形态交给 BCL 处理，
                // 同时天然带上溢出检测。
                long parsed = 0;
                foreach (var b in value)
                {
                    parsed = (parsed * 10) + (b - (byte)'0');
                    if (parsed > MaxPayloadBytes) break;
                }

                declared = (int)Math.Min(parsed, int.MaxValue);
                found = true;

                if (declared > MaxPayloadBytes)
                {
                    failure = FramingFailureKind.PayloadTooLarge;
                    return false;
                }
            }

            if (!found)
            {
                failure = FramingFailureKind.MissingContentLength;
                return false;
            }

            return true;
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool IsContentLength(ReadOnlySpan<byte> name)
    {
        ReadOnlySpan<byte> expected = "content-length"u8;
        if (name.Length != expected.Length) return false;
        for (var i = 0; i < expected.Length; i++)
        {
            var b = name[i];
            if (b is >= (byte)'A' and <= (byte)'Z') b = (byte)(b + 32);
            if (b != expected[i]) return false;
        }
        return true;
    }

    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && (value[start] == (byte)' ' || value[start] == (byte)'\t')) start++;
        while (end > start && (value[end - 1] == (byte)' ' || value[end - 1] == (byte)'\t')) end--;
        return value[start..end];
    }

    /// <summary>把失败类别翻译成人类可读文案（写进日志与 window/showMessage）。</summary>
    public static string Describe(FramingFailureKind kind) => kind switch
    {
        FramingFailureKind.None => "正常",
        FramingFailureKind.EndOfStream => "对端关闭了标准输入（宿主退出）",
        FramingFailureKind.HeaderTooLarge => "头部块超过 " + MaxHeaderBytes + " 字节仍未出现空行",
        FramingFailureKind.MalformedHeader => "头部含非 ASCII 字节或缺少冒号分隔",
        FramingFailureKind.MissingContentLength => "头部块中没有 Content-Length",
        FramingFailureKind.InvalidContentLength => "Content-Length 不是合法的十进制整数",
        FramingFailureKind.PayloadTooLarge => "Content-Length 超过 " + MaxPayloadBytes + " 字节上限",
        FramingFailureKind.TruncatedPayload => "报文未收全，对端就已关闭",
        _ => "未知",
    };
}
