namespace MicroShader.ShaderLab;

/// <summary>行首有效词元的种类。</summary>
internal enum LineTokenKind : byte
{
    None = 0,
    Identifier = 1,
    HashDirective = 2,
    String = 3,
    Symbol = 4,
}

/// <summary>
/// 单行扫描结果。"ref struct"：内部持有的全是源文本切片，热路径零分配。
/// </summary>
internal ref struct LineInfo
{
    public ReadOnlySpan<char> Line;

    /// <summary>行内第一个「有效」词元种类（注释与空白被跳过）。</summary>
    public LineTokenKind FirstKind;

    /// <summary>第一个有效词元文本；HashDirective 时为 "#" 之后、跳过空白后的指令词。</summary>
    public ReadOnlySpan<char> FirstToken;

    public int FirstTokenStart;
    public int FirstTokenEnd;

    /// <summary>第一个有效词元是否由 "#" 引导。</summary>
    public bool HasHash;

    /// <summary>本行是否存在代码态（非注释/非字符串）的左花括号。</summary>
    public bool HasBraceOpen;

    /// <summary>本行代码态的净花括号增量。</summary>
    public int BraceDelta;

    /// <summary>首个左花括号出现之前的净增量（用于计算 Pass 作用域进入深度）。</summary>
    public int BraceDeltaBeforeFirstOpen;

    /// <summary>本行首个字符串字面量的内容（不含引号）。</summary>
    public ReadOnlySpan<char> FirstStringContent;

    public int FirstStringStart;

    /// <summary>是否存在字符串字面量。</summary>
    public bool HasString;

    /// <summary>是否出现了未闭合的字符串字面量。</summary>
    public bool HasUnterminatedString;

    /// <summary>本行是否存在任何有效词元。</summary>
    public bool HasCodeToken;

    /// <summary>是否以标识符等于给定文本开头。</summary>
    public bool StartsWithIdentifier(ReadOnlySpan<char> text) =>
        FirstKind == LineTokenKind.Identifier && FirstToken.SequenceEqual(text);
}

/// <summary>
/// ShaderLab/HLSL 行扫描器：把一行文本里「注释与字符串之外」的结构信息一次性抽出来。
/// </summary>
/// <remarks>
/// 设计要点（对应规格书 v2.0 修正清单第 12 条）：
/// <list type="bullet">
/// <item>必须做词边界判定 —— "UsePass "…/ForwardLit"" 里的 "Pass" 子串不得被当成 Pass 关键字。</item>
/// <item>必须跳过注释与字符串 —— 注释里的 "HLSLPROGRAM"、字符串里的 "Pass" 都不得触发状态迁移。</item>
/// <item>必须统计代码态的净花括号增量 —— 用于 Pass 作用域的进出判定，且不得被字符串里的 "{" 干扰。</item>
/// <item>块注释可跨行，状态由调用方持有并回传。</item>
/// </list>
/// </remarks>
internal static class ShaderLineScanner
{
    /// <summary>扫描一行。<paramref name="inBlockComment"/> 为跨行块注释状态，进入时是上一行的遗留状态，返回时是本行结束后的状态。</summary>
    public static void Scan(ReadOnlySpan<char> line, ref bool inBlockComment, ref LineInfo info)
    {
        info = default;
        info.Line = line;

        var n = line.Length;
        var i = 0;

        var firstKind = LineTokenKind.None;
        var firstTokenStart = -1;
        var firstTokenEnd = -1;
        var firstIsHash = false;

        var braceDelta = 0;
        var braceDeltaBeforeFirstOpen = 0;
        var hasBraceOpen = false;

        var firstStringStart = -1;
        var firstStringEnd = -1;
        var unterminatedString = false;

        var significant = 0;

        while (i < n)
        {
            var c = line[i];

            if (inBlockComment)
            {
                if (c == '*' && i + 1 < n && line[i + 1] == '/')
                {
                    inBlockComment = false;
                    i += 2;
                }
                else
                {
                    i++;
                }

                continue;
            }

            if (c == '/')
            {
                if (i + 1 >= n)
                {
                    break;
                }

                if (line[i + 1] == '/')
                {
                    break; // 行注释：本行剩余部分整体丢弃
                }

                if (line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }
            }

            if (c is ' ' or '\t' or '\r' or '\f' or '\v')
            {
                i++;
                continue;
            }

            if (c == '"')
            {
                var j = i + 1;
                var closed = false;
                while (j < n)
                {
                    if (line[j] == '\\')
                    {
                        j += 2;
                        continue;
                    }

                    if (line[j] == '"')
                    {
                        closed = true;
                        break;
                    }

                    j++;
                }

                if (firstKind == LineTokenKind.None)
                {
                    firstKind = LineTokenKind.String;
                    firstTokenStart = i;
                    firstTokenEnd = closed ? j + 1 : n;
                }

                if (firstStringStart < 0)
                {
                    firstStringStart = i + 1;
                    firstStringEnd = closed ? j : n;
                }

                if (!closed)
                {
                    unterminatedString = true;
                }

                significant++;
                i = closed ? j + 1 : n;
                continue;
            }

            if (c == '{')
            {
                if (!hasBraceOpen)
                {
                    hasBraceOpen = true;
                    braceDeltaBeforeFirstOpen = braceDelta;
                }

                braceDelta++;
                significant++;
                if (firstKind == LineTokenKind.None)
                {
                    firstKind = LineTokenKind.Symbol;
                    firstTokenStart = i;
                    firstTokenEnd = i + 1;
                }

                i++;
                continue;
            }

            if (c == '}')
            {
                braceDelta--;
                significant++;
                if (firstKind == LineTokenKind.None)
                {
                    firstKind = LineTokenKind.Symbol;
                    firstTokenStart = i;
                    firstTokenEnd = i + 1;
                }

                i++;
                continue;
            }

            if (c == '#')
            {
                var j = i + 1;
                while (j < n && (line[j] == ' ' || line[j] == '\t'))
                {
                    j++;
                }

                var w = j;
                while (w < n && IsIdentifierChar(line[w]))
                {
                    w++;
                }

                if (firstKind == LineTokenKind.None)
                {
                    firstKind = LineTokenKind.HashDirective;
                    firstTokenStart = j;
                    firstTokenEnd = w;
                    firstIsHash = true;
                }

                significant++;
                i = w > j ? w : j + 1;
                continue;
            }

            if (IsIdentifierStart(c))
            {
                var j = i;
                while (j < n && IsIdentifierChar(line[j]))
                {
                    j++;
                }

                if (firstKind == LineTokenKind.None)
                {
                    firstKind = LineTokenKind.Identifier;
                    firstTokenStart = i;
                    firstTokenEnd = j;
                }

                significant++;
                i = j;
                continue;
            }

            if (firstKind == LineTokenKind.None)
            {
                firstKind = LineTokenKind.Symbol;
                firstTokenStart = i;
                firstTokenEnd = i + 1;
            }

            significant++;
            i++;
        }

        info.FirstKind = firstKind;
        info.FirstToken = firstTokenStart >= 0 ? line[firstTokenStart..firstTokenEnd] : default;
        info.FirstTokenStart = firstTokenStart;
        info.FirstTokenEnd = firstTokenEnd;
        info.HasHash = firstIsHash;
        info.HasBraceOpen = hasBraceOpen;
        info.BraceDelta = braceDelta;
        info.BraceDeltaBeforeFirstOpen = braceDeltaBeforeFirstOpen;
        info.HasString = firstStringStart >= 0;
        info.FirstStringStart = firstStringStart;
        info.FirstStringContent = firstStringStart >= 0 ? line[firstStringStart..firstStringEnd] : default;
        info.HasUnterminatedString = unterminatedString;
        info.HasCodeToken = significant > 0;
    }

    /// <summary>标识符首字符（Unity/HLSL 惯例：字母或下划线）。</summary>
    public static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    /// <summary>标识符后续字符（额外允许数字）。</summary>
    public static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
