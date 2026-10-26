using System.Diagnostics;
using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 3 的真机语料验收：拿真实 Unity 工程里的 URP 14.0.12 全量 .shader 当靶子，
/// 组装成可编译文本并逐条验证 ADR-023 的行映射不变量。
/// </summary>
/// <remarks>
/// 固定用例只能证明「按设计跑」；真实语料才能证明「设计覆盖了现实」——
/// 例如 URP 里 HLSLINCLUDE 放在第一个 SubShader 之前的顶层、10 处 include_with_pragmas、
/// 以及 700+ 条横跨 Packages/ 与相对路径的 include。
/// </remarks>
internal static class AssemblerRealWorldTests
{
    private static string? _projectRoot;
    private static VfsIndex? _index;
    private static string[]? _shaderFiles;

    public static void Configure(string? explicitRoot) => _projectRoot = explicitRoot;

    public static void Register()
    {
        TestSuite.Add("AsmReal.Lit", "真实 Lit.shader 的 include_with_pragmas 全部降级", LitShaderPragmasDowngraded);
        TestSuite.Add("AsmReal.Assemble", "URP 全量 .shader 组装并通过全部不变量", FullCorpusAssembles);
        TestSuite.Add("AsmReal.Audit", "URP 全量依赖审计零死链", DependencyAuditClean);
        TestSuite.Add("AsmReal.Cost", "组装吞吐与分配基线", Cost);
    }

    // ───────────────────────────── 基座 ─────────────────────────────

    private static VfsIndex Index()
    {
        if (_index is not null)
        {
            return _index;
        }

        var candidate = _projectRoot
            ?? Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT")
            ?? Path.Combine("D:", "Program Files", "U3D", "NewWorld");

        if (!File.Exists(Path.Combine(candidate, "ProjectSettings/ProjectVersion.txt")))
        {
            throw new SkipException($"未找到可用的 Unity 工程：{candidate}（可用 MICROSHADER_UNITY_PROJECT 指定）");
        }

        _index = VfsIndexBuilder.Build(candidate);
        return _index;
    }

    private static string UrpRoot()
    {
        var index = Index();
        if (!index.TryGetPackage("com.unity.render-pipelines.universal", out var urp))
        {
            throw new SkipException("该工程未安装 URP 包");
        }

        return urp!.PhysicalRoot;
    }

    private static string[] ShaderFiles()
    {
        if (_shaderFiles is not null)
        {
            return _shaderFiles;
        }

        var files = Directory.GetFiles(VfsPath.ToNative(UrpRoot()), "*.shader", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        _shaderFiles = files;
        return files;
    }

    private static (SourceShaderDocument Document, ShaderLabParseResult Parse) Open(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var parse = new ShaderLabStateMachine().Parse(filePath, text);
        var document = new SourceShaderDocument
        {
            FileUri = parse.TargetUri,
            FilePath = filePath,
            FullText = text,
            Version = 1,
        };

        return (document, parse);
    }

    // ───────────────────────────── 用例 ─────────────────────────────

    private static void LitShaderPragmasDowngraded()
    {
        var lit = Path.Combine(VfsPath.ToNative(UrpRoot()), "Shaders", "Lit.shader");
        Check.True(File.Exists(lit), "URP 应带官方 Lit.shader");

        var (document, parse) = Open(lit);
        Check.True(parse.Passes.Count > 0, "Lit.shader 应切出编译单元");

        // 文件自身声明的 include_with_pragmas 行（URP 官方 Lit.shader 实测 10 处）。
        var lines = document.FullText.Split('\n');
        var declaredLines = new HashSet<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("include_with_pragmas", StringComparison.Ordinal))
            {
                declaredLines.Add(i + 1);
            }
        }

        Check.True(declaredLines.Count >= 10, $"Lit.shader 原文应含至少 10 处 include_with_pragmas，实际 {declaredLines.Count}");

        var assembler = new VirtualTextAssembler(new ShaderContextRegistry(Index()));
        var rewrittenLines = new HashSet<int>();
        var rewrittenPragmas = new HashSet<int>();
        var pathRewrites = 0;
        string? samplePathRewrite = null;

        foreach (var snippet in parse.Passes)
        {
            var result = assembler.Assemble(document, parse, snippet, ShaderStage.Fragment);
            Check.True(result.Succeeded, $"Lit.shader 第 {snippet.PassIndex} 个单元装配失败：{result.FailureDetail}");
            using var text = result.Text!;

            var violations = AssemblyInvariants.Verify(text, document.FullText);
            Check.Equal(0, violations.Count, "不变量违反：\n  - " + string.Join("\n  - ", violations));

            for (var line = 1; line <= text.LineMap.LineCount; line++)
            {
                Check.True(
                    !text.GetLine(line).Contains("include_with_pragmas", StringComparison.Ordinal),
                    $"渲染文本第 {line} 行仍残留 include_with_pragmas（DXC 判为非法预处理指令）");
            }

            for (var index = 0; index < text.LineMap.RewriteCount; index++)
            {
                var rewrite = text.LineMap.RewriteAt(index);
                rewrittenLines.Add(rewrite.SourceLine);
                if (rewrite.Kind is LineRewriteKind.IncludeWithPragmasDowngrade or LineRewriteKind.IncludeWithPragmasAndPath)
                {
                    rewrittenPragmas.Add(rewrite.SourceLine);
                }
                else
                {
                    pathRewrites++;
                    samplePathRewrite ??= rewrite.ToString();
                }
            }
        }

        TestCaseHelpers.Report(
            $"Lit.shader: {parse.Passes.Count} 个单元，文件声明 {declaredLines.Count} 处 include_with_pragmas，"
            + $"共重写 {rewrittenLines.Count} 个源行（其中 with_pragmas {rewrittenPragmas.Count} 行、Packages/Assets 路径重写 {pathRewrites} 次）");

        foreach (var declared in declaredLines)
        {
            Check.True(rewrittenPragmas.Contains(declared), $"源行 {declared} 的 include_with_pragmas 未被降级重写");
        }

        Check.Equal(declaredLines.Count, rewrittenPragmas.Count, "降级重写的源行集合应与文件声明完全一致");

        if (samplePathRewrite is not null)
        {
            TestCaseHelpers.Report("纯路径重写样例: " + samplePathRewrite);
        }
    }

    private static void FullCorpusAssembles()
    {
        var files = ShaderFiles();
        var registry = new ShaderContextRegistry(Index());
        var assembler = new VirtualTextAssembler(registry);

        var fileCount = 0;
        var unitCount = 0;
        var sharedUnitCount = 0;
        var failures = new List<string>();
        var violationsAll = new List<string>();
        var blockCount = 0;

        // 抑制原生派发是模块 1 的既定策略（URP 自带的编辑器工具 CG 着色器不引用现代核心头文件），
        // 属「按设计不编译」，不是装配失败 —— 但它必须与真正的失败分开计数，不能被混进去放过。
        var suppressed = new List<string>();

        foreach (var file in files)
        {
            var (document, parse) = Open(file);
            fileCount++;
            blockCount += parse.Passes.Count;

            foreach (var snippet in parse.Passes)
            {
                var result = assembler.Assemble(document, parse, snippet, ShaderStage.Fragment);
                if (!result.Succeeded)
                {
                    if (result.Failure == AssemblyFailureKind.SuppressedNativeDispatch)
                    {
                        suppressed.Add(Path.GetFileName(file));
                        continue;
                    }

                    if (failures.Count < 12)
                    {
                        failures.Add($"{Path.GetFileName(file)}#{snippet.PassIndex}: {result.Failure} {result.FailureDetail}");
                    }

                    continue;
                }

                using var text = result.Text!;
                unitCount++;
                if (parse.SharedBlocks.Count > 0)
                {
                    sharedUnitCount++;
                }

                foreach (var violation in AssemblyInvariants.Verify(text, document.FullText))
                {
                    if (violationsAll.Count < 12)
                    {
                        violationsAll.Add($"{Path.GetFileName(file)}#{snippet.PassIndex}: {violation}");
                    }
                }
            }
        }

        TestCaseHelpers.Report(
            $"URP 全量: {fileCount} 个 .shader / {blockCount} 个代码块，组装成功 {unitCount} 个（其中 {sharedUnitCount} 个注入了 HLSLINCLUDE 共享块），"
            + $"按策略抑制 {suppressed.Count} 个（{string.Join(", ", suppressed.Distinct())}）");

        Check.Equal(blockCount, unitCount + suppressed.Count, "每个代码块都必须有确定结局：要么组装成功，要么按策略抑制");
        Check.Equal(0, violationsAll.Count, "组装产物必须通过 I1/I2/I3/I5 全部不变量：\n  - " + string.Join("\n  - ", violationsAll));
        Check.Equal(0, failures.Count, "不得有装配失败的单元：\n  - " + string.Join("\n  - ", failures));
        Check.True(unitCount >= 70, $"URP 全量应至少产生 70 个编译单元，实际 {unitCount}");
        Check.Equal(231, blockCount, "URP 14.0.12 全量代码块数（与模块 1 切片结果交叉校验）");
    }

    private static void DependencyAuditClean()
    {
        var files = ShaderFiles();
        var registry = new ShaderContextRegistry(Index());

        var includes = 0;
        var dead = new List<string>();

        foreach (var file in files)
        {
            var (document, parse) = Open(file);
            includes += TestCaseHelpers.EnumerateIncludes(file).Count();

            foreach (var diagnostic in IncludeAudit.Audit(document, parse, registry))
            {
                if (dead.Count < 12)
                {
                    dead.Add($"{Path.GetFileName(file)}:{diagnostic.Line} {diagnostic.Message}");
                }
            }
        }

        TestCaseHelpers.Report($"依赖审计: {files.Length} 个 .shader / {includes} 条 #include / 死链 {dead.Count} 条（最多列 12）");
        Check.Equal(0, dead.Count, "URP 在本机索引下不应出现依赖缺失：\n  - " + string.Join("\n  - ", dead));
    }

    private static void Cost()
    {
        var files = ShaderFiles();
        var registry = new ShaderContextRegistry(Index());
        var assembler = new VirtualTextAssembler(registry);

        // 预热：让对象池与 ArrayPool 进入稳态。
        foreach (var file in files.Take(8))
        {
            var (warmDocument, warmParse) = Open(file);
            foreach (var snippet in warmParse.Passes)
            {
                assembler.Assemble(warmDocument, warmParse, snippet, ShaderStage.Fragment).Text?.Dispose();
            }
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();

        var units = 0;
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var startedAt = Stopwatch.GetTimestamp();

        foreach (var file in files)
        {
            var (document, parse) = Open(file);
            foreach (var snippet in parse.Passes)
            {
                var result = assembler.Assemble(document, parse, snippet, ShaderStage.Fragment);
                if (result.Text is { } text)
                {
                    text.Dispose();
                    units++;
                }
            }
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - startedAt;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var milliseconds = elapsedTicks * 1000.0 / Stopwatch.Frequency;

        TestCaseHelpers.Report(
            $"组装基线: {files.Length} 文件 / {units} 单元，{milliseconds:F1} ms（{milliseconds / Math.Max(1, files.Length):F3} ms/文件），"
            + $"托管分配 {allocated / 1024.0:F1} KB（{allocated / (double)Math.Max(1, units) / 1024.0:F2} KB/单元）");

        Check.True(units >= 70, $"应组装出至少 70 个单元，实际 {units}");
        Check.True(milliseconds < 5000, $"全量组装应在 5 秒内完成，实际 {milliseconds:F1} ms");
    }
}
