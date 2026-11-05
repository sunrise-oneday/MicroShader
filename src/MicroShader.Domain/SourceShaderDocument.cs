namespace MicroShader.Domain;

/// <summary>
/// 代表编辑器当前打开的 .shader 物理文档（规格书 §四 契约类型）。
/// 一个文档对应一个 LSP "textDocument"。
/// </summary>
/// <remarks>
/// "不可变性"：所有字段在构造时确定后不再变更——每次 didChange 都由
/// <c>ShaderDocumentAnalyzer</c> 从 <c>AnalysisRequest</c> 新建一个实例，
/// 而非原地改字段。用 <c>required init</c> 把这一不变量从约定提升为编译期约束，
/// 杜绝「事后 <c>doc.Version = ...</c>」这类半修改状态。
/// </remarks>
public sealed class SourceShaderDocument
{
    /// <summary>LSP 文档 URI（"file:///…"）。</summary>
    public required string FileUri { get; init; }

    /// <summary>物理绝对路径。</summary>
    public required string FilePath { get; init; }

    /// <summary>LSP 文档版本号，用于诊断回传时的版本仲裁（ADR-008）。</summary>
    public required int Version { get; init; }

    /// <summary>当前全文（Full 同步，"TextDocumentSyncKind.Full"）。</summary>
    public required string FullText { get; init; }

    /// <summary>最近一次切片结果。</summary>
    public List<ShaderPassSnippet> Passes { get; } = new();
}
