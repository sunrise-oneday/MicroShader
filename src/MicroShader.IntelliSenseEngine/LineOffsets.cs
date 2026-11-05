namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// 行首偏移表：在「绝对字符偏移 ↔ LSP 行列」之间双向换算。
/// </summary>
/// <remarks>
/// 
/// "为什么需要一张表"：符号索引内部用绝对偏移存位置（行号在打字期随时漂移，
/// 偏移不会），而 LSP 出口要 "(line, character)"。若每次换算都从文件头数换行，
/// 大纲与 F12 会退化成 O(文件大小)；建一张行首表后是 O(1) 查表 + O(log L) 二分。
/// 
/// "单位口径"：全部是 UTF-16 code unit（.NET "char" 计数），与 LSP 的
/// "positionEncoding: utf-16" 一致（详设 v2.0 第 11 条）。DXC 报的是 UTF-8 "字节"列，
/// 那是模块 3 的坐标换算职责，与本表无关 —— 别把两种口径混起来。
/// 
/// "换行定义"：行首 = 每个 "\n" 之后的第一个字符。CRLF 里的 "\r" 算作
/// 前一行的最后一个 code unit（与客户端一致）。
/// </remarks>
public static class LineOffsets
{
    /// <summary>构建行首偏移表（至少含一个元素：0）。</summary>
    public static int[] Build(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var count = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == (char)10)
            {
                count++;
            }
        }

        var starts = new int[count];
        var line = 0;
        starts[0] = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == (char)10)
            {
                line++;
                starts[line] = i + 1;
            }
        }

        return starts;
    }

    /// <summary>偏移 → 行号（0-based）。</summary>
    public static int LineOf(int[] starts, int offset)
    {
        var low = 0;
        var high = starts.Length - 1;

        while (low < high)
        {
            var mid = (low + high + 1) >> 1;
            if (starts[mid] <= offset)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    /// <summary>偏移 → 列号（0-based，UTF-16 code unit）。</summary>
    public static int CharacterOf(int[] starts, int offset)
        => offset - starts[LineOf(starts, offset)];

    /// <summary>行列 → 偏移；行列越界时返回 false（调用方据此降级，绝不静默夹取到别的位置）。</summary>
    public static bool TryOffsetOf(int[] starts, int line, int character, int textLength, out int offset)
    {
        if (line < 0 || line >= starts.Length || character < 0)
        {
            offset = 0;
            return false;
        }

        var candidate = starts[line] + character;
        if (candidate > textLength)
        {
            offset = 0;
            return false;
        }

        offset = candidate;
        return true;
    }

    /// <summary>某行的结束偏移（"不含"行尾的 CR/LF）。</summary>
    public static int EndOfLine(string text, int[] starts, int line)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (line < 0 || line >= starts.Length)
        {
            return 0;
        }

        var start = starts[line];
        var end = line + 1 < starts.Length ? starts[line + 1] : text.Length;

        while (end > start && (text[end - 1] == (char)10 || text[end - 1] == (char)13))
        {
            end--;
        }

        return end;
    }

    /// <summary>某行的 (起始偏移, 结束偏移) 闭区间半开形态。</summary>
    public static (int Start, int End) LineSpan(string text, int[] starts, int line)
        => (line >= 0 && line < starts.Length ? starts[line] : 0, EndOfLine(text, starts, line));
}
