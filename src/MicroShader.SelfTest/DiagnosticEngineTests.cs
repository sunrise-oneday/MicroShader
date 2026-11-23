using System.Diagnostics;
using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 3（DiagnosticEngine / LineCalibrator）的固定用例：
/// Segment 模型、"#line" 锚定、include 重写、列号换算、宏注入、不变量。
/// </summary>
internal static class DiagnosticEngineTests
{
    public static void Register()
    {
        TestSuite.Add("Asm.Layout", "注入段/锚定段布局与 #line 收尾（I1）", PrologueLayout);
        TestSuite.Add("Asm.Invariants", "组装产物通过 I1/I2/I3/I5 全部不变量", InvariantsHold);
        TestSuite.Add("Asm.Shared", "HLSLINCLUDE 注入到每个 HLSLPROGRAM 块并各自锚定", SharedBlockInjected);
        TestSuite.Add("Asm.Matrix", "共享块里声明的关键字进入宏矩阵", SharedBlockKeywordsFeedMatrix);
        TestSuite.Add("Asm.Pragmas", "#include_with_pragmas 降级且全文不再出现该指令", IncludeWithPragmasDowngraded);
        TestSuite.Add("Asm.Relative", "裸名/相对 include 原样交给 DXC", RelativeIncludeUntouched);
        TestSuite.Add("Asm.Dead", "未知包的 include 中止单元并按位置去重", DeadIncludeAborts);
        TestSuite.Add("Asm.Fuse", "未闭合块抑制编译且不重复报诊断", UnterminatedBlockSuppressed);
        TestSuite.Add("Asm.LegacyCg", "遗留 CG 块抑制原生派发", LegacyCgSuppressed);
        TestSuite.Add("Asm.Column", "UTF-8 字节列与 UTF-16 列往返", ColumnConversion);
        TestSuite.Add("Asm.ColumnMap", "重写行的列映射按公共前缀分段", RewriteColumnMapping);
        TestSuite.Add("Asm.VirtualPath", "虚拟路径不含冒号与反斜杠且稳定", VirtualPathShape);
        TestSuite.Add("Diag.SharedProbe", "探针行落在共享块/Pass 之外时仍必须选中编译单元", SharedBlockProbeSelectsPasses);
        TestSuite.Add("Diag.Failure", "DXC 调用失败不得被当成干净结果缓存", CompilerFailureNotCached);
        TestSuite.Add("Diag.Budget", "超上限的 Pass 不得被当成干净结果发布", OverBudgetPassNotPublishedAsClean);
        TestSuite.Add("Diag.PragmaShadow", "外部头文件的 warning 不得被主文件 pragma 规则吞掉", ExternalHeaderWarningKept);
        TestSuite.Add("Diag.Dispose", "释放网关前必须等在途分析落地（有上限）", DisposeWaitsForInFlightAnalysis);
        TestSuite.Add("Platform.EditorLog", "Editor.log 层优先于推导与推定", PlatformEditorLogFirst);
        TestSuite.Add("Platform.Project", "ProjectSettings 层推导色彩空间宏", PlatformProjectSettingsTier);
        TestSuite.Add("Platform.Table", "版本×平台表只给平台能力宏并标推定", PlatformTableTier);
    }

    // ───────────────────────────── 沙盒基座 ─────────────────────────────

    private static ShaderContextRegistry EmptyRegistry() => new();

    /// <summary>真实的诊断引擎（依赖 dxcompiler.dll；缺库时这条用例整体跳过）。</summary>
    private static ShaderDiagnosticEngine CreateEngine()
        => CreateEngine(new DiagnosticEngineOptions());

    /// <summary>同上，但可以按用例需要改旋钮（例如把单次分析上限压到 1）。</summary>
    private static ShaderDiagnosticEngine CreateEngine(DiagnosticEngineOptions options)
    {
        if (!ShaderDiagnosticEngine.TryCreate(EmptyRegistry(), DxcGatewayTests.DxcDirectory(), out var engine, out var error, options) || engine is null)
        {
            throw new SkipException("dxcompiler.dll 不可用：" + error);
        }

        return engine;
    }

    /// <summary>把源码落到临时工程的 Assets 下再切片（装配按物理路径推导虚拟路径与 include 基准）。</summary>
    private static (SourceShaderDocument Document, ShaderLabParseResult Parse) OpenSource(
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

    private static (SourceShaderDocument Document, ShaderLabParseResult Parse) Open(string fixture)
    {
        var path = Corpus.PathOf(fixture);
        var text = Corpus.Read(fixture);
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

    // ───────────────────────────── 用例 ─────────────────────────────

    private static void PrologueLayout()
    {
        var (document, parse) = Open("IncludeMatrix.shader");
        Check.Equal(2, parse.Passes.Count, "IncludeMatrix 应切出 2 个编译单元");

        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var result = assembler.Assemble(document, parse, parse.Passes[0], ShaderStage.Fragment);
        Check.True(result.Succeeded, "装配应当成功：" + result.FailureDetail);
        using var text = result.Text!;

        var segments = text.LineMap.Segments;
        Check.Equal(4, segments.Count, "期望 4 个段：头部注入 + 共享块 + 单行 #line + Pass 块");

        Check.Equal(SegmentKind.Injected, segments[0].Kind, "第 1 段应为注入段");
        Check.Equal(SegmentKind.Anchored, segments[1].Kind, "第 2 段应为锚定段");
        Check.True(segments[1].IsSharedBlock, "第 2 段应来自 HLSLINCLUDE");
        Check.Equal(4, segments[1].SourceStartLine, "HLSLINCLUDE（第 3 行）的内容应从第 4 行开始");
        Check.Equal(3, segments[1].SourceLineCount, "共享块内容为第 4..6 行");

        Check.Equal(SegmentKind.Injected, segments[2].Kind, "第 3 段应为文件中间的注入段");
        Check.Equal(1, segments[2].RenderedLineCount, "文件中间的注入段只允许 1 行");

        Check.Equal(SegmentKind.Anchored, segments[3].Kind, "第 4 段应为 Pass 锚定段");
        Check.Equal(15, segments[3].SourceStartLine, "HLSLPROGRAM（第 14 行）的内容应从第 15 行开始");

        var headLast = text.GetLine(segments[0].RenderedEndLine).ToString();
        Check.True(headLast.StartsWith("#line 4 ", StringComparison.Ordinal), "头部注入段必须以 #line 收尾，实际：" + headLast);

        var midLine = text.GetLine(segments[2].RenderedStartLine).ToString();
        Check.Equal("#line 15 \"" + text.VirtualPath + "\"", midLine, "中段注入行应为 Pass 锚");

        // 锚定段的第 k 行必须映射回源文件的第 4+k 行
        Check.Equal(4, text.LineMap.SourceLineOf(segments[1].RenderedStartLine), "共享块首行映射");
        Check.Equal(15, text.LineMap.SourceLineOf(segments[3].RenderedStartLine), "Pass 首行映射");
        Check.True(text.LineMap.IsInjected(segments[0].RenderedStartLine), "注入行不应映射到任何源行");

        TestCaseHelpers.Report("段布局: " + string.Join(" | ", segments));
    }

    private static void InvariantsHold()
    {
        var (document, parse) = Open("IncludeMatrix.shader");
        var assembler = new VirtualTextAssembler(EmptyRegistry());

        var total = 0;
        foreach (var snippet in parse.Passes)
        {
            var result = assembler.Assemble(document, parse, snippet, ShaderStage.Fragment);
            Check.True(result.Succeeded, "装配应当成功：" + result.FailureDetail);
            using var text = result.Text!;

            var violations = AssemblyInvariants.Verify(text, document.FullText);
            Check.Equal(0, violations.Count, "不变量必须全部通过：\n  - " + string.Join("\n  - ", violations));
            total++;
        }

        Check.Equal(2, total, "两个编译单元都应被校验");
        TestCaseHelpers.Report($"不变量 I1/I2/I3/I5 在 {total} 个单元上全部通过");
    }

    private static void SharedBlockInjected()
    {
        var (document, parse) = Open("IncludeMatrix.shader");
        var assembler = new VirtualTextAssembler(EmptyRegistry());

        foreach (var snippet in parse.Passes)
        {
            var result = assembler.Assemble(document, parse, snippet, ShaderStage.Fragment);
            Check.True(result.Succeeded, "装配应当成功：" + result.FailureDetail);
            using var text = result.Text!;

            var shared = text.LineMap.Segments.Where(s => s.IsSharedBlock).ToArray();
            Check.Equal(1, shared.Length, "每个单元都应注入恰好一个共享块");
            Check.Equal(4, shared[0].SourceStartLine, "共享块锚回第 4 行");

            var rendered = text.GetText();
            Check.True(
                rendered.Contains("#pragma multi_compile _ SHARED_TOGGLE", StringComparison.Ordinal),
                "共享块的 pragma 行应出现在渲染文本里");
            Check.True(
                rendered.Contains("float4 _SharedTint;", StringComparison.Ordinal),
                "共享块的声明应出现在渲染文本里");
        }

        TestCaseHelpers.Report("HLSLINCLUDE 已注入到全部 2 个 HLSLPROGRAM 单元");
    }

    private static void SharedBlockKeywordsFeedMatrix()
    {
        var (document, parse) = Open("IncludeMatrix.shader");
        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var result = assembler.Assemble(document, parse, parse.Passes[0], ShaderStage.Fragment);
        Check.True(result.Succeeded, "装配应当成功：" + result.FailureDetail);
        using var text = result.Text!;

        var plan = result.KeywordPlan!;
        Check.Contains(plan.EnabledKeywords, "SHARED_TOGGLE", "共享块声明的关键字必须进入启用集");
        Check.Contains(plan.EnabledKeywords, "_MAIN_LIGHT_SHADOWS", "Pass 自身的 multi_compile 关键字应启用");
        Check.Contains(plan.EnabledKeywords, "_ALPHATEST_ON", "阶段限定的 shader_feature 在片元阶段应启用");
        Check.Contains(plan.DeclaredKeywords, "SHARED_TOGGLE", "共享块关键字必须补 _KEYWORD_DECLARED");
        Check.Equal("SHADER_STAGE_FRAGMENT", plan.StageMacro, "阶段宏");
        Check.Contains(plan.Lines, "#define SHARED_TOGGLE 1", "启用关键字必须带值定义");

        var rendered = text.GetText();
        Check.True(!rendered.Contains("#define _MAIN_LIGHT_SHADOWS 0", StringComparison.Ordinal), "绝不把未启用关键字定义成 0");

        TestCaseHelpers.Report($"启用集: [{string.Join(", ", plan.EnabledKeywords)}]");
        TestCaseHelpers.Report($"声明集: [{string.Join(", ", plan.DeclaredKeywords)}]");
    }

    private static void IncludeWithPragmasDowngraded()
    {
        var (document, parse) = Open("IncludeMatrix.shader");
        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var result = assembler.Assemble(document, parse, parse.Passes[0], ShaderStage.Fragment);
        Check.True(result.Succeeded, "装配应当成功：" + result.FailureDetail);
        using var text = result.Text!;

        Check.Equal(1, text.LineMap.RewriteCount, "只有 include_with_pragmas 那一行需要重写");

        var rewrite = text.LineMap.RewriteAt(0);
        Check.Equal(LineRewriteKind.IncludeWithPragmasDowngrade, rewrite.Kind, "重写类型");
        Check.Equal("Pragmas.hlsl", rewrite.SourcePath, "源路径");
        Check.Equal("Pragmas.hlsl", rewrite.RenderedPath, "规则 3 不做路径重写");
        Check.Equal(20, rewrite.SourceLine, "include_with_pragmas 在第 20 行");

        var renderedLine = text.GetLine(rewrite.RenderedLine).ToString();
        Check.Equal("#include \"Pragmas.hlsl\"", renderedLine.Trim(), "指令名必须降级为 #include");

        for (var line = 1; line <= text.LineMap.LineCount; line++)
        {
            Check.True(
                !text.GetLine(line).Contains("include_with_pragmas", StringComparison.Ordinal),
                $"渲染文本第 {line} 行仍残留 include_with_pragmas（DXC 会判为非法预处理指令）");
        }

        TestCaseHelpers.Report($"重写: {rewrite}");
    }

    private static void RelativeIncludeUntouched()
    {
        var (document, parse) = Open("IncludeMatrix.shader");
        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var result = assembler.Assemble(document, parse, parse.Passes[0], ShaderStage.Fragment);
        Check.True(result.Succeeded, "装配应当成功：" + result.FailureDetail);
        using var text = result.Text!;

        var renderedLines = new List<int>();
        foreach (var sourceLine in new[] { 5, 21 })
        {
            renderedLines.Clear();
            text.LineMap.CollectRenderedLinesOfSource(sourceLine, renderedLines);
            Check.Equal(1, renderedLines.Count, $"源行 {sourceLine} 应恰有一个渲染行");
            Check.True(!text.LineMap.IsRewritten(renderedLines[0]), $"源行 {sourceLine} 不应被重写");

            var actual = text.GetLine(renderedLines[0]).ToString();
            var expected = Corpus.LineAt(document.FullText, sourceLine);
            Check.Equal(expected, actual, $"源行 {sourceLine} 必须逐字节照抄");
        }

        TestCaseHelpers.Report("相对 include（SharedCommon.hlsl / Local.hlsl）原样保留，交给 DXC 的包含方目录 + -I 解析");
    }

    private static void DeadIncludeAborts()
    {
        var (document, parse) = Open("DeadIncludeShared.shader");
        var registry = EmptyRegistry();
        var assembler = new VirtualTextAssembler(registry);

        var report = assembler.AssembleDocument(document, parse, ShaderStage.Fragment);
        Check.Equal(2, report.Units.Count, "两个编译单元");
        Check.Equal(2, report.AbortedUnitCount, "共享块里的死链会让每个单元都中止");
        Check.Equal(1, report.Diagnostics.Count, "同一条死链按位置去重只报一次");

        var diagnostic = report.Diagnostics[0];
        Check.Equal(4, diagnostic.Line, "死链在第 4 行");
        Check.Equal(DiagnosticEngineCodes.MissingInclude, diagnostic.Code, "错误码");
        Check.True(diagnostic.Message.Contains("依赖缺失", StringComparison.Ordinal), "文案必须说明依赖缺失");
        Check.True(
            !diagnostic.Message.Contains("#if defined", StringComparison.Ordinal),
            "文案绝不能建议用户改用 #if defined(K)");
        Check.True(diagnostic.Message.Contains("依赖扫描层", StringComparison.Ordinal), "必须点明是依赖扫描层结论");

        foreach (var unit in report.Units)
        {
            Check.Equal(AssemblyFailureKind.MissingInclude, unit.Failure, "失败原因应为依赖缺失");
            Check.True(unit.Text is null, "中止时绝不产出可喂给 DXC 的文本");
        }

        var audit = IncludeAudit.Audit(document, parse, registry);
        Check.Equal(1, audit.Count, "审计层同样按位置去重");
        TestCaseHelpers.Report("死链诊断: " + diagnostic.Message);
    }

    private static void UnterminatedBlockSuppressed()
    {
        var (document, parse) = Open("UnterminatedBlock.shader");
        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var result = assembler.Assemble(document, parse, parse.Passes[0], ShaderStage.Fragment);

        Check.Equal(AssemblyFailureKind.UnterminatedBlock, result.Failure, "未闭合块必须抑制编译");
        Check.True(result.Text is null, "不得产出文本");
        Check.Equal(0, result.Diagnostics.Count, "诊断由切片层（SL0001）给出，装配层不得重复产出");

        TestCaseHelpers.Report("EOF 熔断生效，且未产生重复诊断");
    }

    private static void LegacyCgSuppressed()
    {
        var (document, parse) = Open("LegacyCg.shader");
        var snippet = parse.Passes.FirstOrDefault(p => p.SuppressNativeDispatch);
        if (snippet is null)
        {
            throw new SkipException("LegacyCg 语料里没有被抑制的块（策略判定变化时本条会提示）");
        }

        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var result = assembler.Assemble(document, parse, snippet, ShaderStage.Fragment);
        Check.Equal(AssemblyFailureKind.SuppressedNativeDispatch, result.Failure, "遗留 CG 块应抑制派发");
        Check.True(result.Text is null, "不得产出文本");
        Check.True(!string.IsNullOrEmpty(result.FailureDetail), "应给出抑制原因");

        TestCaseHelpers.Report("抑制原因: " + result.FailureDetail);
    }

    private static void ColumnConversion()
    {
        // DXC 的 column 是 1-based UTF-8 字节偏移（ADR-013 探针定论）；我们对外一律 UTF-16。
        var line = "a😀中b".AsSpan();

        Check.Equal(1, Utf8Column.ToUtf8Column(line, 1), "首列");
        Check.Equal(2, Utf8Column.ToUtf8Column(line, 2), "emoji 起点：前面 1 个 ASCII 字节");

        // emoji 之后的 '中'：UTF-8 里 emoji 占 4 字节，UTF-16 里占 2 code unit。
        var utf8OfCjk = Utf8Column.ToUtf8Column(line, 4);
        var utf16OfCjk = Utf8Column.ToUtf16Column(line, utf8OfCjk, out var exact);
        Check.True(exact, "字符边界上的换算必须精确");
        Check.Equal(4, utf16OfCjk, "UTF-8 列必须换算回正确的 UTF-16 列");

        // 探针里那条「emoji 让两条口径差 2」的现象要能被识别出来。
        var wide = "0123456789ab😀x".AsSpan();
        // 'x' 的 UTF-16 列 = 12 个 ASCII + 2 个代理项 + 1 = 15；UTF-8 列 = 12 + 4 + 1 = 17。
        var utf8X = Utf8Column.ToUtf8Column(wide, 15);
        Check.Equal(17, utf8X, "'x' 的 UTF-8 列");
        Check.Equal(2, utf8X - 15, "ASCII+emoji 行上，UTF-8 列应比 UTF-16 列大 2（4 字节 vs 2 code unit）");

        // 落在多字节字符中间的字节偏移必须降级而不是抛异常。
        var mid = Utf8Column.ToUtf16Column(line, 4, out var midExact);
        Check.True(!midExact, "落在 emoji 内部字节上的列必须标记为非精确");
        Check.Equal(4, mid, "降级时返回该字符起点所在列");

        // 往返：所有字符边界列都必须自洽。
        for (var column = 1; column <= line.Length + 1; column++)
        {
            if (column <= line.Length && char.IsLowSurrogate(line[column - 1]))
            {
                continue;
            }

            var utf8 = Utf8Column.ToUtf8Column(line, column);
            Check.Equal(column, Utf8Column.ToUtf16Column(line, utf8, out _), $"列 {column} 往返失败");
        }

        TestCaseHelpers.Report($"'a😀中b' 的 UTF-8 总长 = {Utf8Column.Utf8Length(line)} 字节（UTF-16 {line.Length} code unit）");
    }

    private static void RewriteColumnMapping()
    {
        var (document, parse) = Open("IncludeMatrix.shader");
        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var result = assembler.Assemble(document, parse, parse.Passes[0], ShaderStage.Fragment);
        Check.True(result.Succeeded, "装配应当成功：" + result.FailureDetail);
        using var text = result.Text!;
        var rewrite = text.LineMap.RewriteAt(0);

        // 源行: 12 空格 + "#include_with_pragmas "Pragmas.hlsl""
        // 渲染: 12 空格 + "#include "Pragmas.hlsl""
        Check.Equal(20, rewrite.CommonPrefixLength, "公共前缀 = 缩进 12 + #include 8");
        // "#include_with_pragmas" 21 字符 + 空格 + 开引号：12 + 21 + 1 + 1 = 35。
        Check.Equal(35, rewrite.SourcePrefixLength, "源前缀（含开引号）长 35");
        Check.Equal(22, rewrite.RenderedPrefixLength, "渲染前缀（含开引号）长 22");

        Check.Equal(20, rewrite.MapRenderedColumnToSource(20), "公共前缀内恒等映射");
        Check.Equal(35, rewrite.MapRenderedColumnToSource(21), "指令名差异区夹紧到源引号列");
        Check.Equal(36, rewrite.MapRenderedColumnToSource(23), "渲染路径首字符映射到源路径首字符");

        // 尾区（闭合引号之后）：两段长度差应当被平移掉。
        var renderedTailColumn = rewrite.RenderedPrefixLength + rewrite.RenderedPathLength + 1;
        var sourceTailColumn = rewrite.SourcePrefixLength + rewrite.SourcePathLength + 1;
        Check.Equal(sourceTailColumn, rewrite.MapRenderedColumnToSource(renderedTailColumn), "闭合引号列");

        Check.True(
            text.LineMap.TryMapRenderedToSource(rewrite.RenderedLine, 23, out var sourceLine, out var sourceColumn, out _),
            "重写行应能映射回源");
        Check.Equal(20, sourceLine, "重写行映射回第 20 行");
        Check.Equal(36, sourceColumn, "重写行的路径首字符列");

        TestCaseHelpers.Report("列映射: 公共前缀 20 / 源前缀 35 / 渲染前缀 22 / 闭合引号 35→48");
    }

    private static void VirtualPathShape()
    {
        var withBackslashes = VirtualPath.For("D:\\Projects\\MyProj\\Assets\\Shaders\\Foo.shader", null);
        var withSlashes = VirtualPath.For("D:/Projects/MyProj/Assets/Shaders/Foo.shader", null);

        Check.Equal(withBackslashes, withSlashes, "同一物理路径的虚拟路径必须稳定（分隔符不影响）");
        Check.True(!withBackslashes.Contains(':', StringComparison.Ordinal), "虚拟路径不得含冒号（会与 file:line:col 的分隔符歧义）");
        Check.True(!withBackslashes.Contains('\\', StringComparison.Ordinal), "虚拟路径不得含反斜杠（#line 里会被转义吃掉）");
        Check.True(withBackslashes.StartsWith("vfs-", StringComparison.Ordinal), "脱离工程的文件退化为 vfs-<hash> 形态");
        Check.True(withBackslashes.EndsWith("/Foo.shader", StringComparison.Ordinal), "虚拟路径保留文件名");

        TestCaseHelpers.Report("虚拟路径: " + withBackslashes);
    }

    // ── 回归：探针行没有归属时也必须有编译单元 ──

    // 探针行就在这两行的位置：HLSLINCLUDE 内容行 / Shader 名所在行（任何 Pass 之外）。
    private const string SharedProbeSource = """
Shader "Test/SharedProbe"
{
    HLSLINCLUDE
    float _SharedValue;
    ENDHLSL

    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 positionOS : POSITION) : SV_POSITION { return positionOS; }
            float4 frag() : SV_Target { return float4(_SharedValue, _SharedValue, _SharedValue, 1); }
            ENDHLSL
        }
    }

    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 positionOS : POSITION) : SV_POSITION { return positionOS; }
            float4 frag() : SV_Target { return float4(1, 0, 0, 1); }
            ENDHLSL
        }
    }
}
""";

    /// <summary>
    /// 锁的行为：探针行落在 HLSLINCLUDE 共享块里时，必须选中所有引用该块的 Pass；
    /// 落在任何 Pass 之外时退化为全量扫描。修复前这两处都返回 0 个编译单元，
    /// 编辑共享块或 ShaderLab 头部完全不产生任何诊断刷新。
    /// </summary>
    private static void SharedBlockProbeSelectsPasses()
    {
        using var project = new TempProject("sharedprobe");
        var (document, parse) = OpenSource(project, "Assets/Shaders/SharedProbe.shader", SharedProbeSource);

        Check.Equal(2, parse.Passes.Count, "样例应切出 2 个 Pass");
        Check.Equal(1, parse.SharedBlocks.Count, "样例应切出 1 个 HLSLINCLUDE 共享块");

        var sharedLine = Corpus.LineNumberContaining(SharedProbeSource, "float _SharedValue;");
        var outsideLine = Corpus.LineNumberContaining(SharedProbeSource, "Test/SharedProbe");
        Check.True(sharedLine > 0 && outsideLine > 0, "样例里必须能找到这两条探针行");

        using var engine = CreateEngine();

        // ① 共享块内容行：共享块按 Unity 语义属于每一个同方言 Pass，两个 Pass 都得重编译。
        var inShared = engine.Analyze(document, parse, sharedLine, isSave: false);
        Check.Equal(2, inShared.UnitCount, "探针行在 HLSLINCLUDE 内时必须选中全部引用它的 Pass（修复前为 0）");
        Check.True(inShared.CompiledUnitCount > 0, "选中的单元必须真的送去编译，而不是只计数");

        // ② Shader 名那一行：既不在 Pass 也不在共享块里，无从归属 —— 退化为全量扫描。
        var outside = engine.Analyze(document, parse, outsideLine, isSave: false);
        Check.Equal(2, outside.UnitCount, "探针行在任何 Pass 之外时必须退化为全量扫描（修复前为 0）");

        TestCaseHelpers.Report(
            $"共享块探针行 {sharedLine} → 单元 {inShared.UnitCount}（编译 {inShared.CompiledUnitCount}）；"
            + $"Pass 外探针行 {outsideLine} → 单元 {outside.UnitCount}（缓存 {outside.CachedUnitCount}）");
    }

    // ── 回归：DXC 调用失败不得被当成「干净」缓存 ──

    private const string FailureSource = """
Shader "Test/CompilerFailure"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 positionOS : POSITION) : SV_POSITION { return positionOS; }
            float4 frag() : SV_Target { return float4(1, 0, 0, 1); }
            ENDHLSL
        }
    }
}
""";

    /// <summary>
    /// 锁的行为：DXC 调用本身失败（HRESULT 非 0 / 结果对象为空）时，网关给出的只是一句说明文字，
    /// 解析出 0 条诊断。修复前这 0 条会按内容哈希写进单元缓存，之后这个文件永远显示为干净。
    /// 真机上无法稳定造出 HRESULT 非 0，因此用引擎的注入点复现该分支。
    /// </summary>
    private static void CompilerFailureNotCached()
    {
        using var project = new TempProject("compilerfail");
        var (document, parse) = OpenSource(project, "Assets/Shaders/CompilerFailure.shader", FailureSource);
        Check.Equal(1, parse.Passes.Count, "样例应切出 1 个 Pass");

        using var engine = CreateEngine();
        var failed = new DxcCompileResult
        {
            Status = -1,
            CompileHr = unchecked((int)0x80070005),
            DiagnosticText = "DXC 调用本身失败：0x80070005",
            ObjectSize = 0,
            ElapsedMicroseconds = 0,
            InvocationFailed = true,
        };
        engine.CompileOverride = (_, _) => failed;

        var first = engine.Analyze(document, parse, probeLine: 0, isSave: true);
        Check.True(
            first.Items.Any(i => i.Code == DiagnosticEngineCodes.InvariantViolation),
            "DXC 调用失败必须变成一条用户可见的错误（修复前解析出 0 条，什么都不报）");
        Check.True(first.HasErrors, "失败诊断必须是 Error 级");
        // 注意：CachedUnitCount 统计的是「命中」而不是「写入」，这里恒为 0，不构成门禁 ——
        // 真正的门禁是下面第二轮必须真的重新编译。

        var second = engine.Analyze(document, parse, probeLine: 0, isSave: true);
        // 失败单元永远进不了 compiled（它在写缓存前就中止了），所以这里不能拿 CompiledUnitCount 当门禁；
        // 真正有区分度的是「第二次有没有命中缓存」与「第二次有没有重新跑一遍编译」。
        Check.Equal(0, second.CachedUnitCount, "失败结果不得当成干净结果缓存：第二次不得命中缓存");
        Check.True(second.AbortedUnitCount > 0, "第二次必须真的重跑编译并中止（命中缓存时这里会是 0）");
        Check.True(
            second.Items.Any(i => i.Code == DiagnosticEngineCodes.InvariantViolation),
            "重试后仍须报出同一条失败诊断（被缓存成干净时这里会是 0 条）");

        TestCaseHelpers.Report(
            $"失败单元：首次 {first.Items.Count} 条诊断 / 中止 {first.AbortedUnitCount}；"
            + $"二次 {second.Items.Count} 条 / 缓存 {second.CachedUnitCount}");
    }

    // ── 回归：超过单次分析上限的 Pass 不得被当成「干净」 ──

    /// <summary>
    /// 锁的行为：单元数超过 MaxUnitsPerAnalysis 时，被截断的 Pass 这一次根本没编译。
    /// 修复前截断是静默的：报告里没有任何痕迹，宿主又把这些 Pass 当成「零诊断」发布，
    /// 于是大文件改一处之后，后半段 Pass 的红线自己消失。
    /// 现在报告必须点名「哪个 Pass 没检查到」（宿主据此不发布空诊断集，保留旧红线），
    /// 并在文件级留一条可见的 warning。
    /// </summary>
    private static void OverBudgetPassNotPublishedAsClean()
    {
        using var project = new TempProject("budget");
        var (document, parse) = OpenSource(project, "Assets/Shaders/SharedProbe.shader", SharedProbeSource);
        Check.Equal(2, parse.Passes.Count, "样例应切出 2 个 Pass");
        var secondPass = parse.Passes[1];

        // 保存路径 = 全量扫描。上限 1 时第二个 Pass 被截断，它这一次没有编译。
        // 两个引擎分开建（先释放再建下一个）：本套用例的其他部分都假设同时只有一个网关活着。
        DiagnosticReport report;
        using (var engine = CreateEngine(new DiagnosticEngineOptions { MaxUnitsPerAnalysis = 1 }))
        {
            report = engine.Analyze(document, parse, probeLine: 0, isSave: true);
        }

        Check.Equal(1, report.UnitCount, "上限 1 时只应选中一个单元");
        Check.Equal(1, report.SkippedUnitCount, "被截断的单元必须单独计数（修复前它和「不存在」无从区分）");
        Check.True(
            report.SkippedPassIndices.Contains(secondPass.PassIndex),
            "被截断的 Pass 序号必须报出来 —— 宿主靠它跳过空诊断集发布，否则旧红线会被擦掉");
        Check.True(
            report.CompiledUnitCount + report.CachedUnitCount > 0,
            "被选中的单元必须真的送去编译，而不是只计数");

        var trace = report.Items.Where(i => i.Code == DiagnosticEngineCodes.AnalysisBudgetExceeded).ToList();
        Check.Equal(1, trace.Count, "超限必须留下一条可见的 warning（修复前报告里 0 条痕迹）");
        Check.True(
            trace[0].Line < secondPass.StartLineNumber,
            "这条痕迹不得落在被跳过的 Pass 行区间里，否则发布时会把那个槽位的旧诊断顶掉");

        // 反向门禁：没超限时不得报截断，否则这条 warning 会常驻在正常文件上。
        DiagnosticReport all;
        using (var unbounded = CreateEngine())
        {
            all = unbounded.Analyze(document, parse, probeLine: 0, isSave: true);
        }

        Check.Equal(0, all.SkippedUnitCount, "未超限时 SkippedUnitCount 必须为 0");
        Check.True(
            all.Items.All(i => i.Code != DiagnosticEngineCodes.AnalysisBudgetExceeded),
            "未超限时不得出现截断提示");

        TestCaseHelpers.Report(
            $"上限 1：单元 {report.UnitCount} / 跳过 {report.SkippedUnitCount}（Pass {string.Join(",", report.SkippedPassIndices)}）；"
            + $"默认上限：单元 {all.UnitCount} / 跳过 {all.SkippedUnitCount}");
    }

    // ── 回归：外部头文件的 warning 不得被主文件的 #pragma message 规则吞掉 ──

    // 第 9 行是 #pragma message —— 外部头文件的一条 warning 只要报在同一行号上，
    // 修复前就会被当成「主文件的 pragma 提示」丢掉。
    private const string PragmaShadowSource = """
Shader "Test/PragmaShadow"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #include "Bad.hlsl"
            #pragma message("主文件里的一条编译器提示")
            #pragma vertex vert
            #pragma fragment frag
            float4 vert(float4 positionOS : POSITION) : SV_POSITION { return positionOS; }
            float4 frag() : SV_Target { return float4(1, 1, 1, 1); }
            ENDHLSL
        }
    }
}
""";

    // 行数足够长，让「第 9 行」这条 warning 在外头文件里是自然的。
    private const string PragmaShadowHeader = """
// 外部头文件：只为造一条行号与主文件 #pragma message 行相同的 warning
float _Unused0;
float _Unused1;
float _Unused2;
float _Unused3;
float _Unused4;
float _Unused5;
float _Unused6;
float _Unused7;
float _Unused8;
float _Unused9;
float _Unused10;
""";

    /// <summary>
    /// 锁的行为：pragma message 过滤必须先看诊断的真实归属。修复前它直接拿 raw.Line 去查
    /// 主文件的源行 —— 外部头文件里第 9 行的 warning 撞上主文件第 9 行的 #pragma message 就被
    /// 静默丢弃（结果里 0 条，只留下一个 DroppedPragmaMessages 计数）。
    /// 现在只有真属于主文件、且那一行确实是 #pragma message 才丢；连归属都算不上的也必须留在结果里。
    /// </summary>
    private static void ExternalHeaderWarningKept()
    {
        using var project = new TempProject("pragmashadow");
        var (document, parse) = OpenSource(project, "Assets/Shaders/PragmaShadow.shader", PragmaShadowSource);
        project.Write("Assets/Shaders/Bad.hlsl", PragmaShadowHeader);
        Check.Equal(1, parse.Passes.Count, "样例应切出 1 个 Pass");

        var pragmaLine = Corpus.LineNumberContaining(PragmaShadowSource, "#pragma message");
        var includeLine = Corpus.LineNumberContaining(PragmaShadowSource, "#include \"Bad.hlsl\"");
        var headerPath = Path.Combine(project.Layout.ProjectRoot, "Assets", "Shaders", "Bad.hlsl");
        Check.True(pragmaLine > 0 && includeLine > 0, "样例里必须能找到 pragma 行与 include 行");

        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var assembly = assembler.Assemble(document, parse, parse.Passes[0], ShaderStage.Fragment);
        Check.True(assembly.Succeeded, "装配应当成功：" + assembly.FailureDetail);
        using var text = assembly.Text!;

        // 头文件里的 warning 行号故意与主文件的 #pragma message 行相同。
        var warningText = headerPath + ":" + pragmaLine + ":5: warning: 未使用的变量 _Unused0";

        var withBinding = new DxcCompileResult
        {
            Status = 0,
            CompileHr = 0,
            DiagnosticText = warningText,
            ObjectSize = 4,
            ElapsedMicroseconds = 0,
        };

        // ① 头文件在 include 绑定表里：warning 必须挂回那条 #include 并留在结果里。
        var attributed = new DiagnosticAttributor().Attribute(
            withBinding,
            text,
            new[] { new IncludeBinding(includeLine, 13, "Bad.hlsl", headerPath) },
            document.FileUri,
            parse.Passes[0].StartLineNumber);

        Check.Equal(0, attributed.DroppedPragmaMessages, "外部头文件的 warning 不得被当成主文件的 pragma message 丢弃");
        var kept = attributed.Items.SingleOrDefault(i => i.Message.Contains("未使用的变量", StringComparison.Ordinal));
        Check.True(kept is not null, "这条 warning 必须出现在结果里（修复前被静默丢弃，结果 0 条）");
        Check.Equal(includeLine, kept!.Line, "红线必须挂回主文件里那条 #include 的行");
        Check.Equal(DiagnosticSeverity.Warning, kept.Severity, "严重级别必须保持 warning");

        // ② 归属不上（绑定表里没有这个文件）：仍然不得静默吞掉，只能降级成「外部头文件」条目。
        var orphan = new DiagnosticAttributor().Attribute(
            withBinding,
            text,
            Array.Empty<IncludeBinding>(),
            document.FileUri,
            parse.Passes[0].StartLineNumber);

        Check.Equal(0, orphan.DroppedPragmaMessages, "归属不明的 warning 同样不得走 pragma 过滤");
        Check.Equal(1, orphan.Unmapped, "归属不上的诊断必须被记账，便于查绑定表漏项");
        Check.True(
            orphan.Items.Any(i => i.Message.Contains("外部头文件错误", StringComparison.Ordinal)),
            "归属不上的 warning 必须留在结果里（修复前这里同样是 0 条）");

        TestCaseHelpers.Report(
            $"头文件 warning 行 {pragmaLine}：有绑定 → 挂回第 {kept.Line} 行；无绑定 → 未归属 {orphan.Unmapped} 条");
    }

    // ── 回归：释放网关必须排在在途分析之后 ──

    /// <summary>
    /// 锁的行为：Dispose 必须等在途分析落地之后才释放原生网关，而且等待有上限。
    /// </summary>
    /// <remarks>
    /// 现场：停机时宿主释放分析器 → 引擎释放网关 → FreeLibrary(dxcompiler.dll)，
    /// 而那一刻可能还有一次编译在跑；它随后仍会去调 dxcompiler 的 vtable，进程直接原生崩溃
    /// （没有托管异常、没有堆栈，AOT 下只剩一个访问冲突）。修复前 Dispose 立刻返回，
    /// 网关就这样在编译进行中被卸掉。
    /// 真编译的时长不可控，所以用引擎已有的 CompileOverride 注入一个「慢编译」，
    /// 靠它把「Dispose 与编译是否重叠」这个顺序固定下来 —— 顺序正是崩溃的充要条件。
    /// </remarks>
    private static void DisposeWaitsForInFlightAnalysis()
    {
        using var project = new TempProject("disposewait");
        var (document, parse) = OpenSource(project, "Assets/Shaders/DisposeWait.shader", FailureSource);
        Check.Equal(1, parse.Passes.Count, "样例应切出 1 个 Pass");

        var engine = CreateEngine();

        var compileEntered = new ManualResetEventSlim(false);
        var compileMayExit = new ManualResetEventSlim(false);
        var compileExited = false;

        engine.CompileOverride = (_, _) =>
        {
            compileEntered.Set();
            compileMayExit.Wait(TimeSpan.FromSeconds(10));
            compileExited = true;
            return new DxcCompileResult
            {
                Status = 0,
                CompileHr = 0,
                DiagnosticText = string.Empty,
                ObjectSize = 4,
                ElapsedMicroseconds = 0,
            };
        };

        var analysis = Task.Run(() => engine.Analyze(document, parse, probeLine: 0, isSave: true));
        Check.True(compileEntered.Wait(TimeSpan.FromSeconds(10)), "分析应已进入编译阶段");

        var dispose = Task.Run(engine.Dispose);

        // 编译没放行之前 Dispose 绝不能返回：一旦返回，网关就已经卸载，剩下的编译必然踩空。
        Check.True(
            !dispose.Wait(TimeSpan.FromMilliseconds(500)),
            "编译尚未结束时 Dispose 不得返回（修复前它立刻返回，网关就在编译进行中被释放）");

        compileMayExit.Set();

        Check.True(dispose.Wait(TimeSpan.FromSeconds(10)), "放行编译后 Dispose 必须尽快返回：等待有上限，不能卡死");
        Check.True(compileExited, "Dispose 返回时编译必须已经退场");

        Check.True(analysis.Wait(TimeSpan.FromSeconds(10)), "在途分析必须已经落地");
        var report = analysis.Result;
        Check.True(report.CompiledUnitCount > 0, "在途分析必须完整跑完（而不是被释放打断）");

        TestCaseHelpers.Report(
            "释放顺序：编译在途时 Dispose 阻塞，放行后返回（分析编译单元 " + report.CompiledUnitCount + "）");
    }

    private static void PlatformEditorLogFirst()
    {
        using var project = new TempProject("editorlog");
        project.Write("ProjectSettings/ProjectSettings.asset", "  m_ActiveColorSpace: 0\n");
        project.Write("Logs/Editor.log", "some noise\nPlatform defines: SHADER_API_D3D11 UNITY_NO_DXT5nm=1\nmore noise\n");

        var set = PlatformMacroResolver.Resolve(project.Layout, null, "2022.3.62f3c1");
        Check.Equal(PlatformMacroSourceKind.EditorLog, set.SourceKind, "有 Platform defines 行时必须优先采信它");
        Check.Contains(set.Macros.Select(m => m.Name), "SHADER_API_D3D11", "应读到实测宏");
        Check.Contains(set.Macros.Select(m => m.Name), "UNITY_NO_DXT5nm", "应读到带值形态的实测宏");
        Check.True(!set.HasPostulated, "实测层可用时不应再混入推定项");

        var dxt = set.Macros.Single(m => m.Name == "UNITY_NO_DXT5nm");
        Check.Equal("1", dxt.Value, "带 = 的 token 应拆成名字与值");
        Check.Equal(MacroProvenance.Measured, dxt.Provenance, "Editor.log 来源是实测");

        TestCaseHelpers.Report("Editor.log 层: " + set.Describe());
    }

    private static void PlatformProjectSettingsTier()
    {
        using var gammaProject = new TempProject("gamma");
        gammaProject.Write("ProjectSettings/ProjectSettings.asset", "  m_ActiveColorSpace: 0\n");
        var gamma = PlatformMacroResolver.Resolve(gammaProject.Layout, null, "2022.3.62f3c1");
        Check.Contains(gamma.Macros.Select(m => m.Name), "UNITY_COLORSPACE_GAMMA", "Gamma 色彩空间必须定义 UNITY_COLORSPACE_GAMMA");

        using var linearProject = new TempProject("linear");
        linearProject.Write("ProjectSettings/ProjectSettings.asset", "  m_ActiveColorSpace: 1\n");
        var linear = PlatformMacroResolver.Resolve(linearProject.Layout, null, "2022.3.62f3c1");
        Check.DoesNotContain(linear.Macros.Select(m => m.Name), "UNITY_COLORSPACE_GAMMA", "Linear 色彩空间不得定义该宏（URP 有 5 处 #if UNITY_COLORSPACE_GAMMA）");
        Check.True(
            linear.Notes.Any(n => n.Contains("Linear", StringComparison.Ordinal)),
            "Linear 这个「不定义」的结论必须留下可追溯的备注");

        TestCaseHelpers.Report("ProjectSettings 层: " + string.Join(" / ", linear.Notes));
    }

    private static void PlatformTableTier()
    {
        using var project = new TempProject("table");
        project.Write("ProjectSettings/ProjectSettings.asset", "  m_ActiveColorSpace: 1\n");

        var set = PlatformMacroResolver.Resolve(project.Layout, null, "2022.3.62f3c1");
        Check.Equal(PlatformMacroSourceKind.Mixed, set.SourceKind, "推导 + 推定混合来源");
        Check.Contains(set.Macros.Select(m => m.Name), "SHADER_API_D3D11", "版本×平台表应给出 D3D11 平台宏");
        Check.Contains(set.Macros.Select(m => m.Name), "UNITY_COMPILER_DXC", "DXC 编译路径宏");
        Check.DoesNotContain(set.Macros.Select(m => m.Name), "SHADER_API_DESKTOP", "URP+Core 里 0 次引用的宏不得注入");
        Check.True(set.HasPostulated, "推定项必须被标记出来");

        foreach (var macro in set.Macros.Where(m => m.Provenance == MacroProvenance.Postulated))
        {
            Check.True(macro.Source.Contains("版本×平台表", StringComparison.Ordinal), "推定项必须写明来源");
        }

        TestCaseHelpers.Report("版本×平台表: " + set.Describe());
    }
}

/// <summary>自检用的临时工程目录（只造出平台宏解算需要的那几个文件）。</summary>
internal sealed class TempProject : IDisposable
{
    private readonly string _root;

    public TempProject(string name)
    {
        _root = Path.Combine(Path.GetTempPath(), "microshader-selftest", name + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_root, "ProjectSettings"));
        File.WriteAllText(
            Path.Combine(_root, "ProjectSettings", "ProjectVersion.txt"),
            "m_EditorVersion: 2022.3.62f3c1\n");
        Layout = UnityProjectLayout.FromRoot(_root);
    }

    public UnityProjectLayout Layout { get; }

    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响验收结论。
        }
    }
}
