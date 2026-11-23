using System.Collections.Frozen;

namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// HLSL 标量/向量/矩阵/纹理类型名的识别表。
/// </summary>
/// <remarks>
/// 这是"语言层"知识（HLSL 内建类型），不是 URP 源码符号，因此可以内置 ——
/// 详设 v2.0 第 1 条的法律约束只针对「抽取 URP 源码符号随包分发」。
/// </remarks>
internal static class HlslTypes
{
    /// <summary>基础类型前缀（可整词命中，也可跟维度后缀，如 float / float3 / float4x4 / real4）。</summary>
    /// <remarks>
    /// "两处实测修正（2026-09-26，真实语料回归）"：
    /// ① 补入 "real"。Unity 的 "real" / "real2" / "real3" / "real4"
    /// 是宏族（URP/核心库里按精度重定义），此前不在表里 → "real4 x;" 声明的变量一个都登记不上。
    /// ② 允许"整词"命中。原实现用 "token.Length &lt;= prefix.Length" 把裸 "float" / "half" /
    /// "int" / "uint" / "bool" / "double" 全部排除，导致 "float _GrassWidth;"
    /// 这类最常见的标量声明反而不被识别为类型。
    /// </remarks>
    private static readonly string[] ScalarPrefixes =
    [
        "float", "half", "real", "int", "uint", "bool", "double", "dword",
        "min16float", "min10float", "min16int", "min16uint", "min12int",
        "vector", "matrix",
    ];

    /// <summary>带后缀的纹理/缓冲类型（必须整词匹配）。</summary>
    private static readonly FrozenSet<string> ExactNames = new[]
    {
        "void", "string",
        "Texture1D", "Texture1DArray", "Texture2D", "Texture2DArray", "Texture2DMS", "Texture2DMSArray",
        "Texture3D", "TextureCube", "TextureCubeArray", "RWTexture1D", "RWTexture1DArray",
        "RWTexture2D", "RWTexture2DArray", "RWTexture3D",
        "SamplerState", "SamplerComparisonState",
        "StructuredBuffer", "RWStructuredBuffer", "AppendStructuredBuffer", "ConsumeStructuredBuffer",
        "ByteAddressBuffer", "RWByteAddressBuffer",
        "ConstantBuffer", "cbuffer", "tbuffer",
        "InputPatch", "OutputPatch", "PointStream", "LineStream", "TriangleStream",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>零分配替身查找：直接用 ReadOnlySpan&lt;char&gt; 查 ExactNames，避免逐标识符 ToString() 堆分配。</summary>
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> ExactNameLookup =
        ExactNames.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// 判断一个标识符在声明位置是否「看起来像类型」。
    /// </summary>
    /// <remarks>
    /// 启发式三条（按序）：① 内建带后缀类型整词命中；② 内建标量前缀 + 后续全是维度数字或 x+数字；
    /// ③ 段首字母大写（HLSL 结构体命名惯例）—— 这一条让 "Varyings input" 这类自定义结构体也能识别。
    /// 刻意"不"要求类型必须已知：半书写状态下结构体可能还没定义完（详设 §六.1 的容错要求）。
    /// </remarks>
    /// <summary>
    /// 把内建"标量 / 向量 / 矩阵"类型名拆成「标量名 + 行 + 列」。
    /// </summary>
    /// <remarks>
    /// 只认三种后缀形态：空（"float" → 1x1）、单个 2~4（"float4" → 4x1）、
    /// "NxM"（"float4x4" → 4x4）。"vector"/"matrix" 裸词形状未知，返回 false
    /// （它们仍然是类型，只是没有可枚举的分量）——成员补全据此决定是否给出分量候选。
    /// </remarks>
    public static bool TryParseShape(ReadOnlySpan<char> token, out string scalar, out int rows, out int cols)
    {
        scalar = string.Empty;
        rows = 0;
        cols = 0;

        foreach (var prefix in ScalarPrefixes)
        {
            if (prefix is "vector" or "matrix")
            {
                continue;
            }

            if (token.Length < prefix.Length || !token.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = token[prefix.Length..];

            if (rest.IsEmpty)
            {
                scalar = prefix;
                rows = 1;
                cols = 1;
                return true;
            }

            if (rest.Length == 1 && rest[0] is >= '2' and <= '4')
            {
                scalar = prefix;
                rows = rest[0] - '0';
                cols = 1;
                return true;
            }

            if (rest.Length == 3 && rest[0] is >= '1' and <= '4' && rest[1] == 'x' && rest[2] is >= '1' and <= '4')
            {
                scalar = prefix;
                rows = rest[0] - '0';
                cols = rest[2] - '0';
                return true;
            }

            return false;
        }

        return false;
    }

    public static bool LooksLikeType(ReadOnlySpan<char> token)
    {
        if (token.IsEmpty)
        {
            return false;
        }

        if (ExactNameLookup.Contains(token))
        {
            return true;
        }

        foreach (var prefix in ScalarPrefixes)
        {
            if (token.Length < prefix.Length || !token.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (token.Length == prefix.Length)
            {
                return true;   // 裸标量类型：float / half / int / uint / real ...
            }

            var rest = token[prefix.Length..];
            var ok = true;
            for (var i = 0; i < rest.Length; i++)
            {
                var c = rest[i];
                if (c is >= '1' and <= '4')
                {
                    continue;
                }

                if (c == 'x' && i + 1 < rest.Length && rest[i + 1] is >= '1' and <= '4')
                {
                    i++;
                    continue;
                }

                ok = false;
                break;
            }

            if (ok)
            {
                return true;
            }
        }

        // 回退：段首大写 → 自定义结构体（Varyings / Attributes / SurfaceData…）
        return char.IsAsciiLetterUpper(token[0]);
    }
}
