using System.Buffers;

namespace MicroShader.NavigationEngine;

/// <summary>一条 "#include" 的可点击范围与目标路径。</summary>
/// <param name="RangeStart">路径文本（引号内）的起始偏移。</param>
/// <param name="RangeEnd">路径文本的结束偏移（独占）。</param>
/// <param name="Target">原样的目标字符串（如 "Packages/com.unity…/Core.hlsl"）。</param>
/// <param name="Virtual">是否形如工程虚拟路径（"Packages/…"）；false 表示相对/裸名。</param>
internal readonly record struct IncludeLink(int RangeStart, int RangeEnd, string Target, bool Virtual);

/// <summary>
/// "#include" 超链接的"纯词法"扫描器。
/// </summary>
/// <remarks>
/// 
/// 详设 v2.0 第 4 条：首屏只做词法扫描，"不碰磁盘、不查 VFS"。
/// 一份含 200 个 include 的 .shader 因此从 200 次 VFS 查询降到 0 次；
/// 真正的路径解析与存活性校验推迟到 "documentLink/resolve"（用户悬停/点击时才发生）。
/// 
/// 同时识别 "#include_with_pragmas"（Unity 专有指令，URP 的 Lit 系列全靠它）。
/// 少认这一种会导致 URP 的 include 完全没有下划线 —— 而这正是本模块最常用的入口。
/// </remarks>
internal static class DocumentLinkScanner
{
    private const char Nl = (char)10;

    /// <summary>行首空白：空格、制表符、回车。'\r' 只在未经切行处理的行尾出现。</summary>
    private static readonly SearchValues<char> LeadWhitespace =
        SearchValues.Create(" \t\r");

    /// <summary>行内空白：空格与制表符。</summary>
    private static readonly SearchValues<char> Spaces =
        SearchValues.Create(" \t");

    public static List<IncludeLink> Scan(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var links = new List<IncludeLink>();
        var start = 0;

        while (start < text.Length)
        {
            var nl = text.IndexOf(Nl, start);
            var end = nl < 0 ? text.Length : nl;

            ScanLine(text, start, end, links);

            if (nl < 0)
            {
                break;
            }

            start = nl + 1;
        }

        return links;
    }

    private static void ScanLine(string text, int lineStart, int lineEnd, List<IncludeLink> links)
    {
        var p = lineStart;
        var leadStop = text.AsSpan(p, lineEnd - p).IndexOfAnyExcept(LeadWhitespace);
        p = leadStop < 0 ? lineEnd : p + leadStop;

        if (p >= lineEnd || text[p] != '#')
        {
            return;
        }

        p++;
        var spaceStop = text.AsSpan(p, lineEnd - p).IndexOfAnyExcept(Spaces);
        p = spaceStop < 0 ? lineEnd : p + spaceStop;

        if (!StartsWith(text, p, lineEnd, "include"))
        {
            return;
        }

        p += "include".Length;

        // #include_with_pragmas / #include_xxx 这类 Unity 后缀：继续吃掉下划线后的标识符。
        // 不限定后缀词（Unity 除 with_pragmas 外还有别的变体），只要求它以关键字边界起头。
        var suffixStop = text.AsSpan(p, lineEnd - p).IndexOfAnyExcept(SymbolAtCursor.IdentifierChars);
        p = suffixStop < 0 ? lineEnd : p + suffixStop;

        var trailStop = text.AsSpan(p, lineEnd - p).IndexOfAnyExcept(Spaces);
        p = trailStop < 0 ? lineEnd : p + trailStop;

        if (p >= lineEnd)
        {
            return;
        }

        var open = text[p];
        char close;

        if (open == '"')
        {
            close = '"';
        }
        else if (open == '<')
        {
            close = '>';
        }
        else
        {
            return;
        }

        var from = p + 1;
        var to = text.IndexOf(close, from);
        if (to < 0 || to > lineEnd || to <= from)
        {
            return;
        }

        var target = text[from..to];
        if (target.Length == 0)
        {
            return;
        }

        var isVirtual = target.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);

        links.Add(new IncludeLink(from, to, target, isVirtual));
    }

    private static bool StartsWith(string text, int at, int limit, string keyword)
        => at + keyword.Length <= limit
        && string.CompareOrdinal(text, at, keyword, 0, keyword.Length) == 0
        && (at + keyword.Length >= limit || IsKeywordBoundary(text[at + keyword.Length]));

    // 关键字边界不能只认「非标识符」：Unity 的 "#include_with_pragmas" 用下划线把后缀直接接在
    // include 后面，而下划线本身属于标识符字符。若把下划线也当标识符，下面的后缀循环永远进不去，
    // URP Lit 那类 shader 的 include 会一条链接都没有。所以下划线单列为合法边界；
    // 不带下划线的长标识符（"includeSomething"）仍然被排除，不会被误判成 include。
    private static bool IsKeywordBoundary(char c)
        => !SymbolAtCursor.IsIdentifierChar(c) || c == '_';
}
