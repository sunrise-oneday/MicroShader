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
        while (p < lineEnd && (text[p] == ' ' || text[p] == (char)9 || text[p] == (char)13))
        {
            p++;
        }

        if (p >= lineEnd || text[p] != '#')
        {
            return;
        }

        p++;
        while (p < lineEnd && (text[p] == ' ' || text[p] == (char)9))
        {
            p++;
        }

        if (!StartsWith(text, p, lineEnd, "include"))
        {
            return;
        }

        p += "include".Length;

        // #include_with_pragmas / #include_xxx 这类 Unity 后缀：继续吃掉下划线后的标识符
        while (p < lineEnd && SymbolAtCursor.IsIdentifierChar(text[p]))
        {
            p++;
        }

        while (p < lineEnd && (text[p] == ' ' || text[p] == (char)9))
        {
            p++;
        }

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
        && (at + keyword.Length >= limit || !SymbolAtCursor.IsIdentifierChar(text[at + keyword.Length]));
}
