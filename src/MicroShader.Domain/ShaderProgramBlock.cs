namespace MicroShader.Domain;

/// <summary>
/// 一段从 ShaderLab 中切片出的程序块（<see cref="ShaderPassSnippet"/> 的只读几何描述）。
/// 刻意设计为 readonly struct：切片阶段每篇文档会产生数十个块，热路径上不应产生堆对象。
/// </summary>
public readonly struct ShaderProgramBlock
{
    /// <summary>创建块描述。</summary>
    public ShaderProgramBlock(
        ShaderBlockKind kind,
        bool isShared,
        int startLineNumber,
        int endLineNumber,
        int contentStartOffset,
        int contentEndOffset,
        bool isUnterminated,
        bool isForceClosed)
    {
        Kind = kind;
        IsShared = isShared;
        StartLineNumber = startLineNumber;
        EndLineNumber = endLineNumber;
        ContentStartOffset = contentStartOffset;
        ContentEndOffset = contentEndOffset;
        IsUnterminated = isUnterminated;
        IsForceClosed = isForceClosed;
    }

    /// <summary>方言。</summary>
    public ShaderBlockKind Kind { get; }

    /// <summary>是否为文件级共享块（"HLSLINCLUDE"/"CGINCLUDE"）。</summary>
    public bool IsShared { get; }

    /// <summary>开启标记所在物理行号。</summary>
    public int StartLineNumber { get; }

    /// <summary>结束标记所在物理行号；未闭合时为文件末行号。</summary>
    public int EndLineNumber { get; }

    /// <summary>块内容在源文本中的起始字符偏移（开启标记行之后）。</summary>
    public int ContentStartOffset { get; }

    /// <summary>块内容在源文本中的结束字符偏移（结束标记行之前，独占）。</summary>
    public int ContentEndOffset { get; }

    /// <summary>是否为 EOF 熔断（读到文件尾仍未见到结束标记）。</summary>
    public bool IsUnterminated { get; }

    /// <summary>是否因遇到另一个开启标记而被强制闭合（畸形嵌套）。</summary>
    public bool IsForceClosed { get; }

    /// <summary>块内容长度（字符）。</summary>
    public int ContentLength => ContentEndOffset - ContentStartOffset;
}
