using System.Collections.Frozen;

namespace MicroShader.ShaderLab;

/// <summary>快照表的值来源（用于诊断文案与自检断言）。</summary>
public enum ShaderTagSchemaProvenance
{
    /// <summary>三层全空 —— 校验器必须整体沉默。</summary>
    None = 0,

    /// <summary>全部来自现场包扫描（最高权威）。</summary>
    HarvestedFromPackages = 1,

    /// <summary>只有内置表（没有扫描到任何包）。</summary>
    BuiltInTable = 2,

    /// <summary>现场采集 ∪ 内置表。</summary>
    Mixed = 3,
}

/// <summary>
/// 标签语义校验的判定表（不可变快照，由 <see cref="ShaderTagSchemaBuilder"/> 从现场采集 + 内置表合成）。
/// </summary>
/// <remarks>
/// "为什么用 <see cref="FrozenSet{T}"/>/<see cref="FrozenDictionary{TKey,TValue}"/>"：
/// 既拿到 O(1) 查找，又拿到结构化的枚举器（"foreach" 不装箱、零分配）——
/// 校验器要在候选集合上跑距离比较，若暴露成 "IReadOnlySet&lt;string&gt;" 会因接口枚举器装箱而每次分配。
/// "大小写口径"：标签名用 <see cref="StringComparer.Ordinal"/>（名字写错大小写本身就是拼写错误，
/// 要能被 SL0105 抓到）；标签值集合用 <see cref="StringComparer.OrdinalIgnoreCase"/>（Unity 侧对值的
/// 大小写容忍度不明确，按「零误报优先」一律放宽）。
/// </remarks>
public sealed class ShaderTagSchema
{
    /// <summary>所有已知标签名的并集（现场采集 ∪ 内置封闭集合名）。</summary>
    public required FrozenSet<string> AllKnownTagNames { get; init; }

    /// <summary>封闭集合标签：标签名 → 合法值集合（大小写不敏感）。</summary>
    public required FrozenDictionary<string, FrozenSet<string>> ClosedSets { get; init; }

    /// <summary>
    /// "LightMode" 合法值集合。LightMode "不是"封闭集合 —— 用户可以自定义，
    /// 因此只有「精确不等但近似命中」才报（SL0103），其余一律沉默。
    /// </summary>
    public required FrozenSet<string> LightModeValues { get; init; }

    /// <summary>现场是否检测到 SRP 包（决定是否把内置管线 LightMode 纳入合法集合）。</summary>
    public required bool HasScriptableRenderPipeline { get; init; }

    /// <summary>表来源。</summary>
    public required ShaderTagSchemaProvenance Provenance { get; init; }

    /// <summary>参与采集的 shader 文件数。</summary>
    public required int SourceFileCount { get; init; }

    /// <summary>快照构建时间。</summary>
    public required DateTimeOffset BuiltAt { get; init; }

    /// <summary>空快照：三层全空 → 校验器整体沉默。</summary>
    public static ShaderTagSchema Empty { get; } = CreateEmpty();

    /// <summary>是否为空表（此时任何标签都不校验）。</summary>
    public bool IsEmpty => AllKnownTagNames.Count == 0;

    private static ShaderTagSchema CreateEmpty() => new()
    {
        AllKnownTagNames = FrozenSet<string>.Empty,
        ClosedSets = FrozenDictionary<string, FrozenSet<string>>.Empty,
        LightModeValues = FrozenSet<string>.Empty,
        HasScriptableRenderPipeline = false,
        Provenance = ShaderTagSchemaProvenance.None,
        SourceFileCount = 0,
        BuiltAt = DateTimeOffset.UnixEpoch,
    };
}
