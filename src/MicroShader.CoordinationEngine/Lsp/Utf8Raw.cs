using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MicroShader.CoordinationEngine.Lsp;

/// <summary>
/// <see cref="ReadOnlySequence{T}"/> 上的 UTF-8 取值助手。
/// </summary>
/// <remarks>
/// 全部走 <see cref="Utf8JsonReader"/> 或逐字节比较，"不做 "ToArray()" 兜底"
/// （v2.0 修正清单第 6 条：帧跨多个 "ReadOnlySequence" 段是常态，兜底拷贝会破坏零 GC）。
/// 方法名/ID 这类短值拷进 "stackalloc" 缓冲后比较；文档正文这类长值交给
/// <see cref="Utf8JsonReader.GetString"/> 一次性物化（Full 同步下无论如何都要一份 string，见 README 裁定 5）。
/// </remarks>
public static class Utf8Raw
{
    /// <summary>方法名等短值比较用的栈缓冲上限。</summary>
    private const int ShortValueLimit = 256;

    /// <summary>把 JSON 字符串字面量与给定 ASCII 名称逐字节比较（不分配、不解转义）。</summary>
    /// <remarks>方法名/协议关键字按规范不会含转义字符；一旦发现反斜杠就判定不等并让调用方走慢路径。</remarks>
    public static bool JsonStringEquals(in ReadOnlySequence<byte> value, string ascii)
    {
        Span<byte> buffer = stackalloc byte[ShortValueLimit];
        if (value.Length < 2 || value.Length > buffer.Length) return false;

        value.CopyTo(buffer);
        var span = buffer[..(int)value.Length];

        if (span[0] != (byte)'"' || span[^1] != (byte)'"') return false;

        var inner = span[1..^1];
        if (inner.Length != ascii.Length) return false;
        if (inner.IndexOf((byte)'\\') >= 0) return false;

        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != (byte)ascii[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// 解出 JSON 标量值：字符串去引号并反转义；数字 / true / false / null 原样返回字面量。
    /// </summary>
    /// <remarks>
    /// 「为什么不只处理字符串」：JSON-RPC 的 id 允许 integer|string。此方法原先只调 GetString()，
    /// 遇到数字 token 抛的是 InvalidOperationException（不是 JsonException，因此没被下面的 catch 兜住），
    /// 它会一路冒泡到读循环；而 Native AOT 下线程上的未处理异常会让进程 FailFast
    /// （退出码 0xC0000409，且不产生托管转储、不写 stderr —— 现场表现为「服务端凭空消失」）。
    /// 2026-09-29 实测复现：VSCode 取消挂起请求时发的 $/cancelRequest 用的就是数字 id，
    /// 于是「一次取消就打死整个语言服务器」——一处日志取值失误让服务完全不可用。
    /// </remarks>
    public static string Unquote(in ReadOnlySequence<byte> value)
    {
        try
        {
            var reader = new Utf8JsonReader(value, isFinalBlock: true, state: default);
            if (!reader.Read())
            {
                return string.Empty;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                return reader.GetString() ?? string.Empty;
            }

            if (reader.TokenType == JsonTokenType.Null)
            {
                return "null";
            }

            // 数字 / True / False：按 UTF-8 原文取字面量，绝不再假定它是字符串。
            var raw = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
            return Encoding.UTF8.GetString(raw);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>把 JSON 整数值取成 <see cref="int"/>。</summary>
    public static bool TryGetInt32(in ReadOnlySequence<byte> value, out int result)
    {
        Span<byte> buffer = stackalloc byte[32];
        result = 0;
        if (value.Length == 0 || value.Length > buffer.Length) return false;

        value.CopyTo(buffer);
        return int.TryParse(
            buffer[..(int)value.Length],
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out result);
    }

    /// <summary>序列是否包含指定字节。</summary>
    public static bool Contains(in ReadOnlySequence<byte> value, byte needle)
    {
        foreach (var memory in value)
        {
            if (memory.Span.IndexOf(needle) >= 0) return true;
        }
        return false;
    }
}
