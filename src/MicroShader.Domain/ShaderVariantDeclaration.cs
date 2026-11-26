namespace MicroShader.Domain;

/// <summary>变体声明种类。</summary>
public enum ShaderVariantKind : byte
{
    /// <summary>"#pragma multi_compile*"。</summary>
    MultiCompile = 0,

    /// <summary>"#pragma shader_feature*"。</summary>
    ShaderFeature = 1,

    /// <summary>
    /// Unity 内建变体指令（"multi_compile_fog"/"multi_compile_instancing"/"multi_compile_fwdbase" 等），
    /// 关键字集合由 Unity 内部与平台宏集提供，本模块不推导、不注入。
    /// </summary>
    BuiltIn = 2,
}

/// <summary>一条变体声明（对应一行 "#pragma multi_compile*" / "#pragma shader_feature*"）。</summary>
public sealed class ShaderVariantDeclaration
{
    /// <summary>声明种类。</summary>
    public ShaderVariantKind Kind { get; init; }

    /// <summary>原始指令名（如 "multi_compile_local_fragment"），保留供诊断与黄金测试使用。</summary>
    public string DirectiveName { get; init; } = string.Empty;

    /// <summary>阶段修饰（来自 "_fragment"/"_vertex" 后缀）；<see cref="ShaderStage.None"/> 表示对所有阶段生效。</summary>
    public ShaderStage Stage { get; init; }

    /// <summary>是否带 "_local" 修饰。</summary>
    public bool IsLocal { get; init; }

    /// <summary>
    /// 声明的关键字列表，已剔除占位符 "_" / "__"（它们代表「该 set 的关闭变体」，不是关键字）。
    /// 保持源码书写顺序。
    /// </summary>
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 按选变体策略（ADR-006）挑出的关键字 = 本 set 中"第一个真实关键字"；
    /// 列表为空或全是占位符时返回空串。占位符 "_"/"__" 代表「该 set 的关闭变体」，
    /// 选中它会让 "#if defined(K)" 保护的分支被整段裁掉 —— ADR-006 的判定理由正是要杜绝
    /// 「无宏激活导致逻辑被预处理器裁掉」，故必须跳过占位符。该值"只"用于选择编译哪个变体，
    /// 绝不可作为编译正确性依赖。
    /// </summary>
    public string SelectedKeyword { get; init; } = string.Empty;

    /// <summary>1-based 物理绝对行号。</summary>
    public int LineNumber { get; init; }
}
