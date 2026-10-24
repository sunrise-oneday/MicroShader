namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 源文本的行索引（行号 ↔ 字符偏移）。切片结果给出的是偏移，装配器要的是行号，二者靠本类型换算。
/// </summary>
internal sealed class SourceLineIndex
{
    private readonly int[] _starts;

    private SourceLineIndex(int[] starts)
    {
        _starts = starts;
    }

    /// <summary>行数（最后一行即使没有结尾换行也算一行）。</summary>
    public int LineCount => _starts.Length;

    public static SourceLineIndex Build(ReadOnlySpan<char> text)
    {
        // 两遍扫描：先数 '\n' 个数，再直填预分配数组——消除 List<int> 对象 + ToArray 拷贝。
        var newlineCount = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                newlineCount++;
            }
        }

        // 文本以 '\n' 结尾时最后一个起始位置落在文本之外（空尾行），剔除。
        if (text.Length > 0 && text[text.Length - 1] == '\n')
        {
            newlineCount--;
        }

        var lineCount = newlineCount + 1; // 首行不需要换行符
        var starts = new int[lineCount];
        starts[0] = 0;

        var idx = 1;
        for (var i = 0; i < text.Length && idx < lineCount; i++)
        {
            if (text[i] == '\n')
            {
                starts[idx++] = i + 1;
            }
        }

        return new SourceLineIndex(starts);
    }

    /// <summary>行首字符偏移（1-based 行号）。</summary>
    public int LineStart(int line) => _starts[Math.Clamp(line, 1, _starts.Length) - 1];

    /// <summary>行内容的结束偏移（不含换行符与回车符），不超过 <paramref name="limit"/>。</summary>
    public int LineEnd(ReadOnlySpan<char> text, int line, int limit)
    {
        var start = LineStart(line);
        var bound = Math.Min(limit, text.Length);
        var end = start;
        while (end < bound && text[end] != '\n' && text[end] != '\r')
        {
            end++;
        }

        return end;
    }

    /// <summary>包含该偏移的行号（1-based）。</summary>
    public int LineOfOffset(int offset)
    {
        var low = 0;
        var high = _starts.Length - 1;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_starts[mid] <= offset)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low + 1;
    }
}
