namespace MicroShader.Domain;

/// <summary>
/// ShaderLab 程序块方言。ShadeLab 允许两类互不交叉的程序块：
/// 现代 "HLSLPROGRAM/ENDHLSL" 与遗留 "CGPROGRAM/ENDCG"。
/// </summary>
public enum ShaderBlockKind : byte
{
    /// <summary>不在任何程序块内。</summary>
    None = 0,

    /// <summary>"HLSLPROGRAM ... ENDHLSL"（含 "HLSLINCLUDE ... ENDHLSL"）。</summary>
    Hlsl = 1,

    /// <summary>"CGPROGRAM ... ENDCG"（含 "CGINCLUDE ... ENDCG"）。</summary>
    Cg = 2,
}
