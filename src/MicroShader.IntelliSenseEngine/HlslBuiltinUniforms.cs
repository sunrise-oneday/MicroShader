using System.Collections.Frozen;

namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// Unity "引擎内置"着色器变量表：名字 → 类型。
/// </summary>
/// <remarks>
/// "为什么需要它"：真实语料验收（4653 个补全点）显示，未命中里有相当一部分是
/// "_Time.y" / "_WorldSpaceCameraPos.x" / "unity_ObjectToWorld._m00" 这类
/// "引擎内置 uniform 的成员访问"。它们不属于本文件、也不在任何 include 链里 ——
/// 是引擎在编译时注入的，所以「本文件索引 + include 链预热」两条路都覆盖不到：
/// 基变量 "_Time" 压根没有声明，成员补全自然降级为空。
/// "法律边界（详设 v2.0 第 1 条）"：本表只含 "Unity 引擎层"文档化的内置着色器变量
/// （Built-in shader variables / 内置矩阵族），"不含"任何 URP 源码符号 ——
/// 与 <see cref="HlslBuiltins"/> 里已有的 "TRANSFORM_TEX" / "SAMPLE_TEXTURE2D" 属同一类。
/// URP 自己的 uniform（"_MainLightPosition" 之类）仍由现场包符号采集负责。
/// "命中优先级"：本文件声明 &gt; include 链预热 &gt; "本表" &gt; 内建向量族。
/// 作者若真的同名声明了 "_Time"，以他的为准（本表只在查不到声明时才兜底）。
/// </remarks>
internal static class HlslBuiltinUniforms
{
    /// <summary>名字 → 类型。类型只需能喂给 <see cref="HlslTypes.TryParseShape"/> 或结构体表。</summary>
    private static readonly (string Name, string Type)[] Table =
    [
        // ── 时间（Built-in shader variables / Time）──
        ("_Time", "float4"),
        ("_SinTime", "float4"),
        ("_CosTime", "float4"),
        ("_TimeParameters", "float4"),
        ("_DeltaTime", "float"),
        ("_LastTime", "float4"),

        // ── 相机与屏幕 ──
        ("_WorldSpaceCameraPos", "float3"),
        ("_ProjectionParams", "float4"),
        ("_ScreenParams", "float4"),
        ("_ZBufferParams", "float4"),
        ("unity_OrthoParams", "float4"),
        ("unity_CameraProjection", "float4x4"),
        ("unity_CameraInvProjection", "float4x4"),
        ("unity_CameraWorldClipPlanes", "float4"),

        // ── 内置矩阵族（引擎注入 + UNITY_MATRIX_* 宏）──
        ("unity_ObjectToWorld", "float4x4"),
        ("unity_WorldToObject", "float4x4"),
        ("unity_MatrixVP", "float4x4"),
        ("unity_MatrixV", "float4x4"),
        ("unity_MatrixInvV", "float4x4"),
        ("unity_MatrixInvP", "float4x4"),
        ("unity_WorldTransformParams", "float4"),
        ("UNITY_MATRIX_M", "float4x4"),
        ("UNITY_MATRIX_I_M", "float4x4"),
        ("UNITY_MATRIX_V", "float4x4"),
        ("UNITY_MATRIX_I_V", "float4x4"),
        ("UNITY_MATRIX_P", "float4x4"),
        ("UNITY_MATRIX_VP", "float4x4"),
        ("UNITY_MATRIX_I_VP", "float4x4"),
        ("UNITY_MATRIX_MVP", "float4x4"),
        ("UNITY_MATRIX_T_MV", "float4x4"),
        ("UNITY_MATRIX_IT_MV", "float4x4"),

        // ── 光照探针 / 光照贴图 ──
        ("unity_SHAr", "float4"),
        ("unity_SHAg", "float4"),
        ("unity_SHAb", "float4"),
        ("unity_SHBr", "float4"),
        ("unity_SHBg", "float4"),
        ("unity_SHBb", "float4"),
        ("unity_SHC", "float4"),
        ("unity_ProbesOcclusion", "float4"),
        ("unity_LightmapST", "float4"),
        ("unity_DynamicLightmapST", "float4"),
        ("unity_SpecCube0_HDR", "float4"),
        ("unity_SpecCube1_HDR", "float4"),
        ("unity_SpecCube0_BoxMin", "float4"),
        ("unity_SpecCube0_BoxMax", "float4"),
        ("unity_SpecCube0_ProbePosition", "float4"),

        // ── 雾 / LOD / 实例化 ──
        ("unity_FogColor", "half4"),
        ("unity_FogParams", "float4"),
        ("unity_LODFade", "float4"),
        ("unity_InstanceID", "uint"),
    ];

    private static readonly FrozenDictionary<string, string> ByName =
        Table.ToFrozenDictionary(static e => e.Name, static e => e.Type, StringComparer.Ordinal);

    private static readonly string[] SortedNames =
        [.. Table.Select(static e => e.Name).OrderBy(static n => n, StringComparer.Ordinal)];

    /// <summary>查内置 uniform 的类型。</summary>
    public static bool TryGetType(string name, out string type) => ByName.TryGetValue(name, out type!);

    /// <summary>取内置 uniform 的类型（调用方已确认存在时使用）。</summary>
    public static string TypeOf(string name) => ByName.TryGetValue(name, out var type) ? type : string.Empty;

    /// <summary>全部内置 uniform 名（按 Ordinal 升序，保证补全输出稳定）。</summary>
    public static IReadOnlyList<string> SortedAll() => SortedNames;
}
