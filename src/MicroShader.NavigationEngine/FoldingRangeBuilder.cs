using System.Text.Json;

namespace MicroShader.NavigationEngine;

/// <summary>
/// "textDocument/foldingRange" 的扫描器：单遍扫描，按大括号结构产出折叠范围。
/// </summary>
/// <remarks>
/// 这份数据有两个消费者，取舍以两者兼顾为准：编辑器左侧的折叠箭头，以及粘滞滚动窗口的
/// foldingProviderModel（它把每个折叠范围的起始行原样当作粘滞条目）。后者的约束最强 ——
/// 粘滞对折叠范围不做任何过滤，范围里有什么就显示什么。
///
/// 因此刻意排除控制流（if / else / for / while / do / switch / case）的大括号：它们的起始行
/// 是语句而不是结构，进粘滞窗口会很啰嗦；这与官方 C# Dev Kit 的粘滞观感一致（C# 侧靠符号
/// 大纲实现同样的克制）。代价是这些块不可折叠 —— 这是合意的取舍，不是遗漏。
///
/// 收录：除控制流外的大括号块；预处理器条件（#if / #ifdef / #ifndef .. #endif）；
/// 程序块（HLSLPROGRAM / CGPROGRAM / HLSLINCLUDE / CGINCLUDE .. ENDHLSL / ENDCG）；
/// CBUFFER_START .. CBUFFER_END。单行区间一律跳过（折叠没有意义）。
///
/// 复杂度：时间 O(n + k)，额外空间 O(深度 + k)（n = 字符数，k = 范围数）——
/// 单遍扫描；大括号的分类只看向「行首起有限窗口」里的第一个词，前缀判空提前退出；
/// 不建行索引表、不为每个括号分配字符串。上一非空行在扫描中增量维护、O(1) 取用，
/// 语义与按行回找一致：一行「原始文本在第一个 // 之前含非空白」即算非空。
///
/// 扫描容错：控制流的大括号仍要入栈（只是不产出范围），否则它的右括号会错配到外层块；
/// 任何错配的闭合一律忽略（宁可少一个范围，不给错范围）；字符串与注释里的花括号不参与配对。
/// </remarks>
public static class FoldingRangeBuilder
{
    private enum RegionKind : byte
    {
        Brace = 1,
        Preprocessor = 2,
        Program = 3,
        CBuffer = 4,
    }

    private readonly record struct OpenRegion(RegionKind Kind, int StartLine, bool Emittable);

    private readonly record struct FoldingSpan(int StartLine, int EndLine);

    /// <summary>分类只看向行首起这么多个字符内的第一个词；超出即按非控制流处理（防御超长行）。</summary>
    private const int ClassifyWindow = 512;

    /// <summary>
    /// 扫描文本并把折叠范围写成 LSP 的 "FoldingRange[]"（0-based 行号）。
    /// 没有任何范围时不写任何字节并回 false（调用方据此回 null）。
    /// </summary>
    public static bool TryWrite(string text, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var spans = Collect(text);
        if (spans.Count == 0)
        {
            return false;
        }

        writer.WriteStartArray();

        foreach (var span in spans)
        {
            writer.WriteStartObject();
            writer.WriteNumber("startLine", span.StartLine);
            writer.WriteNumber("endLine", span.EndLine);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        return true;
    }

    private static List<FoldingSpan> Collect(string text)
    {
        var spans = new List<FoldingSpan>();
        var stack = new Stack<OpenRegion>();

        var line = 0;
        var lineStart = 0;
        var inBlockComment = false;

        // 当前行的「非空」判定状态：与按行回找同口径 —— 只看原始文本在第一个 // 之前是否含非空白。
        var lineHasContent = false;
        var lineCutByLineComment = false;

        // 最近一条非空行的 [start, end) 与行号，供 Allman 花括号回找头部行（O(1)）。
        var lastContentStart = -1;
        var lastContentEnd = -1;
        var lastContentLine = -1;

        var length = text.Length;
        var index = 0;

        while (index < length)
        {
            var c = text[index];

            if (c == '\n')
            {
                if (lineHasContent)
                {
                    lastContentStart = lineStart;
                    lastContentEnd = index;
                    lastContentLine = line;
                }

                line++;
                index++;
                lineStart = index;
                lineHasContent = false;
                lineCutByLineComment = false;
                continue;
            }

            if (index == lineStart && !inBlockComment)
            {
                CheckLineKeyword(text, lineStart, line, stack, spans);
            }

            if (inBlockComment)
            {
                if (c == '*' && index + 1 < length && text[index + 1] == '/')
                {
                    inBlockComment = false;

                    if (!lineCutByLineComment)
                    {
                        lineHasContent = true;
                    }

                    index += 2;
                    continue;
                }

                if (!lineCutByLineComment)
                {
                    if (c == '/' && index + 1 < length && text[index + 1] == '/')
                    {
                        lineCutByLineComment = true;
                    }
                    else if (!char.IsWhiteSpace(c))
                    {
                        lineHasContent = true;
                    }
                }

                index++;
                continue;
            }

            if (c == '/' && index + 1 < length)
            {
                if (text[index + 1] == '/')
                {
                    lineCutByLineComment = true;
                    var newline = text.IndexOf('\n', index + 2);
                    index = newline < 0 ? length : newline;
                    continue;
                }

                if (text[index + 1] == '*')
                {
                    inBlockComment = true;

                    if (!lineCutByLineComment)
                    {
                        lineHasContent = true;
                    }

                    index += 2;
                    continue;
                }
            }

            if (c == '"')
            {
                if (!lineCutByLineComment)
                {
                    lineHasContent = true;
                }

                index = SkipString(text, index);
                continue;
            }

            if (c == '{')
            {
                int headerLine;
                bool controlFlow;

                if (HasPrefixContent(text, lineStart, index))
                {
                    headerLine = line;
                    controlFlow = IsControlFlowHeader(text.AsSpan(lineStart, index - lineStart).Trim());
                }
                else if (lastContentLine >= 0)
                {
                    headerLine = lastContentLine;
                    controlFlow = IsControlFlowHeader(text.AsSpan(lastContentStart, lastContentEnd - lastContentStart).Trim());
                }
                else
                {
                    headerLine = line;
                    controlFlow = false;
                }

                if (!lineCutByLineComment)
                {
                    lineHasContent = true;
                }

                stack.Push(new OpenRegion(RegionKind.Brace, headerLine, !controlFlow));
                index++;
                continue;
            }

            if (c == '}')
            {
                if (!lineCutByLineComment)
                {
                    lineHasContent = true;
                }

                PopMatching(stack, RegionKind.Brace, line, spans);
                index++;
                continue;
            }

            if (!lineCutByLineComment && !char.IsWhiteSpace(c))
            {
                lineHasContent = true;
            }

            index++;
        }

        // 排序让输出确定（外层在前、同起点长的在前），测试可按顺序断言。
        spans.Sort(static (a, b) => a.StartLine != b.StartLine
            ? a.StartLine.CompareTo(b.StartLine)
            : b.EndLine.CompareTo(a.EndLine));

        return spans;
    }

    /// <summary>行首指令：预处理器、程序块、CBUFFER。只窥视行首一小段，不构造整行字符串。</summary>
    private static void CheckLineKeyword(string text, int lineStart, int line, Stack<OpenRegion> stack, List<FoldingSpan> spans)
    {
        var length = text.Length;
        var i = lineStart;

        while (i < length && text[i] != '\n' && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= length || text[i] == '\n')
        {
            return;
        }

        if (text[i] == '#')
        {
            i++;

            while (i < length && text[i] != '\n' && char.IsWhiteSpace(text[i]))
            {
                i++;
            }

            var wordEnd = i;

            while (wordEnd < length && (char.IsAsciiLetterOrDigit(text[wordEnd]) || text[wordEnd] == '_'))
            {
                wordEnd++;
            }

            var word = text.AsSpan(i, wordEnd - i);

            if (word.SequenceEqual("if") || word.SequenceEqual("ifdef") || word.SequenceEqual("ifndef"))
            {
                stack.Push(new OpenRegion(RegionKind.Preprocessor, line, true));
            }
            else if (word.SequenceEqual("endif"))
            {
                PopMatching(stack, RegionKind.Preprocessor, line, spans);
            }

            return;
        }

        if (text[i] == '/')
        {
            // 行注释或块注释开头的行都不是指令行。
            return;
        }

        if (StartsWithWordAt(text, i, "HLSLPROGRAM") || StartsWithWordAt(text, i, "CGPROGRAM")
            || StartsWithWordAt(text, i, "HLSLINCLUDE") || StartsWithWordAt(text, i, "CGINCLUDE"))
        {
            stack.Push(new OpenRegion(RegionKind.Program, line, true));
            return;
        }

        if (StartsWithWordAt(text, i, "ENDHLSL") || StartsWithWordAt(text, i, "ENDCG"))
        {
            PopMatching(stack, RegionKind.Program, line, spans);
            return;
        }

        if (StartsWithWordAt(text, i, "CBUFFER_START"))
        {
            stack.Push(new OpenRegion(RegionKind.CBuffer, line, true));
        }
        else if (StartsWithWordAt(text, i, "CBUFFER_END"))
        {
            PopMatching(stack, RegionKind.CBuffer, line, spans);
        }
    }

    private static void PopMatching(Stack<OpenRegion> stack, RegionKind kind, int endLine, List<FoldingSpan> spans)
    {
        if (stack.Count == 0 || stack.Peek().Kind != kind)
        {
            return;
        }

        var open = stack.Pop();
        if (open.Emittable && endLine > open.StartLine)
        {
            spans.Add(new FoldingSpan(open.StartLine, endLine));
        }
    }

    /// <summary>花括号之前的本行前缀是否「有内容」（与按行回找同口径：在第一个 // 之前含非空白）。</summary>
    private static bool HasPrefixContent(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                continue;
            }

            return !(c == '/' && i + 1 < end && text[i + 1] == '/');
        }

        return false;
    }

    /// <summary>控制流大括号不产出折叠范围：头部第一个词是 if / else / for / while / do / switch / case / default。</summary>
    private static bool IsControlFlowHeader(ReadOnlySpan<char> header)
    {
        if (header.Length > ClassifyWindow)
        {
            header = header[..ClassifyWindow];
        }

        while (header.Length > 0 && (header[0] == '}' || header[0] == ';'))
        {
            header = header[1..].TrimStart();
        }

        // 属性前缀（[unroll] / [branch] …）不参与分类。
        while (header.Length > 0 && header[0] == '[')
        {
            var close = header.IndexOf(']');

            if (close < 0)
            {
                break;
            }

            header = header[(close + 1)..].TrimStart();
        }

        var wordLength = 0;

        while (wordLength < header.Length
            && (char.IsAsciiLetterOrDigit(header[wordLength]) || header[wordLength] == '_'))
        {
            wordLength++;
        }

        if (wordLength == 0)
        {
            return false;
        }

        var word = header[..wordLength];

        return word.SequenceEqual("if") || word.SequenceEqual("else") || word.SequenceEqual("for")
            || word.SequenceEqual("while") || word.SequenceEqual("do") || word.SequenceEqual("switch")
            || word.SequenceEqual("case") || word.SequenceEqual("default");
    }

    private static int SkipString(string text, int index)
    {
        // index 指向开引号。未闭合的字符串不吞掉整个文件：遇到换行即停。
        var i = index + 1;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '\\' && i + 1 < text.Length)
            {
                i += 2;
                continue;
            }

            if (c == '"')
            {
                return i + 1;
            }

            if (c == '\n')
            {
                return i;
            }

            i++;
        }

        return i;
    }

    private static bool StartsWithWordAt(string text, int index, string word)
    {
        if (index + word.Length > text.Length)
        {
            return false;
        }

        for (var i = 0; i < word.Length; i++)
        {
            if (text[index + i] != word[i])
            {
                return false;
            }
        }

        var next = index + word.Length;
        if (next >= text.Length)
        {
            return true;
        }

        var c = text[next];
        return !char.IsAsciiLetterOrDigit(c) && c != '_';
    }

}
