using System.Collections.Concurrent;
using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 多 kernel compute 文件：每个 #pragma kernel 一个编译单元。
/// </summary>
/// <remarks>
/// 锁的行为：只认第一个 kernel。若编译单元review 按文件而不是按 kernel 生成，
/// 第二个 kernel 里的错误永远不会被报出来 —— 文件能编译通过但运行时空派发。
/// 另外钉住 KernelEntry 与 KernelEntries 的一致性：前者是规格书契约字段，
/// 已有消费者按它取单入口，不能因为加了多 kernel 支持就变成 null。
/// </remarks>
internal static class MultiKernelTests
{
    private const string Suite = "ComputeMultiKernel";
    private const string MultiFile = "sample-compute-multikernel.compute";

    public static void Register()
    {
        TestSuite.Add(Suite, "SliceCollectsAllKernels", SliceCollectsAllKernels);
        TestSuite.Add(Suite, "FirstKernelStaysPrimary", FirstKernelStaysPrimary);
        TestSuite.Add(Suite, "CompileOneUnitPerKernel", CompileOneUnitPerKernel);
        TestSuite.Add(Suite, "ProbeLineCompilesAllKernels", ProbeLineCompilesAllKernels);
        TestSuite.Add(Suite, "CachePerKernel", CachePerKernel);
        TestSuite.Add(Suite, "SingleKernelBaseline", SingleKernelBaseline);
        TestSuite.Add(Suite, "DuplicateKernelNames", DuplicateKernelNames);
    }

    // ── 1. 切片器收集全部 kernel ──

    private static void SliceCollectsAllKernels()
    {
        var parse = ParseFixture(MultiFile);
        Check.Equal(1, parse.Passes.Count, "多 kernel 文件仍只产出一个 compute 伪 snippet");
        var snippet = parse.Passes[0];
        Check.SequenceEqual(new[] { "CSClear", "CSCopy" }, snippet.KernelEntries, "KernelEntries 必须按出现顺序收集全部 kernel");
        TestCaseHelpers.Report("KernelEntries = " + string.Join(",", snippet.KernelEntries));
    }

    // ── 2. KernelEntry 与首个一致 ──

    /// <remarks>
    /// KernelEntry 是规格书契约里的单入口字段，多 kernel 文件必须落到第一个，
    /// 否则按它取入口的老消费者会拿到 null。
    /// </remarks>
    private static void FirstKernelStaysPrimary()
    {
        var snippet = ParseFixture(MultiFile).Passes[0];
        Check.Equal("CSClear", snippet.KernelEntry, "KernelEntry 必须与 KernelEntries[0] 一致");
    }

    // ── 3. 每个 kernel 一个编译单元 ──

    private static void CompileOneUnitPerKernel()
    {
        var engine = CreateEngine();
        var entryPoints = new ConcurrentBag<string>();
        engine.CompileOverride = (_, arguments) =>
        {
            entryPoints.Add(ArgumentValue(arguments, "-E"));
            return new DxcCompileResult
            {
                Status = 0,
                CompileHr = 0,
                DiagnosticText = string.Empty,
                ObjectSize = 1024,
                ElapsedMicroseconds = 1,
            };
        };

        using var project = new TempProject("multikernel");
        var (document, parse) = Open(project, MultiFile);
        var report = engine.Analyze(document, parse, probeLine: 0, isSave: true);

        Check.Equal(0, report.Items.Count, "干净的多 kernel 文件不应报诊断");
        Check.Equal(2, report.CompiledUnitCount, "两个 kernel 必须各生成一个编译单元");
        Check.Equal(2, report.DispatchCount, "DispatchCount 要数到两个已编译单元（UnitCount 是 Pass 口径，恒为 1）");
        Check.SequenceEqual(new[] { "CSClear", "CSCopy" }, entryPoints.OrderBy(name => name, StringComparer.Ordinal), "每个 kernel 都要以 -E 传入口名");
        TestCaseHelpers.Report("编译单元 " + report.CompiledUnitCount + " 个，入口 " + string.Join(",", entryPoints));
    }

    // ── 4. 探针行命中时双 kernel 仍全部编译（第一版降级策略） ──

    /// <remarks>
    /// compute 文件只有一个直接 snippet，探针行必然落在它里面 ——
    /// 第一版不做「只编译光标所在 kernel」的收窄（kernels 通常 1~2 个，全量代价可接受）。
    /// 锁住这个取舍：若以后有人把探针行接进 compute 分支却只编译一个 kernel，
    /// 在 CSCopy 里敲字时 CSClear 的错误就会停止上报。
    /// </remarks>
    private static void ProbeLineCompilesAllKernels()
    {
        var engine = CreateEngine();
        var entryPoints = new ConcurrentBag<string>();
        engine.CompileOverride = (_, arguments) =>
        {
            entryPoints.Add(ArgumentValue(arguments, "-E"));
            return new DxcCompileResult
            {
                Status = 0,
                CompileHr = 0,
                DiagnosticText = string.Empty,
                ObjectSize = 1024,
                ElapsedMicroseconds = 1,
            };
        };

        using var project = new TempProject("multikernel-probe");
        var (document, parse) = Open(project, MultiFile);

        // 探针行 = 第二个 kernel 的 #pragma 行，仍然要两个 kernel 都编译。
        var probeLine = Corpus.LineNumberContaining(document.FullText!, "#pragma kernel CSCopy");
        Check.True(probeLine > 0, "语料里必须有第二个 #pragma kernel");

        var report = engine.Analyze(document, parse, probeLine, isSave: false);
        Check.Equal(2, report.CompiledUnitCount, "打字路径下两个 kernel 仍各编译一次（第一版不收窄）");
        Check.SequenceEqual(
            new[] { "CSClear", "CSCopy" },
            entryPoints.OrderBy(name => name, StringComparer.Ordinal),
            "两个 kernel 都得送进编译器");
        TestCaseHelpers.Report("探针行 " + probeLine + " → 编译 " + report.CompiledUnitCount + " 单元");
    }

    // ── 5. 缓存按编译单元生效 ──

    private static void CachePerKernel()
    {
        var engine = CreateEngine();
        var calls = 0;
        engine.CompileOverride = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return new DxcCompileResult
            {
                Status = 0,
                CompileHr = 0,
                DiagnosticText = string.Empty,
                ObjectSize = 1024,
                ElapsedMicroseconds = 1,
            };
        };

        using var project = new TempProject("multikernel-cache");
        var (document, parse) = Open(project, MultiFile);
        var first = engine.Analyze(document, parse, probeLine: 0, isSave: true);
        Check.Equal(2, first.CompiledUnitCount, "首次分析编译两个单元");

        var second = engine.Analyze(document, parse, probeLine: 0, isSave: true);
        Check.Equal(2, second.CachedUnitCount, "第二次分析两个单元全部命中缓存");
        Check.Equal(2, calls, "命中缓存后不得再调用编译器，实际调用 " + calls + " 次");
        TestCaseHelpers.Report("首次编译 2 单元，二次命中 " + second.CachedUnitCount + " 单元");
    }

    // ── 5. 单 kernel 行为不回归 ──

    private static void SingleKernelBaseline()
    {
        var engine = CreateEngine();
        var entryPoints = new ConcurrentBag<string>();
        engine.CompileOverride = (_, arguments) =>
        {
            entryPoints.Add(ArgumentValue(arguments, "-E"));
            return new DxcCompileResult
            {
                Status = 0,
                CompileHr = 0,
                DiagnosticText = string.Empty,
                ObjectSize = 1024,
                ElapsedMicroseconds = 1,
            };
        };

        using var project = new TempProject("singlekernel");
        var (document, parse) = Open(project, "sample-compute-clean.compute");
        var report = engine.Analyze(document, parse, probeLine: 0, isSave: true);

        Check.Equal(1, report.CompiledUnitCount, "单 kernel 文件仍只编译一个单元");
        Check.SequenceEqual(new[] { "CSMain" }, entryPoints, "入口名必须是 CSMain");
    }

    // ── 6. 重复 kernel 名（现状：不去重） ──

    /// <remarks>
    /// ShaderPragmaScanner 对 kernel 指令直接 Add，不做去重。
    /// 这里只钉住「重复名会被原样收集」这一现状，避免后续误以为切片器做了去重。
    /// </remarks>
    private static void DuplicateKernelNames()
    {
        const string source = """
            #pragma kernel CSMain
            #pragma kernel CSMain

            RWStructuredBuffer<float4> outputBuffer;

            [numthreads(8, 8, 1)]
            void CSMain(uint3 id : SV_DispatchThreadID)
            {
                outputBuffer[id.x] = float4(1, 1, 1, 1);
            }
            """;

        var parse = new ShaderLabStateMachine().Parse("/tmp/DuplicateKernel.compute", source);
        Check.Equal(1, parse.Passes.Count, "重复 kernel 声明不影响切片");
        Check.SequenceEqual(new[] { "CSMain", "CSMain" }, parse.Passes[0].KernelEntries, "重复的 kernel 名当前被原样收集（切片器不去重）");
        TestCaseHelpers.Report("KernelEntries = " + string.Join(",", parse.Passes[0].KernelEntries));
    }

    // ── 辅助 ──

    private static ShaderLabParseResult ParseFixture(string fileName)
        => new ShaderLabStateMachine().Parse(Corpus.PathOf(fileName), Corpus.Read(fileName));

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

    private static (SourceShaderDocument Document, ShaderLabParseResult Parse) Open(TempProject project, string fileName)
    {
        var text = Corpus.Read(fileName);
        project.Write(fileName, text);
        var path = System.IO.Path.Combine(project.Layout.ProjectRoot, fileName);
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

    private static string ArgumentValue(IReadOnlyList<string> arguments, string name)
    {
        for (var i = 0; i + 1 < arguments.Count; i++)
        {
            if (arguments[i].Equals(name, StringComparison.Ordinal))
            {
                return arguments[i + 1];
            }
        }

        Check.True(false, "编译参数缺少 " + name);
        return "<缺失>";
    }
}