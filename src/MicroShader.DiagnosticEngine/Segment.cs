namespace MicroShader.DiagnosticEngine;

/// <summary>段种类（ADR-023）。</summary>
public enum SegmentKind : byte
{
    /// <summary>注入段：本工具自己合成的行（宏、"#line" 锚），在源文件里没有对应行。</summary>
    Injected = 0,

    /// <summary>锚定段：逐字节照抄源文件某段行，前面用一条 "#line" 把行号锚回源文件。</summary>
    Anchored = 1,
}

/// <summary>
/// 渲染文本的一个连续段。
/// </summary>
/// <remarks>
/// "ADR-023 的核心约束"：渲染文本与行映射表必须由"同一个 Segment 列表"生成，
/// 而不是各写一遍。任何「渲染时顺手多写一行、映射表却不知道」的偏差都会让全部诊断坐标整体位移，
/// 而编译器不会报错。
/// 段只能 append，禁止 splice：嵌套（例如 include 自展开）表达为"新开一个 Anchored 段"，
/// 而不是回头改写既有段。
/// "v2.0 清单第 4 条的硬约束"："#line N" 把"下一行"编号为 N，不是当前行。
/// 因此所有注入内容必须排在第一条 "#line" 之前（<see cref="SegmentKind.Injected"/> 段只在文件头），
/// 否则多注入一行就整体偏移且编译器不报错。
/// </remarks>
public readonly struct Segment
{
    /// <summary>段种类。</summary>
    public SegmentKind Kind { get; init; }

    /// <summary>渲染文本中的起始行号（1-based）。</summary>
    public int RenderedStartLine { get; init; }

    /// <summary>渲染文本中占用的行数。</summary>
    public int RenderedLineCount { get; init; }

    /// <summary>锚定段第 0 行对应的源文件行号（1-based）；注入段为 0。</summary>
    public int SourceStartLine { get; init; }

    /// <summary>锚定段覆盖的源文件行数；注入段为 0。</summary>
    public int SourceLineCount { get; init; }

    /// <summary>锚定段在源文本中的起始字符偏移；注入段为 0。</summary>
    public int SourceStartOffset { get; init; }

    /// <summary>锚定段在源文本中占用的字符数；注入段为 0。</summary>
    public int SourceLength { get; init; }

    /// <summary>本段来自哪个文件级共享块（索引指向 "ShaderLabParseResult.SharedBlocks"）；不是共享块时为 -1。</summary>
    public int SharedBlockIndex { get; init; }

    /// <summary>本段内被重写（内容与源不同）的行数。</summary>
    public int RewrittenLineCount { get; init; }

    /// <summary>渲染行号区间的结束行（含）。</summary>
    public int RenderedEndLine => RenderedStartLine + RenderedLineCount - 1;

    /// <summary>是否为文件级共享块。</summary>
    public bool IsSharedBlock => SharedBlockIndex >= 0;

    public override string ToString() =>
        Kind == SegmentKind.Injected
            ? $"注入段 渲染[{RenderedStartLine}..{RenderedEndLine}] ({RenderedLineCount} 行)"
            : $"锚定段 渲染[{RenderedStartLine}..{RenderedEndLine}] 源[{SourceStartLine}..{SourceStartLine + SourceLineCount - 1}]" +
              (IsSharedBlock ? $" 共享块#{SharedBlockIndex}" : string.Empty);
}
