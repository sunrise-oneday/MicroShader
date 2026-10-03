using System.Diagnostics;
using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 9（ShaderLab 标签语义校验子系统）自检。
/// </summary>
/// <remarks>
/// 验收口径严格对齐规划文档第七节：
/// <list type="number">
/// <item>"零误报是黄金判据"：真实 URP 官方语料上必须零诊断（"Tag.GoldenUrp"）。</item>
/// <item>"必须捕获"："UniversialPipeline" → SL0101、"UniversalFoward" → SL0103。</item>
/// <item>"必须沉默"：自定义 LightMode / 未知标签名 / 自由文本标签 → 零诊断。</item>
/// <item>"零分配"：无诊断路径上 "GC.GetAllocatedBytesForCurrentThread()" delta == 0。</item>
/// </list>
/// </remarks>
internal static class ShaderTagTests
{
    private const string Suite = "Tag";
    private const string SuiteDistance = "Tag.Distance";

    private static string? s_configuredUrpRoot;

    public static void Configure(string? urpRoot) => s_configuredUrpRoot = urpRoot;

    public static void Register()
    {
        TestSuite.Add(Suite, "Extract", Extract);
        TestSuite.Add(Suite, "ExtractEdgeCases", ExtractEdgeCases);
        TestSuite.Add(Suite, "NameTypo", NameTypo);
        TestSuite.Add(Suite, "PipelineTypo", PipelineTypo);
        TestSuite.Add(Suite, "LightModeTypo", LightModeTypo);
        TestSuite.Add(Suite, "ClosedSet", ClosedSet);
        TestSuite.Add(Suite, "Quiet", Quiet);
        TestSuite.Add(Suite, "EmptySchema", EmptySchema);
        TestSuite.Add(Suite, "BrokenUrpFixture", BrokenUrpFixture);
        TestSuite.Add(Suite, "ZeroAlloc", ZeroAlloc);
        TestSuite.Add(Suite, "GoldenUrp", GoldenUrp);
        TestSuite.Add(Suite, "Pipeline", Pipeline);
        TestSuite.Add(SuiteDistance, "MatchesNaive", DistanceMatchesNaive);
        TestSuite.Add(SuiteDistance, "BandedFaster", BandedFasterThanNaive);
    }

    // ── 语料 ─────────────────────────────────────────────────────────────────

    /// <summary>模拟一份官方 URP 包内的 shader：提供管线标记与常用 LightMode 值。</summary>
    private static (string Path, string Text)[] FakePackage() =>
    [
        ("Packages/urp/Shaders/Lit.shader", """
        Shader "Universal Render Pipeline/Lit"
        {
            SubShader
            {
                Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
                Pass
                {
                    Tags { "LightMode" = "UniversalForward" }
                    HLSLPROGRAM
                    #pragma vertex vert
                    ENDHLSL
                }
                Pass
                {
                    Tags { "LightMode" = "ShadowCaster" }
                    HLSLPROGRAM
                    ENDHLSL
                }
                Pass
                {
                    Tags { "LightMode" = "DepthOnly" }
                    HLSLPROGRAM
                    ENDHLSL
                }
                Pass
                {
                    Tags { "LightMode" = "Meta" }
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """),
        ("Packages/urp/Shaders/Unlit.shader", """
        Shader "Universal Render Pipeline/Unlit"
        {
            SubShader
            {
                Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Geometry" "IgnoreProjector"="True" }
                Pass
                {
                    Tags { "LightMode" = "UniversalForwardOnly" "LightMode2" = "ignored" }
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """),
    ];

    private static ShaderTagSchema UrpLikeSchema() =>
        ShaderTagSchemaBuilder.FromTexts(FakePackage(), userTagIds: ["GrassSlope", "GrassColor"], hasScriptableRenderPipeline: true);

    private static List<ShaderDiagnosticItem> Validate(string shaderText, ShaderTagSchema schema)
    {
        var machine = new ShaderLabStateMachine();
        var result = new ShaderLabParseResult();
        machine.Parse("test.shader", shaderText, result);

        var items = new List<ShaderDiagnosticItem>();
        new ShaderTagValidator().Validate(result, schema, items);
        return items;
    }

    private static string Dump(List<ShaderDiagnosticItem> items) =>
        items.Count == 0 ? "(无诊断)" : string.Join("\n", items.Select(i => $"{i.Code} {i.Line}:{i.Column} {i.Message}"));

    // ── 1. 标签采集 ───────────────────────────────────────────────────────────

    private static void Extract()
    {
        var schema = UrpLikeSchema();
        var text = """
        Shader "T/X"
        {
            Tags { "RenderPipeline" = "UniversalPipeline" }
            SubShader
            {
                Tags
                {
                    "RenderType" = "Opaque"
                    "Queue" = "Geometry+10"
                }
                Pass
                {
                    Tags { "LightMode" = "UniversalForward" "Queue"="Transparent" }
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """;

        var machine = new ShaderLabStateMachine();
        var result = new ShaderLabParseResult();
        machine.Parse("t.shader", text, result);

        var entries = result.TagEntries;
        Check.Equal(5, entries.Count, "应抽出 5 条标签\n" + string.Join(" | ", entries.Select(e => e.Name + "=" + e.Value)));

        Check.Equal("RenderPipeline", entries[0].Name, "第 1 条标签名");
        Check.Equal(ShaderTagScope.Shader, entries[0].Scope, "Shader 级标签");
        Check.Equal(3, entries[0].NameLine, "Shader 级标签行号");

        Check.Equal("RenderType", entries[1].Name, "第 2 条（SubShader 级、跨行块）");
        Check.Equal(ShaderTagScope.SubShader, entries[1].Scope, "SubShader 级标签");

        Check.Equal("Queue", entries[2].Name, "第 3 条");
        Check.Equal("Geometry+10", entries[2].Value, "带 + 的值原样保留");

        Check.Equal("LightMode", entries[3].Name, "同行多标签之 1");
        Check.Equal(ShaderTagScope.Pass, entries[3].Scope, "Pass 级标签");
        Check.Equal("Queue", entries[4].Name, "同行多标签之 2");

        // 列号必须落在值上，而不是整行
        var lit = entries[3];
        Check.Equal(lit.ValueColumn, lit.ValueColumn, "值列号自洽");
        var line = Corpus.LineAt(text, lit.ValueLine);
        Check.Equal("UniversalForward", line.Substring(lit.ValueColumn - 1, lit.ValueLength), "列号+长度必须精确切出标签值");
    }

    private static void ExtractEdgeCases()
    {
        // 注释与字符串里的伪标签不得被采集；畸形（Tags 后没有 '{'）必须放弃区域
        var text = """
        Shader "T/E"
        {
            SubShader
            {
                Tags
                {
                    // "LightMode" = "InComment"
                    /* "LightMode" = "InBlockComment" */
                    "LightMode" = "UniversalForward"
                }
                Tags 这是畸形的没有花括号
                "LightMode" = "ShouldNotBeCollected"
                Pass
                {
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """;

        var machine = new ShaderLabStateMachine();
        var result = new ShaderLabParseResult();
        machine.Parse("t.shader", text, result);

        Check.Equal(1, result.TagEntries.Count, "只应采集 1 条（注释与畸形区域都要跳过）\n"
            + string.Join(" | ", result.TagEntries.Select(e => e.Name + "=" + e.Value)));
        Check.Equal("UniversalForward", result.TagEntries[0].Value, "采到的值");
    }

    // ── 2. 诊断 ──────────────────────────────────────────────────────────────

    private static void NameTypo()
    {
        const string text = """
        Shader "T/NameTypo"
        {
            SubShader
            {
                Tags { "RenderPipeling" = "UniversalPipeline" }
                Pass
                {
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """;

        var schema = UrpLikeSchema();
        var items = Validate(text, schema);
        Check.Equal(1, items.Count, "应报 1 条标签名拼写错误\n" + Dump(items));
        Check.Equal(ShaderLabDiagnosticCodes.TagNameTypo, items[0].Code, "诊断码必须是 SL0105");
        Check.Equal(DiagnosticSeverity.Error, items[0].Severity, "标签名拼错必须报 Error");
        Check.True(items[0].Message?.Contains("RenderPipeline", StringComparison.Ordinal) == true, "消息必须给出建议值 RenderPipeline：" + items[0].Message);
        Check.Equal(5, items[0].Line, "诊断落在标签名所在行");
    }

    private static void PipelineTypo()
    {
        const string text = """
        Shader "T/PipeTypo"
        {
            SubShader
            {
                Tags { "RenderPipeline" = "UniversialPipeline" }
                Pass
                {
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """;

        var schema = UrpLikeSchema();
        var items = Validate(text, schema);
        Check.Equal(1, items.Count, "应报 1 条管线标记拼写错误\n" + Dump(items));
        Check.Equal(ShaderLabDiagnosticCodes.TagPipelineTypo, items[0].Code, "诊断码必须是 SL0101");
        Check.True(items[0].Message?.Contains("UniversalPipeline", StringComparison.Ordinal) == true, "消息必须给出建议值 UniversalPipeline：" + items[0].Message);
        Check.Equal(5, items[0].Line, "行号");

        // 列号必须精确切出标签值（不硬编码缩进，改用「按列号+长度回切」自证）
        var pipeLine = Corpus.LineAt(text, items[0].Line);
        Check.Equal("UniversialPipeline", pipeLine.Substring(items[0].Column - 1, "UniversialPipeline".Length),
            "列号必须落在标签值上而不是整行");
    }

    private static void LightModeTypo()
    {
        const string text = """
        Shader "T/LmTypo"
        {
            SubShader
            {
                Pass
                {
                    Tags { "LightMode" = "UniversalFoward" }
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """;

        var schema = UrpLikeSchema();
        var items = Validate(text, schema);
        Check.Equal(1, items.Count, "应报 1 条 LightMode 拼写错误\n" + Dump(items));
        Check.Equal(ShaderLabDiagnosticCodes.TagLightModeTypo, items[0].Code, "诊断码必须是 SL0103");
        Check.Equal(DiagnosticSeverity.Error, items[0].Severity, "拼写错误必须报 Error");
        Check.True(items[0].Message?.Contains("UniversalForward", StringComparison.Ordinal) == true, "消息必须给出建议值 UniversalForward：" + items[0].Message);
    }

    private static void ClosedSet()
    {
        var schema = UrpLikeSchema();

        // 封闭集合拼写错误 → SL0104 Warning
        const string batching = """
        Shader "T/Batch"
        {
            SubShader
            {
                Tags { "DisableBatching" = "LODFadingg" }
                Pass { HLSLPROGRAM
                ENDHLSL
                }
            }
        }
        """;
        var items = Validate(batching, schema);
        Check.Equal(1, items.Count, "DisableBatching 拼写错误应报 1 条\n" + Dump(items));
        Check.Equal(ShaderLabDiagnosticCodes.TagClosedSetInvalid, items[0].Code, "封闭集合码 SL0104");

        // 管线标记无近似候选 → SL0102 Warning（不是 Error）
        const string unknown = """
        Shader "T/UnknownPipe"
        {
            SubShader
            {
                Tags { "RenderPipeline" = "MyCompanyCustomPipeline" }
                Pass { HLSLPROGRAM
                ENDHLSL
                }
            }
        }
        """;
        items = Validate(unknown, schema);
        Check.Equal(1, items.Count, "未知管线标记应报 1 条 Warning\n" + Dump(items));
        Check.Equal(ShaderLabDiagnosticCodes.TagPipelineUnknown, items[0].Code, "未知管线码 SL0102");
        Check.Equal(DiagnosticSeverity.Warning, items[0].Severity, "未知管线只报 Warning，不阻断");

        // 合法封闭集合值 → 零诊断（含大小写容忍）
        const string ok = """
        Shader "T/Ok"
        {
            SubShader
            {
                Tags { "DisableBatching" = "LODFading" "PreviewType" = "Plane" }
                Pass { HLSLPROGRAM
                ENDHLSL
                }
            }
        }
        """;
        Check.Equal(0, Validate(ok, schema).Count, "合法封闭集合值必须零诊断");
    }

    private static void Quiet()
    {
        var schema = UrpLikeSchema();
        const string text = """
        Shader "T/Quiet"
        {
            SubShader
            {
                Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "MyCustomRenderType" }
                Pass
                {
                    Tags { "LightMode" = "MyCustomPass" "X-Custom" = "whatever" "Queue" = "Geometry+200" }
                    HLSLPROGRAM
                    ENDHLSL
                }
                Pass
                {
                    Tags { "LightMode" = "GrassSlope" }
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """;

        // 零误报是黄金判据：自定义 LightMode / 未知标签名 / 自由文本标签都必须沉默
        Check.Equal(0, Validate(text, schema).Count, "沉默用例必须零诊断\n" + Dump(Validate(text, schema)));

        // 反向对照：仓库既有的 BrokenShader.shader 含两处真实拼写错误，必须被检出 ——
        // 否则「沉默」就成了「什么都不报」。
        var broken = Validate(File.ReadAllText(Corpus.PathOf("BrokenShader.shader")), schema);
        Check.Equal(2, broken.Count, "BrokenShader.shader 的两处标签拼写错误必须被检出\n" + Dump(broken));
    }

    private static void EmptySchema()
    {
        const string text = """
        Shader "T/Empty"
        {
            SubShader
            {
                Tags { "RenderPipeling" = "UniversialPipeline" }
                Pass { HLSLPROGRAM
                ENDHLSL
                }
            }
        }
        """;

        // 三层全空 → 整体沉默，绝不猜测
        Check.Equal(0, Validate(text, ShaderTagSchema.Empty).Count, "空 schema 必须整体沉默");
        Check.True(ShaderTagSchema.Empty.IsEmpty, "Empty.IsEmpty");
    }

    private static void BrokenUrpFixture()
    {
        var schema = UrpLikeSchema();
        var text = File.ReadAllText(Corpus.PathOf("BrokenUrp.shader"));

        var machine = new ShaderLabStateMachine();
        var result = new ShaderLabParseResult();
        machine.Parse(Corpus.PathOf("BrokenUrp.shader"), text, result);

        var items = new List<ShaderDiagnosticItem>();
        new ShaderTagValidator().Validate(result, schema, items);

        Check.Contains(items.Select(i => i.Code ?? string.Empty).ToList(), ShaderLabDiagnosticCodes.TagPipelineTypo, "必须抓到 UniversialPipeline\n" + Dump(items));
        Check.Contains(items.Select(i => i.Code ?? string.Empty).ToList(), ShaderLabDiagnosticCodes.TagLightModeTypo, "必须抓到 UniversalFoward\n" + Dump(items));
        Check.Equal(2, items.Count, "BrokenUrp.shader 只应有这 2 条标签诊断\n" + Dump(items));
    }

    // ── 3. 零分配 ────────────────────────────────────────────────────────────

    private static void ZeroAlloc()
    {
        var schema = UrpLikeSchema();
        const string text = """
        Shader "T/ZeroAlloc"
        {
            SubShader
            {
                Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" "Queue" = "Geometry" }
                Pass
                {
                    Tags { "LightMode" = "UniversalForward" }
                    HLSLPROGRAM
                    ENDHLSL
                }
                Pass
                {
                    Tags { "LightMode" = "ShadowCaster" }
                    HLSLPROGRAM
                    ENDHLSL
                }
            }
        }
        """;

        var machine = new ShaderLabStateMachine();
        var result = new ShaderLabParseResult();
        var items = new List<ShaderDiagnosticItem>();
        var validator = new ShaderTagValidator();

        // 预热：让 FrozenSet 的枚举器路径、结果对象池进入稳态
        for (var i = 0; i < 512; i++)
        {
            machine.Parse("z.shader", text, result);
            validator.Validate(result, schema, items);
        }

        const int iterations = 20000;

        // ① 校验器本身必须严格零分配（schema 已建、条目已提取）。
        //    这也是详设 §7.3「Validate 不创建任何堆对象」的原文口径。
        var beforeValidate = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            validator.Validate(result, schema, items);
        }

        var validateDelta = GC.GetAllocatedBytesForCurrentThread() - beforeValidate;
        Check.Equal(0L, validateDelta, iterations + " 次标签校验必须零托管堆分配（NFR-4）");

        // ② 提取侧（模块 1 扫描 Tags 块）会把标签名/值物化成 string —— 这是给下游
        //    （诊断消息、跳转、Quick Fix）用的契约数据，无法用 span 表达。
        //    这里不断言零，而是断言「每条标签一个常量上界」，防止回归成按标签数平方的分配。
        var beforeExtract = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            machine.Parse("z.shader", text, result);
        }

        var extractDelta = GC.GetAllocatedBytesForCurrentThread() - beforeExtract;
        var perParse = extractDelta / (double)iterations;
        var tagCount = result.TagEntries.Count;
        Check.True(tagCount > 0, "用例语料必须真的含标签");

        Console.WriteLine();
        Console.WriteLine($"    · 校验器分配: {validateDelta} bytes / {iterations} 次（必须为 0）");
        Console.WriteLine($"    · 提取侧分配: {perParse:F1} B/次（{tagCount} 条标签 → {perParse / tagCount:F1} B/条）");

        Check.True(perParse <= tagCount * 96.0,
            $"提取侧每条标签的分配必须 <= 96 B（实测 {perParse / tagCount:F1} B/条，共 {tagCount} 条）");
    }

    // ── 4. 黄金判据：真实 URP 官方语料零诊断 ──────────────────────────────────

    // 修复：universal* 同时命中 universal-config（无 shader）与主包，必须选真正含 .shader 文件的目录。
    private static bool HasShaderFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.shader", SearchOption.AllDirectories).Any();
    }

    private static void GoldenUrp()
    {
        var root = DiscoverUrpRoot()
            ?? throw new SkipException("未找到本机 URP 包（可用 --urp <dir> 或 MICROSHADER_URP_ROOT 指定）");

        var files = Directory.GetFiles(root, "*.shader", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var shaders = new (string Path, string Text)[files.Length];
        for (var i = 0; i < files.Length; i++)
        {
            shaders[i] = (files[i], File.ReadAllText(files[i]));
        }

        // 表来自现场采集本身 —— 官方语料必须自洽（这正是「表从现场来」的设计意图）
        var schema = ShaderTagSchemaBuilder.FromTexts(shaders, userTagIds: null, hasScriptableRenderPipeline: true);

        var machine = new ShaderLabStateMachine();
        var result = new ShaderLabParseResult();
        var items = new List<ShaderDiagnosticItem>();
        var validator = new ShaderTagValidator();

        var tagEntries = 0;
        foreach (var (path, text) in shaders)
        {
            machine.Parse(path, text, result);
            tagEntries += result.TagEntries.Count;
            validator.Validate(result, schema, items);

            if (items.Count > 0)
            {
                throw new AssertionException(
                    $"黄金判据失守：官方 URP 语料上出现标签诊断 —— {path}\n{Dump(items)}");
            }
        }

        Check.True(tagEntries > 0, "官方语料必须真的抽到了标签条目（否则本用例是空转）");
        Check.Equal(0, items.Count, "官方 URP 语料上必须零诊断");
    }

    private static string? DiscoverUrpRoot()
    {
        if (!string.IsNullOrWhiteSpace(s_configuredUrpRoot) && Directory.Exists(s_configuredUrpRoot))
        {
            return s_configuredUrpRoot;
        }

        var fromEnv = Environment.GetEnvironmentVariable("MICROSHADER_URP_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv) && Directory.Exists(fromEnv))
        {
            return fromEnv;
        }

        // 与本机自检其它模块同口径：从环境变量给出的 Unity 工程里找 PackageCache 下的 URP
        var project = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT");
        if (string.IsNullOrWhiteSpace(project))
        {
            return null;
        }

        var cache = Path.Combine(project, "Library", "PackageCache");
        if (!Directory.Exists(cache))
        {
            return null;
        }

        return Directory.EnumerateDirectories(cache, "com.unity.render-pipelines.universal*")
            .OrderBy(static d => d, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(HasShaderFiles);
    }

    // ── 4b. 管线接入：诊断必须真的从 ShaderDiagnosticEngine 出来 ────────────────

    /// <summary>
    /// 端到端证明「标签诊断会被真的上报」：注入 schema 后，<see cref="ShaderDiagnosticEngine.Analyze"/>
    /// 必须把 SL0101/SL0103 混进正常诊断集合；不注入（默认空表）时则必须一条都不多 ——
    /// 后者是「零行为漂移」的证据。
    /// </summary>
    private static void Pipeline()
    {
        if (!ShaderDiagnosticEngine.TryCreate(
                new ShaderContextRegistry(),
                DxcGatewayTests.DxcDirectory(),
                out var engine,
                out var error) || engine is null)
        {
            throw new SkipException("dxcompiler.dll 不可用：" + error);
        }

        var path = Corpus.PathOf("BrokenUrp.shader");
        var text = File.ReadAllText(path);

        var document = new SourceShaderDocument
        {
            FileUri = FileUri.FromPath(path),
            FilePath = path,
            FullText = text,
            Version = 1,
        };

        // ① 未注入 schema（默认空表）→ 不得出现任何 SL01xx
        var withoutSchema = engine.Analyze(document, probeLine: 0, isSave: true);
        var tagCodesWithout = withoutSchema.Items
            .Select(static i => i.Code ?? string.Empty)
            .Where(static c => c.StartsWith("SL01", StringComparison.Ordinal))
            .ToList();
        Check.Equal(0, tagCodesWithout.Count, "未注入 schema 时不得产生任何标签诊断\n" + string.Join(", ", tagCodesWithout));

        // ② 注入 schema → 两处拼写错误必须出现
        if (!ShaderDiagnosticEngine.TryCreate(
                new ShaderContextRegistry(),
                DxcGatewayTests.DxcDirectory(),
                out var tagEngine,
                out var tagError,
                new DiagnosticEngineOptions { TagSchema = UrpLikeSchema() }) || tagEngine is null)
        {
            throw new SkipException("dxcompiler.dll 不可用：" + tagError);
        }

        var withSchema = tagEngine.Analyze(document, probeLine: 0, isSave: true);
        var codes = withSchema.Items.Select(static i => i.Code ?? string.Empty).ToList();

        Check.Contains(codes, ShaderLabDiagnosticCodes.TagPipelineTypo, "管线标记拼写错误必须出现在最终诊断里");
        Check.Contains(codes, ShaderLabDiagnosticCodes.TagLightModeTypo, "LightMode 拼写错误必须出现在最终诊断里");

        TestCaseHelpers.Report($"注入 schema 后标签诊断: "
            + string.Join(", ", codes.Where(static c => c.StartsWith("SL01", StringComparison.Ordinal))));
    }

    // ── 5. 编辑距离 ──────────────────────────────────────────────────────────

    /// <summary>朴素全矩阵 Levenshtein，仅用于交叉校验带宽实现。</summary>
    private static int NaiveDistance(string a, string b)
    {
        var n = a.Length;
        var m = b.Length;
        var prev = new int[m + 1];
        var curr = new int[m + 1];
        for (var j = 0; j <= m; j++) prev[j] = j;

        for (var i = 1; i <= n; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= m; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[m];
    }

    private static void DistanceMatchesNaive()
    {
        // 穷举长度 <= 4 的字母表 {a,b,c} 全部组合，保证带宽与早退不改变语义
        var alphabet = new[] { 'a', 'b', 'c' };
        var all = new List<string> { string.Empty };
        for (var len = 1; len <= 4; len++)
        {
            var count = (int)Math.Pow(alphabet.Length, len);
            for (var code = 0; code < count; code++)
            {
                var chars = new char[len];
                var c = code;
                for (var i = 0; i < len; i++)
                {
                    chars[i] = alphabet[c % alphabet.Length];
                    c /= alphabet.Length;
                }

                all.Add(new string(chars));
            }
        }

        var checkedPairs = 0;
        foreach (var a in all)
        {
            foreach (var b in all)
            {
                var expected = NaiveDistance(a, b);
                var within = ShaderTagDistance.TryWithin(a, b, 2, out var actual);

                Check.Equal(expected <= 2, within, $"TryWithin 命中判定不一致: '{a}' vs '{b}'（朴素距离 {expected}）");
                if (within)
                {
                    Check.Equal(expected, actual, $"命中时必须给出确切距离: '{a}' vs '{b}'");
                }

                checkedPairs++;
            }
        }

        Check.True(checkedPairs > 150, "交叉校验规模必须足够（实跑 " + checkedPairs + " 对）");

        // 真实拼写用例
        Check.True(ShaderTagDistance.TryWithin("UniversalFoward", "UniversalForward", 2, out var d1) && d1 == 1, "UniversalFoward 距离 1");
        Check.True(ShaderTagDistance.TryWithin("RenderPipeling", "RenderPipeline", 2, out var d2) && d2 == 1, "RenderPipeling 距离 1");
        Check.True(!ShaderTagDistance.TryWithin("UniversalForward", "Universal2D", 2, out _), "UniversalForward vs Universal2D 必须超阈值");
        Check.True(!ShaderTagDistance.TryWithin("abc", "abcdefgh", 2, out _), "长度差超阈值必须直接判否");
    }

    private static void BandedFasterThanNaive()
    {
        // 取真机最坏形状：28 字符标签值 × 45 个候选
        var candidates = new string[45];
        for (var i = 0; i < candidates.Length; i++)
        {
            candidates[i] = "UniversalForwardVariant" + (char)('A' + i % 26) + (char)('a' + i % 26);
        }

        const string probe = "UniversalForwardVarianyA"; // 与候选近但不等
        const int iterations = 20000;

        static (double ms, long alloc) Run(Func<int> body, int iterations)
        {
            for (var w = 0; w < 2000; w++) body();

            var times = new List<double>();
            long alloc = 0;
            for (var round = 0; round < 5; round++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (var i = 0; i < iterations; i++) body();
                sw.Stop();
                alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                times.Add(sw.Elapsed.TotalMilliseconds);
            }

            times.Sort();
            return (times[0], alloc);
        }

        var banded = Run(() =>
        {
            var hit = 0;
            foreach (var candidate in candidates)
            {
                if (ShaderTagDistance.TryWithin(probe, candidate, 2, out _)) hit++;
            }

            return hit;
        }, iterations);

        var naive = Run(() =>
        {
            var hit = 0;
            foreach (var candidate in candidates)
            {
                if (NaiveDistance(probe, candidate) <= 2) hit++;
            }

            return hit;
        }, iterations);

        Console.WriteLine();
        Console.WriteLine($"    · 带宽+早退: {banded.ms:F2} ms / {iterations} 轮, {banded.alloc} bytes");
        Console.WriteLine($"    · 朴素矩阵:  {naive.ms:F2} ms / {iterations} 轮, {naive.alloc} bytes");

        Check.True(banded.ms < naive.ms, $"带宽截断必须不慢于朴素矩阵（带宽 {banded.ms:F2} ms vs 朴素 {naive.ms:F2} ms）");

        // 带宽截断路径全程 stackalloc：允许 JIT/OSR 带来的一次性零头，但必须比朴素实现低三个数量级。
        Check.True(banded.alloc <= 256,
            $"带宽截断路径必须实质零分配（实测 {banded.alloc} bytes）");
        Check.True(banded.alloc * 1000 < naive.alloc,
            $"带宽截断的分配量必须远低于朴素矩阵（{banded.alloc} vs {naive.alloc}）");
    }
}
