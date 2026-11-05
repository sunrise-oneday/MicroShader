namespace MicroShader.NavigationEngine;

/// <summary>
/// LSP "SymbolKind" 取值与「本模块节点类型 → 允许值」映射表。
/// </summary>
/// <remarks>
/// 
/// 详设 v2.0 第 8 条："SymbolKind 全集里没有 Shader / SubShader / Pass / HLSLPROGRAM"，
/// 必须给出映射；且「缺 "symbolKind.valueSet" 时客户端只支持 File..Array」，
/// 因此选取时要在客户端给出的允许集内择优，而不是硬发一个它不认识的值。
/// 
/// 映射（与详设建议一致）：Shader → File，SubShader → Namespace，Pass → Method，
/// HLSLPROGRAM/CGPROGRAM → Module，函数 → Function，结构体 → Struct，
/// 结构体字段 → Field，变量 → Variable，宏 → Constant。
/// </remarks>
internal static class SymbolKinds
{
    public const int File = 1;
    public const int Module = 2;
    public const int Namespace = 3;
    public const int Method = 6;
    public const int Field = 8;
    public const int Function = 12;
    public const int Variable = 13;
    public const int Constant = 14;
    public const int Struct = 23;

    /// <summary>Shader 根节点。</summary>
    public static readonly int[] Shader = [File, Module, Namespace, Method];

    /// <summary>SubShader。</summary>
    public static readonly int[] SubShader = [Namespace, Module, File, Method];

    /// <summary>Pass。</summary>
    public static readonly int[] Pass = [Method, Namespace, Module, File];

    /// <summary>程序块（HLSLPROGRAM / CGPROGRAM / HLSLINCLUDE）。</summary>
    public static readonly int[] Program = [Module, Method, Namespace, File];

    /// <summary>函数。</summary>
    public static readonly int[] FunctionPref = [Function, Method, File];

    /// <summary>结构体。</summary>
    public static readonly int[] StructPref = [Struct, Method, Function, File];

    /// <summary>结构体字段。</summary>
    public static readonly int[] FieldPref = [Field, Variable, Constant, File];

    /// <summary>变量。</summary>
    public static readonly int[] VariablePref = [Variable, Constant, Field, File];

    /// <summary>宏。</summary>
    public static readonly int[] MacroPref = [Constant, Function, Variable, File];

    /// <summary>在客户端的 "valueSet" 内择优；<paramref name="allowed"/> 为空表示不限制。</summary>
    public static int Pick(int[] preference, int[] allowed)
    {
        if (allowed.Length == 0)
        {
            return preference[0];
        }

        foreach (var candidate in preference)
        {
            foreach (var value in allowed)
            {
                if (value == candidate)
                {
                    return candidate;
                }
            }
        }

        // 客户端声明的允许集与我们的偏好完全不相交：退到它允许的第一个值。
        // 绝不发 out-of-set 的值（详设第 8 条：那会让大纲/面包屑显示成空节点）。
        return allowed[0];
    }
}
