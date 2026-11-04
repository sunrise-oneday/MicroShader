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

    /// <summary>
    /// 把 "file://" URI 还原为 Windows 物理路径；非 file scheme 返回 null。
    /// </summary>
    /// <remarks>
    /// 必须按 URI 语义还原，不能拿字符串裁前缀：file://server/share/x.hlsl 里的 server 是
    /// URI 的 authority（主机名），裁掉 "file://" 会把它当成路径的第一段，还原出
    /// server/share/x.hlsl 这种相对路径；按语义还原得到的是 UNC 前缀 \\server\share\x.hlsl。
    /// 百分号解码只做一次，且按 UTF-8 字节成组还原 —— 中文与空格路径靠这一条才不被破坏。
    /// </remarks>
    public static string? ToPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;

        var rest = uri[7..];

        // "file://" 本身没有 authority 也没有路径：还原成 "\\" 会被下游当成「成功拿到一个路径」。
        if (rest.Length == 0) return null;

        // file:///C:/x 多一个前导斜杠（空 authority）；file://server/share 才是 UNC。
        string path;
        if (rest.StartsWith('/'))
        {
            path = rest[1..];
        }
        else
        {
            path = @"\\" + rest;
        }

        var decoded = PercentDecode(path);
        return decoded.Replace('/', '\\');
    }

    /// <summary>百分号解码：连续的 %XX 必须成组按 UTF-8 还原（一个中文字符是 3 个字节、3 组 %XX）。</summary>
    private static string PercentDecode(string value)
    {
        if (value.IndexOf('%') < 0) return value;

        var builder = new StringBuilder(value.Length);
        var bytes = new List<byte>(8);
        var i = 0;

        while (i < value.Length)
        {
            if (value[i] == '%' && TryHex(value, i, out _))
            {
                bytes.Clear();
                while (i < value.Length && value[i] == '%' && TryHex(value, i, out var b))
                {
                    bytes.Add(b);
                    i += 3;
                }

                builder.Append(Encoding.UTF8.GetString(bytes.ToArray()));
                continue;
            }

            builder.Append(value[i]);
            i++;
        }

        return builder.ToString();
    }

    private static bool TryHex(string value, int index, out byte result)
    {
        result = 0;
        if (index + 2 >= value.Length) return false;

        var hi = HexValue(value[index + 1]);
        var lo = HexValue(value[index + 2]);
        if (hi < 0 || lo < 0) return false;

        result = (byte)((hi << 4) | lo);
        return true;
    }

    private static int HexValue(char ch) => ch switch
    {
        >= '0' and <= '9' => ch - '0',
        >= 'a' and <= 'f' => ch - 'a' + 10,
        >= 'A' and <= 'F' => ch - 'A' + 10,
        _ => -1,
    };

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
