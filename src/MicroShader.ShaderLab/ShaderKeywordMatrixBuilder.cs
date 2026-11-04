using MicroShader.Domain;

namespace MicroShader.ShaderLab;

/// <summary>
/// 关键字宏矩阵生成器 —— ADR-018 的唯一落地实现。
/// </summary>
/// <remarks>
/// 矩阵（逐字对应规格书 v2.0 修正清单第 1 条）：
///
/// 启用（选中）关键字 → #define K 1          // 必须带值：裸 #define K 会让 URP 的 #if K 风格报 expected value in expression
/// 同组未启用关键字   → 【完全不定义】        // 定义成 0 会让 #if defined(K) 恒真，URP 立即渲染错误
/// 所有已声明关键字   → #define K_KEYWORD_DECLARED 1
///
/// 
/// 决策原则：两种互斥实现之间永远选「失败得响亮」的那种 —— 未定义关键字的失败模式是可见可归因的硬错误
/// （"use of undeclared identifier"），注入 0 的失败模式是不可见的静默语义漂移。
/// 
/// 阶段维度硬约束（v2.0 清单第 2、16、19 条）：
/// <list type="bullet">
/// <item>宏集是 platform × stage 两维；本生成器负责 stage 维，platform 维由模块 3 从 Editor.log 离线采集后合并。</item>
/// <item>阶段宏"恰好一个成员"，且 "vs_6_0" 与 "ps_6_0" 两次派发必须使用不同宏集。</item>
/// <item>非 RT 派发"绝不"定义 "SHADER_STAGE_RAY_TRACING" —— core "Common.hlsl:390" 的裸表达式
/// "#if (SHADER_STAGE_RAY_TRACING &amp;&amp; …)" 依赖「未定义标识符在 #if 中求值为 0」才为假。</item>
/// <item>带 "_fragment"/"_vertex" 修饰的声明只在本阶段派发中参与；其 "_KEYWORD_DECLARED" 按 Unity 实际
/// prologue 形态包在阶段宏守卫内。</item>
/// </list>
/// </remarks>
public static class ShaderKeywordMatrixBuilder
{
    // 线程级复用的去重集合（本方法是静态纯函数，没有实例可挂；分析线程各持一份）。
    // 用 O(1) 的 HashSet.Add 取代 List.Contains 的线性扫描：关键字数 k 大时从 O(k^2) 降为 O(k)。
    [ThreadStatic]
    private static HashSet<string>? t_enabledSeen;

    [ThreadStatic]
    private static HashSet<string>? t_plainDeclaredSeen;

    [ThreadStatic]
    private static HashSet<string>? t_guardedDeclaredSeen;

    private static HashSet<string> EnabledSeen => t_enabledSeen ??= new HashSet<string>(StringComparer.Ordinal);

    private static HashSet<string> PlainDeclaredSeen => t_plainDeclaredSeen ??= new HashSet<string>(StringComparer.Ordinal);

    private static HashSet<string> GuardedDeclaredSeen => t_guardedDeclaredSeen ??= new HashSet<string>(StringComparer.Ordinal);

    /// <summary>为指定派发阶段生成宏矩阵。</summary>
    /// <param name="declarations">本编译单元的变体声明。</param>
    /// <param name="stage">本次派发阶段。</param>
    /// <param name="into">输出计划。</param>
    /// <param name="activeKeywords">
    /// Unity 桥接上报的「当前已启用全局关键字」（模块 11B）。非空时，每个 set 的选中项
    /// 改为「该 set 中确实处于启用态的那一个」；找不到就回落到 ADR-006 的变体首项。
    /// **整组语义不变**（仍然每组只启用一个关键字），只是把「猜哪一个」换成「Unity 说是哪一个」。
    /// </param>
    public static void Build(
        IReadOnlyList<ShaderVariantDeclaration> declarations,
        ShaderStage stage,
        ShaderKeywordPlan into,
        ReadOnlySpan<string> activeKeywords = default)
    {
        ArgumentNullException.ThrowIfNull(declarations);
        ArgumentNullException.ThrowIfNull(into);

        into.Reset();
        into.Stage = stage;

        var stageMacro = stage.ToStageMacro();
        into.StageMacro = stageMacro;

        if (stageMacro.Length != 0)
        {
            into.Lines.Add("#define " + stageMacro + " 1");
        }

        // 互斥性硬约束：无论上层有没有配错，都显式抹掉 RT 阶段宏（非 RT 派发定义它会让 core Common.hlsl:390 的
        // 裸表达式求值为真，进而把整条光追分支拉进普通 pass 的编译单元）。
        if (stage != ShaderStage.RayTracing)
        {
            into.Lines.Add("#undef SHADER_STAGE_RAY_TRACING");
        }

        var plainDeclared = new List<string>();
        var guardedDeclared = new List<string>();

        // into.Reset() 在上方已清空 EnabledKeywords，因此这三个集合从空态开始即可。
        var enabledSeen = EnabledSeen;
        var plainSeen = PlainDeclaredSeen;
        var guardedSeen = GuardedDeclaredSeen;
        enabledSeen.Clear();
        plainSeen.Clear();
        guardedSeen.Clear();

        foreach (var declaration in declarations)
        {
            if (declaration.Kind == ShaderVariantKind.BuiltIn || declaration.Keywords.Count == 0)
            {
                // Unity 内建变体指令（multi_compile_fog / _instancing / …）的关键字由 Unity 与平台宏集提供，
                // 本模块既不启用也不声明，避免与真实 Unity 行为分叉。
                continue;
            }

            if (declaration.Stage != ShaderStage.None && declaration.Stage != stage)
            {
                into.SkippedDeclarations++;
                continue;
            }

            var selectedKeyword = SelectKeyword(declaration, activeKeywords);
            if (selectedKeyword.Length != 0 && enabledSeen.Add(selectedKeyword))
            {
                into.EnabledKeywords.Add(selectedKeyword);
            }

            var isPlain = declaration.Stage == ShaderStage.None;
            var target = isPlain ? plainDeclared : guardedDeclared;
            var targetSeen = isPlain ? plainSeen : guardedSeen;
            foreach (var keyword in declaration.Keywords)
            {
                if (targetSeen.Add(keyword))
                {
                    target.Add(keyword);
                }
            }
        }

        foreach (var keyword in into.EnabledKeywords)
        {
            into.Lines.Add("#define " + keyword + " 1");
        }

        // 同一关键字既有无限定声明又有阶段限定声明时，以无限定（更宽）为准，只产出一次。
        foreach (var keyword in plainDeclared)
        {
            into.Lines.Add("#define " + keyword + "_KEYWORD_DECLARED 1");
            into.DeclaredKeywords.Add(keyword);
        }

        if (guardedDeclared.Count > 0 && stageMacro.Length != 0)
        {
            var emitted = false;
            foreach (var keyword in guardedDeclared)
            {
                // plainSeen 就是 plainDeclared 的成员索引，等价于原来的列表线性查找。
                if (plainSeen.Contains(keyword))
                {
                    continue;
                }

                if (!emitted)
                {
                    into.Lines.Add("#if defined(" + stageMacro + ")");
                    emitted = true;
                }

                into.Lines.Add("#define " + keyword + "_KEYWORD_DECLARED 1");
                into.DeclaredKeywords.Add(keyword);
            }

            if (emitted)
            {
                into.Lines.Add("#endif");
            }
        }
    }

    /// <summary>
    /// 为本 set 挑一个「选中的关键字」。
    /// </summary>
    /// <remarks>
    /// 默认（无桥接数据）走 ADR-006 的变体首项 —— 即跳过 "_"/"__" 占位符后的第一个真实关键字。
    /// 桥接在线时改走「该 set 中**确实处于启用态**的那一个关键字」：
    /// 这不是放松整组语义，而是把「猜哪一个」换成「Unity 说是哪一个」。
    /// 判定实例："#pragma multi_compile _ _MAIN_LIGHT_SHADOWS" 的首项是占位符，
    /// 离线时本 set 一个宏都不能启用；而真实编辑器里 _MAIN_LIGHT_SHADOWS 是全局启用态的，
    /// 此时不启用它就会让 "#if defined(_MAIN_LIGHT_SHADOWS)" 分支被整段裁掉（ADR-006 要杜绝的事）。
    /// 该 set 内没有任何关键字处于启用态时，仍然回落首项，行为与离线一致。
    /// </remarks>
    private static string SelectKeyword(ShaderVariantDeclaration declaration, ReadOnlySpan<string> activeKeywords)
    {
        if (activeKeywords.Length == 0)
        {
            return declaration.SelectedKeyword;
        }

        foreach (var keyword in declaration.Keywords)
        {
            foreach (var active in activeKeywords)
            {
                if (string.Equals(keyword, active, StringComparison.Ordinal))
                {
                    return keyword;
                }
            }
        }

        return declaration.SelectedKeyword;
    }
}
