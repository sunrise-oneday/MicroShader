namespace MicroShader.Domain;

/// <summary>
/// 诊断严重度。数值刻意与 LSP 3.17 "DiagnosticSeverity" 取值一致，
/// 以保证跨边界零映射（Error=1 / Warning=2 / Information=3 / Hint=4）。
/// </summary>
public enum DiagnosticSeverity
{
    Error = 1,
    Warning = 2,
    Information = 3,
    Hint = 4,
}
