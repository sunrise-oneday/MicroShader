using System.Buffers;
using System.Globalization;
using System.Text.Json;
using MicroShader.CoordinationEngine.Lsp;

namespace MicroShader.CoordinationEngine.Transport;

/// <summary>
/// LSP 出站帧的构造器：把 JSON 报文体与 "Content-Length" 头部拼成一帧。
/// </summary>
/// <remarks>
/// "为什么不在 PipeWriter.GetSpan 上一次性格式化"（v2.0 修正清单第 6 条）：
/// "GetSpan()" 不保证一次给足整帧，而且头部与报文体"必须同帧提交"，
/// 否则对端会一直等它声明的 N 字节。这里的做法是：在池化缓冲里把头部与报文体拼完整，
/// 再由 <see cref="OutboundDrainSink"/> 用一次 "Write" + 一次 "FlushAsync" 原子提交。
/// </remarks>
public static class LspFrame
{
    /// <summary>头部前缀（含一个空格）。</summary>
    private static ReadOnlySpan<byte> HeaderPrefix => "Content-Length: "u8;

    private static ReadOnlySpan<byte> HeaderTerminator => "\r\n\r\n"u8;

    /// <summary>把报文体包装成完整帧写入 <paramref name="frame"/>（会先 <see cref="PooledBufferWriter.Reset"/>）。</summary>
    public static void WriteFrame(PooledBufferWriter frame, ReadOnlySpan<byte> body)
    {
        frame.Reset();

        Span<byte> digits = stackalloc byte[16];
        var count = FormatInt(body.Length, digits);
        var headerLength = HeaderPrefix.Length + count + HeaderTerminator.Length;

        var span = frame.GetSpan(headerLength + body.Length);
        HeaderPrefix.CopyTo(span);
        digits[..count].CopyTo(span[HeaderPrefix.Length..]);
        HeaderTerminator.CopyTo(span[(HeaderPrefix.Length + count)..]);
        body.CopyTo(span[headerLength..]);
        frame.Advance(headerLength + body.Length);
    }

    /// <summary>构造通知（无 id）报文体。</summary>
    public static void WriteNotificationBody(
        PooledBufferWriter body,
        string method,
        Action<Utf8JsonWriter>? writeParams)
    {
        body.Reset();
        using var writer = new Utf8JsonWriter(body, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        writer.WriteString("method", method);
        if (writeParams is not null)
        {
            writer.WritePropertyName("params");
            writeParams(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>构造成功响应报文体。</summary>
    public static void WriteResultBody(
        PooledBufferWriter body,
        in ReadOnlySequence<byte> id,
        Action<Utf8JsonWriter>? writeResult)
    {
        body.Reset();
        using var writer = new Utf8JsonWriter(body, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        WriteId(writer, id);
        writer.WritePropertyName("result");
        if (writeResult is null) writer.WriteNullValue();
        else writeResult(writer);
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>构造错误响应报文体。</summary>
    public static void WriteErrorBody(
        PooledBufferWriter body,
        in ReadOnlySequence<byte> id,
        int code,
        string message)
    {
        body.Reset();
        using var writer = new Utf8JsonWriter(body, new JsonWriterOptions { SkipValidation = true });
        writer.WriteStartObject();
        writer.WriteString("jsonrpc", "2.0");
        WriteId(writer, id);
        writer.WritePropertyName("error");
        writer.WriteStartObject();
        writer.WriteNumber("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
    }

    /// <summary>
    /// 原样回写 id。JSON-RPC 的 id 可能是数字也可能是字符串，"必须原样返回"
    /// （把数字 id 变成字符串 id 会让客户端找不到对应的挂起请求）。
    /// </summary>
    private static void WriteId(Utf8JsonWriter writer, in ReadOnlySequence<byte> id)
    {
        writer.WritePropertyName("id");
        if (id.Length == 0)
        {
            writer.WriteNullValue();
            return;
        }

        if (id.IsSingleSegment)
        {
            writer.WriteRawValue(id.FirstSpan, skipInputValidation: true);
            return;
        }

        Span<byte> buffer = stackalloc byte[128];
        if (id.Length <= buffer.Length)
        {
            id.CopyTo(buffer);
            writer.WriteRawValue(buffer[..(int)id.Length], skipInputValidation: true);
            return;
        }

        writer.WriteStringValue(Utf8Raw.Unquote(id));
    }

    /// <summary>把非负整数写成十进制 ASCII，返回位数。</summary>
    private static int FormatInt(int value, Span<byte> destination)
    {
        if (value == 0)
        {
            destination[0] = (byte)'0';
            return 1;
        }

        var index = 0;
        Span<byte> reversed = stackalloc byte[16];
        while (value > 0)
        {
            reversed[index++] = (byte)('0' + (value % 10));
            value /= 10;
        }

        for (var i = 0; i < index; i++) destination[i] = reversed[index - 1 - i];
        return index;
    }

    /// <summary>把长度数字直接格式化成字符串（日志/自检断言用）。</summary>
    public static string DescribeFrameLength(int bodyLength) => bodyLength.ToString(CultureInfo.InvariantCulture);
}
