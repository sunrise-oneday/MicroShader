using System.Diagnostics;
using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 4 端到端验收：装配 → 原生编译 → 诊断归属。
/// 沙盒用例证明「坐标链条正确」，真实语料用例证明「URP 官方 shader 在正确宏集下零 Error」（黄金判据）。
/// </summary>
internal static class CompilerRealWorldTests
{
    private static string? _projectRoot;
    private static VfsIndex? _index;

    public static void Configure(string? explicitRoot) => _projectRoot = explicitRoot;

    public static void Register()
    {
        TestSuite.Add("Compile.Error", "真实编译错误被映射回源文件行列（含 HLSLINCLUDE 偏移）", ErrorMappedToSource);
        TestSuite.Add("Compile.Args", "DXC 参数表形态：源名位置参数 + 禁 -Wall/-WX", ArgumentsShape);
        TestSuite.Add("Compile.Lit", "黄金判据：URP 官方 Lit.shader 在正确宏集下零 Error", LitGolden);
        TestSuite.Add("Compile.Cache", "同文本二次分析命中诊断缓存（0 编译且诊断一致）", CacheReuse);
        TestSuite.Add("Compile.Corpus", "真实工程 URP 前若干 shader 全量编译零内部错误", CorpusSmoke);
        TestSuite.Add("Compile.DeadInclude", "死链 #include 必须经引擎上报（装配中止 ≠ 诊断消失）", DeadIncludeSurfaces);
    }

    // ───────────────────────────── 基座 ─────────────────────────────

    internal static string ProjectRoot()
    {
        foreach (var candidate in Candidates())
        {
            if (File.Exists(Path.Combine(candidate, "ProjectSettings/ProjectVersion.txt")))
            {
                return candidate;
            }
        }

        throw new SkipException("未找到可用的 Unity 工程（可用 MICROSHADER_UNITY_PROJECT 指定）");
    }

    private static IEnumerable<string> Candidates()
    {
        if (!string.IsNullOrEmpty(_projectRoot))
        {
            yield return _projectRoot!;
        }

        var env = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT");
        if (!string.IsNullOrEmpty(env))
        {
            yield return env!;
        }

        // 用户指定的真机语料优先，其次模块 2/3 一直使用的工程。
        yield return Path.Combine("D:", "Program Files", "U3D", "ImproveCamera");
        yield return Path.Combine("D:", "Program Files", "U3D", "NewWorld");
    }

    internal static VfsIndex Index()
    {
        if (_index is not null)
        {
            return _index;
        }

        var root = ProjectRoot();
        _index = VfsIndexBuilder.Build(root);
        TestCaseHelpers.Report($"VFS 工程：{root}（{_index.PackageCount} 个包）");
        return _index;
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

    /// <summary>
    /// 沙盒：故意写错的 shader，错误语句在源文件第 16 行。
    /// 该 shader 带 HLSLINCLUDE 共享块 → 装配后的渲染行号与源行号必然不同，
    /// 因此「诊断行 == 16」同时证明了 #line 锚定与两步坐标映射都成立。
    /// </summary>
    private static void ErrorMappedToSource()
    {
        const string source = """
Shader "Test/Err"
{
    HLSLINCLUDE
    float shared_helper() { return 1; }
    ENDHLSL

    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 vert(float4 positionOS : POSITION) : SV_POSITION
            {
                return positionOS;
            }

            float4 frag() : SV_Target
            {
                return undeclared_symbol_here;
            }
            ENDHLSL
        }
    }
}
""";

        var directory = Path.Combine(Path.GetTempPath(), "MicroShaderCompileError", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Err.shader");
        File.WriteAllText(path, source);

        try
        {
            var document = new SourceShaderDocument
            {
                FileUri = "file:///Err.shader",
                FilePath = path,
                FullText = source,
                Version = 7,
            };

            var (_, parse) = Open(path);
            Check.True(parse.Passes.Count == 1, "应切出 1 个编译单元");
            Check.True(parse.SharedBlocks.Count == 1, "应识别 1 个 HLSLINCLUDE 共享块");

            using var engine = CreateEngine();
            var report = engine.Analyze(document, probeLine: 0, isSave: true);

            TestCaseHelpers.Report(
                $"单元 {report.UnitCount}（编译 {report.CompiledUnitCount}）原始诊断 {report.RawDiagnosticCount} 条，"
                + $"编译器耗时 {report.CompilerMicroseconds / 1000.0:F2} ms，总耗时 {report.ElapsedMicroseconds / 1000.0:F2} ms");

            Check.Equal(0, report.InternalErrors, "不得出现装配注入段内部错误");
            Check.True(report.HasErrors, "未声明标识符必须报 Error");

            foreach (var item in report.Items)
            {
                TestCaseHelpers.Report("→ " + item.Line + ":" + item.Column + " " + item.Severity + " " + item.Message);
            }

            var error = report.Items.FirstOrDefault(i => i.Message.Contains("undeclared_symbol_here", StringComparison.Ordinal));
            Check.NotNull(error, "应报出 undeclared_symbol_here");
            Check.Equal(22, error!.Line, "诊断必须落在源文件第 22 行（渲染文本里它在第 24 行）");
            Check.Equal(24, error.Column, "标识符 'undeclared_symbol_here' 起始列应为 24");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    // ── 模块 6 剩余项：诊断缓存。同一装配文本跳过 DXC 重编译，诊断必须逐条一致 ──

    private static void CacheReuse()
    {
        const string source = """
Shader "Test/Cache"
{
    HLSLINCLUDE
    float shared_helper() { return 1; }
    ENDHLSL

    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            float4 vert(float4 positionOS : POSITION) : SV_POSITION
            {
                return positionOS;
            }

            float4 frag() : SV_Target
            {
                return undeclared_symbol_here;
            }
            ENDHLSL
        }
    }
}
""";

        var directory = Path.Combine(Path.GetTempPath(), "MicroShaderCacheReuse", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Cache.shader");
        File.WriteAllText(path, source);

        try
        {
            var document = new SourceShaderDocument
            {
                FileUri = "file:///Cache.shader",
                FilePath = path,
                FullText = source,
                Version = 1,
            };

            var (_, parse) = Open(path);
            using var engine = CreateEngine();

            var first = engine.Analyze(document, probeLine: 0, isSave: true);
            Check.True(first.CompiledUnitCount >= 1, "首轮必须真实编译");
            Check.Equal(0, first.CachedUnitCount, "首轮缓存命中应为 0");
            Check.True(first.HasErrors, "首轮应报出 undeclared_symbol_here");

            var second = engine.Analyze(document, probeLine: 0, isSave: true);
            Check.Equal(0, second.CompiledUnitCount, "同文本第二轮必须命中缓存（0 编译）");
            Check.True(second.CachedUnitCount >= 1, "第二轮应报告缓存命中单元数");
            Check.True(second.HasErrors, "缓存复用不得吞掉错误诊断");
            Check.Equal(first.Items.Count, second.Items.Count, "两轮诊断条数必须一致");

            for (var i = 0; i < Math.Min(first.Items.Count, second.Items.Count); i++)
            {
                Check.Equal(first.Items[i].Line, second.Items[i].Line, "第 " + i + " 条行号必须一致");
                Check.Equal(first.Items[i].Column, second.Items[i].Column, "第 " + i + " 条列号必须一致");
                Check.Equal(first.Items[i].Message, second.Items[i].Message, "第 " + i + " 条消息必须一致");
            }

            TestCaseHelpers.Report("缓存复用：首轮编译 " + first.CompiledUnitCount + " 单元，二轮全部命中（" +
                second.CachedUnitCount + "），诊断 " + second.Items.Count + " 条逐条一致");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void ArgumentsShape()
    {
        var arguments = ShaderDiagnosticEngine.BuildArguments(
            "D:/Proj/Assets/Shaders/Lit.shader",
            "ps_6_0",
            "frag",
            ["D:/Unity/Editor/Data/CGIncludes"]);

        Check.Equal("D:/Proj/Assets/Shaders/Lit.shader", arguments[0], "argv[0] 必须是源文件物理路径");
        Check.True(!arguments.Contains("-Wall"), "绝不传 -Wall（会刷满 URP 私有 pragma 噪声）");
        Check.True(!arguments.Contains("-WX"), "绝不传 -WX");
        Check.True(!arguments.Contains("-Gec"), "只有 never_use_dxc 的 Pass 才允许 -Gec");
        Check.True(arguments.Contains("-HV"), "必须显式指定 -HV（对齐 Unity 2022.3 → 2018）");
        Check.True(arguments.Contains("-I"), "必须带 include 搜索路径（裸名兜底 CGIncludes）");

        TestCaseHelpers.Report("参数：" + string.Join(' ', arguments));
    }

    private static void LitGolden()
    {
        var index = Index();
        if (!index.TryGetPackage("com.unity.render-pipelines.universal", out var urp) || urp is null)
        {
            throw new SkipException("该工程未安装 URP 包");
        }

        var lit = Path.Combine(VfsPath.ToNative(urp.PhysicalRoot), "Shaders", "Lit.shader");
        if (!File.Exists(lit))
        {
            throw new SkipException("URP 包内没有官方 Lit.shader：" + lit);
        }

        var (document, _) = Open(lit);
        using var engine = CreateEngine();
        MicroShader.DiagnosticEngine.Native.DxcIncludeHandlerBridge.ClearRecentRequests();
        var report = engine.Analyze(document, probeLine: 0, isSave: true);

        var handlerError = MicroShader.DiagnosticEngine.Native.DxcIncludeHandlerBridge.TakeLastError();
        if (handlerError is not null)
        {
            TestCaseHelpers.Report("include 处理器异常：" + handlerError.Replace("\r", string.Empty).Replace("\n", " | ").Replace("   ", " "));
        }

        var requests = MicroShader.DiagnosticEngine.Native.DxcIncludeHandlerBridge.SnapshotRecentRequests();
        TestCaseHelpers.Report(
            $"include 处理器：实例 {MicroShader.DiagnosticEngine.Native.DxcIncludeHandlerBridge.CreatedCount} 个、"
            + $"LoadSource 调用 {MicroShader.DiagnosticEngine.Native.DxcIncludeHandlerBridge.CallCount} 次");
        Check.True(requests.Count == 0, "排障记录应为空（Trace 默认关闭，生产路径不建字符串）");

        TestCaseHelpers.Report(
            $"Lit.shader：单元 {report.UnitCount}（编译 {report.CompiledUnitCount}、抑制 {report.SuppressedUnitCount}）、"
            + $"原始诊断 {report.RawDiagnosticCount} 条、过滤 pragma message {report.DroppedPragmaMessages} 条、"
            + $"编译器 {report.CompilerMicroseconds / 1000.0:F1} ms、总耗时 {report.ElapsedMicroseconds / 1000.0:F1} ms");

        Check.Equal(0, report.InternalErrors, "不得出现装配注入段内部错误");
        Check.Equal(0, report.DegradedColumns, "不得出现列号换算降级");

        var errors = report.Items.Where(i => i.Severity == DiagnosticSeverity.Error).Take(8).ToList();
        foreach (var item in errors)
        {
            TestCaseHelpers.Report("ERR " + item.Line + ":" + item.Column + " " + item.Message);
        }

        Check.Equal(0, errors.Count, $"黄金判据失败：URP 官方 Lit.shader 在正确宏集下应零 Error，实际 {report.Items.Count(i => i.Severity == DiagnosticSeverity.Error)} 条");
    }

    private static void CorpusSmoke()
    {
        var index = Index();
        if (!index.TryGetPackage("com.unity.render-pipelines.universal", out var urp) || urp is null)
        {
            throw new SkipException("该工程未安装 URP 包");
        }

        var files = Directory.GetFiles(VfsPath.ToNative(urp.PhysicalRoot), "*.shader", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        const int limit = 12;
        using var engine = CreateEngine();

        var compiled = 0;
        var errors = 0;
        var warnings = 0;
        var internalErrors = 0;
        var dirty = new List<string>();
        long microseconds = 0;

        foreach (var file in files.Take(limit))
        {
            var (document, _) = Open(file);
            var report = engine.Analyze(document, probeLine: 0, isSave: true);
            compiled += report.CompiledUnitCount;
            microseconds += report.ElapsedMicroseconds;
            internalErrors += report.InternalErrors;

            var fileErrors = report.Items.Count(i => i.Severity == DiagnosticSeverity.Error);
            errors += fileErrors;
            warnings += report.Items.Count(i => i.Severity == DiagnosticSeverity.Warning);
            if (fileErrors > 0)
            {
                dirty.Add(Path.GetFileName(file) + "(" + fileErrors + ")");
            }
        }

        TestCaseHelpers.Report(
            $"前 {Math.Min(limit, files.Length)} 个 URP shader：编译单元 {compiled}、Error {errors}、Warning {warnings}、"
            + $"内部错误 {internalErrors}、耗时 {microseconds / 1000.0:F1} ms");

        if (dirty.Count > 0)
        {
            TestCaseHelpers.Report("有 Error 的文件：" + string.Join(", ", dirty.Take(12)));
        }

        Check.Equal(0, internalErrors, "不得出现装配注入段内部错误");
        Check.Equal(0, errors, $"黄金判据：真实 URP shader 在正确宏集下应零 Error，实际 Error {errors}（文件：{string.Join(", ", dirty)}）");
    }

    /// <summary>
    /// 回归：用户交付的 BrokenUrp.shader 里 "#include" 的包名被拼错。
    /// 装配会按「绝不把虚拟路径喂给 DXC」中止整个单元 —— 但"中止不等于诊断消失"：
    /// 死链诊断必须穿过引擎到达客户端，否则用户看到的是「零诊断」而 shader 其实是坏的。
    /// </summary>
    private static void DeadIncludeSurfaces()
    {
        var path = Corpus.PathOf("BrokenUrp.shader");
        var text = File.ReadAllText(path);
        var (document, parse) = Open(path);

        Check.Equal(1, parse.Passes.Count, "该用例只有 1 个 Pass");

        using var engine = CreateEngine();
        var report = engine.Analyze(document, probeLine: 0, isSave: true);

        TestCaseHelpers.Report(
            $"单元 {report.UnitCount}（派发 {report.DispatchCount}：编译 {report.CompiledUnitCount}、"
            + $"抑制 {report.SuppressedUnitCount}、中止 {report.AbortedUnitCount}）→ 诊断 {report.Items.Count} 条");

        Check.Equal(report.DispatchCount, report.CompiledUnitCount + report.SuppressedUnitCount + report.AbortedUnitCount + report.CachedUnitCount,
            "派发数必须等于编译 + 抑制 + 中止");
        Check.Equal(2, report.AbortedUnitCount, "顶点/片元两次派发都应因死链中止");
        Check.Equal(0, report.CompiledUnitCount, "死链单元绝不发给 DXC");
        Check.True(report.Items.Count > 0, "★ 中止的单元仍必须上报装配诊断（否则就是静默吞错）");
        Check.True(report.HasErrors, "死链是 Error 级");

        var missing = report.Items.FirstOrDefault(i => i.Code == DiagnosticEngineCodes.MissingInclude);
        Check.NotNull(missing, "应报出依赖缺失：" + string.Join(" | ", report.Items.Select(i => i.Code + " " + i.Message)));
        Check.Equal(32, missing!.Line, "拼错的 #include 在源文件第 32 行");
        Check.True(missing.Message.Contains("universial", StringComparison.Ordinal), "文案应点出无法解析的路径");

        foreach (var item in report.Items)
        {
            TestCaseHelpers.Report("→ " + item.Line + ":" + item.Column + " " + item.Severity + " [" + item.Code + "] " + item.Message);
        }
    }

    private static ShaderDiagnosticEngine CreateEngine()
    {
        if (!ShaderDiagnosticEngine.TryCreate(new ShaderContextRegistry(Index()), DxcGatewayTests.DxcDirectory(), out var engine, out var error) || engine is null)
        {
            throw new SkipException("dxcompiler.dll 不可用：" + error);
        }

        return engine;
    }
}
