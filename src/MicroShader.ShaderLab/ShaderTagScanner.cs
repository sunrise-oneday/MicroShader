using MicroShader.Domain;

namespace MicroShader.ShaderLab;

/// <summary>
/// "Tags" 块的行级扫描器：从一行文本里抽出全部 ""name" = "value"" 键值对。
/// </summary>
/// <remarks>
/// 与 <see cref="ShaderLineScanner"/> 共用同一套「注释/字符串感知」口径（含跨行块注释状态），
/// 但不做任何语法树化：Tags 块的语法就是「成对的带引号字符串夹一个等号」。
/// 刻意不依赖 "LineInfo" 只记首个字符串的约定 —— 真机上
/// "Tags { "Queue"="Transparent" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }"
/// 这种同行多标签非常常见，必须全部抽出来。
/// </remarks>
internal static class ShaderTagScanner
{
    /// <summary>
    /// 扫描一行并把其中所有键值对追加到 <paramref name="into"/>（通过 <paramref name="result"/> 的槽位池取对象）。
    /// </summary>
    /// <param name="line">行内容（不含换行符）。</param>
    /// <param name="lineNumber">该行的 1-based 物理行号。</param>
    /// <param name="scope">本 Tags 块所在层级。</param>
    /// <param name="subShaderIndex">所属 SubShader 序号；不在任何 SubShader 内时为 -1。</param>
    /// <param name="ordinal">本 Tags 块内已产出的条目数（按引用累加）。</param>
    /// <param name="inBlockComment">跨行块注释状态（进入时是上一行遗留状态）。</param>
    /// <param name="result">产出目标。</param>
    public static void ScanLine(
        ReadOnlySpan<char> line,
        int lineNumber,
        ShaderTagScope scope,
        int subShaderIndex,
        ref int ordinal,
        ref bool inBlockComment,
        ShaderLabParseResult result)
    {
        var n = line.Length;
        var i = 0;

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
                if (i + 1 < n && line[i + 1] == '/')
                {
                    return; // 行注释：本行到此为止
                }

                if (i + 1 < n && line[i + 1] == '*')
                {
                    inBlockComment = true;
                    i += 2;
                    continue;
                }

                i++;
                continue;
            }

            if (c != '"')
            {
                i++;
                continue;
            }

            // ── 读一个字符串字面量，视作「标签名」候选 ──
            var nameStart = i + 1;
            var nameEnd = SkipStringLiteral(line, nameStart, out var closed);
            var afterName = closed ? nameEnd + 1 : n;

            // ── 后面必须紧跟 '=' ──
            if (!TrySkipSpaces(line, ref afterName, out var equals) || line[equals] != '=')
            {
                i = afterName;
                continue;
            }

            var afterEquals = equals + 1;
            if (!TrySkipSpaces(line, ref afterEquals, out var valueQuote) || line[valueQuote] != '"')
            {
                i = afterName;
                continue;
            }

            // ── 读「标签值」──
            var valueStart = valueQuote + 1;
            var valueEnd = SkipStringLiteral(line, valueStart, out var valueClosed);

            var entry = result.RentTagEntry();
            entry.Name = line[nameStart..nameEnd].ToString();
            entry.Value = line[valueStart..valueEnd].ToString();
            entry.NameLine = lineNumber;
            entry.NameColumn = nameStart + 1;
            entry.NameLength = nameEnd - nameStart;
            entry.ValueLine = lineNumber;
            entry.ValueColumn = valueStart + 1;
            entry.ValueLength = valueEnd - valueStart;
            entry.Scope = scope;
            entry.ScopeOrdinal = ordinal++;
            entry.SubShaderIndex = subShaderIndex;
            result.TagEntries.Add(entry);

            i = valueClosed ? valueEnd + 1 : n;
        }
    }

    /// <summary>返回字符串字面量内容的结束下标（不含引号）；<paramref name="closed"/> 表示是否找到了收尾引号。</summary>
    private static int SkipStringLiteral(ReadOnlySpan<char> line, int start, out bool closed)
    {
        var j = start;
        var n = line.Length;

        while (j < n)
        {
            if (line[j] == '\\')
            {
                j += 2; // 转义：跳过下一个字符（Unity 路径里的 \\ 很少见，但语义必须正确）
                continue;
            }

            if (line[j] == '"')
            {
                closed = true;
                return j;
            }

            j++;
        }

        closed = false;
        return n;
    }

    private static bool TrySkipSpaces(ReadOnlySpan<char> line, ref int index, out int result)
    {
        var i = index;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        index = i;
        result = i;
        return i < line.Length;
    }
}
