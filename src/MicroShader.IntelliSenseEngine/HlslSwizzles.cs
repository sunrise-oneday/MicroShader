namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// 内建向量类型的分量（swizzle）候选表。
/// </summary>
/// <remarks>
/// "为什么需要它（R7）"：成员补全原先只查「本文件结构体表」，于是 "float4 slope;" 的
/// "slope." 会走到「结构体定义不在本文件 → 降级为空」。可 shader 里最高频的成员访问恰恰就是
/// "slope.r" / "pivot.xz" / "i.color.rgb" 这类分量摆弄 —— 真实手写语料实测：
/// 类型解对了（"slope → float4"）却一个候选都给不出来。
/// "只放语言层知识"：分量名是 HLSL 语言规定（"xyzw" / "rgba"），不是 URP 源码符号，
/// 因此可以内置（与 <see cref="HlslBuiltins"/> 同一法律口径，详设 v2.0 第 1 条）。
/// "取哪些组合"：单个分量给足（"x y z w" + "r g b a"），多分量只给真实高频形态
/// （"xy" / "xz" / "zw" / "xyz" / "rgb" / "xyzw" / "rgba"），
/// 不做 4^n 全枚举 —— 客户端会按前缀本地过滤，候选集保持小。
/// </remarks>
internal static class HlslSwizzles
{
    private static readonly string[] Arity1 = ["r", "x"];

    private static readonly string[] Arity2 = ["g", "r", "rg", "x", "xy", "y"];

    private static readonly string[] Arity3 = ["b", "g", "r", "rb", "rg", "rgb", "x", "xy", "xyz", "xz", "y", "yz", "z"];

    private static readonly string[] Arity4 =
        ["a", "b", "g", "r", "rg", "rgb", "rgba", "w", "x", "xy", "xyz", "xyzw", "xz", "y", "z", "zw"];

    /// <summary>按分量个数取分量名表（按 Ordinal 升序，保证输出稳定）。</summary>
    public static string[] ForArity(int components) => components switch
    {
        1 => Arity1,
        2 => Arity2,
        3 => Arity3,
        4 => Arity4,
        _ => [],
    };
}
