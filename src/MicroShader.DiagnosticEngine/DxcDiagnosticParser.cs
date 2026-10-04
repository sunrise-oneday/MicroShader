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
///  * 诊断后跟随源码片段与脱字符行，必须忽略（无级别标记即成不了诊断）。
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

        var markerStart = -1;
        var severity = DiagnosticSeverity.Error;
        var markerLength = 0;

        foreach (var marker in Markers)
        {
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                continue;
            }

            if (markerStart < 0 || index < markerStart)
            {
                markerStart = index;
                markerLength = marker.Length;
                severity = marker[0] switch
                {
                    'w' => DiagnosticSeverity.Warning,
                    'n' or 'r' => DiagnosticSeverity.Information,
                    _ => DiagnosticSeverity.Error,
                };
            }
        }

        if (markerStart < 0)
        {
            return false;
        }

        var message = line[(markerStart + markerLength)..].Trim().ToString();
        // 级别标记前必然有一个冒号（"…:10: fatal error: "）→ 先剥掉尾冒号再解析位置，否则整段前缀解析失败。
        var prefix = line[..markerStart].TrimEnd();
        if (!prefix.IsEmpty && prefix[^1] == ':')
        {
            prefix = prefix[..^1].TrimEnd();
        }

        string file = string.Empty;
        var lineNumber = 0;
        var column = 0;

        var lastColon = prefix.LastIndexOf(':');
        if (lastColon > 0 && IsDigits(prefix[(lastColon + 1)..]))
        {
            var secondColon = prefix[..lastColon].LastIndexOf(':');
            if (secondColon > 0 && IsDigits(prefix[(secondColon + 1)..lastColon]))
            {
                file = prefix[..secondColon].ToString();
                lineNumber = ParseInt(prefix[(secondColon + 1)..lastColon]);
                column = ParseInt(prefix[(lastColon + 1)..]);
            }
            else
            {
                file = prefix[..lastColon].ToString();
                lineNumber = ParseInt(prefix[(lastColon + 1)..]);
            }
        }
        else if (!prefix.IsEmpty)
        {
            // 有前缀但不是 file:line 形态 —— 例如 dxc 把整段上下文带进首行，丢掉位置但保留文案。
            message = prefix.ToString() + ": " + message;
        }

        diagnostic = new DxcDiagnostic(severity, file, lineNumber, column, message);
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
