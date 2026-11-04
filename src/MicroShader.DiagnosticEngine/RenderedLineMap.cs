namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 渲染文本 ↔ 源文件的行/列映射表，由 <see cref="Segment"/> 列表派生（ADR-023）。
/// </summary>
/// <remarks>
/// 不变量 I2 要求锚定段逐行逐字节等于源文件（重写行除外），因此本表的行映射是「段算术」：
/// 锚定段内第 k 行 ↔ 源文件 "SourceStartLine + k"；注入段的行映射到 0（源文件没有这一行）。
/// 行内列映射只在被重写的行上不是恒等 —— 见 <see cref="LineRewrite.MapRenderedColumnToSource"/>。
/// </remarks>
public sealed class RenderedLineMap
{
    private readonly int[] _lineStarts;
    private readonly int[] _lineLengths;
    private readonly int[] _lineSource;
    private readonly int[] _lineRewrite;
    private readonly LineRewrite[] _rewrites;
    private readonly Segment[] _segments;

    internal RenderedLineMap(
        int[] lineStarts,
        int[] lineLengths,
        int[] lineSource,
        int[] lineRewrite,
        LineRewrite[] rewrites,
        Segment[] segments)
    {
        _lineStarts = lineStarts;
        _lineLengths = lineLengths;
        _lineSource = lineSource;
        _lineRewrite = lineRewrite;
        _rewrites = rewrites;
        _segments = segments;
    }

    /// <summary>渲染文本的行数。</summary>
    public int LineCount => _lineStarts.Length;

    /// <summary>生成本映射表的段列表（唯一的真相来源）。</summary>
    public IReadOnlyList<Segment> Segments => _segments;

    /// <summary>被重写的行数。</summary>
    public int RewriteCount => _rewrites.Length;

    /// <summary>按索引取一条重写记录。</summary>
    public LineRewrite RewriteAt(int index) => _rewrites[index];

    /// <summary>渲染行的起始字符偏移（1-based 行号）。</summary>
    public int LineStartOffset(int renderedLine) => _lineStarts[renderedLine - 1];

    /// <summary>渲染行的字符长度（不含换行符）。</summary>
    public int LineLength(int renderedLine) => _lineLengths[renderedLine - 1];

    /// <summary>该渲染行是否为注入行（源文件没有对应行）。</summary>
    public bool IsInjected(int renderedLine) =>
        renderedLine >= 1 && renderedLine <= LineCount && _lineSource[renderedLine - 1] == 0;

    /// <summary>该渲染行对应的源行号；注入行返回 0。</summary>
    public int SourceLineOf(int renderedLine) =>
        renderedLine >= 1 && renderedLine <= LineCount ? _lineSource[renderedLine - 1] : 0;

    /// <summary>该渲染行是否被重写。</summary>
    public bool IsRewritten(int renderedLine) =>
        renderedLine >= 1 && renderedLine <= LineCount && _lineRewrite[renderedLine - 1] >= 0;

    /// <summary>取该渲染行的重写记录；未重写时返回 false。</summary>
    public bool TryGetRewrite(int renderedLine, out LineRewrite rewrite)
    {
        if (renderedLine >= 1 && renderedLine <= LineCount)
        {
            var index = _lineRewrite[renderedLine - 1];
            if (index >= 0)
            {
                rewrite = _rewrites[index];
                return true;
            }
        }

        rewrite = default;
        return false;
    }

    /// <summary>
    /// 把「渲染文本中的行/列」映射回「用户源文件中的行/列」。
    /// </summary>
    /// <param name="renderedLine">渲染行号（1-based）。</param>
    /// <param name="renderedColumn">渲染列号（1-based UTF-16 code unit）。</param>
    /// <param name="sourceLine">映射后的源行号；注入行返回 0。</param>
    /// <param name="sourceColumn">映射后的源列号。</param>
    /// <param name="approximate">true 表示列落在被重写的路径区内，只能给出近似列。</param>
    public bool TryMapRenderedToSource(
        int renderedLine,
        int renderedColumn,
        out int sourceLine,
        out int sourceColumn,
        out bool approximate)
    {
        approximate = false;

        if (renderedLine < 1 || renderedLine > LineCount)
        {
            sourceLine = 0;
            sourceColumn = 0;
            return false;
        }

        sourceLine = _lineSource[renderedLine - 1];
        if (sourceLine == 0)
        {
            sourceColumn = 0;
            return false;
        }

        var index = _lineRewrite[renderedLine - 1];
        if (index < 0)
        {
            sourceColumn = renderedColumn;
            return true;
        }

        var rewrite = _rewrites[index];
        sourceColumn = rewrite.MapRenderedColumnToSource(renderedColumn);
        approximate = renderedColumn >= rewrite.RenderedPathStartColumn;
        return true;
    }

    /// <summary>把源文件行号映射回渲染行号（跳转/高亮反向定位用）；不唯一时返回第一个。</summary>
    public bool TryMapSourceLineToRendered(int sourceLine, out int renderedLine)
    {
        foreach (var segment in _segments)
        {
            if (segment.Kind != SegmentKind.Anchored || segment.SourceLineCount == 0)
            {
                continue;
            }

            var offset = sourceLine - segment.SourceStartLine;
            if (offset >= 0 && offset < segment.SourceLineCount)
            {
                renderedLine = segment.RenderedStartLine + offset;
                return true;
            }
        }

        renderedLine = 0;
        return false;
    }

    /// <summary>取某个源行对应的全部渲染行（同一源行可能因共享块注入而出现多次）。</summary>
    public void CollectRenderedLinesOfSource(int sourceLine, List<int> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (var segment in _segments)
        {
            if (segment.Kind != SegmentKind.Anchored || segment.SourceLineCount == 0)
            {
                continue;
            }

            var offset = sourceLine - segment.SourceStartLine;
            if (offset >= 0 && offset < segment.SourceLineCount)
            {
                into.Add(segment.RenderedStartLine + offset);
            }
        }
    }
}
