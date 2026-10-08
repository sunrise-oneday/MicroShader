using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 1（"ShaderLabStateMachine"）的固定用例验收。
/// 每个用例都对应规格书里一条明确的边界要求，失败信息直接写清「哪条规则被破坏」。
/// </summary>
internal static class FixtureTests
{
    private const string Suite = "ShaderLab.Fixtures";

    public static void Register()
    {
        TestSuite.Add(Suite, "SimpleUrp_结构切片", SimpleUrpStructure);
        TestSuite.Add(Suite, "SimpleUrp_入口与变体推导", SimpleUrpEntriesAndVariants);
        TestSuite.Add(Suite, "TopLevelInclude_文件级共享块跨SubShader生效", TopLevelIncludeScope);
        TestSuite.Add(Suite, "LegacyCg_抑制原生派发", LegacyCgSuppressed);
        TestSuite.Add(Suite, "ModernCg_引用核心头文件时不抑制", ModernCgNotSuppressed);
        TestSuite.Add(Suite, "UnterminatedBlock_EOF熔断", UnterminatedBlockFuse);
        TestSuite.Add(Suite, "MarkersInCommentsAndStrings_标记词不得误触发", MarkersInCommentsAndStrings);
        TestSuite.Add(Suite, "OrphanBlock_游离块仍产出编译单元", OrphanBlock);
        TestSuite.Add(Suite, "MixedBlocks_共享块按方言隔离", MixedBlocksIsolation);
        TestSuite.Add(Suite, "UnterminatedBlockComment_EOF熔断", UnterminatedBlockCommentFuse);
        TestSuite.Add(Suite, "NestedMarkerForceClose_强制闭合", NestedMarkerForceClose);
        TestSuite.Add(Suite, "PragmaRobustness_空白容错与降级重写记录", PragmaRobustness);
        TestSuite.Add(Suite, "UnterminatedString_行长诊断", UnterminatedString);
        TestSuite.Add(Suite, "结果复用_两次解析结果一致", ResultReuse);
        TestSuite.Add(Suite, "全语料_诊断码集合与预期一致", CorpusDiagnosticSurface);
    }

    private static ShaderLabParseResult ParseFixture(string fileName, out string text)
    {
        text = Corpus.Read(fileName);
        var machine = new ShaderLabStateMachine();
        return machine.Parse(Corpus.PathOf(fileName), text);
    }

    private static void SimpleUrpStructure()
    {
        var result = ParseFixture("SimpleUrp.shader", out var text);

        Check.Equal(1, result.SubShaderCount, "SubShader 计数");
        Check.Equal(1, result.Passes.Count, "编译单元数量");
        Check.Equal(0, result.SharedBlocks.Count, "不应识别出文件级共享块");
        Check.Equal(0, result.Diagnostics.Count, "健康 shader 不应产出任何诊断");
        Check.True(!result.HasUnterminatedBlock, "不应有未闭合块");

        var pass = result.Passes[0];
        Check.Equal(0, pass.PassIndex, "Pass 序号");
        Check.Equal("ForwardLit", pass.PassName, "Pass 名（Name \"ForwardLit\"）");
        Check.Equal(ShaderBlockKind.Hlsl, pass.Kind, "方言");
        Check.Equal(0, pass.SubShaderIndex, "SubShader 序号");
        Check.Equal(0, pass.BlockOrdinalInPass, "同一 Pass 内的块序号");
        Check.True(!pass.IsOrphan, "Pass 内的块不应被判为游离");

        Check.Equal("HLSLPROGRAM", Corpus.LineAt(text, pass.StartLineNumber).Trim(), "起始行应为 HLSLPROGRAM");
        Check.Equal("ENDHLSL", Corpus.LineAt(text, pass.EndLineNumber).Trim(), "结束行应为 ENDHLSL");

        // 原文切片必须与源文本逐字节一致（Segment 模型不变量 I2 的前置条件）。
        var slice = pass.RawHlslBlock.ToString();
        Check.True(slice.Contains("#pragma vertex vert", StringComparison.Ordinal), "块切片应含顶点 pragma");
        Check.True(slice.Contains("ENDHLSL", StringComparison.Ordinal) is false, "块切片不得包含结束标记本身");
        Check.True(slice.Contains("HLSLPROGRAM", StringComparison.Ordinal) is false, "块切片不得包含开启标记本身");
    }

    private static void SimpleUrpEntriesAndVariants()
    {
        var result = ParseFixture("SimpleUrp.shader", out _);
        var pass = result.Passes[0];

        Check.Equal("vert", pass.VertexEntry ?? string.Empty, "顶点入口");
        Check.Equal("frag", pass.FragmentEntry ?? string.Empty, "片元入口");

        Check.SequenceEqual(
            new[] { "_MAIN_LIGHT_SHADOWS", "_SHADOWS_SOFT", "_ALPHATEST_ON" },
            pass.ActivePragmaDefines,
            "选变体策略取每组首项（ADR-006）");

        Check.Equal(3, pass.Variants.Count, "变体声明条数");
        Check.Equal(ShaderVariantKind.MultiCompile, pass.Variants[0].Kind, "第一条应为 multi_compile");
        Check.Equal(ShaderStage.None, pass.Variants[0].Stage, "无限定后缀 → 全阶段");
        Check.SequenceEqual(
            new[] { "_MAIN_LIGHT_SHADOWS", "_MAIN_LIGHT_SHADOWS_CASCADE" },
            pass.Variants[0].Keywords,
            "第一条多编译声明的关键字集");

        Check.Equal(ShaderVariantKind.ShaderFeature, pass.Variants[2].Kind, "第三条应为 shader_feature");
        Check.Equal(ShaderStage.Fragment, pass.Variants[2].Stage, "shader_feature_local_fragment → 片元阶段");
        Check.True(pass.Variants[2].IsLocal, "应识别 _local 修饰");

        Check.Equal(1, pass.Includes.Count, "#include 条数");
        Check.Equal(
            "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl",
            pass.Includes[0].Path,
            "#include 路径应原样记录（归一化属模块 2）");
        Check.True(!pass.Includes[0].IsWithPragmas, "#include 不是 include_with_pragmas");
    }

    private static void TopLevelIncludeScope()
    {
        var result = ParseFixture("TopLevelInclude.shader", out _);

        Check.Equal(1, result.SharedBlocks.Count, "应识别出 1 个文件级 HLSLINCLUDE 块");
        Check.Equal(2, result.SubShaderCount, "SubShader 计数");
        Check.Equal(2, result.Passes.Count, "编译单元数量");

        var shared = result.SharedBlocks[0];
        Check.True(shared.IsShared, "HLSLINCLUDE 必须标记为共享块");
        Check.Equal(ShaderBlockKind.Hlsl, shared.Kind, "共享块方言应为 HLSL");

        foreach (var pass in result.Passes)
        {
            // 关键回归点：HLSLINCLUDE 写在第一个 SubShader 之前的 Shader 顶层（URP 官方写法），
            // 必须作用于本文件内全部 HLSLPROGRAM 块，而不是「同一 SubShader 内」。
            Check.SequenceEqual(new[] { "0" }, pass.ApplicableSharedBlockIndices.Select(i => i.ToString()), "共享块索引");
            Check.True(pass.StartLineNumber > shared.StartLineNumber, "共享块位于 Pass 之前");
        }

        // 共享块自身不得被当成 Pass 编译单元。
        Check.True(result.Passes.All(p => !p.IsSharedBlock), "共享块不应出现在 Passes 编译单元中");
    }

    private static void LegacyCgSuppressed()
    {
        var result = ParseFixture("LegacyCg.shader", out _);
        var pass = result.Passes[0];

        Check.Equal(ShaderBlockKind.Cg, pass.Kind, "方言应为 CG");
        Check.True(pass.IsLegacyCg, "应标记为遗留 CG 块");
        Check.True(pass.SuppressNativeDispatch, "未引用现代核心头文件时必须抑制 DXC 派发");
        Check.NotNull(pass.SuppressReason, "抑制原因必须有可读文案");

        Check.Equal(ShaderVariantKind.BuiltIn, pass.Variants[0].Kind, "multi_compile_fog 应判为 Unity 内建变体指令");
        Check.Equal(0, pass.Variants[0].Keywords.Count, "内建变体指令不推导关键字");
    }

    private static void ModernCgNotSuppressed()
    {
        var result = ParseFixture("ModernCg.shader", out _);
        var pass = result.Passes[0];

        Check.True(pass.IsLegacyCg, "仍是 CG 方言");
        Check.True(!pass.SuppressNativeDispatch, "引用了 com.unity.render-pipelines 核心头文件时不得抑制派发");
    }

    private static void UnterminatedBlockFuse()
    {
        var result = ParseFixture("UnterminatedBlock.shader", out var text);
        var openLine = Corpus.LineNumberContaining(text, "HLSLPROGRAM");

        Check.True(result.HasUnterminatedBlock, "应标记存在未闭合块");
        Check.Equal(1, result.Passes.Count, "未闭合块仍应产出编译单元");
        Check.True(result.Passes[0].IsUnterminated, "编译单元应标记未闭合");

        var diagnostic = Check.Single(result.Diagnostics, d => d.Code == ShaderLabDiagnosticCodes.UnterminatedBlock, "应有且仅有一条 EOF 熔断诊断");
        Check.Equal(DiagnosticSeverity.Error, diagnostic.Severity, "熔断必须是 Error");
        Check.Equal(openLine, diagnostic.Line, "合成诊断必须挂在「开启标记那一行」，不是永远第 1 行");
        Check.True(diagnostic.Message.Contains("未闭合", StringComparison.Ordinal), "文案应说明未闭合");
    }

    private static void MarkersInCommentsAndStrings()
    {
        var result = ParseFixture("MarkersInCommentsAndStrings.shader", out _);

        // 属性字符串里的 "Pass HLSLPROGRAM ENDHLSL"、行注释里的 Pass、块注释里的整段
        // HLSLPROGRAM/ENDHLSL/Pass、UsePass 里的 Pass 子串、块内的 // ENDHLSL 与 /* CGPROGRAM ENDCG */
        // 全部不得触发状态迁移。
        Check.Equal(1, result.SubShaderCount, "只有 1 个真实 SubShader");
        Check.Equal(1, result.Passes.Count, "只有 1 个真实代码块");
        Check.Equal(0, result.SharedBlocks.Count, "不得把注释里的 HLSLPROGRAM 当成共享块");
        Check.Equal(0, result.Diagnostics.Count, "不得产生任何诊断");
        Check.Equal("Real", result.Passes[0].PassName, "只有 Name \"Real\" 是真实 Pass 名");
    }

    private static void OrphanBlock()
    {
        var result = ParseFixture("OrphanBlock.shader", out var text);
        var orphanLine = Corpus.LineNumberContaining(text, "HLSLPROGRAM");
        var realLine = Corpus.LineNumberContaining(text, "HLSLPROGRAM", 2);

        Check.Equal(2, result.Passes.Count, "游离块仍应产出编译单元");
        var orphan = result.Passes[0];
        var real = result.Passes[1];

        Check.Equal(orphanLine, orphan.StartLineNumber, "游离块起始行");
        Check.Equal(-1, orphan.PassIndex, "游离块的 PassIndex 必须为 -1");
        Check.True(orphan.IsOrphan, "应标记为游离块");

        Check.Equal(realLine, real.StartLineNumber, "Pass 内块的起始行");
        Check.Equal(0, real.PassIndex, "Pass 内块序号");
        Check.True(!real.IsOrphan, "Pass 内的块不应是游离块");
    }

    private static void MixedBlocksIsolation()
    {
        var result = ParseFixture("MixedBlocks.shader", out _);

        Check.Equal(2, result.SharedBlocks.Count, "应有 2 个共享块（CGINCLUDE + HLSLINCLUDE）");
        Check.Equal(ShaderBlockKind.Cg, result.SharedBlocks[0].Kind, "第 0 个共享块是 CGINCLUDE");
        Check.Equal(ShaderBlockKind.Hlsl, result.SharedBlocks[1].Kind, "第 1 个共享块是 HLSLINCLUDE");
        Check.Equal(3, result.Passes.Count, "2 个 HLSL 块 + 1 个 CG 块");

        var firstHlsl = result.Passes[0];
        var secondHlsl = result.Passes[1];
        var cg = result.Passes[2];

        Check.Equal(0, firstHlsl.PassIndex, "两个 HLSL 块同属第 0 个 Pass");
        Check.Equal(0, secondHlsl.PassIndex, "两个 HLSL 块同属第 0 个 Pass");
        Check.Equal(0, firstHlsl.BlockOrdinalInPass, "同 Pass 内块序号 0");
        Check.Equal(1, secondHlsl.BlockOrdinalInPass, "同 Pass 内块序号 1");
        Check.Equal(1, cg.PassIndex, "CG 块属第 1 个 Pass");

        // 关键：HLSLINCLUDE 与 CGINCLUDE 互不交叉。
        Check.SequenceEqual(new[] { "1" }, firstHlsl.ApplicableSharedBlockIndices.Select(i => i.ToString()), "HLSL 块只吃 HLSLINCLUDE");
        Check.SequenceEqual(new[] { "1" }, secondHlsl.ApplicableSharedBlockIndices.Select(i => i.ToString()), "HLSL 块只吃 HLSLINCLUDE");
        Check.SequenceEqual(new[] { "0" }, cg.ApplicableSharedBlockIndices.Select(i => i.ToString()), "CG 块只吃 CGINCLUDE");
    }

    private static void UnterminatedBlockCommentFuse()
    {
        var result = ParseFixture("UnterminatedBlockComment.shader", out var text);
        var commentLine = Corpus.LineNumberContaining(text, "/*");

        Check.True(result.HasUnterminatedBlockComment, "应标记未闭合块注释");
        var diagnostic = Check.Single(result.Diagnostics, d => d.Code == ShaderLabDiagnosticCodes.UnterminatedBlockComment, "应有未闭合块注释诊断");
        Check.Equal(commentLine, diagnostic.Line, "诊断应挂在 /* 所在行");

        // 整段被注释吞掉 → 不得切出任何代码块。
        Check.Equal(0, result.Passes.Count, "被注释吞掉的代码不得产出编译单元");
    }

    private static void NestedMarkerForceClose()
    {
        var result = ParseFixture("NestedMarkerForceClose.shader", out var text);
        var secondOpenLine = Corpus.LineNumberContaining(text, "HLSLPROGRAM", 2);

        Check.Equal(2, result.Passes.Count, "强制闭合后应产出两个编译单元");
        var forced = result.Passes[0];
        var second = result.Passes[1];

        Check.True(forced.IsForceClosed, "第一个块应标记为被强制闭合");
        Check.True(forced.IsUnterminated, "被强制闭合等价于未正常闭合");
        Check.Equal(secondOpenLine - 1, forced.EndLineNumber, "被强制闭合块的结束行应为新标记的上一行");
        Check.True(second.IsForceClosed is false, "第二个块本身没有被强制闭合");
        Check.Equal(secondOpenLine, second.StartLineNumber, "第二个块从新标记行开始");
        Check.Equal("vert", forced.VertexEntry ?? string.Empty, "第一个块保留自己的入口");
        Check.Equal("frag", second.FragmentEntry ?? string.Empty, "第二个块保留自己的入口");

        Check.Single(result.Diagnostics, d => d.Code == ShaderLabDiagnosticCodes.ForceClosedBlock, "应有强制闭合诊断");
    }

    private static void PragmaRobustness()
    {
        var result = ParseFixture("PragmaRobustness.shader", out var text);
        var pass = result.Passes[0];

        Check.Equal("vert", pass.VertexEntry ?? string.Empty, "制表符分隔的 vertex pragma");
        Check.Equal("frag", pass.FragmentEntry ?? string.Empty, "多空格分隔的 fragment pragma");
        Check.Equal(0, result.Diagnostics.Count, "健康文件不得产出诊断");

        var localFragment = Check.Single(pass.Variants, v => v.DirectiveName == "multi_compile_local_fragment", "应有 multi_compile_local_fragment 声明");
        Check.True(localFragment.IsLocal, "应识别 _local");
        Check.Equal(ShaderStage.Fragment, localFragment.Stage, "应识别 _fragment");
        Check.Equal("_FOO", localFragment.SelectedKeyword, "首项 _FOO 被选为默认变体");
        Check.SequenceEqual(new[] { "_FOO", "_BAR" }, localFragment.Keywords, "关键字集应剔除占位符");

        var secondSet = Check.Single(pass.Variants, v => v.DirectiveName == "multi_compile" && v.Keywords.Contains("_SECOND_SET"), "应有 __ 开头的多编译声明");
        Check.Equal("_SECOND_SET", secondSet.SelectedKeyword, "首项 __ 代表关闭变体，应选下一个真实关键字");

        Check.Equal(2, pass.Includes.Count, "#include 与 #include_with_pragmas 各一条");
        var withPragmas = Check.Single(pass.Includes, i => i.IsWithPragmas, "应记录 include_with_pragmas");
        Check.Equal(
            "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl",
            withPragmas.Path,
            "include_with_pragmas 路径原样记录（降级重写属模块 3）");
        Check.Equal(Corpus.LineNumberContaining(text, "#include_with_pragmas"), withPragmas.LineNumber, "include 行号");

        // 非变体 pragma（target / only_renderers）不得被误当成变体声明。
        Check.True(pass.Variants.All(v => v.DirectiveName is not ("target" or "only_renderers")), "target/only_renderers 不是变体声明");
    }

    private static void UnterminatedString()
    {
        var result = ParseFixture("UnterminatedString.shader", out var text);
        var brokenLine = Corpus.LineNumberContaining(text, "Name \"Broken");

        var diagnostic = Check.Single(result.Diagnostics, d => d.Code == ShaderLabDiagnosticCodes.UnterminatedString, "应有未闭合字符串诊断");
        Check.Equal(brokenLine, diagnostic.Line, "诊断行号");
        Check.Equal(DiagnosticSeverity.Warning, diagnostic.Severity, "未闭合字符串按 Warning 上报");
    }

    private static void ResultReuse()
    {
        var text = Corpus.Read("MixedBlocks.shader");
        var machine = new ShaderLabStateMachine();

        var fresh = machine.Parse(Corpus.PathOf("MixedBlocks.shader"), text);
        var snapshot = Snapshot(fresh);

        var reused = new ShaderLabParseResult();
        machine.Parse(Corpus.PathOf("MixedBlocks.shader"), text, reused);
        Check.Equal(snapshot, Snapshot(reused), "复用结果对象的第二次解析必须与首次完全一致");

        machine.Parse(Corpus.PathOf("MixedBlocks.shader"), text, reused);
        Check.Equal(snapshot, Snapshot(reused), "第三次解析（切片对象全部来自池）仍必须一致");
    }

    private static string Snapshot(ShaderLabParseResult result)
    {
        var lines = new List<string>
        {
            $"file={result.FilePath}|subshaders={result.SubShaderCount}|shared={result.SharedBlocks.Count}|passes={result.Passes.Count}|diag={result.Diagnostics.Count}",
        };

        foreach (var pass in result.Passes)
        {
            lines.Add(
                $"pass idx={pass.PassIndex} name={pass.PassName} kind={pass.Kind} start={pass.StartLineNumber} end={pass.EndLineNumber} " +
                $"ord={pass.BlockOrdinalInPass} orphan={pass.IsOrphan} v={pass.VertexEntry} f={pass.FragmentEntry} " +
                $"kw=[{string.Join(",", pass.ActivePragmaDefines)}] shared=[{string.Join(",", pass.ApplicableSharedBlockIndices)}]");
        }

        return string.Join("\n", lines);
    }

    private static void CorpusDiagnosticSurface()
    {
        // 固定语料里「健康」的文件必须零诊断，「故意破坏」的文件必须只产出预期诊断码。
        var healthy = new[]
        {
            "SimpleUrp.shader",
            "TopLevelInclude.shader",
            "LegacyCg.shader",
            "ModernCg.shader",
            "MarkersInCommentsAndStrings.shader",
            "OrphanBlock.shader",
            "MixedBlocks.shader",
            "StageScopedKeywords.shader",
            "PragmaRobustness.shader",
        };

        var machine = new ShaderLabStateMachine();
        foreach (var fileName in healthy)
        {
            var result = machine.Parse(Corpus.PathOf(fileName), Corpus.Read(fileName));
            Check.Equal(
                0,
                result.Diagnostics.Count,
                $"健康用例 {fileName} 不得产出任何诊断（实际: {string.Join(", ", result.Diagnostics.Select(d => d.Code))}）");
        }

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["UnterminatedBlock.shader"] = ShaderLabDiagnosticCodes.UnterminatedBlock,
            ["UnterminatedBlockComment.shader"] = ShaderLabDiagnosticCodes.UnterminatedBlockComment,
            ["NestedMarkerForceClose.shader"] = ShaderLabDiagnosticCodes.ForceClosedBlock,
            ["UnterminatedString.shader"] = ShaderLabDiagnosticCodes.UnterminatedString,
        };

        foreach (var (fileName, code) in expected)
        {
            var result = machine.Parse(Corpus.PathOf(fileName), Corpus.Read(fileName));
            var codes = result.Diagnostics.Select(d => d.Code ?? "<null>").Distinct().ToArray();
            Check.SequenceEqual(new[] { code }, codes, $"破坏用例 {fileName} 的诊断码集合");
        }
    }
}
