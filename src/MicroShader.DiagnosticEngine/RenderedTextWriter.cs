namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 渲染文本与行映射表"同步"增长的单向写入器（ADR-023 的落地处）。
/// </summary>
/// <remarks>
/// 写入器是唯一同时触碰「文本」和「映射」的地方：<see cref="WriteLine"/> 一次同时追加行内容、
/// 记下行首偏移、记下该行的源行号与重写索引。因此不可能出现「文本多一行、映射没跟上」的偏差。
/// 段边界由 <see cref="BeginSegment"/> / <see cref="EndSegment"/> 显式框定；
/// 段只 append，禁止 splice。
/// </remarks>
internal sealed class RenderedTextWriter : IDisposable
{
    private RentedCharBuffer? _buffer;
    private readonly List<int> _lineStarts = new(64);
    private readonly List<int> _lineLengths = new(64);
    private readonly List<int> _lineSource = new(64);
    private readonly List<int> _lineRewrite = new(64);
    private readonly List<LineRewrite> _rewrites = new(4);
    private readonly List<Segment> _segments = new(4);

    private bool _segmentOpen;
    private SegmentKind _openKind;
    private int _openRenderedStart;
    private int _openSourceStart;
    private int _openSourceOffset;
    private int _openSharedBlock = -1;
    private int _openRewritten;

    public RenderedTextWriter(int initialCapacity = 16384)
    {
        _buffer = new RentedCharBuffer(initialCapacity);
    }

    /// <summary>底层缓冲；已移交或已释放时抛异常（防止交出所有权后继续写）。</summary>
    private RentedCharBuffer Buffer =>
        _buffer ?? throw new InvalidOperationException("渲染缓冲已移交或已释放，不能再写入。");

    /// <summary>已写入的行数。</summary>
    public int LineCount => _lineStarts.Count;

    /// <summary>下一条待写行的行号（1-based）。</summary>
    public int NextLineNumber => _lineStarts.Count + 1;

    /// <summary>缓冲当前容量（排障用）。</summary>
    public int BufferCapacity => Buffer.Capacity;

    /// <summary>登记一条重写记录，返回其索引。</summary>
    public int AddRewrite(in LineRewrite rewrite)
    {
        _rewrites.Add(rewrite);
        return _rewrites.Count - 1;
    }

    /// <summary>开启一个段。</summary>
    public void BeginSegment(SegmentKind kind, int sourceStartLine = 0, int sourceStartOffset = 0, int sharedBlockIndex = -1)
    {
        if (_segmentOpen)
        {
            throw new InvalidOperationException("上一个段尚未闭合（Segment 只能 append，禁止嵌套 splice）。");
        }

        _segmentOpen = true;
        _openKind = kind;
        _openRenderedStart = NextLineNumber;
        _openSourceStart = sourceStartLine;
        _openSourceOffset = sourceStartOffset;
        _openSharedBlock = sharedBlockIndex;
        _openRewritten = 0;
    }

    /// <summary>闭合当前段。<paramref name="sourceLength"/> 只对锚定段有意义。</summary>
    public void EndSegment(int sourceLength = 0)
    {
        if (!_segmentOpen)
        {
            throw new InvalidOperationException("没有开启中的段。");
        }

        var renderedCount = NextLineNumber - _openRenderedStart;
        if (renderedCount > 0)
        {
            _segments.Add(new Segment
            {
                Kind = _openKind,
                RenderedStartLine = _openRenderedStart,
                RenderedLineCount = renderedCount,
                SourceStartLine = _openKind == SegmentKind.Anchored ? _openSourceStart : 0,
                SourceLineCount = _openKind == SegmentKind.Anchored ? renderedCount : 0,
                SourceStartOffset = _openKind == SegmentKind.Anchored ? _openSourceOffset : 0,
                SourceLength = _openKind == SegmentKind.Anchored ? sourceLength : 0,
                SharedBlockIndex = _openSharedBlock,
                RewrittenLineCount = _openRewritten,
            });
        }

        _segmentOpen = false;
        _openSharedBlock = -1;
        _openRewritten = 0;
    }

    /// <summary>写入一行。</summary>
    /// <param name="text">行内容（不含换行符）。</param>
    /// <param name="sourceLine">对应源行号；注入行为 0。</param>
    /// <param name="rewriteIndex">重写记录索引；未重写为 -1。</param>
    public void WriteLine(ReadOnlySpan<char> text, int sourceLine = 0, int rewriteIndex = -1)
    {
        _lineStarts.Add(Buffer.Length);
        _lineLengths.Add(text.Length);
        _lineSource.Add(sourceLine);
        _lineRewrite.Add(rewriteIndex);
        Buffer.Append(text);
        Buffer.AppendNewLine();

        if (rewriteIndex >= 0)
        {
            _openRewritten++;
        }
    }

    /// <summary>写入一行字符串（少量非热路径调用点使用）。</summary>
    public void WriteLine(string text, int sourceLine = 0, int rewriteIndex = -1)
        => WriteLine(text.AsSpan(), sourceLine, rewriteIndex);

    /// <summary>产出不可变的组装结果，并把缓冲所有权移交出去。</summary>
    public AssembledShaderText Build(string physicalPath, string virtualPath, ShaderStageInfo stage)
    {
        if (_segmentOpen)
        {
            throw new InvalidOperationException("仍有未闭合的段，拒绝产出结果。");
        }

        var buffer = DetachBuffer();
        var map = new RenderedLineMap(
            _lineStarts.ToArray(),
            _lineLengths.ToArray(),
            _lineSource.ToArray(),
            _lineRewrite.ToArray(),
            _rewrites.ToArray(),
            _segments.ToArray());

        return new AssembledShaderText(buffer, map, physicalPath, virtualPath, stage);
    }

    /// <summary>取出（并放弃）内部缓冲，仅供组装失败时释放用。</summary>
    public RentedCharBuffer DetachBuffer()
    {
        if (_segmentOpen)
        {
            throw new InvalidOperationException("仍有未闭合的段，拒绝移交缓冲。");
        }

        var buffer = Buffer;
        _buffer = null;
        return buffer;
    }

    /// <summary>释放未移交的缓冲（幂等：已移交时是空操作）。</summary>
    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }
}
