using System.Buffers;
using MicroShader.IntelliSenseEngine;

namespace MicroShader.NavigationEngine;

/// <summary>光标处的标识符（名字 + 起始偏移）。</summary>
internal static class SymbolAtCursor
{
    /// <summary>
    /// 标识符正文允许的字符：ASCII 字母、数字与下划线，与 IsIdentifierChar 同一口径。
    /// 光标右侧的标识符扫描是正向的，用 SearchValues 向量化一次定位边界。
    /// </summary>
    internal static readonly SearchValues<char> IdentifierChars =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_");

    /// <summary>
    /// 取出 "(line, character)" 处的完整标识符。
    /// </summary>
    /// <remarks>
    /// 
    /// 展开顺序"先右后左"：这样「光标停在标识符最后一个字符之后」也命中同一个词
    /// （用户从行首敲到词尾再按 F12 是常见操作），而先左后右会把这种情况判成空前缀。
    /// 
    /// 只在"本行内"展开，绝不跨行 —— 跨行展开会把换行符当成标识符边界之外的东西，
    /// 得到的词没有意义。
    /// </remarks>
    public static bool TryFind(string text, int[] lineStarts, int line, int character, out string name, out int offset)
    {
        name = string.Empty;
        offset = 0;

        if (line < 0 || line >= lineStarts.Length)
        {
            return false;
        }

        var lineStart = lineStarts[line];
        var lineEnd = LineOffsets.EndOfLine(text, lineStarts, line);

        var cursor = Math.Min(text.Length, Math.Max(lineStart, lineStart + character));

        var start = cursor;
        var end = cursor;
        if (end < lineEnd)
        {
            var rightStop = text.AsSpan(end, lineEnd - end).IndexOfAnyExcept(IdentifierChars);
            end = rightStop < 0 ? lineEnd : end + rightStop;
        }


        while (start > lineStart && IsIdentifierChar(text[start - 1]))
        {
            start--;
        }

        if (end <= start)
        {
            return false;
        }

        name = text[start..end];
        offset = start;
        return true;
    }

    public static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
