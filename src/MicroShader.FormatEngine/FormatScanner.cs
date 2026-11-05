namespace MicroShader.FormatEngine;

/// <summary>
/// 单行扫描结果。
/// </summary>
/// <param name="Start">行首在原文中的偏移。</param>
/// <param name="End">行尾（不含换行符）的偏移。</param>
/// <param name="LeadingWhitespace">行首空白字符数。</param>
/// <param name="FirstCode">首个代码字符的偏移；整行没有代码时为 -1。</param>
/// <param name="LastCode">末个代码字符的偏移；整行没有代码时为 -1。</param>
/// <param name="BraceDelta">本行代码花括号的净增量（预处理器行恒为 0）。</param>
/// <param name="ParenDelta">本行代码圆括号的净增量（预处理器行恒为 0）。</param>
/// <param name="IsPreprocessor">本行是否属于预处理器指令（含反斜杠续行）。</param>
/// <param name="StartsInBlockComment">本行行首是否处在块注释内部。</param>
internal readonly record struct FormatLineInfo(
    int Start,
    int End,
    int LeadingWhitespace,
    int FirstCode,
    int LastCode,
    int BraceDelta,
    int ParenDelta,
    bool IsPreprocessor,
    bool StartsInBlockComment);

/// <summary>
/// 一次扫描的全部产物。
/// </summary>
/// <remarks>
/// 掩码文本是把注释与字符串内部替换成等长空格后的副本：之后所有结构分析
/// （花括号深度、行首代码字符、续行判定、case 识别）都只看这份文本，
/// 因此不会被注释里的括号或字符串里的括号带偏。长度与原文一致，偏移量可以直接互用。
/// </remarks>
internal sealed class FormatScan
{
    /// <summary>与原文等长的掩码文本（注释/字符串内部为空格，定界符保留）。</summary>
    public required IReadOnlyList<char> Masked { get; init; }

    /// <summary>逐行信息。</summary>
    public required IReadOnlyList<FormatLineInfo> Lines { get; init; }

    /// <summary>每个偏移上的左花括号是否为块开括号（与 Masked 等长）。</summary>
    public required IReadOnlyList<bool> BlockBrace { get; init; }

    /// <summary>每行行尾处的圆括号净深度（索引与 <see cref="Lines"/> 对齐）。</summary>
    public required IReadOnlyList<int> ParenDepthAtEnd { get; init; }

    /// <summary>文件主用的换行序列。</summary>
    public required string Newline { get; init; }

    /// <summary>原文是否以换行结尾。</summary>
    public required bool HasFinalNewline { get; init; }
}

/// <summary>
/// 单遍扫描器：把源码切成行，同时产出掩码文本与逐行结构信息。
/// </summary>
/// <remarks>
/// 刻意单遍：格式化只是重排空白，不需要完整词法，只需要知道
/// 哪个字符是代码字符、哪个不是。扫描器把注释与字符串内部替换成空格，
/// 于是后续所有结构判断都不必再处理转义与嵌套。
/// </remarks>
internal static class FormatScanner
{
    /// <summary>扫描全文。</summary>
    public static FormatScan Scan(string text)
    {
        var masked = text.ToCharArray();
        var blockBrace = new bool[text.Length];
        var lines = new List<FormatLineInfo>(text.Length / 24 + 1);
        var parenDepthAtEnd = new List<int>(text.Length / 24 + 1);

        var parenDepth = 0;
        var inBlockComment = false;
        var inPreprocessor = false;
        var previousCodeChar = '\0';

        var position = 0;
        while (position < text.Length)
        {
            var lineStart = position;
            var lineEnd = position;
            while (lineEnd < text.Length && text[lineEnd] != '\n' && text[lineEnd] != '\r')
            {
                lineEnd++;
            }

            var startsInBlockComment = inBlockComment;

            var index = lineStart;
            while (index < lineEnd && IsIndentWhitespace(text[index]))
            {
                index++;
            }

            var leading = index - lineStart;
            var isPreprocessor = inPreprocessor || (index < lineEnd && text[index] == '#');
            inPreprocessor = false;

            var firstCode = -1;
            var lastCode = -1;
            var braceDelta = 0;
            var parenDelta = 0;

            while (index < lineEnd)
            {
                var c = text[index];

                if (inBlockComment)
                {
                    masked[index] = ' ';
                    if (c == '*' && index + 1 < lineEnd && text[index + 1] == '/')
                    {
                        masked[index + 1] = ' ';
                        index += 2;
                        inBlockComment = false;
                        continue;
                    }

                    index++;
                    continue;
                }

                if (c == '/' && index + 1 < lineEnd && text[index + 1] == '/')
                {
                    // 行注释：本行剩余部分全部掩掉（它们是注释，不是代码）。
                    for (var k = index; k < lineEnd; k++)
                    {
                        masked[k] = ' ';
                    }

                    break;
                }

                if (c == '/' && index + 1 < lineEnd && text[index + 1] == '*')
                {
                    masked[index] = ' ';
                    masked[index + 1] = ' ';
                    index += 2;
                    inBlockComment = true;
                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    // 字符串/字符字面量：内部掩掉，定界符保留（定界符是代码字符）。
                    var quote = c;
                    masked[index] = quote;
                    if (firstCode < 0)
                    {
                        firstCode = index;
                    }

                    index++;
                    while (index < lineEnd)
                    {
                        if (text[index] == '\\' && index + 1 < lineEnd)
                        {
                            masked[index] = ' ';
                            masked[index + 1] = ' ';
                            index += 2;
                            continue;
                        }

                        if (text[index] == quote)
                        {
                            masked[index] = quote;
                            index++;
                            break;
                        }

                        masked[index] = ' ';
                        index++;
                    }

                    lastCode = index - 1;
                    previousCodeChar = quote;
                    continue;
                }

                var before = previousCodeChar;
                masked[index] = c;
                if (firstCode < 0)
                {
                    firstCode = index;
                }

                lastCode = index;
                previousCodeChar = c;

                if (!isPreprocessor)
                {
                    switch (c)
                    {
                        case '{':
                            braceDelta++;
                            if (!IsInitializerBrace(before))
                            {
                                blockBrace[index] = true;
                            }

                            break;
                        case '}':
                            braceDelta--;
                            break;
                        case '(':
                            parenDelta++;
                            break;
                        case ')':
                            parenDelta--;
                            break;
                    }
                }

                index++;
            }

            // 预处理器反斜杠续行：续行同样按预处理器处理（不重排缩进、不数括号）。
            if (isPreprocessor)
            {
                var k = lineEnd - 1;
                while (k >= lineStart && IsIndentWhitespace(text[k]))
                {
                    k--;
                }

                if (k >= lineStart && text[k] == '\\')
                {
                    inPreprocessor = true;
                }
            }

            lines.Add(new FormatLineInfo(
                lineStart,
                lineEnd,
                leading,
                firstCode,
                lastCode,
                braceDelta,
                parenDelta,
                isPreprocessor,
                startsInBlockComment));

            parenDepth += parenDelta;
            parenDepthAtEnd.Add(parenDepth);

            if (lineEnd >= text.Length)
            {
                break;
            }

            if (text[lineEnd] == '\r' && lineEnd + 1 < text.Length && text[lineEnd + 1] == '\n')
            {
                position = lineEnd + 2;
            }
            else
            {
                position = lineEnd + 1;
            }
        }

        return new FormatScan
        {
            Masked = masked,
            Lines = lines,
            BlockBrace = blockBrace,
            ParenDepthAtEnd = parenDepthAtEnd,
            Newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n",
            HasFinalNewline = text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r'),
        };
    }

    /// <summary>
    /// 初始化器开括号（float a[] = {、= {、, {、( {、[ {）不是块括号，
    /// 不参与风格转换。
    /// </summary>
    internal static bool IsInitializerBrace(char before)
        => before is '=' or ',' or '(' or '[' or '{';

    private static bool IsIndentWhitespace(char c) => c is ' ' or '\t' or '\v' or '\f';
}
