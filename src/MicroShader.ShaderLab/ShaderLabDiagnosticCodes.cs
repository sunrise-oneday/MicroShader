namespace MicroShader.ShaderLab;

/// <summary>
/// 模块 1 产出的稳定诊断码。这些码是模块内部契约（黄金测试、去重、回归比对都依赖它），
/// 一旦发布不得复用或改语义。
/// </summary>
public static class ShaderLabDiagnosticCodes
{
    /// <summary>代码块在文件结束前未闭合（EOF 熔断）。</summary>
    public const string UnterminatedBlock = "SL0001";

    /// <summary>遇到新的开启标记，前一个代码块被强制闭合（畸形嵌套）。</summary>
    public const string ForceClosedBlock = "SL0002";

    /// <summary>结束标记与开启标记方言不匹配（HLSLPROGRAM 配 ENDCG 之类）。</summary>
    public const string MismatchedEndMarker = "SL0003";

    /// <summary>块注释 "/*" 到文件结束仍未闭合。</summary>
    public const string UnterminatedBlockComment = "SL0004";

    /// <summary>字符串字面量在行末未闭合。</summary>
    public const string UnterminatedString = "SL0005";

    // ── SL01xx：ShaderLab 标签语义校验（模块 9）──────────────
    // 与 SL0001..SL0005 同域、不同段；SL02xx 及以后留给扩展（FallBack 路径校验等）。

    /// <summary>"RenderPipeline" 值非法，且近似某个合法值（拼写错误）。</summary>
    public const string TagPipelineTypo = "SL0101";

    /// <summary>"RenderPipeline" 值未知，且找不到近似候选。</summary>
    public const string TagPipelineUnknown = "SL0102";

    /// <summary>"LightMode" 值近似某个已知值（拼写错误）。</summary>
    public const string TagLightModeTypo = "SL0103";

    /// <summary>封闭集合标签的值非法。</summary>
    public const string TagClosedSetInvalid = "SL0104";

    /// <summary>标签名拼写错误（近似某个已知标签名）。</summary>
    public const string TagNameTypo = "SL0105";
}
