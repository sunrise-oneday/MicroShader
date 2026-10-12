using System.Text;

namespace MicroShader.CoordinationEngine.Lsp;

/// <summary>
/// LSP 文档 URI 与物理路径的互转（"file:///C:/…"）。
/// </summary>
/// <remarks>
/// 正向转换由 "MicroShader.Domain.FileUri.FromPath" 提供；这里是"反向"：
/// 百分号解码必须按 "UTF-8 字节"还原（中文工程路径常见），不能逐字符处理。
/// </remarks>
public static class LspUri
{
    /// <summary>把 "file://" URI 还原为 Windows 物理路径；非 file scheme 返回 "null"。</summary>
    public static string? ToPath(string? uri)
    {
        if (string.IsNullOrEmpty(uri)) return null;
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;

        var rest = uri[7..];

        // file:///C:/x → 去掉前导 '/'；file://server/share → UNC 前缀
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

    /// <summary>把物理路径转成 LSP URI。</summary>
    public static string FromPath(string path) => Domain.FileUri.FromPath(path);

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
                // 连续的 %XX 必须成组还原（一个中文字符 = 3 个字节 = 3 组 %XX）。
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
}
