using System.Text;

namespace MicroShader.Domain;

/// <summary>
/// 物理路径 ↔ "file:///" URI 的零依赖转换。
/// LSP 要求 URI 为 UTF-8 百分号编码，含中文的工程路径必须正确编码，否则客户端会拒绝打开文档。
/// </summary>
public static class FileUri
{
    private const string HexDigits = "0123456789ABCDEF";

    // 线程级「上一条」记忆化：LSP 的 didChange 会在同一线程上反复为同一个文档路径求 URI。
    // 纯函数 + string 不可变，因此缓存安全；只保留一条，内存恒定、无锁、无字典查找。
    [ThreadStatic]
    private static string? t_lastPath;

    [ThreadStatic]
    private static string? t_lastUri;

    /// <summary>把 Windows 物理路径转换为 "file:///" URI。</summary>
    public static string FromPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        if (path == t_lastPath)
        {
            return t_lastUri!;
        }

        var uri = FromPathCore(path);
        t_lastPath = path;
        t_lastUri = uri;
        return uri;
    }

    private static string FromPathCore(string path)
    {
        var normalized = path.Replace('\\', '/');

        // UNC（\\server\share）与盘符路径统一成「file:///…」形式。
        var prefix = normalized.StartsWith("//", StringComparison.Ordinal) ? "file://" : "file:///";
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        var builder = new StringBuilder(prefix.Length + normalized.Length + 8);
        builder.Append(prefix);
        foreach (var ch in normalized)
        {
            if (IsUnreserved(ch) || ch == '/' || ch == ':')
            {
                builder.Append(ch);
            }
            else
            {
                AppendPercentEncoded(builder, ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(char ch) =>
        (ch >= 'a' && ch <= 'z') ||
        (ch >= 'A' && ch <= 'Z') ||
        (ch >= '0' && ch <= '9') ||
        ch is '-' or '_' or '.' or '~';

    private static void AppendPercentEncoded(StringBuilder builder, char ch)
    {
        if (ch < 0x80)
        {
            builder.Append('%').Append(HexDigits[(ch >> 4) & 0xF]).Append(HexDigits[ch & 0xF]);
            return;
        }

        // 非 ASCII：按 UTF-8 字节逐个百分号编码（代理对会被 Encoding 正确合并）。
        Span<char> single = stackalloc char[1];
        single[0] = ch;
        Span<byte> bytes = stackalloc byte[4];
        var written = Encoding.UTF8.GetBytes(single, bytes);
        for (var i = 0; i < written; i++)
        {
            builder.Append('%').Append(HexDigits[(bytes[i] >> 4) & 0xF]).Append(HexDigits[bytes[i] & 0xF]);
        }
    }
}
