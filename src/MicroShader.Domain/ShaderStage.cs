namespace MicroShader.Domain;

/// <summary>
/// 着色器阶段。对应 Unity 的 "SHADER_STAGE_*" 宏集合，必须「恰好一个成员」且可互斥。
/// </summary>
public enum ShaderStage : byte
{
    /// <summary>未做阶段修饰（"#pragma multi_compile" 不带 "_fragment"/"_vertex" 后缀）。</summary>
    None = 0,

    Vertex = 1,
    Fragment = 2,
    Hull = 3,
    Domain = 4,
    Geometry = 5,
    Compute = 6,
    RayTracing = 7,
    Mesh = 8,
    Amplification = 9,
}

/// <summary>阶段与 Unity 宏名/DXC 目标档位的映射。</summary>
public static class ShaderStageExtensions
{
    /// <summary>该阶段对应的 Unity 阶段宏名；<see cref="ShaderStage.None"/> 返回空串。</summary>
    public static string ToStageMacro(this ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => "SHADER_STAGE_VERTEX",
        ShaderStage.Fragment => "SHADER_STAGE_FRAGMENT",
        ShaderStage.Hull => "SHADER_STAGE_HULL",
        ShaderStage.Domain => "SHADER_STAGE_DOMAIN",
        ShaderStage.Geometry => "SHADER_STAGE_GEOMETRY",
        ShaderStage.Compute => "SHADER_STAGE_COMPUTE",
        ShaderStage.RayTracing => "SHADER_STAGE_RAY_TRACING",
        ShaderStage.Mesh => "SHADER_STAGE_MESH",
        ShaderStage.Amplification => "SHADER_STAGE_AMPLIFICATION",
        _ => string.Empty,
    };

    /// <summary>该阶段对应的 DXC 目标档位（SM6.0）；<see cref="ShaderStage.None"/> 返回空串。</summary>
    public static string ToTargetProfile(this ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => "vs_6_0",
        ShaderStage.Fragment => "ps_6_0",
        ShaderStage.Hull => "hs_6_0",
        ShaderStage.Domain => "ds_6_0",
        ShaderStage.Geometry => "gs_6_0",
        ShaderStage.Compute => "cs_6_0",
        ShaderStage.RayTracing => "lib_6_3",
        ShaderStage.Mesh => "ms_6_5",
        ShaderStage.Amplification => "as_6_5",
        _ => string.Empty,
    };
}
