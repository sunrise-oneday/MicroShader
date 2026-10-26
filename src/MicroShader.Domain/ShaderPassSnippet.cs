namespace MicroShader.Domain;

/// <summary>
/// 从 ShaderLab 中切片出的单个程序块（规格书 §四 契约类型）。
/// 前 8 个成员即规格书原文契约；其余为模块 1 落地所必需的"纯增量"字段，不改变既有语义。
/// </summary>
public sealed class ShaderPassSnippet
{
    // ───────────────────────── 规格书 §四 契约字段 ─────────────────────────

    /// <summary>Pass 序号（按文件内出现顺序 0..N-1）；块不在任何 Pass 内时为 "-1"。</summary>
    public int PassIndex { get; set; }

    /// <summary>Pass 名（"Name "ForwardLit""）；无名时为空串。</summary>
    public string PassName { get; set; } = string.Empty;

    /// <summary>开启标记（"HLSLPROGRAM"/"CGPROGRAM"）所在物理绝对行号。</summary>
    public int StartLineNumber { get; set; }

    /// <summary>
    /// "Pass" 关键字所在行（与 <see cref="StartLineNumber"/> 同口径）。
    /// "-1" = 未知（孤儿块，或该块不在任何 Pass 内）。
    /// </summary>
    /// <remarks>
    /// 与 StartLineNumber 的区别：后者指向**程序块**（HLSLPROGRAM 等）那一行。
    /// 文档大纲需要 Pass 自己的行 —— 否则 Pass 节点的 range 会与它的子节点（程序块）
    /// 完全相同，粘滞滚动窗口取最内层就只剩程序块、看不见 Pass。
    /// </remarks>
    public int PassStartLineNumber { get; set; } = -1;

    /// <summary>结束标记（"ENDHLSL"/"ENDCG"）所在物理绝对行号；未闭合时为文件末行号。</summary>
    public int EndLineNumber { get; set; }

    /// <summary>顶点入口（"#pragma vertex"）。</summary>
    public string? VertexEntry { get; set; }

    /// <summary>片元入口（"#pragma fragment"）。</summary>
    public string? FragmentEntry { get; set; }

    /// <summary>本次编译实际启用（被选中）的变体关键字（ADR-018 矩阵的「启用集」）。</summary>
    public List<string> ActivePragmaDefines { get; } = new();

    /// <summary>块原文切片（不含开启/结束标记行）。</summary>
    public ReadOnlyMemory<char> RawHlslBlock { get; set; }

    // ───────────────────────── 模块 1 增量字段 ─────────────────────────

    /// <summary>方言。</summary>
    public ShaderBlockKind Kind { get; set; }

    /// <summary>是否为文件级共享块（"HLSLINCLUDE"/"CGINCLUDE"）。共享块不会被当作 Pass 编译单元。</summary>
    public bool IsSharedBlock { get; set; }

    /// <summary>是否未闭合（EOF 熔断）。</summary>
    public bool IsUnterminated { get; set; }

    /// <summary>是否因遇到另一个开启标记被强制闭合。</summary>
    public bool IsForceClosed { get; set; }

    /// <summary>是否游离于任何 Pass 之外（<see cref="PassIndex"/> 此时为 -1，但仍会产出编译单元）。</summary>
    public bool IsOrphan { get; set; }

    /// <summary>所属 SubShader 序号（0-based）；不在任何 SubShader 内时为 "-1"。</summary>
    public int SubShaderIndex { get; set; } = -1;

    /// <summary>同一 Pass 内的第几个程序块（0-based）。</summary>
    public int BlockOrdinalInPass { get; set; }

    /// <summary>块内容在源文本中的起始字符偏移。</summary>
    public int ContentStartOffset { get; set; }

    /// <summary>块内容在源文本中的结束字符偏移（独占）。</summary>
    public int ContentEndOffset { get; set; }

    /// <summary>是否为遗留 CG 方言块（"CGPROGRAM"）。</summary>
    public bool IsLegacyCg { get; set; }

    /// <summary>
    /// 是否抑制原生 DXC 派发。规则：遗留 CG 块且未引用现代 URP 核心头文件
    /// （"Core.hlsl" / "com.unity.render-pipelines.*" / "ShaderLibrary/"），
    /// 这类老式内置管线代码喂给 SM6.0 只会产出大面积误报。
    /// </summary>
    public bool SuppressNativeDispatch { get; set; }

    /// <summary>抑制派发的原因（面向排障，写入诊断/日志）。</summary>
    public string? SuppressReason { get; set; }

    /// <summary>计算入口（"#pragma kernel"）。</summary>
    public string? KernelEntry { get; set; }

    /// <summary>本块声明的全部变体（含 Unity 内建变体指令，标记为 <see cref="ShaderVariantKind.BuiltIn"/>）。</summary>
    public List<ShaderVariantDeclaration> Variants { get; } = new();

    /// <summary>本块记录到的全部 "#include" / "#include_with_pragmas"。</summary>
    public List<IncludeDirective> Includes { get; } = new();

    /// <summary>
    /// 应当拼入本块头部的文件级共享块索引（指向 "ShaderLabParseResult.SharedBlocks"）。
    /// v2.0 语义："HLSLINCLUDE" 作用于"本文件内所有" "HLSLPROGRAM" 块（官方原文
    /// "anywhere in this source file"），"不是"「同一 SubShader 内」。
    /// </summary>
    public List<int> ApplicableSharedBlockIndices { get; } = new();

    /// <summary>清空以便复用（配合 "ShaderLabStateMachine.Parse(..., into)" 的零分配模式）。</summary>
    public void Reset()
    {
        PassIndex = 0;
        PassName = string.Empty;
        StartLineNumber = 0;
        EndLineNumber = 0;
        PassStartLineNumber = -1;
        VertexEntry = null;
        FragmentEntry = null;
        ActivePragmaDefines.Clear();
        RawHlslBlock = default;

        Kind = ShaderBlockKind.None;
        IsSharedBlock = false;
        IsUnterminated = false;
        IsForceClosed = false;
        IsOrphan = false;
        SubShaderIndex = -1;
        BlockOrdinalInPass = 0;
        ContentStartOffset = 0;
        ContentEndOffset = 0;
        IsLegacyCg = false;
        SuppressNativeDispatch = false;
        SuppressReason = null;
        KernelEntry = null;
        Variants.Clear();
        Includes.Clear();
        ApplicableSharedBlockIndices.Clear();
    }
}
