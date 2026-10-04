namespace MicroShader.Domain;

/// <summary>
/// 代表编辑器当前打开的 .shader 物理文档（规格书 §四 契约类型）。
/// 一个文档对应一个 LSP "textDocument"。
/// </summary>
public sealed class SourceShaderDocument
{
    /// <summary>LSP 文档 URI（"file:///…"）。</summary>
    public required string FileUri { get; init; }

    /// <summary>物理绝对路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>LSP 文档版本号，用于诊断回传时的版本仲裁（ADR-008）。</summary>
    public int Version { get; set; }

    /// <summary>当前全文（Full 同步，"TextDocumentSyncKind.Full"）。</summary>
    public string FullText { get; set; } = string.Empty;

    /// <summary>最近一次切片结果。</summary>
    public List<ShaderPassSnippet> Passes { get; } = new();
}
