using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// compute 文件的诊断派发：伪 snippet → 每 kernel 一个 cs_6_0 编译单元 → 行号归属。
/// </summary>
/// <remarks>
/// 锁的行为：切片兜底只解决「能切出块」，还得让诊断引擎真的把 compute 块派发出去。
/// 这条链上有三个各自独立的坑：
/// 1. 没有 #pragma kernel 时不能默默「干净」—— 必须给一条用户可见的诊断；
/// 2. profile 必须跟随 ShaderStage.Compute 走 cs_6_0（沿用 vs_6_0/ps_6_0 的 SM6 统一口径）；
/// 3. DXC 报出的行号是「源文件物理行号」，虚拟文本里的 #line 锚定必须正确，
///    否则 compute 的错会飘到别的行上去。
/// </remarks>
internal static class ComputeDiagnosticTests
{
    private const string Suite = "ComputeDiag";

    public static void Register()
    {
        TestSuite.Add(Suite, "MissingKernel", MissingKernelReported);
        TestSuite.Add(Suite, "MissingKernelNoCompile", MissingKernelDoesNotCompile);
        TestSuite.Add(Suite, "CleanFile", CleanFileIsClean);
        TestSuite.Add(Suite, "BrokenFile", BrokenFileReportsBothErrors);
        TestSuite.Add(Suite, "ProfileIsCs60", ProfileIsCs60);
        TestSuite.Add(Suite, "ShaderComputePassUnchanged", ShaderComputePassUnchanged);
    }

    // ── 沙盒基座 ──

    private static ShaderDiagnosticEngine CreateEngine()
    {
        if (!ShaderDiagnosticEngine.TryCreate(
                new ShaderContextRegistry(),
                DxcGatewayTests.DxcDirectory(),
                out var engine,
                out var error,
                new DiagnosticEngineOptions())
            || engine is null)
        {
            throw new SkipException("dxcompiler.dll 不可用：" + error);
        }

        return engine;
    }

    /// <summary>把 compute 正文落到临时工程的 Assets 下（物理路径决定虚拟路径与 include 基准）。</summary>
    private static (SourceShaderDocument Document, ShaderLabParseResult Parse) Open(
        TempProject project, string relativePath, string source)
    {
        project.Write(relativePath, source);
        var path = Path.Combine(project.Layout.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        var parse = new ShaderLabStateMachine().Parse(path, source);
        var document = new SourceShaderDocument
        {
            FileUri = parse.TargetUri,
            FilePath = path,
            FullText = source,
            Version = 1,
        };

        return (document, parse);
    }

    private static (SourceShaderDocument Document, ShaderLabParseResult Parse) OpenFixture(string fileName)
    {
        var path = Corpus.PathOf(fileName);
        var text = Corpus.Read(fileName);
        var parse = new ShaderLabStateMachine().Parse(path, text);
        var document = new SourceShaderDocument
        {
            FileUri = parse.TargetUri,
            FilePath = path,
            FullText = text,
            Version = 1,
        };

        return (document, parse);
    }

    // ── 1. 缺 #pragma kernel：必须有诊断 ──

    private static void MissingKernelReported()
    {
        using var project = new TempProject("computenokernel");
        var text = Corpus.Read("sample-compute-nokernel.compute");
        var (document, parse) = Open(project, "Assets/Shaders/NoKernel.compute", text);

        using var engine = CreateEngine();
        var report = engine.Analyze(document, parse, probeLine: 0, isSave: true);

        var item = report.Items.FirstOrDefault(i => i.Code == DiagnosticEngineCodes.ComputeMissingKernel);
        Check.True(item is not null, "缺 #pragma kernel 必须报出 " + DiagnosticEngineCodes.ComputeMissingKernel);
        Check.Equal(DiagnosticSeverity.Warning, item!.Severity, "缺 kernel 是 Warning（文件可打开、只是不能编译）");
        Check.True(
            item.Message.Contains("pragma kernel", StringComparison.Ordinal),
            "诊断文案要点明缺的是 #pragma kernel（实际：" + item.Message + "）");
        Check.False(report.HasErrors, "缺 kernel 不该升级成 Error");
        TestCaseHelpers.Report("缺 kernel 诊断：" + item.Message);
    }

    /// <summary>
    /// 缺 kernel 时不得派发任何编译单元。
    /// </summary>
    /// <remarks>
    /// 门禁用 SuppressedUnitCount：它统计「被抑制、没送去 DXC」的单元。
    /// 若实现里漏了 continue，一个没有入口点的文件会被 DXC 以「找不到入口」报错，
    /// 用户看到的就不再是「你缺 #pragma kernel」而是难懂的 DXC 文案。
    /// </remarks>
    private static void MissingKernelDoesNotCompile()
    {
        using var project = new TempProject("computenokernel2");
        var text = Corpus.Read("sample-compute-nokernel.compute");
        var (document, parse) = Open(project, "Assets/Shaders/NoKernel2.compute", text);

        using var engine = CreateEngine();
        var report = engine.Analyze(document, parse, probeLine: 0, isSave: true);

        Check.Equal(0, report.CompiledUnitCount, "缺 kernel 不得派发编译单元");
        Check.Equal(0, report.AbortedUnitCount, "缺 kernel 不该产生中止单元");
        Check.True(report.SuppressedUnitCount > 0, "缺 kernel 必须记为「抑制」而不是「编译过且干净」");
        TestCaseHelpers.Report(
            $"缺 kernel 单元口径：编译 {report.CompiledUnitCount} / 抑制 {report.SuppressedUnitCount} / 中止 {report.AbortedUnitCount}");
    }

    // ── 2. 干净文件：零诊断 ──

    private static void CleanFileIsClean()
    {
        using var project = new TempProject("computeclean");
        var text = Corpus.Read("sample-compute-clean.compute");
        var (document, parse) = Open(project, "Assets/Shaders/Clean.compute", text);

        using var engine = CreateEngine();
        var report = engine.Analyze(document, parse, probeLine: 0, isSave: true);

        Check.Equal(0, report.InternalErrors, "干净 compute 不得有内部错误：" + Describe(report));
        Check.Equal(0, report.AbortedUnitCount, "干净 compute 不得有中止单元");
        Check.Equal(1, report.CompiledUnitCount, "单 kernel 干净文件应恰好编译 1 个单元");
        Check.False(report.HasErrors, "干净 compute 不得报错：" + Describe(report));
        Check.Equal(0, report.Items.Count, "干净 compute 不该有任何诊断：" + Describe(report));
        TestCaseHelpers.Report("干净 compute：编译 " + report.CompiledUnitCount + " 单元，0 条诊断");
    }

    // ── 3. 有错的样本：两个错都要报，且行号落在源文件上 ──

    private static void BrokenFileReportsBothErrors()
    {
        using var project = new TempProject("computebroken");
        var text = Corpus.Read("sample-compute-broken.compute");
        var (document, parse) = Open(project, "Assets/Shaders/Broken.compute", text);

        using var engine = CreateEngine();
        var report = engine.Analyze(document, parse, probeLine: 0, isSave: true);

        Check.True(report.HasErrors, "有未声明标识符的 compute 必须报错：" + Describe(report));
        Check.Equal(1, report.CompiledUnitCount, "单 kernel 文件应恰好编译 1 个单元");

        var weighLine = Corpus.LineNumberContaining(text, "p.weigh");
        var missingLine = Corpus.LineNumberContaining(text, "missingValue");
        Check.True(weighLine > 0, "样本里必须有 p.weigh 那行");
        Check.True(missingLine > 0, "样本里必须有 missingValue 那行");

        var weigh = report.Items.FirstOrDefault(i => i.Line == weighLine);
        var missing = report.Items.FirstOrDefault(i => i.Line == missingLine);
        Check.True(weigh is not null, $"拼错的成员 p.weigh 必须报在第 {weighLine} 行：" + Describe(report));
        Check.True(missing is not null, $"未声明标识符 missingValue 必须报在第 {missingLine} 行：" + Describe(report));
        TestCaseHelpers.Report(
            $"有错 compute：{report.Items.Count} 条诊断，p.weigh@{weighLine}、missingValue@{missingLine} 均命中");
    }

    // ── 4. profile 口径：cs_6_0，entry 是 kernel 名 ──

    /// <summary>
    /// 通过 CompileOverride 截获实际派发给 DXC 的参数。
    /// </summary>
    /// <remarks>
    /// 断言 "-T cs_6_0" 与 "-E CSMain"：这两项是「compute 走对了编译器入口」的唯一硬证据。
    /// 只断言「编译过 1 个单元」是不够的 —— 派发成 ps_6_0 也会编译 1 个单元，只是永远报错。
    /// </remarks>
    private static void ProfileIsCs60()
    {
        using var project = new TempProject("computeprofile");
        var text = Corpus.Read("sample-compute-clean.compute");
        var (document, parse) = Open(project, "Assets/Shaders/Profile.compute", text);

        using var engine = CreateEngine();
        var captured = new List<string>();
        engine.CompileOverride = (_, arguments) =>
        {
            captured.AddRange(arguments);

            // 返回「无诊断」以让单元走完成功路径（这里只关心参数，不关心编译结果）。
            return new DxcCompileResult
            {
                Status = 0,
                CompileHr = 0,
                DiagnosticText = string.Empty,
                ObjectSize = 0,
                ElapsedMicroseconds = 0,
            };
        };

        engine.Analyze(document, parse, probeLine: 0, isSave: true);

        Check.True(captured.Count > 0, "必须真的调用了编译器");
        var target = ArgumentValue(captured, "-T");
        var entry = ArgumentValue(captured, "-E");
        Check.Equal("cs_6_0", target, "compute 单元的 target profile 必须是 cs_6_0");
        Check.Equal("CSMain", entry, "compute 单元的入口必须是 kernel 名");
        TestCaseHelpers.Report($"compute 派发参数：-T {target} -E {entry}");
    }

    /// <summary>
    /// 不回归：.shader 里的 compute Pass 不受新分支影响。
    /// </summary>
    /// <remarks>
    /// 新分支的条件是 `snippet.Kind == ComputeFile`，而 HLSLPROGRAM 块里写 #pragma kernel
    /// 的 .shader 走的是老路径（每个 Pass 一个单元）。这条用例钉住「新分支不抢老路径」。
    /// </remarks>
    private static void ShaderComputePassUnchanged()
    {
        using var project = new TempProject("shadercompute");
        var (document, parse) = Open(project, "Assets/Shaders/InShader.shader", InShaderSource);
        Check.Equal(1, parse.Passes.Count, "示例 .shader 应切出 1 个 Pass");

        using var engine = CreateEngine();
        var report = engine.Analyze(document, parse, probeLine: 0, isSave: true);

        Check.Equal(0, report.InternalErrors, ".shader 内的 compute Pass 不得触发内部错误：" + Describe(report));
        foreach (var item in report.Items)
        {
            Check.True(
                item.Code != DiagnosticEngineCodes.ComputeMissingKernel,
                ".shader 的 compute Pass 不得走新的 ComputeFile 分支");
        }

        TestCaseHelpers.Report(".shader 内 compute Pass：编译 " + report.CompiledUnitCount + " 单元，" + report.Items.Count + " 条诊断");
    }

    // ── 语料 ──

    private const string InShaderSource = """
        Shader "T/InShaderCompute"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma kernel CSMain
                    RWStructuredBuffer<float4> outputBuffer;
                    [numthreads(1, 1, 1)]
                    void CSMain(uint3 id : SV_DispatchThreadID)
                    {
                        outputBuffer[id.x] = float4(1, 0, 0, 1);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // ── 辅助 ──

    private static string ArgumentValue(List<string> arguments, string name)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i].Equals(name, StringComparison.Ordinal))
            {
                return arguments[i + 1];
            }
        }

        return "<缺失>";
    }

    private static string Describe(DiagnosticReport report)
    {
        if (report.Items.Count == 0)
        {
            return "（0 条诊断）";
        }

        var parts = new List<string>(report.Items.Count);
        foreach (var item in report.Items)
        {
            parts.Add($"{item.Code ?? item.Severity.ToString()}@{item.Line}:{item.Message}");
        }

        return string.Join(" | ", parts);
    }
}