using System.Buffers;

namespace MicroShader.CoordinationEngine.Lsp;

/// <summary>JSON-RPC 2.0 报文类别。</summary>
public enum LspMessageKind
{
    Invalid = 0,
    Request,
    Notification,
    Response,
}

/// <summary>
/// 一条已定界的 JSON-RPC 报文的"零拷贝视图"：只记录各字段在原始字节序列中的区间，
/// 不物化任何值（需要字符串时由调用方按需解出）。
/// </summary>
public readonly struct LspEnvelope
{
    /// <summary>报文类别。</summary>
    public LspMessageKind Kind { get; init; }

    /// <summary>"method" 的原始 JSON 值区间（含引号）。</summary>
    public ReadOnlySequence<byte> Method { get; init; }

    /// <summary>"id" 的原始 JSON 值区间（整数或字符串）。</summary>
    public ReadOnlySequence<byte> Id { get; init; }

    /// <summary>"params" 的原始 JSON 值区间。</summary>
    public ReadOnlySequence<byte> Params { get; init; }

    /// <summary>是否带 "params"。</summary>
    public bool HasParams { get; init; }

    /// <summary>是否带 "id"（有 "id" 才是请求，必须回包）。</summary>
    public bool HasId { get; init; }

    /// <summary>解析失败原因；成功时为 "null"。</summary>
    public string? Malformed { get; init; }

    /// <summary>方法名是否等于给定名称（零分配快路径）。</summary>
    public bool MethodIs(string name) => Kind != LspMessageKind.Invalid && Utf8Raw.JsonStringEquals(Method, name);
}
