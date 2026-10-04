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
        TestSuite.Add("Platform.EditorLog", "Editor.log 层优先于推导与推定", PlatformEditorLogFirst);
        TestSuite.Add("Platform.Project", "ProjectSettings 层推导色彩空间宏", PlatformProjectSettingsTier);
        TestSuite.Add("Platform.Table", "版本×平台表只给平台能力宏并标推定", PlatformTableTier);
    }

    // ───────────────────────────── 沙盒基座 ─────────────────────────────

    private static ShaderContextRegistry EmptyRegistry() => new();

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
