using System.Collections.Frozen;
using MicroShader.Domain;

namespace MicroShader.ShaderLab;

/// <summary>
/// 从现场（已安装的包 + 用户工程）采集标签合法值，构建 <see cref="ShaderTagSchema"/> 快照。
/// </summary>
/// <remarks>
/// "为什么表是采集来的而不是写死的"：URP 14 与 HDRP 14 的 LightMode 值集合差异显著，
/// 且同一工程可以同时装两条管线。写死的表会随 Unity 版本演进而过时，并把没见过的合法值判成非法
/// —— 直接违反「零误报优先」。扫一遍现场包就是一份与版本精确对齐的权威表。
/// "三层来源"：
/// ① 包内 ".shader" 的 Tags（标签名与值都采集）；
/// ② 用户工程 C# 里的 "new ShaderTagId("…")"（自定义 LightMode 只能从这里知道）；
/// ③ Unity 文档确证的封闭集合（"PreviewType" / "DisableBatching" / 布尔类标签）。
/// 这是一个一次性的冷启动工作，与 VFS 索引同批进行，不参与任何热路径。
/// </remarks>
public static class ShaderTagSchemaBuilder
{
    /// <summary>Unity 文档确证的管线标记（第 2 层）。</summary>
    private static readonly string[] BuiltInPipelineValues = ["UniversalPipeline", "HDRenderPipeline"];

    /// <summary>内置管线的经典 LightMode（第 3 层）——仅当工程不含 SRP 包时启用。</summary>
    private static readonly string[] BuiltInPipelineLightModes =
    [
        "Always", "ForwardBase", "ForwardAdd", "Deferred",
        "ShadowCaster", "MotionVectors", "Vertex", "VertexLMRGBM", "VertexLM",
    ];

    private static readonly string[] BooleanValues = ["True", "False"];

    /// <summary>Unity 文档确证的 "DisableBatching" 取值。</summary>
    private static readonly string[] DisableBatchingValues = ["True", "False", "LODFading"];

    /// <summary>Unity 文档确证的 "PreviewType" 取值。</summary>
    private static readonly string[] PreviewTypeValues = ["Plane", "Sphere", "Skybox", "Cube"];

    /// <summary>无论现场采集到什么，都视为「已知标签名」的固定集合（封闭集合 + 自由文本标签）。</summary>
    private static readonly string[] BuiltInTagNames =
    [
        ShaderTagNames.LightMode, ShaderTagNames.RenderPipeline, ShaderTagNames.RenderType, ShaderTagNames.Queue,
        ShaderTagNames.DisableBatching, ShaderTagNames.PreviewType, ShaderTagNames.IgnoreProjector,
        ShaderTagNames.ForceNoShadowCasting, ShaderTagNames.ShaderGraphTargetId,
    ];

    /// <summary>
    /// 从文件系统构建快照：递归扫描 <paramref name="shaderRoots"/> 下的 ".shader"，
    /// 并从 <paramref name="userCodeRoots"/> 下的 ".cs" 提取 "ShaderTagId" 定义。
    /// </summary>
    /// <param name="hasScriptableRenderPipeline">工程是否装了 SRP 包（为 false 时启用第 3 层内置管线 LightMode）。</param>
    public static ShaderTagSchema Build(
        IReadOnlyList<string> shaderRoots,
        IReadOnlyList<string> userCodeRoots,
        bool hasScriptableRenderPipeline,
        Action<int, string>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(shaderRoots);
        ArgumentNullException.ThrowIfNull(userCodeRoots);

        var shaders = new List<(string Path, string Text)>();
        var visited = 0;

        foreach (var root in shaderRoots)
        {
            foreach (var file in SafeEnumerate(root, "*.shader"))
            {
                if (TryReadAllText(file, out var text))
                {
                    shaders.Add((file, text));
                    onProgress?.Invoke(++visited, file);
                }
            }
        }

        var tagIds = new List<string>();
        foreach (var root in userCodeRoots)
        {
            foreach (var file in SafeEnumerate(root, "*.cs"))
            {
                if (TryReadAllText(file, out var text))
                {
                    HarvestShaderTagIds(text, tagIds);
                }
            }
        }

        return FromTexts(shaders, tagIds, hasScriptableRenderPipeline);
    }

    /// <summary>
    /// 从内存文本构建快照（自检与单元测试用；与文件版本共用同一套合成逻辑）。
    /// </summary>
    public static ShaderTagSchema FromTexts(
        IEnumerable<(string Path, string Text)> shaders,
        IEnumerable<string>? userTagIds,
        bool hasScriptableRenderPipeline)
    {
        ArgumentNullException.ThrowIfNull(shaders);

        var tagNames = new HashSet<string>(StringComparer.Ordinal);
        var lightMode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pipeline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disablesBatching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var previewType = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ignoreProjector = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var forceNoShadow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sourceFiles = 0;

        var machine = new ShaderLabStateMachine();
        var result = new ShaderLabParseResult();

        foreach (var (path, text) in shaders)
        {
            sourceFiles++;
            machine.Parse(path, text, result);

            foreach (var entry in result.TagEntries)
            {
                tagNames.Add(entry.Name);

                switch (entry.Name)
                {
                    case ShaderTagNames.LightMode:
                        lightMode.Add(entry.Value);
                        break;
                    case ShaderTagNames.RenderPipeline:
                        pipeline.Add(entry.Value);
                        break;
                    case ShaderTagNames.DisableBatching:
                        disablesBatching.Add(entry.Value);
                        break;
                    case ShaderTagNames.PreviewType:
                        previewType.Add(entry.Value);
                        break;
                    case ShaderTagNames.IgnoreProjector:
                        ignoreProjector.Add(entry.Value);
                        break;
                    case ShaderTagNames.ForceNoShadowCasting:
                        forceNoShadow.Add(entry.Value);
                        break;
                }
            }
        }

        // 第 1.5 层：用户工程 C# 里定义的 ShaderTagId —— 自定义 LightMode 的唯一权威来源。
        if (userTagIds is not null)
        {
            foreach (var id in userTagIds)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    lightMode.Add(id);
                }
            }
        }

        var harvestedScriptablePipeline = pipeline.Count > 0 || hasScriptableRenderPipeline;

        // 第 2 层：管线标记的内置兜底。
        foreach (var value in BuiltInPipelineValues)
        {
            pipeline.Add(value);
        }

        // 第 3 层：仅当工程不含 SRP 包时才把内置管线 LightMode 算作合法。
        if (!harvestedScriptablePipeline)
        {
            foreach (var value in BuiltInPipelineLightModes)
            {
                lightMode.Add(value);
            }
        }

        foreach (var name in BuiltInTagNames)
        {
            tagNames.Add(name);
        }

        // 文档确证的封闭集合：内置取值必须无条件并入。现场采集只可能「补全」这些集合，
        // 若某个取值在本工程里恰好没出现（例如没人写 PreviewType），缺了它就会把合法值判非法。
        var closedSets = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            [ShaderTagNames.RenderPipeline] = pipeline.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            [ShaderTagNames.PreviewType] = Union(previewType, PreviewTypeValues),
            [ShaderTagNames.IgnoreProjector] = Union(ignoreProjector, BooleanValues),
            [ShaderTagNames.ForceNoShadowCasting] = Union(forceNoShadow, BooleanValues),
            [ShaderTagNames.DisableBatching] = Union(disablesBatching, DisableBatchingValues),
        };

        var provenance = sourceFiles == 0
            ? ShaderTagSchemaProvenance.BuiltInTable
            : harvestedScriptablePipeline
                ? ShaderTagSchemaProvenance.Mixed
                : ShaderTagSchemaProvenance.HarvestedFromPackages;

        return new ShaderTagSchema
        {
            AllKnownTagNames = tagNames.ToFrozenSet(StringComparer.Ordinal),
            ClosedSets = closedSets.ToFrozenDictionary(StringComparer.Ordinal),
            LightModeValues = lightMode.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            HasScriptableRenderPipeline = harvestedScriptablePipeline,
            Provenance = provenance,
            SourceFileCount = sourceFiles,
            BuiltAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// 从 C# 文本里抽取 "new ShaderTagId("…")" 的字面量。
    /// </summary>
    /// <remarks>
    /// 刻意用 span 扫描而不是正则：正则对象在 AOT 下的行为与分配都更难控，
    /// 而这里要识别的模式只有「标识符 ShaderTagId + 左括号 + 字符串」一种。
    /// </remarks>
    public static void HarvestShaderTagIds(string csText, List<string> into)
    {
        ArgumentNullException.ThrowIfNull(csText);
        ArgumentNullException.ThrowIfNull(into);

        const string marker = "ShaderTagId";
        var span = csText.AsSpan();
        var pos = 0;

        while (pos < span.Length)
        {
            var at = span[pos..].IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                break;
            }

            var i = pos + at + marker.Length;

            // 跳过空白后必须紧跟 '('
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            if (i >= span.Length || span[i] != '(')
            {
                pos = pos + at + marker.Length;
                continue;
            }

            i++;
            while (i < span.Length && char.IsWhiteSpace(span[i])) i++;
            if (i >= span.Length || span[i] != '"')
            {
                pos = i;
                continue;
            }

            var start = i + 1;
            var end = span[start..].IndexOf('"');
            if (end < 0)
            {
                break;
            }

            var value = span.Slice(start, end);
            if (!value.IsEmpty)
            {
                into.Add(value.ToString());
            }

            pos = start + end + 1;
        }
    }

    /// <summary>现场采集集合 ∪ 内置取值（大小写不敏感）。</summary>
    private static FrozenSet<string> Union(HashSet<string> harvested, string[] builtIn)
    {
        var merged = new HashSet<string>(harvested, StringComparer.OrdinalIgnoreCase);
        foreach (var value in builtIn)
        {
            merged.Add(value);
        }

        return merged.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            yield break;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }
    }

    private static bool TryReadAllText(string path, out string text)
    {
        try
        {
            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 单个文件读失败不致命：跳过它，继续采集其余文件（不因一个坏文件整体沉默）。
            text = string.Empty;
            return false;
        }
    }
}
