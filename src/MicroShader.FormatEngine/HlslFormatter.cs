using System.Text;

namespace MicroShader.FormatEngine;

/// <summary>
/// HLSL/ShaderLab 文本格式化器。
/// </summary>
/// <remarks>
/// 两遍管线，刻意分开。第一步做括号重构：按 BraceStyle 把块开括号在
/// 头部行末与独占一行之间搬移。第二步重排缩进：重新扫描后按花括号深度重算每行缩进。
/// 为什么分两遍：括号搬移会改变某一行属于哪个深度，而缩进又依赖深度。
/// 若在一遍里同时做，就得在拆括号时预判目标行的深度，逻辑会互相缠绕；
/// 分两遍后，拆出来的左花括号行与合并后的行在重排缩进时自然落到正确层级，
/// 深度在行间搬移，重算即正确。
/// </remarks>
public static class HlslFormatter
{
    /// <summary>格式化全文，返回新文本（不改动入参）。</summary>
    public static string Format(string text, FormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);

        var first = FormatScanner.Scan(text);
        var restructured = RestructureBraces(text, first, options);
        var second = FormatScanner.Scan(restructured);
        return Reindent(restructured, second, options);
    }

    /// <summary>格式化的差异编辑；无需改动时返回空列表。</summary>
    public static IReadOnlyList<TextEdit> FormatEdits(string text, FormatOptions options)
    {
        var formatted = Format(text, options);
        return DiffEdits(text, formatted);
    }

    // ---------------------------------------------------------------- 括号重构

    private static string RestructureBraces(string text, FormatScan scan, FormatOptions options)
    {
        if (options.BraceStyle == BraceStyle.Auto)
        {
            return text;
        }

        var lines = SplitLines(text);
        var output = new List<string>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            RestructureLine(lines[i], i, scan, options, output);
        }

        var builder = new StringBuilder(text.Length + 64);
        for (var i = 0; i < output.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(scan.Newline);
            }

            builder.Append(output[i]);
        }

        if (scan.HasFinalNewline)
        {
            builder.Append(scan.Newline);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 处理一行；必要时把结果拆成多行写进 <paramref name="output"/>。
    /// </summary>
    /// <param name="scanLine">该行在 <paramref name="scan"/> 中的索引；合成行传 -1。</param>
    private static void RestructureLine(
        string text,
        int scanLine,
        FormatScan scan,
        FormatOptions options,
        List<string> output)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0)
        {
            output.Add(text);
            return;
        }

        var indent = text.Substring(0, text.Length - trimmed.Length);
        var info = scanLine >= 0 && scanLine < scan.Lines.Count ? scan.Lines[scanLine] : default;
        var inComment = scanLine >= 0 && info.StartsInBlockComment;

        if (options.BraceStyle == BraceStyle.Kr)
        {
            // 规则一：整行只有一个块开括号 → 并入上一行末尾（void f() 加 { 变成 void f() {）。
            // 上一行带行尾注释时不并：行末的 // 会把并入的开括号吞掉，块就再也不会开启。
            if (!inComment
                && trimmed.Length == 1
                && trimmed[0] == '{'
                && output.Count > 0
                && output[^1].Trim().Length > 0
                && !HasTrailingComment(output[^1])
                && IsBlockBraceAt(text, info.LeadingWhitespace, scan, info))
            {
                output[^1] = output[^1].TrimEnd() + " {";
                return;
            }

            // 规则二：上一行以 `}` 结尾、本行以 else/catch 开头 → 并入上一行。
            if (!inComment
                && output.Count > 0
                && StartsWithElseOrCatch(trimmed)
                && EndsWithCloseBrace(output[^1]))
            {
                output[^1] = output[^1].TrimEnd() + " " + trimmed;
                return;
            }

            output.Add(text);
            return;
        }

        // Allman。
        // 规则一：行首是 `}` 且后面还跟着 else/catch → 先拆出 `}`，其余部分递归处理
        //（于是 `else {` 会再被规则二拆成 `else` 与 `{`）。
        if (!inComment && trimmed[0] == '}' && trimmed.Length > 1)
        {
            var rest = trimmed.Substring(1).TrimStart();
            if (rest.Length > 0 && StartsWithElseOrCatch(rest))
            {
                output.Add(indent + "}");
                // 递归时把缩进一起带上：`} else {` 拆成 `}` 与 `else {` 后，后半段自己再走一次
                // 规则二拆成 `else` 与 `{`，两层都要落在原来的缩进上。
                RestructureLine(indent + rest, -1, scan, options, output);
                return;
            }
        }

        // 规则二：行末的块开括号不在行首 → 拆成「头部」与「{」两行。
        if (!inComment)
        {
            var column = FindLastCodeColumn(text, scan, info, scanLine);
            if (column > 0 && text[column] == '{' && IsBlockBraceAt(text, column, scan, info))
            {
                // 只有「行末有个块括号、且它前面确实有内容」才拆。
                // 整行就是 {（Allman 形态）时 prefix 全是空白 —— 拆了会凭空多出一行纯空白，
                // 而且每跑一遍就多一行（不幂等）。那种行本来就已经是 Allman，原样输出。
                // 开括号后面的部分（行尾注释）必须跟着括号走，否则拆行等于删注释。
                var prefix = text.Substring(0, column).TrimEnd();
                if (prefix.Trim().Length > 0)
                {
                    output.Add(prefix);
                    output.Add(indent + text.Substring(column).TrimEnd());
                    return;
                }
            }
        }

        output.Add(text);
    }

    /// <summary>
    /// 该偏移上的左花括号是否为块开括号。
    /// </summary>
    private static bool IsBlockBraceAt(string text, int column, FormatScan scan, FormatLineInfo info)
    {
        var absolute = info.Start + column;
        if (absolute >= 0 && absolute < scan.BlockBrace.Count && info.Start <= absolute && absolute < info.End)
        {
            return scan.BlockBrace[absolute];
        }

        // 合成行没有扫描信息：退回「看前一个代码字符」这条启发式。
        for (var i = column - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            return !FormatScanner.IsInitializerBrace(text[i]);
        }

        return true;
    }

    private static int FindLastCodeColumn(string text, FormatScan scan, FormatLineInfo info, int scanLine)
    {
        if (scanLine >= 0 && info.LastCode >= 0)
        {
            // 扫描器把空白也算作代码字符，所以末个代码字符常是行尾空格；
            // 往回跳过空白才拿到真正的末个可见字符（否则 "f() { " 这种行判不出括号在行末）。
            var column = info.LastCode - info.Start;
            while (column >= 0 && column < text.Length && char.IsWhiteSpace(text[column]))
            {
                column--;
            }

            if (column >= 0 && column < text.Length)
            {
                return column;
            }
        }

        return FindLastCodeChar(text);
    }

    /// <summary>
    /// 合成行的兜底：找最后一个非空白、且不在注释里的字符。
    /// </summary>
    private static int FindLastCodeChar(string text)
    {
        var last = -1;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                break;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, text.Length);
                continue;
            }

            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == quote)
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                last = i - 1;
                continue;
            }

            if (!char.IsWhiteSpace(c))
            {
                last = i;
            }

            i++;
        }

        return last;
    }

    private static bool StartsWithElseOrCatch(string text)
    {
        if (text.StartsWith("else", StringComparison.Ordinal)
            && (text.Length == 4 || !IsIdentifierChar(text[4])))
        {
            return true;
        }

        return text.StartsWith("catch", StringComparison.Ordinal)
            && (text.Length == 5 || !IsIdentifierChar(text[5]));
    }

    /// <summary>
    /// 该行是否以行尾注释收场，或停在一个没闭合的块注释里。
    /// </summary>
    /// <remarks>
    /// 只为一件事存在：K&amp;R 把开括号并到上一行末尾时，若上一行末是注释，
    /// 并进去的开括号会被注释吞掉（块不再开启、后续行缩进全塌）。这种行宁可不并。
    /// </remarks>
    private static bool HasTrailingComment(string text)
    {
        var inBlock = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (inBlock)
            {
                if (c == '*' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    inBlock = false;
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == quote)
                    {
                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                return true;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                inBlock = true;
                i += 2;
                continue;
            }

            i++;
        }

        return inBlock;
    }

    private static bool EndsWithCloseBrace(string text)
    {
        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                continue;
            }

            return text[i] == '}';
        }

        return false;
    }

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_';

    // ---------------------------------------------------------------- 重排缩进

    private static string Reindent(string text, FormatScan scan, FormatOptions options)
    {
        var lines = SplitLines(text);
        var result = new List<string>(lines.Count);
        var unit = options.InsertSpaces ? new string(' ', Math.Max(1, options.TabSize)) : "\t";

        // 缩进层级缓存：level → 缩进串。
        var indents = new List<string> { string.Empty };

        string Indent(int level)
        {
            if (level < 0)
            {
                level = 0;
            }

            while (indents.Count <= level)
            {
                indents.Add(indents[^1] + unit);
            }

            return indents[level];
        }

        var depth = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var info = i < scan.Lines.Count ? scan.Lines[i] : default;
            var raw = lines[i];
            var lineLength = info.End - info.Start;
            var contentStart = info.Start + info.LeadingWhitespace;

            // 空白行：清空。
            if (lineLength == 0)
            {
                result.Add(string.Empty);
                continue;
            }

            var content = raw.Substring(info.LeadingWhitespace, lineLength - info.LeadingWhitespace);
            if (options.TrimTrailingWhitespace)
            {
                content = content.TrimEnd();
            }

            // 整行没有代码字符：可能是纯空白，也可能是纯注释行 —— 两者都保留原缩进。
            if (info.FirstCode < 0)
            {
                result.Add(content.Length == 0 ? string.Empty : raw.Substring(0, info.LeadingWhitespace) + content);
                depth += info.BraceDelta;
                continue;
            }

            // 预处理器行、行首处于块注释内的行、续行：保留原缩进，只去行尾空白。
            if (info.IsPreprocessor || info.StartsInBlockComment || IsContinuation(scan, i))
            {
                result.Add(raw.Substring(0, info.LeadingWhitespace) + content);
                depth += info.BraceDelta;
                continue;
            }

            var level = depth;
            var first = scan.Masked[info.FirstCode];
            if (first == '}')
            {
                level--;
            }
            else if (IsCaseOrDefault(scan.Masked, info.FirstCode))
            {
                level--;
            }

            result.Add(Indent(level) + content);
            depth += info.BraceDelta;
        }

        var builder = new StringBuilder(text.Length + 64);
        for (var i = 0; i < result.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(scan.Newline);
            }

            builder.Append(result[i]);
        }

        if (scan.HasFinalNewline)
        {
            builder.Append(scan.Newline);
        }

        return builder.ToString();
    }

    /// <summary>
    /// 续行：上一行末圆括号未闭合，或上一行末的代码字符表明语句还没结束。
    /// </summary>
    /// <remarks>
    /// 刻意不含左花括号、冒号、点号与问号：
    /// 以左花括号结尾的下一行是块体，该走深度；以 case 1: 结尾的下一行
    /// 不该被当成续行，否则 switch 分支体会被多缩一级。
    /// </remarks>
    private static bool IsContinuation(FormatScan scan, int index)
    {
        if (index <= 0)
        {
            return false;
        }

        var previous = scan.Lines[index - 1];
        if (scan.ParenDepthAtEnd[index - 1] > 0)
        {
            return true;
        }

        if (previous.LastCode < 0)
        {
            return false;
        }

        return IsContinuationChar(scan.Masked[previous.LastCode]);
    }

    private static bool IsContinuationChar(char c)
        => c is ',' or '(' or '[' or '+' or '-' or '*' or '/' or '%' or '=' or '<' or '>' or '&' or '|' or '^';

    private static bool IsCaseOrDefault(IReadOnlyList<char> masked, int offset)
    {
        var c = masked[offset];
        if (c != 'c' && c != 'd')
        {
            return false;
        }

        if (c == 'c')
        {
            return Matches(masked, offset, "case") && !IsIdentifierChar(At(masked, offset + 4));
        }

        return Matches(masked, offset, "default") && !IsIdentifierChar(At(masked, offset + 7));
    }

    private static bool Matches(IReadOnlyList<char> masked, int offset, string word)
    {
        if (offset + word.Length > masked.Count)
        {
            return false;
        }

        for (var i = 0; i < word.Length; i++)
        {
            if (masked[offset + i] != word[i])
            {
                return false;
            }
        }

        return true;
    }

    private static char At(IReadOnlyList<char> masked, int index)
        => index >= 0 && index < masked.Count ? masked[index] : '\0';

    // ---------------------------------------------------------------- 差异编辑

    private static IReadOnlyList<TextEdit> DiffEdits(string original, string formatted)
    {
        var edits = new List<TextEdit>();

        if (string.Equals(original, formatted, StringComparison.Ordinal))
        {
            return edits;
        }

        var originalLines = SplitLines(original);
        var formattedLines = SplitLines(formatted);

        if (originalLines.Count == formattedLines.Count)
        {
            for (var i = 0; i < originalLines.Count; i++)
            {
                if (string.Equals(originalLines[i], formattedLines[i], StringComparison.Ordinal))
                {
                    continue;
                }

                edits.Add(new TextEdit(i, 0, i, originalLines[i].Length, formattedLines[i]));
            }

            return edits;
        }

        // 行数变了（括号被拆行/并行）：退化为整文档一条编辑。
        var endLine = originalLines.Count;
        var endCharacter = 0;
        if (endLine > 0 && originalLines.Count == 0)
        {
            endLine = 0;
        }

        if (originalLines.Count > 0 && !EndsWithNewline(original))
        {
            endLine = originalLines.Count - 1;
            endCharacter = originalLines[^1].Length;
        }

        edits.Add(new TextEdit(0, 0, endLine, endCharacter, formatted));
        return edits;
    }

    private static bool EndsWithNewline(string text)
        => text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r');

    /// <summary>
    /// 按换行符切行并剥掉行尾的回车符；以换行结尾时不多出一个空行。
    /// </summary>
    private static List<string> SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return new List<string>();
        }

        var parts = text.Split('\n');
        var count = parts.Length;
        if (text[^1] == '\n')
        {
            count--;
        }

        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var part = parts[i];
            if (part.Length > 0 && part[^1] == '\r')
            {
                part = part.Substring(0, part.Length - 1);
            }

            result.Add(part);
        }

        return result;
    }
}
