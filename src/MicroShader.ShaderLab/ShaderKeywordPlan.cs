using MicroShader.Domain;

namespace MicroShader.ShaderLab;

/// <summary>
/// 一次原生派发所需的「阶段宏 + 关键字宏矩阵」文本计划（ADR-018）。
/// 产出是逐行预处理器指令，由模块 3 的装配器拼到注入段中。
/// </summary>
public sealed class ShaderKeywordPlan
{
    /// <summary>逐行宏指令（不含换行符）。</summary>
    public List<string> Lines { get; } = new();

    /// <summary>本派发中被置为 1 的关键字（"#define K 1"）。</summary>
    public List<string> EnabledKeywords { get; } = new();

    /// <summary>本派发中被声明（"#define K_KEYWORD_DECLARED 1"）的关键字。</summary>
    public List<string> DeclaredKeywords { get; } = new();

    /// <summary>本派发的阶段。</summary>
    public ShaderStage Stage { get; internal set; }

    /// <summary>本派发注入的阶段宏名（"SHADER_STAGE_*"）；未知阶段时为空串。</summary>
    public string StageMacro { get; internal set; } = string.Empty;

    /// <summary>是否因阶段修饰不匹配而被忽略的声明数（供排障）。</summary>
    public int SkippedDeclarations { get; internal set; }

    /// <summary>清空以便复用。</summary>
    public void Reset()
    {
        Lines.Clear();
        EnabledKeywords.Clear();
        DeclaredKeywords.Clear();
        Stage = ShaderStage.None;
        StageMacro = string.Empty;
        SkippedDeclarations = 0;
    }
}
