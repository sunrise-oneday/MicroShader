using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// ADR-018 关键字宏矩阵的验收。这些断言直接对应「原设计会全线误报」的三条修正，
/// 任何一条被破坏都会让真实 URP 工程瞬间满屏红。
/// </summary>
internal static class MacroMatrixTests
{
    private const string Suite = "ShaderLab.MacroMatrix";

    public static void Register()
    {
        TestSuite.Add(Suite, "启用关键字必须带值定义", EnabledKeywordHasValue);
        TestSuite.Add(Suite, "同组未启用关键字完全不定义", DisabledKeywordsAreNotDefinedAtAll);
        TestSuite.Add(Suite, "所有已声明关键字带_DECLARED", DeclaredKeywordsGetDeclaredMacro);
        TestSuite.Add(Suite, "阶段宏恰好一个成员", ExactlyOneStageMacro);
        TestSuite.Add(Suite, "非光追派发绝不定义RAY_TRACING", RayTracingNeverDefinedForNonRtDispatch);
        TestSuite.Add(Suite, "阶段修饰声明按派发阶段过滤", StageScopedDeclarationsAreFiltered);
        TestSuite.Add(Suite, "内建变体指令不参与宏矩阵", BuiltInVariantsDoNotContribute);
        TestSuite.Add(Suite, "矩阵文本整体形态", PlanShape);
    }

    private static ShaderPassSnippet LoadScopedSnippet()
    {
        var text = Corpus.Read("StageScopedKeywords.shader");
        var machine = new ShaderLabStateMachine();
        var result = machine.Parse(Corpus.PathOf("StageScopedKeywords.shader"), text);
        Check.Equal(1, result.Passes.Count, "固定用例应只有 1 个编译单元");
        return result.Passes[0];
    }

    private static ShaderKeywordPlan BuildPlan(ShaderPassSnippet snippet, ShaderStage stage)
    {
        var plan = new ShaderKeywordPlan();
        ShaderKeywordMatrixBuilder.Build(snippet.Variants, stage, plan);
        return plan;
    }

    private static void EnabledKeywordHasValue()
    {
        var plan = BuildPlan(LoadScopedSnippet(), ShaderStage.Fragment);

        Check.Contains(plan.Lines, "#define _MAIN_LIGHT_SHADOWS 1", "启用关键字必须写成带值定义（裸 #define 会让 #if K 报 expected value in expression）");
        Check.Contains(plan.Lines, "#define _SCREEN_SPACE_OCCLUSION 1", "片元派发应启用片元阶段选中的关键字");
        Check.True(
            plan.Lines.All(l => !l.StartsWith("#define ", StringComparison.Ordinal) || l.EndsWith(" 1", StringComparison.Ordinal)),
            "任何 #define 都必须以 \" 1\" 结尾，不得出现裸定义");
    }

    private static void DisabledKeywordsAreNotDefinedAtAll()
    {
        var plan = BuildPlan(LoadScopedSnippet(), ShaderStage.Fragment);

        // _VERTEX_SCOPE_ON 属于顶点阶段声明，在片元派发里连声明都不该出现。
        Check.DoesNotContain(plan.EnabledKeywords, "_VERTEX_SCOPE_ON", "其它阶段的选中关键字不得启用");

        // 同组未选中关键字：_MAIN_LIGHT_SHADOWS 与 _MAIN_LIGHT_SHADOWS_CASCADE 同组，只选前者；
        // 这里用 _FOO/_BAR 那组更直观（PragmaRobustness 用例）。
        var pragmaText = Corpus.Read("PragmaRobustness.shader");
        var machine = new ShaderLabStateMachine();
        var result = machine.Parse(Corpus.PathOf("PragmaRobustness.shader"), pragmaText);
        var plan2 = BuildPlan(result.Passes[0], ShaderStage.Fragment);

        Check.Contains(plan2.EnabledKeywords, "_FOO", "_FOO 是首项，应被启用");
        Check.DoesNotContain(plan2.EnabledKeywords, "_BAR", "_BAR 同组未选中，不得启用");
        Check.DoesNotContain(plan2.Lines, "#define _BAR 0", "同组未选中关键字绝不可定义为 0（会让 #if defined(K) 恒真）");
        Check.DoesNotContain(plan2.Lines, "#define _BAR 1", "同组未选中关键字不得被置 1");
        Check.Contains(plan2.Lines, "#define _BAR_KEYWORD_DECLARED 1", "但必须补 DECLARED 标记");
    }

    private static void DeclaredKeywordsGetDeclaredMacro()
    {
        var plan = BuildPlan(LoadScopedSnippet(), ShaderStage.Fragment);

        Check.Contains(plan.Lines, "#define _MAIN_LIGHT_SHADOWS_KEYWORD_DECLARED 1", "无限定声明 → 直接 DECLARED");
        Check.Contains(plan.Lines, "#define _SCREEN_SPACE_OCCLUSION_KEYWORD_DECLARED 1", "阶段限定声明也要 DECLARED");
        Check.Contains(plan.DeclaredKeywords, "_MAIN_LIGHT_SHADOWS", "DECLARED 集合应含无限定关键字");
        Check.Contains(plan.DeclaredKeywords, "_SCREEN_SPACE_OCCLUSION", "DECLARED 集合应含阶段限定关键字");
    }

    private static void ExactlyOneStageMacro()
    {
        foreach (var stage in new[] { ShaderStage.Vertex, ShaderStage.Fragment, ShaderStage.Compute })
        {
            var plan = BuildPlan(LoadScopedSnippet(), stage);
            var stageDefines = plan.Lines.Count(l => l.StartsWith("#define SHADER_STAGE_", StringComparison.Ordinal));
            Check.Equal(1, stageDefines, $"{stage} 派发的阶段宏定义条数必须恰好为 1");
            Check.Contains(plan.Lines, "#define " + stage.ToStageMacro() + " 1", $"{stage} 派发的阶段宏");
        }
    }

    private static void RayTracingNeverDefinedForNonRtDispatch()
    {
        foreach (var stage in new[] { ShaderStage.Vertex, ShaderStage.Fragment, ShaderStage.Compute })
        {
            var plan = BuildPlan(LoadScopedSnippet(), stage);
            Check.DoesNotContain(plan.Lines, "#define SHADER_STAGE_RAY_TRACING 1", $"{stage} 派发绝不可定义 SHADER_STAGE_RAY_TRACING");
            Check.Contains(plan.Lines, "#undef SHADER_STAGE_RAY_TRACING", $"{stage} 派发应显式抹掉 RT 阶段宏（core Common.hlsl:390 的裸表达式依赖未定义即为假）");
        }
    }

    private static void StageScopedDeclarationsAreFiltered()
    {
        var snippet = LoadScopedSnippet();

        var vertexPlan = BuildPlan(snippet, ShaderStage.Vertex);
        Check.Contains(vertexPlan.EnabledKeywords, "_VERTEX_SCOPE_ON", "顶点派发应启用 _vertex 声明的首项");
        Check.DoesNotContain(vertexPlan.EnabledKeywords, "_SCREEN_SPACE_OCCLUSION", "顶点派发不得启用 _fragment 声明的关键字");
        Check.True(vertexPlan.SkippedDeclarations >= 2, "顶点派发应跳过 2 条片元阶段声明");
        Check.Contains(vertexPlan.Lines, "#if defined(SHADER_STAGE_VERTEX)", "阶段限定关键字应包在阶段守卫内");

        var fragmentPlan = BuildPlan(snippet, ShaderStage.Fragment);
        Check.Contains(fragmentPlan.EnabledKeywords, "_SCREEN_SPACE_OCCLUSION", "片元派发应启用 _fragment 声明的首项");
        Check.DoesNotContain(fragmentPlan.EnabledKeywords, "_VERTEX_SCOPE_ON", "片元派发不得启用 _vertex 声明的关键字");
        Check.Contains(fragmentPlan.Lines, "#if defined(SHADER_STAGE_FRAGMENT)", "阶段限定关键字应包在阶段守卫内");
    }

    private static void BuiltInVariantsDoNotContribute()
    {
        var snippet = LoadScopedSnippet();
        var plan = BuildPlan(snippet, ShaderStage.Fragment);

        // multi_compile_fog / multi_compile_instancing 是 Unity 内建变体指令，其关键字由 Unity 与平台宏集提供。
        Check.True(
            snippet.Variants.Count(v => v.Kind == ShaderVariantKind.BuiltIn) == 2,
            "固定用例应有 2 条内建变体指令");
        Check.True(
            plan.Lines.All(l => !l.Contains("FOG", StringComparison.Ordinal) && !l.Contains("INSTANCING", StringComparison.Ordinal)),
            "内建变体指令不得产出任何宏行");
    }

    private static void PlanShape()
    {
        var plan = BuildPlan(LoadScopedSnippet(), ShaderStage.Fragment);

        Check.Equal(ShaderStage.Fragment, plan.Stage, "计划阶段");
        Check.Equal("SHADER_STAGE_FRAGMENT", plan.StageMacro, "计划阶段宏");
        Check.True(plan.Lines.Count > 0, "计划不得为空");
        Check.True(plan.Lines[0] == "#define SHADER_STAGE_FRAGMENT 1", "第一条必须是阶段宏");

        // 守卫必须成对闭合。
        var ifs = plan.Lines.Count(l => l.StartsWith("#if ", StringComparison.Ordinal));
        var endifs = plan.Lines.Count(l => l == "#endif");
        Check.Equal(ifs, endifs, "#if 与 #endif 必须成对");

        // 幂等：同一输入重复构建结果一致。
        var again = new ShaderKeywordPlan();
        ShaderKeywordMatrixBuilder.Build(LoadScopedSnippet().Variants, ShaderStage.Fragment, again);
        Check.Equal(string.Join("\n", plan.Lines), string.Join("\n", again.Lines), "重复构建必须完全一致");

        // 复用同一计划对象也要一致（Reset 必须干净）。
        var reused = new ShaderKeywordPlan();
        ShaderKeywordMatrixBuilder.Build(LoadScopedSnippet().Variants, ShaderStage.Vertex, reused);
        ShaderKeywordMatrixBuilder.Build(LoadScopedSnippet().Variants, ShaderStage.Fragment, reused);
        Check.Equal(string.Join("\n", plan.Lines), string.Join("\n", reused.Lines), "复用计划对象不得残留上一次的阶段宏");
    }
}
