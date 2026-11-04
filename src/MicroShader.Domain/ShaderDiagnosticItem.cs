namespace MicroShader.Domain;

/// <summary>
/// 统一的内部诊断模型（规格书 §四 契约）。所有模块只产出这一种诊断，
/// 由协议层负责翻译成 LSP "Diagnostic"。
/// </summary>
public sealed class ShaderDiagnosticItem
{
    /// <summary>严重度。</summary>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    /// <summary>1-based 物理绝对行号（永远相对于用户看到的源文件，而非渲染后的虚拟文本）。</summary>
    public int Line { get; init; }

    /// <summary>1-based UTF-16 code unit 列号；"0" 表示「只给行、列未知」的降级形态。</summary>
    public int Column { get; init; }

    /// <summary>面向用户的诊断文案。</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>诊断归属文档的 file:// URI。</summary>
    public string TargetUri { get; init; } = string.Empty;

    /// <summary>报错落在外部头文件时，指向该头文件的物理路径。</summary>
    public string? RelatedFilePath { get; init; }

    /// <summary>外部头文件中的物理行号。</summary>
    public int? RelatedLine { get; init; }

    /// <summary>
    /// 模块内部稳定错误码（如 "SL0001"）。不面向用户，用于去重、黄金测试断言与回归比对。
    /// </summary>
    public string? Code { get; init; }
}
