using MicroShader.Domain;

namespace MicroShader.DiagnosticEngine;

/// <summary>DXC 原始诊断的一条记录（未做任何坐标映射；列号单位是 UTF-8 字节偏移）。</summary>
public readonly record struct DxcDiagnostic(
    DiagnosticSeverity Severity,
    string File,
    int Line,
    int Column,
    string Message)
{
    /// <summary>是否带有效位置（DXC 有时只给文件名或干脆不给）。</summary>
    public bool HasLocation => File.Length > 0 && Line > 0;

    public bool HasColumn => Column > 0;

    public override string ToString()
        => HasLocation ? $"{File}:{Line}:{Column} {Severity}: {Message}" : $"{Severity}: {Message}";
}

/// <summary>
/// DXC 诊断文本解析器。形态："&lt;file&gt;:&lt;line&gt;[:&lt;col&gt;]: &lt;severity&gt;: &lt;message&gt;"。
///
/// 解析难点与对策：
///  * 文件名可能含盘符冒号（物理包路径）→ 必须从右往左切冒号，不能从左往右 split；
///  * 严重级别可能是 "fatal error"（含子串 "error: "）→ 取最左的级别标记，fatal 形式优先命中；
///  * 诊断后紧跟源码回显与脱字符行：回显是原文照抄，里面可能本身就有 "error: " 字样，
///    因此判定不能只看「有没有级别标记」，必须要求标记之前是一段合法的文件位置前缀；
///  * 位置前缀有两种形态："路径:行:列"（DXC 实际输出）与 "路径(行,列)"（MSVC），两者都要认；
///  * 无位置的诊断也存在（如 "error: missing entry point definition"），只接受整行从第 0 列开头的那种。
/// </summary>
public static class DxcDiagnosticParser
{
    // 顺序无关，取最左命中；"fatal error: " 必须整词出现，否则 fatal 会被吃进文件名段。
    private static readonly string[] Markers = ["fatal error: ", "error: ", "warning: ", "note: ", "remark: "];

    public static List<DxcDiagnostic> Parse(string? text)
    {
        var list = new List<DxcDiagnostic>();
        if (string.IsNullOrEmpty(text))
        {
            return list;
        }

        // 用 span 逐行推进，避免 Split('\n') 先切出整段 string[] 的分配（每编译单元一次）。
        var span = text.AsSpan();
        var pos = 0;
        while (pos < span.Length)
        {
            var newline = span[pos..].IndexOf('\n');
            var line = newline < 0 ? span[pos..] : span.Slice(pos, newline);
            if (TryParseLine(line, out var diagnostic))
            {
                list.Add(diagnostic);
            }

            if (newline < 0)
            {
                break;
            }

            pos += newline + 1;
        }

        return list;
    }

    public static bool TryParseLine(ReadOnlySpan<char> line, out DxcDiagnostic diagnostic)
    {
        diagnostic = default;

        var end = line.Length;
        while (end > 0 && (line[end - 1] == '\r' || line[end - 1] == '\n'))
        {
            end--;
        }

        line = line[..end];
        if (line.IsEmpty)
        {
            return false;
        }

        // 从左往右逐个试级别标记，只有「标记前面确实是一段文件位置」才认这条诊断。
        // 这样 DXC 紧跟诊断打印的源码回显（例如 "return x; // error: 说明"）里的标记字样
        // 也不会被当成一条诊断 —— 修复前它会被解析成「无文件、无行号」的错误，
        // 归属阶段据此判成工具自身缺陷，在用户文件上凭空多出一条红线。
        var searchStart = 0;
        while (searchStart < line.Length)
        {
            if (!TryFindMarker(line[searchStart..], out var offset, out var markerLength, out var severity))
            {
                return false;
            }

            var markerStart = searchStart + offset;
            var message = line[(markerStart + markerLength)..].Trim().ToString();

            // 级别标记前必然有一个冒号（"…:10: fatal error: "）→ 先剥掉尾冒号再解析位置，否则整段前缀解析失败。
            var prefix = line[..markerStart].TrimEnd();
            if (!prefix.IsEmpty && prefix[^1] == ':')
            {
                prefix = prefix[..^1].TrimEnd();
            }

            if (prefix.IsEmpty)
            {
                // 无位置诊断确实存在（实测 "error: missing entry point definition"），但只允许整行
                // 从第 0 列就以级别标记开头：带缩进的回显行必须继续往下找，不能被当成这种诊断。
                if (markerStart == 0)
                {
                    diagnostic = new DxcDiagnostic(severity, string.Empty, 0, 0, message);
                    return true;
                }
            }
            else if (TryParseLocation(prefix, out var file, out var lineNumber, out var column))
            {
                diagnostic = new DxcDiagnostic(severity, file, lineNumber, column, message);
                return true;
            }

            // 这个标记不属于位置前缀（例如文件名本身就带 "error: " 字样），跳过它继续找下一个。
            searchStart = markerStart + markerLength;
        }

        return false;
    }

    /// <summary>在一行里找最左的级别标记；"fatal error: " 与 "error: " 同时命中时取更左的那个。</summary>
    private static bool TryFindMarker(
        ReadOnlySpan<char> text, out int index, out int length, out DiagnosticSeverity severity)
    {
        index = -1;
        length = 0;
        severity = DiagnosticSeverity.Error;

        foreach (var marker in Markers)
        {
            var at = text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue;
            }

            if (index < 0 || at < index)
            {
                index = at;
                length = marker.Length;
                severity = marker[0] switch
                {
                    'w' => DiagnosticSeverity.Warning,
                    'n' or 'r' => DiagnosticSeverity.Information,
                    _ => DiagnosticSeverity.Error,
                };
            }
        }

        return index >= 0;
    }

    /// <summary>
    /// 严格解析级别标记之前那段位置前缀，两种形态都认：
    /// "路径:行:列"（DXC 实际输出的形态）与 "路径(行,列)"（MSVC 形态）。
    /// 解析不出来就返回 false —— 源码回显行正是因为没有位置前缀而在这一步被排除。
    /// </summary>
    private static bool TryParseLocation(
        ReadOnlySpan<char> prefix, out string file, out int line, out int column)
    {
        file = string.Empty;
        line = 0;
        column = 0;

        // 形态一：路径(行,列) 或 路径(行)。路径本身可以带括号，所以取最后一个左括号。
        if (prefix[^1] == ')')
        {
            var open = prefix.LastIndexOf('(');
            if (open > 0)
            {
                var inner = prefix[(open + 1)..^1];
                var comma = inner.IndexOf(',');
                if (comma < 0)
                {
                    if (!IsDigits(inner))
                    {
                        return false;
                    }

                    line = ParseInt(inner);
                }
                else
                {
                    var lineSpan = inner[..comma];
                    var columnSpan = inner[(comma + 1)..];
                    if (!IsDigits(lineSpan) || !IsDigits(columnSpan))
                    {
                        return false;
                    }

                    line = ParseInt(lineSpan);
                    column = ParseInt(columnSpan);
                }

                return TryTakeFileName(prefix[..open], out file);
            }
        }

        // 形态二：路径:行[:列]。文件名可能含盘符冒号，必须从右往左切。
        var lastColon = prefix.LastIndexOf(':');
        if (lastColon <= 0 || !IsDigits(prefix[(lastColon + 1)..]))
        {
            return false;
        }

        var secondColon = prefix[..lastColon].LastIndexOf(':');
        if (secondColon > 0 && IsDigits(prefix[(secondColon + 1)..lastColon]))
        {
            line = ParseInt(prefix[(secondColon + 1)..lastColon]);
            column = ParseInt(prefix[(lastColon + 1)..]);
            return TryTakeFileName(prefix[..secondColon], out file);
        }

        line = ParseInt(prefix[(lastColon + 1)..]);
        return TryTakeFileName(prefix[..lastColon], out file);
    }

    /// <summary>
    /// 文件名段必须非空、且不以空白开头：路径里允许空格、括号、中文，但绝不可能以缩进开头，
    /// 后者是源码回显（"    int a = 1;"）的特征。
    /// </summary>
    private static bool TryTakeFileName(ReadOnlySpan<char> span, out string file)
    {
        file = string.Empty;
        if (span.IsEmpty || char.IsWhiteSpace(span[0]))
        {
            return false;
        }

        file = span.ToString();
        return true;
    }

    private static bool IsDigits(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (c < '0' || c > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static int ParseInt(ReadOnlySpan<char> span)
    {
        var value = 0;
        foreach (var c in span)
        {
            value = (value * 10) + (c - '0');
        }

        return value;
    }
}
