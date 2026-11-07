namespace MicroShader.DiagnosticEngine;

/// <summary>
/// DXC 列号 → UTF-16 列号的单向换算（v2.0 清单第 5 条）。
/// </summary>
/// <remarks>
/// 探针（ADR-013，probes/dxc-aot/RESULT.md）定论："DXC 报告的 column 是 1-based UTF-8 字节偏移"
/// —— emoji 探针的字符 "U+1F600"（UTF-8 4 字节 / UTF-16 2 code unit）得到列 16，只有按 UTF-8 字节解释才自洽。
/// 本工具内部与对外（LSP）一律使用 "UTF-16 code unit" 列号，因此只在「读 DXC 结果」这一处做一次换算，
/// 且是单向的：绝不做 UTF-16 → 字节的反向换算去构造位置（那会引入第二种口径）。
/// 降级纪律：字节偏移若落在某个多字节字符中间（理论上不该发生，除非 DXC 口径变了），
/// 不得抛异常、不得静默取整 —— 返回该字符起点并标记 "exact: false"，由上层决定是否把列降级成 0
/// （「只给行、列未知」）。
/// </remarks>
public static class Utf8Column
{
    /// <summary>把 1-based UTF-8 字节列换算成 1-based UTF-16 code unit 列。</summary>
    /// <param name="utf8Column">DXC 报告的列（1-based UTF-8 字节偏移）。</param>
    /// <param name="line">该渲染行的文本（不含换行符）。</param>
    /// <param name="exact">false 表示字节偏移没有落在字符边界上，返回值为近似列。</param>
    public static int ToUtf16Column(ReadOnlySpan<char> line, int utf8Column, out bool exact)
    {
        exact = true;

        if (utf8Column <= 1)
        {
            return 1;
        }

        var target = utf8Column - 1;
        var bytes = 0;
        var index = 0;

        while (index < line.Length && bytes < target)
        {
            var width = CharWidth(line, index);
            bytes += width.Bytes;
            index += width.Chars;
        }

        if (bytes == target)
        {
            return index + 1;
        }

        // bytes > target：落在一个多字节字符内部，无法精确表达。
        exact = false;
        return index + 1;
    }

    /// <summary>把 1-based UTF-16 code unit 列换算成 1-based UTF-8 字节列（仅用于测试与不变量自检）。</summary>
    public static int ToUtf8Column(ReadOnlySpan<char> line, int utf16Column)
    {
        if (utf16Column <= 1)
        {
            return 1;
        }

        var limit = Math.Min(utf16Column - 1, line.Length);
        var bytes = 0;
        var index = 0;
        while (index < limit)
        {
            var width = CharWidth(line, index);
            bytes += width.Bytes;
            index += width.Chars;
        }

        return bytes + 1;
    }

    /// <summary>该行文本的 UTF-8 字节总长（不含换行符）。</summary>
    public static int Utf8Length(ReadOnlySpan<char> line)
    {
        var bytes = 0;
        var index = 0;
        while (index < line.Length)
        {
            var width = CharWidth(line, index);
            bytes += width.Bytes;
            index += width.Chars;
        }

        return bytes;
    }

    private static (int Bytes, int Chars) CharWidth(ReadOnlySpan<char> line, int index)
    {
        var c = line[index];

        if (char.IsHighSurrogate(c) && index + 1 < line.Length && char.IsLowSurrogate(line[index + 1]))
        {
            return (4, 2);
        }

        if (c < 0x80)
        {
            return (1, 1);
        }

        if (c < 0x800)
        {
            return (2, 1);
        }

        // 落单的代理项（非法文本）按 UTF-8 的替换字符宽度 3 处理，保证换算永不抛异常。
        return (3, 1);
    }
}
