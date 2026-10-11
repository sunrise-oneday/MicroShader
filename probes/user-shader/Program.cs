using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MicroShader.IntelliSenseEngine;
using MicroShader.ShaderLab;

namespace UserShaderProbe;

/// <summary>
/// 用户真实手写 shader 的诊断探针。
/// 目的：回答四个问题 ——
///   1. 补全点的语义是否真匹配（补全点所在行 + 前后 200 字符肉眼确认）
///   2. 类型名是否被宏/typedef 遮住
///   3. 440KB 塞进单个 HLSLPROGRAM 块时切片器/索引器的行为
///   4. 最小可复现：逐层剥离 include，看候选在第几层出现/消失
/// </summary>
internal static class Program
{
    private const char NL = (char)10;
    private const char Q = (char)34;

    private const string ShaderPath =
        "D:/Program Files/U3D/FamiliarWithURP/Assets/URPshader-Plans/URPshader/ComputeShader/GrassLand/HighPerformance/Shaders/GrassBladeShader.shader";

    /// <summary>一个补全探针：在 Anchor 之后插入 Insert，再在插入文本末尾请求补全。</summary>
    private sealed record Probe(string Label, string Anchor, string Insert);

    private sealed record BlockView(string Label, string Kind, int StartLine, int EndLine, string Text, bool Unterminated);

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!File.Exists(ShaderPath))
        {
            Console.WriteLine("[FATAL] 找不到语料: " + ShaderPath);
            return 2;
        }

        var text = File.ReadAllText(ShaderPath);

        Console.WriteLine("################ 0. 语料基本信息 ################");
        Console.WriteLine("路径      : " + ShaderPath);
        Console.WriteLine("字符数    : " + text.Length);
        Console.WriteLine("UTF8 字节 : " + Encoding.UTF8.GetByteCount(text));
        Console.WriteLine("行数(NL)  : " + (CountChar(text, NL) + 1));
        Console.WriteLine("CRLF?     : " + text.Contains((char)13));
        Console.WriteLine();

        var blocks = Slice(text);
        Console.WriteLine();

        ProbePoints(text, blocks);
        Console.WriteLine();

        IncludeLadder(text);
        Console.WriteLine();

        HugeBlock();
        Console.WriteLine();

        Shadowing();
        Console.WriteLine();

        Shadowing2();
        Console.WriteLine();

        ServiceLatency();
        Console.WriteLine();

        IndexAudit(text);

        ManyBlocks();

        ScaleScan(args);

        RealCorpusAcceptance(args);

        NavigationAcceptance(args);

        Console.WriteLine("################ 完成 ################");
        return 0;
    }

    // ───────────────────────────── 1. 切片报告 ─────────────────────────────

    private static List<BlockView> Slice(string text)
    {
        Console.WriteLine("################ 1. 切片报告 ################");

        var machine = new ShaderLabStateMachine();
        var res = machine.Parse("<probe>", text);

        Console.WriteLine("SubShaderCount            = " + res.SubShaderCount);
        Console.WriteLine("Passes(HLSLPROGRAM 块)    = " + res.Passes.Count);
        Console.WriteLine("SharedSnippets(HLSLINCLUDE)= " + res.SharedSnippets.Count);
        Console.WriteLine("HasUnterminatedBlock      = " + res.HasUnterminatedBlock);
        Console.WriteLine("HasUnterminatedBlockComment= " + res.HasUnterminatedBlockComment);
        Console.WriteLine("切片诊断数                = " + res.Diagnostics.Count);

        var views = new List<BlockView>();

        foreach (var shared in res.SharedSnippets)
        {
            var v = ToView("SHARED#" + views.Count, "HLSLINCLUDE", shared);
            views.Add(v);
            Print(v, shared, null);
        }

        foreach (var pass in res.Passes)
        {
            var label = "PASS#" + pass.PassIndex + ".BLK" + pass.BlockOrdinalInPass;
            var v = ToView(label, pass.Kind.ToString(), pass);
            views.Add(v);
            Print(v, pass, pass);
        }

        return views;
    }

    private static BlockView ToView(string label, string kind, MicroShader.Domain.ShaderPassSnippet s) =>
        new(label, kind, s.StartLineNumber, s.EndLineNumber, s.RawHlslBlock.ToString(), s.IsUnterminated);

    private static void Print(BlockView v, MicroShader.Domain.ShaderPassSnippet s, MicroShader.Domain.ShaderPassSnippet? pass)
    {
        Console.WriteLine("--------------------------------------------");
        Console.WriteLine(v.Label + "  [" + v.Kind + "]  物理行 " + v.StartLine + ".." + v.EndLine
            + "  字符 " + s.ContentStartOffset + ".." + s.ContentEndOffset
            + "  长度 " + v.Text.Length
            + (v.Unterminated ? "  <<未闭合>>" : string.Empty));

        if (pass is not null)
        {
            Console.WriteLine("    PassIndex=" + pass.PassIndex + " PassName='" + pass.PassName + "' SubShaderIndex=" + pass.SubShaderIndex
                + " IsOrphan=" + pass.IsOrphan + " IsLegacyCg=" + pass.IsLegacyCg
                + " Vertex=" + (pass.VertexEntry ?? "-") + " Fragment=" + (pass.FragmentEntry ?? "-"));
            Console.WriteLine("    Includes=" + pass.Includes.Count + " Variants=" + pass.Variants.Count
                + " ApplicableSharedBlockIndices=[" + string.Join(",", pass.ApplicableSharedBlockIndices) + "]");
        }
    }

    // ───────────────────────── 2. 补全点逐点诊断 ─────────────────────────

    private static readonly Probe[] Probes =
    [
        new("i.  frag 形参 → VertexOutput（结构体声明在 HLSLINCLUDE 里）", "real4(i.", ""),
        new("o.  vert 内局部变量 → VertexOutput", "                o.", ""),
        new("v.  vert 形参 → VertexInput（同块内声明）", "grassWidth *= (1 - v.", ""),
        new("slope.  float4 局部（内建向量类型的成员补全）", "float xSlope = slope.", ""),
        new("var_ColorRT.  float4 局部", "lerp(albedo,var_ColorRT.", ""),
        new("pivot.  float3 局部", "float2 uv = (pivot.", ""),
        new("light.  Light 形参（类型来自 include）", "normalize(light.", ""),
        new("mainLight.  Light 局部（类型来自 include，模拟输入点号）", "ApplySingleDirectLight(mainLight", "."),
        new("_GrassPositionsBuffer.  StructuredBuffer 全局（在 HLSLINCLUDE 里）", "float3 pivot = _GrassPositionsBuffer", "."),
        new("Tra 标识符前缀补全（模拟输入 Tra）", "                float fogFactor = ", "Tra"),
        new("_Grass 标识符前缀补全（用户全局变量，模拟输入）", "float grassWidth = ", "_Grass"),
        new("SAMPLE 标识符前缀补全（Unity 宏，模拟输入）", "                float fogFactor = ", "SAMPLE"),
        new("_Base 标识符前缀补全（本文件 uniform，R4）", "float grassWidth = ", "_Base"),
        new("sampler_ 标识符前缀补全（本文件 sampler，R4）", "float grassWidth = ", "sampler_"),
    ];

    private static void ProbePoints(string text, List<BlockView> blocks)
    {
        Console.WriteLine("################ 2. 补全点逐点诊断 ################");
        Console.WriteLine("（每个探针 = 在真实语料的 Anchor 之后插入 Insert，再在插入末尾请求一次 completion）");
        Console.WriteLine();

        var merged = BuildMergedIndex(blocks);
        var perBlock = BuildPerBlockIndex(blocks);

        foreach (var probe in Probes)
        {
            RunOneProbe(text, blocks, merged, perBlock, probe);
        }
    }

    private static void RunOneProbe(
        string text,
        List<BlockView> blocks,
        HlslDocumentIndex merged,
        List<(string Label, HlslDocumentIndex Index)> perBlock,
        Probe probe)
    {
        Console.WriteLine("────────────────────────────────────────────");
        Console.WriteLine("[探针] " + probe.Label);

        var at = text.IndexOf(probe.Anchor, StringComparison.Ordinal);
        if (at < 0)
        {
            Console.WriteLine("  [跳过] 语料里找不到 Anchor: " + Quote(probe.Anchor));
            return;
        }

        var insertAt = at + probe.Anchor.Length;
        var mutated = text[..insertAt] + probe.Insert + text[insertAt..];
        var caret = insertAt + probe.Insert.Length;

        var (line, character) = OffsetToLineChar(mutated, caret);

        Console.WriteLine("  Anchor 命中偏移 " + at + " → 光标 offset=" + caret);
        Console.WriteLine("  光标 LSP 位置: line=" + line + " character=" + character);

        var trigger = TriggerContext.Detect(mutated, line, character);
        Console.WriteLine("  TriggerContext: Kind=" + trigger.Kind + " base='" + trigger.MemberBase + "' prefix='" + trigger.Prefix + "'");

        Console.WriteLine("  插入后窗口（±200 字符，<|> 为光标）:");
        Console.WriteLine("    " + Window(mutated, caret, 200));

        if (trigger.Kind == TriggerKind.MemberAccess)
        {
            Console.WriteLine("  分块索引（各块自己看到的 '" + trigger.MemberBase + "' → 类型）:");
            foreach (var (label, index) in perBlock)
            {
                if (index.VariablesToType.TryGetValue(trigger.MemberBase, out var t))
                {
                    Console.WriteLine("    " + label.PadRight(16) + " → " + t
                        + (index.Structs.ContainsKey(t) ? "   [本块内有该结构体定义, " + index.Structs[t].Fields.Count + " 字段]" : "   [结构体定义不在本块]"));
                }
            }

            if (merged.VariablesToType.TryGetValue(trigger.MemberBase, out var mergedType))
            {
                Console.WriteLine("  合并后（= 服务实际使用）: " + trigger.MemberBase + " → " + mergedType
                    + (merged.Structs.ContainsKey(mergedType) ? "  → 结构体命中" : "  → 结构体 MISS（服务将返回 0 候选）"));
                if (merged.Structs.ContainsKey(mergedType))
                {
                    Console.WriteLine("    字段: " + string.Join(", ", merged.Structs[mergedType].Fields.Select(f => f.Type + " " + f.Name)));
                }
            }
            else
            {
                Console.WriteLine("  合并后（= 服务实际使用）: " + trigger.MemberBase + " → 未登记（服务将返回 0 候选）");
            }
        }

        var json = Complete(mutated, line, character);
        var labels = json is null ? new List<string>() : ExtractLabels(json);
        Console.WriteLine("  服务返回: " + (json is null ? "null（无候选）" : labels.Count + " 候选 → " + string.Join(", ", labels)));
        Console.WriteLine();
    }

    // ───────────────────────── 3. include 剥离阶梯 ─────────────────────────

    private static void IncludeLadder(string text)
    {
        Console.WriteLine("################ 3. 最小可复现：逐层剥离 include ################");

        var machine = new ShaderLabStateMachine();
        var res = machine.Parse("<probe>", text);

        var sharedText = res.SharedSnippets.Count > 0 ? res.SharedSnippets[0].RawHlslBlock.ToString() : string.Empty;
        var passText = res.Passes.Count > 0 ? res.Passes[0].RawHlslBlock.ToString() : string.Empty;

        var minimalNoStruct = Wrap("MinNoStruct",
            "real4 frag (VertexOutput i) : SV_Target { return real4(i.color.rgb,1); }");

        var minimal = Wrap("Min",
            "struct VertexOutput { float4 posCS : SV_POSITION; half3 color : COLOR; half4 color2 : COLOR1; float2 uv : TEXCOORD0; };"
            + NL
            + "real4 frag (VertexOutput i) : SV_Target { return real4(i.color.rgb,1); }");

        var ladder = new (string Label, string Body)[]
        {
            ("L0 完整原文", text),
            ("L1 去掉 Lighting.hlsl", RemoveLines(text, "Lighting.hlsl")),
            ("L2 去掉两个 include", RemoveLines(RemoveLines(text, "Lighting.hlsl"), "Core.hlsl")),
            ("L3 再去掉所有 #pragma", RemoveLines(RemoveLines(RemoveLines(text, "Lighting.hlsl"), "Core.hlsl"), "#pragma")),
            ("L4 只留 HLSLINCLUDE 内容 + 首个 HLSLPROGRAM 块", Wrap("L4", sharedText + NL + passText)),
            ("L5 只留 VertexOutput 声明 + frag 函数", minimal),
            ("L6 同样但删掉 VertexOutput 声明", minimalNoStruct),
        };

        Console.WriteLine("探针列（只统计仍能定位到 Anchor 的）：");
        Console.WriteLine();

        var header = new StringBuilder();
        header.Append("变体".PadRight(46));
        for (var i = 0; i < Probes.Length; i++)
        {
            header.Append(("P" + (i + 1)).PadLeft(5));
        }

        Console.WriteLine(header.ToString());

        foreach (var (label, body) in ladder)
        {
            var blocks = SafeSlice(body);
            var merged = BuildMergedIndex(blocks);
            var perBlock = BuildPerBlockIndex(blocks);

            var row = new StringBuilder();
            row.Append(label.PadRight(46));

            foreach (var probe in Probes)
            {
                var at = body.IndexOf(probe.Anchor, StringComparison.Ordinal);
                if (at < 0)
                {
                    row.Append("   --");
                    continue;
                }

                var insertAt = at + probe.Anchor.Length;
                var mutated = body[..insertAt] + probe.Insert + body[insertAt..];
                var caret = insertAt + probe.Insert.Length;
                var (line, character) = OffsetToLineChar(mutated, caret);
                var json = Complete(mutated, line, character);
                var count = json is null ? 0 : ExtractLabels(json).Count;
                row.Append(count.ToString().PadLeft(5));
            }

            Console.WriteLine(row.ToString());
            GC.KeepAlive(merged);
            GC.KeepAlive(perBlock);
        }

        Console.WriteLine();
        Console.WriteLine("探针图例:");
        for (var i = 0; i < Probes.Length; i++)
        {
            Console.WriteLine("  P" + (i + 1) + " = " + Probes[i].Label);
        }

        Console.WriteLine();
    }

    private static string Wrap(string name, string body) =>
        "Shader " + Q + name + Q + NL
        + "{" + NL
        + "    SubShader" + NL
        + "    {" + NL
        + "        Pass" + NL
        + "        {" + NL
        + "            HLSLPROGRAM" + NL
        + body + NL
        + "            ENDHLSL" + NL
        + "        }" + NL
        + "    }" + NL
        + "}" + NL;

    private static string RemoveLines(string text, string marker)
    {
        var sb = new StringBuilder(text.Length);
        var start = 0;
        while (start <= text.Length)
        {
            var nl = text.IndexOf(NL, start);
            var end = nl < 0 ? text.Length : nl;
            var lineText = text[start..end];
            if (!lineText.Contains(marker, StringComparison.Ordinal))
            {
                sb.Append(lineText);
                if (nl >= 0)
                {
                    sb.Append(NL);
                }
            }

            if (nl < 0)
            {
                break;
            }

            start = nl + 1;
        }

        return sb.ToString();
    }

    // ───────────────────── 4. 440KB 单块压测 ─────────────────────

    private static void HugeBlock()
    {
        Console.WriteLine("################ 4. 单块超大 HLSLPROGRAM（440KB 量级）################");

        foreach (var target in new[] { 440 * 1024, 2 * 1024 * 1024 })
        {
            var body = BuildHugeBody(target);
            var wrapped = Wrap("Huge" + target, body);

            Console.WriteLine("--------------------------------------------");
            Console.WriteLine("目标块内容 " + target + " 字符；文件总长 " + wrapped.Length + " 字符，"
                + (CountChar(wrapped, NL) + 1) + " 行");

            var blocks = SafeSlice(wrapped);
            foreach (var b in blocks)
            {
                Console.WriteLine("  块 " + b.Label + " 行 " + b.StartLine + ".." + b.EndLine
                    + " 长度 " + b.Text.Length + (b.Unterminated ? "  <<未终止>>" : string.Empty));
            }

            var (parseMs, indexMs, parseAlloc, indexAlloc) = Measure(wrapped, 5);
            Console.WriteLine("  ShaderLabStateMachine.Parse 稳态最小 = " + parseMs.ToString("F3") + " ms"
                + "   (分配 " + parseAlloc + " B)");
            Console.WriteLine("  HlslDocumentIndex.Build     稳态最小 = " + indexMs.ToString("F3") + " ms"
                + "   (分配 " + indexAlloc + " B)");

            // 在超大块内部取两个补全点
            var probe = "real4(i.";
            var at = wrapped.LastIndexOf(probe, StringComparison.Ordinal);
            if (at >= 0)
            {
                var caret = at + probe.Length;
                var (line, character) = OffsetToLineChar(wrapped, caret);
                var json = Complete(wrapped, line, character);
                Console.WriteLine("  块尾补全点 i. (line=" + line + ") → "
                    + (json is null ? "0 候选" : ExtractLabels(json).Count + " 候选 → " + string.Join(", ", ExtractLabels(json))));
            }

            var ident = "float      F10(";
            var at2 = wrapped.IndexOf(ident, StringComparison.Ordinal);
            if (at2 >= 0)
            {
                var caret = at2 + 6 + "      F10".Length;
                var (line, character) = OffsetToLineChar(wrapped, caret);
                var json = Complete(wrapped, line, character);
                Console.WriteLine("  标识符前缀 F10 处 (line=" + line + ") → "
                    + (json is null ? "0 候选" : ExtractLabels(json).Count + " 候选"));
            }
        }

        Console.WriteLine();
    }

    private static string BuildHugeBody(int targetChars)
    {
        var sb = new StringBuilder(targetChars + 4096);
        sb.Append("struct VertexOutput { float4 posCS : SV_POSITION; half3 color : COLOR; half4 color2 : COLOR1; float2 uv : TEXCOORD0; };").Append(NL);
        var n = 0;
        while (sb.Length < targetChars)
        {
            sb.Append("struct S").Append(n).Append(" { float3 a").Append(n).Append("; float4 b").Append(n).Append("; };").Append(NL);
            sb.Append("static float g").Append(n).Append(" = ").Append(n).Append(".5;").Append(NL);
            sb.Append("float      F").Append(n).Append("(float x) { return x * g").Append(n).Append(" + S").Append(n).Append("().a").Append(n).Append(".x; }").Append(NL);
            n++;
        }

        sb.Append("real4 frag (VertexOutput i) : SV_Target { return real4(i.color.rgb,1); }").Append(NL);
        return sb.ToString();
    }

    private static (double ParseMs, double IndexMs, long ParseAlloc, long IndexAlloc) Measure(string text, int rounds)
    {
        var bestParse = double.MaxValue;
        var bestIndex = double.MaxValue;
        long parseAlloc = 0;
        long indexAlloc = 0;

        for (var r = 0; r < rounds; r++)
        {
            var a0 = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            var machine = new ShaderLabStateMachine();
            var res = machine.Parse("<probe>", text);
            sw.Stop();
            var a1 = GC.GetAllocatedBytesForCurrentThread();

            var sb = new StringBuilder(text.Length);
            foreach (var p in res.Passes)
            {
                sb.Append(p.RawHlslBlock.Span);
            }

            foreach (var s in res.SharedSnippets)
            {
                sb.Append(s.RawHlslBlock.Span);
            }

            var mergedText = sb.ToString();

            var sw2 = Stopwatch.StartNew();
            var index = HlslDocumentIndex.Build(mergedText.AsSpan());
            sw2.Stop();
            var a2 = GC.GetAllocatedBytesForCurrentThread();

            if (r > 0)
            {
                if (sw.Elapsed.TotalMilliseconds < bestParse)
                {
                    bestParse = sw.Elapsed.TotalMilliseconds;
                    parseAlloc = a1 - a0;
                }

                if (sw2.Elapsed.TotalMilliseconds < bestIndex)
                {
                    bestIndex = sw2.Elapsed.TotalMilliseconds;
                    indexAlloc = a2 - a1;
                }
            }

            GC.KeepAlive(index);
        }

        return (bestParse, bestIndex, parseAlloc, indexAlloc);
    }

    // ───────────── 5. 同名变量串味 / 合并顺序 ─────────────

    private static void Shadowing()
    {
        Console.WriteLine("################ 5. 同名变量与被宏/typedef 遮住的类型 ################");

        Case("5a. 同块内两个同名不同型变量（后者覆盖前者）",
            "struct LightOutput { float3 direction; float3 color; };"
            + NL
            + "LightOutput GetLight() { LightOutput output; return output; }"
            + NL
            + "float4 frag() : SV_Target { float3 output; output.x = 1; return 0; }",
            "return output;", ".");

        Case("5b. HLSLINCLUDE 里的全局覆盖 Pass 内的局部（合并顺序：Pass 先、Shared 后）",
            "static float4 output;" + NL + "struct LightOutput { float3 direction; float3 color; };"
            + NL
            + "float4 frag() : SV_Target { LightOutput output; return output; }",
            "LightOutput output; return output", ".");

        Case("5c. 类型名被 #define 遮住（real4 = float4）",
            "struct VertexOutputX { float4 posCS : SV_POSITION; half3 color : COLOR; };"
            + NL
            + "real4 frag (VertexOutputX i) : SV_Target { return real4(i.color.rgb,1); }",
            "real4(i.", "");

        Case("5d. typedef 别名（HLSL 无 typedef，测试 Unity 风格 struct 别名）",
            "struct VertexOutputY { float4 posCS : SV_POSITION; half3 color : COLOR; };" + NL
            + "real4 frag (VertexOutputY i) : SV_Target { return real4(i.color.rgb,1); }",
            "real4(i.", "");

        Console.WriteLine();
    }

    private static void Case(string title, string body, string anchor, string insert)
    {
        var text = Wrap("Case", body);
        var at = text.IndexOf(anchor, StringComparison.Ordinal);
        Console.WriteLine("--------------------------------------------");
        Console.WriteLine("[" + title + "]");
        if (at < 0)
        {
            Console.WriteLine("  Anchor 未命中: " + Quote(anchor));
            return;
        }

        var insertAt = at + anchor.Length;
        var mutated = text[..insertAt] + insert + text[insertAt..];
        var caret = insertAt + insert.Length;
        var (line, character) = OffsetToLineChar(mutated, caret);
        var trigger = TriggerContext.Detect(mutated, line, character);

        var blocks = SafeSlice(text);
        var merged = BuildMergedIndex(blocks);
        var perBlock = BuildPerBlockIndex(blocks);

        Console.WriteLine("  TriggerContext: Kind=" + trigger.Kind + " base='" + trigger.MemberBase + "' prefix='" + trigger.Prefix + "'");
        if (trigger.Kind == TriggerKind.MemberAccess)
        {
            foreach (var (label, index) in perBlock)
            {
                if (index.VariablesToType.TryGetValue(trigger.MemberBase, out var t))
                {
                    Console.WriteLine("  分块 " + label.PadRight(16) + " " + trigger.MemberBase + " → " + t);
                }
            }

            Console.WriteLine("  合并后 " + trigger.MemberBase + " → "
                + (merged.VariablesToType.TryGetValue(trigger.MemberBase, out var mt) ? mt : "未登记"));
        }

        var json = Complete(mutated, line, character);
        var labels = json is null ? [] : ExtractLabels(json);
        Console.WriteLine("  服务返回: " + (json is null ? "null（0 候选）" : labels.Count + " 候选 → " + string.Join(", ", labels)));
    }

    // ───────────── 6. 追加用例：带窗口打印 + 类型识别边界 ─────────────

    private static void Shadowing2()
    {
        Console.WriteLine("################ 6. 追加用例（含窗口打印）################");

        Case2("6a. 同块内两个同名不同型变量（后写者覆盖）",
            "struct LightOutput { float3 direction; float3 color; };"
            + NL
            + "LightOutput GetLight() { LightOutput output; return output; }"
            + NL
            + "float4 frag() : SV_Target { float3 output; output.x = 1; return 0; }",
            "return output", ".", "output");

        Case2("6b. real4 作为函数返回类型（宏遮蔽是否影响参数登记）",
            "struct VertexOutputX { float4 posCS : SV_POSITION; half3 color : COLOR; };"
            + NL
            + "real4 frag (VertexOutputX i) : SV_Target { return real4(i.color.rgb,1); }",
            "real4(i.", "", "i");

        Case2("6c. real4 声明的变量（real* 不在类型识别表里）",
            "static real4 gTmp;" + NL
            + "float4 frag() : SV_Target { return gTmp; }",
            "return gTmp", ".", "gTmp");

        Case2("6d. half4 声明的变量（对照组：half* 在表里）",
            "static half4 hTmp;" + NL
            + "float4 frag() : SV_Target { return hTmp; }",
            "return hTmp", ".", "hTmp");

        Case2("6e. StructuredBuffer<Varyings> 模板尖括号",
            "struct Varyings { float4 positionCS; float3 normalWS; };" + NL
            + "StructuredBuffer<Varyings> gBuf;" + NL
            + "float4 frag() : SV_Target { return Varyings; }",
            "return Varyings", ".", "Varyings");

        Case2("6f. 结构体字段带 : SEMANTIC 标注",
            "struct V { float4 a : SV_POSITION; half3 b : COLOR; float2 c : TEXCOORD0; };" + NL
            + "float4 frag (V i) : SV_Target { return i.a; }",
            "{ return i", ".", "i");

        Case2("6h. out 形参（out TOutput output）—— 用户最担心的语义误匹配",
            "struct TOutput { float4 positionCS; float3 normalWS; };"
            + NL
            + "void Build(out TOutput output) { output.positionCS = 1; }"
            + NL
            + "float4 frag() : SV_Target { return 0; }",
            "{ output", ".", "output");

        Case2("6i. 声明之后才出现的同名函数调用（R1 隔离）",
            "struct LightOutput { float3 direction; float3 color; };"
            + NL
            + "LightOutput Make() { LightOutput output; return output; }"
            + NL
            + "float Consume(LightOutput v) { return v.direction.x; }"
            + NL
            + "float4 frag() : SV_Target { LightOutput output; Dummy(output); return output.direction; }",
            "Dummy(output); return output", ".", "output");

        Case2("6g. Texture2D<float4> 纹理声明",
            "Texture2D<float4> _Tex;" + NL
            + "float4 frag() : SV_Target { return _Tex; }",
            "return _Tex", ".", "_Tex");

        Console.WriteLine();
    }

    private static void Case2(string title, string body, string anchor, string insert, string baseName)
    {
        var text = Wrap("Case", body);
        var at = text.IndexOf(anchor, StringComparison.Ordinal);
        Console.WriteLine("--------------------------------------------");
        Console.WriteLine("[" + title + "]");
        if (at < 0)
        {
            Console.WriteLine("  Anchor 未命中: " + Quote(anchor));
            return;
        }

        var insertAt = at + anchor.Length;
        var mutated = text[..insertAt] + insert + text[insertAt..];
        var caret = insertAt + insert.Length;
        var (line, character) = OffsetToLineChar(mutated, caret);

        Console.WriteLine("  光标 LSP: line=" + line + " character=" + character);
        Console.WriteLine("  窗口: " + Window(mutated, caret, 120));

        var trigger = TriggerContext.Detect(mutated, line, character);
        Console.WriteLine("  TriggerContext: Kind=" + trigger.Kind + " base='" + trigger.MemberBase + "' prefix='" + trigger.Prefix + "'");

        var blocks = SafeSlice(text);
        var merged = BuildMergedIndex(blocks);
        var perBlock = BuildPerBlockIndex(blocks);

        foreach (var (label, index) in perBlock)
        {
            if (index.VariablesToType.TryGetValue(baseName, out var t))
            {
                Console.WriteLine("  分块 " + label.PadRight(16) + " " + baseName + " → " + t);
            }
        }

        Console.WriteLine("  合并后 " + baseName + " → "
            + (merged.VariablesToType.TryGetValue(baseName, out var mt) ? mt : "未登记"));

        var json = Complete(mutated, line, character);
        var labels = json is null ? new List<string>() : ExtractLabels(json);
        Console.WriteLine("  服务返回: " + (json is null ? "null（0 候选）" : labels.Count + " 候选 → " + string.Join(", ", labels)));
    }

    // ───────────── 6.5 R6 端到端补全延迟（经 IntelliSenseService）─────────────

    private static readonly MemoryStream SinkStream = new();

    private static readonly Utf8JsonWriter SinkWriter = new(SinkStream);

    private static void ServiceLatency()
    {
        Console.WriteLine("################ 6.5 R6 端到端补全延迟（经 IntelliSenseService）################");

        var body = BuildHugeBody(440 * 1024);
        var wrapped = Wrap("Latency", body);
        var caretMarker = "real4(i.";
        var caret = wrapped.LastIndexOf(caretMarker, StringComparison.Ordinal) + caretMarker.Length;
        Console.WriteLine("语料：单块 " + body.Length + " 字符，文件 " + wrapped.Length + " 字符");
        Console.WriteLine();

        // ① 同一文本连续请求（块内容未变 → 块缓存 + 合并记忆都应命中）
        var service = new IntelliSenseService();
        MeasureCompletion(service, wrapped, caret);   // 预热（首次必然全量重建）
        var hits = new List<double>();
        for (var k = 0; k < 20; k++)
        {
            hits.Add(MeasureCompletion(service, wrapped, caret));
        }

        Report("① 同一文本连续 20 次（缓存命中）", hits);

        // ② 只编辑 HLSL 块之外的内容（追加注释）：块内容不变 → 仍应命中
        var outside = new List<double>();
        for (var k = 0; k < 10; k++)
        {
            var mutated = wrapped + "// 只改块外内容 " + k;
            var (line, character) = OffsetToLineChar(mutated, caret);
            outside.Add(MeasureCompletionAt(service, mutated, line, character));
        }

        Report("② 只改块外注释（块内容不变）", outside);

        // ③ 每次微改 HLSL 块内一个字符（模拟在块内打字）→ 必须重建该块
        var inside = new List<double>();
        for (var k = 0; k < 5; k++)
        {
            var mutated = wrapped.Replace("static float g0 = 0.5;", "static float g0 = 0.5 + " + k + ";", StringComparison.Ordinal);
            var (line, character) = OffsetToLineChar(mutated, caret);
            inside.Add(MeasureCompletionAt(service, mutated, line, character));
        }

        Report("③ 每次改块内一个数字（必须重建该块）", inside);
        Console.WriteLine();
    }

    private static void Report(string label, List<double> samples)
    {
        samples.Sort();
        var min = samples[0];
        var median = samples[samples.Count / 2];
        Console.WriteLine("  " + label.PadRight(40) + " 最小 " + min.ToString("F3") + " ms   中位 "
            + median.ToString("F3") + " ms   (n=" + samples.Count + ")");
    }

    private static double MeasureCompletion(IntelliSenseService service, string text, int caret)
    {
        var (line, character) = OffsetToLineChar(text, caret);
        return MeasureCompletionAt(service, text, line, character);
    }

    private static double MeasureCompletionAt(IntelliSenseService service, string text, int line, int character)
    {
        SinkWriter.Reset();
        SinkStream.SetLength(0);

        var request = new MicroShader.Domain.PositionRequest("<probe>", text, line, character);
        var sw = Stopwatch.StartNew();
        service.ProvideCompletions(request, SinkWriter);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    // ───────────── 8. 极限 · 多块（300 个 HLSLPROGRAM 块）─────────────

    private static void ManyBlocks()
    {
        Console.WriteLine("################ 8. 极限 · 多块（300 个 HLSLPROGRAM 块）################");

        const int passes = 300;
        var sb = new StringBuilder();
        sb.Append("Shader \"Many\"").Append(NL).Append('{').Append(NL).Append("SubShader").Append(NL).Append('{').Append(NL);

        for (var p = 0; p < passes; p++)
        {
            sb.Append("Pass").Append(NL).Append('{').Append(NL).Append("HLSLPROGRAM").Append(NL);
            sb.Append("struct V").Append(p).Append(" { float4 posCS : SV_POSITION; half3 color; };").Append(NL);
            sb.Append("static float4 g").Append(p).Append(';').Append(NL);
            sb.Append("V").Append(p).Append(" vert(V").Append(p).Append(" v) { return v; }").Append(NL);
            sb.Append("half4 frag(V").Append(p).Append(" i) : SV_Target { return i.color; }").Append(NL);
            sb.Append("ENDHLSL").Append(NL).Append('}').Append(NL);
        }

        sb.Append('}').Append(NL).Append('}').Append(NL);
        var text = sb.ToString();

        Console.WriteLine("块数 " + passes + "；文件 " + text.Length + " 字符，"
            + (CountChar(text, NL) + 1) + " 行");

        var machine = new ShaderLabStateMachine();
        var res = machine.Parse("<probe>", text);
        Console.WriteLine("  Parse 切片块数 = " + res.Passes.Count);

        var service = new IntelliSenseService();
        var caret = text.IndexOf("return i.color;", StringComparison.Ordinal) + 10;   // 停在 i. 之后
        var cold = MeasureCompletion(service, text, caret);
        var warm = new List<double>();
        for (var k = 0; k < 10; k++)
        {
            warm.Add(MeasureCompletion(service, text, caret));
        }

        Report("多块 · 冷（首帧）", [cold]);
        Report("多块 · 热（同文本 10 次）", warm);

        // 最后一块内的补全点：验证「后面的块」也解析正确
        var last = text.LastIndexOf("return i.color;", StringComparison.Ordinal) + 10;
        var json = Complete(text, OffsetToLineChar(text, last).Line, OffsetToLineChar(text, last).Character);
        Console.WriteLine("  末块 i. 处 → " + (json is null ? "0 候选" : ExtractLabels(json).Count + " 候选"));
        Console.WriteLine();
    }

    // ───────────── 9. 常规 · 真实 URP 语料规模扫描 ─────────────

    private static void ScaleScan(string[] args)
    {
        Console.WriteLine("################ 9. 常规 · 真实 URP 语料规模扫描 ################");

        var root = Environment.GetEnvironmentVariable("MICROSHADER_URP_ROOT");
        if (string.IsNullOrWhiteSpace(root) && args.Length > 0)
        {
            root = args[0];
        }

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            Console.WriteLine("  [跳过] 未给出 URP 根目录（参数或 MICROSHADER_URP_ROOT）: " + (root ?? "(null)"));
            Console.WriteLine();
            return;
        }

        var files = Directory.GetFiles(root, "*.shader", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        Console.WriteLine("  根目录: " + root);
        Console.WriteLine("  .shader 文件数: " + files.Length);
        Console.WriteLine();

        var chars = new List<double>();
        var indexMs = new List<double>();
        var coldMs = new List<double>();
        var warmMs = new List<double>();
        var candidateCounts = new List<double>();
        var failures = 0;

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            if (text.Length == 0)
            {
                continue;
            }

            chars.Add(text.Length);

            // 找一个确定能触发补全（从而必然建索引）的光标位置
            var caret = FindTriggerPosition(text);
            if (caret < 0)
            {
                failures++;
                continue;
            }

            var (line, character) = OffsetToLineChar(text, caret);

            var service = new IntelliSenseService();
            var cold = MeasureCompletionAt(service, text, line, character);
            var warm = MeasureCompletionAt(service, text, line, character);

            coldMs.Add(cold);
            warmMs.Add(warm);

            // 冷索引成本：直接对切出的块建索引
            var machine = new ShaderLabStateMachine();
            var res = machine.Parse(file, text);
            var sw = Stopwatch.StartNew();
            var index = new HlslDocumentIndex();
            GC.KeepAlive(index);
            foreach (var pass in res.Passes)
            {
                HlslDocumentIndex.Build(pass.RawHlslBlock.Span);
            }

            foreach (var shared in res.SharedSnippets)
            {
                HlslDocumentIndex.Build(shared.RawHlslBlock.Span);
            }

            sw.Stop();
            indexMs.Add(sw.Elapsed.TotalMilliseconds);

            SinkWriter.Reset();
            SinkStream.SetLength(0);
            var request = new MicroShader.Domain.PositionRequest("<probe>", text, line, character);
            service.ProvideCompletions(request, SinkWriter);
            var json = SinkStream.Length == 0 ? null : Encoding.UTF8.GetString(SinkStream.ToArray());
            candidateCounts.Add(json is null ? 0 : ExtractLabels(json).Count);
        }

        Console.WriteLine("  " + files.Length + " 个文件，触发点可用 " + warmMs.Count + " 个，跳过 " + failures + " 个");
        Console.WriteLine();
        Console.WriteLine("  指标".PadRight(28) + "最小".PadLeft(12) + "中位".PadLeft(12) + "p95".PadLeft(12) + "最大".PadLeft(12));
        Summarize("文件字符数", chars);
        Summarize("冷索引 Build (ms)", indexMs);
        Summarize("端到端补全 · 冷 (ms)", coldMs);
        Summarize("端到端补全 · 热 (ms)", warmMs);
        Summarize("候选条数", candidateCounts);
        Console.WriteLine();
    }

    /// <summary>找一个必然产生补全（因此必然建索引）的光标位置；找不到返回 -1。</summary>
    private static int FindTriggerPosition(string text)
    {
        for (var i = 1; i < text.Length; i++)
        {
            if (!IsIdentifierChar(text[i - 1]))
            {
                continue;
            }

            var (line, character) = OffsetToLineChar(text, i);
            var trigger = TriggerContext.Detect(text, line, character);
            if (trigger.Kind != TriggerKind.None)
            {
                return i;
            }
        }

        return -1;
    }

    // ───────────── 10. 真实手写 shader 语料验收（预热 vs 基线）─────────────

    /// <summary>IIncludeFileSource 的探针实现：从工程目录现场建立包名 → 包根映射。</summary>
    private sealed class ProbeFileSource : MicroShader.NavigationEngine.INavigationHost
    {
        private readonly Dictionary<string, string> _roots = new(StringComparer.OrdinalIgnoreCase);

        public bool Exists(string physicalPath) => File.Exists(physicalPath);

        public bool TryGetPhysicalPath(string uri, out string physicalPath)
        {
            var text = uri;
            if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                text = text["file:///".Length..];
            }

            physicalPath = Uri.UnescapeDataString(text).Replace('\\', '/');
            return physicalPath.Length > 0;
        }

        public bool TryGetOpenDocument(string physicalPath, out string uri, out string text)
        {
            uri = string.Empty;
            text = string.Empty;
            return false;   // 探针无编辑器会话：脏文件路径在自检里覆盖
        }

        public string ToFileUri(string physicalPath) => new Uri(physicalPath.Replace('\\', '/')).AbsoluteUri;

        public ProbeFileSource(string projectRoot)
        {
            ProjectRoot = projectRoot;

            var cache = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(cache))
            {
                foreach (var dir in Directory.GetDirectories(cache))
                {
                    var name = Path.GetFileName(dir);
                    var at = name.IndexOf('@');
                    if (at > 0)
                    {
                        _roots.TryAdd(name[..at], dir.Replace('\\', '/'));
                    }
                }
            }

            var local = Path.Combine(projectRoot, "Packages");
            if (Directory.Exists(local))
            {
                foreach (var dir in Directory.GetDirectories(local))
                {
                    _roots[Path.GetFileName(dir)] = dir.Replace('\\', '/');
                }
            }
        }

        public int PackageCount => _roots.Count;

        public bool TryResolve(string virtualPath, out string physicalPath)
        {
            physicalPath = string.Empty;
            var normalized = virtualPath.Replace('\\', '/').TrimStart('/');
            if (!normalized.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = normalized["Packages/".Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0 || !_roots.TryGetValue(rest[..slash], out var root))
            {
                return false;
            }

            physicalPath = root + "/" + rest[(slash + 1)..];
            return true;
        }

        public bool TryResolveRelative(string currentPhysicalPath, string target, out string physicalPath)
        {
            physicalPath = string.Empty;
            var dir = Path.GetDirectoryName(currentPhysicalPath);
            if (string.IsNullOrEmpty(dir))
            {
                return false;
            }

            var candidate = Path.Combine(dir, target);
            if (!File.Exists(candidate))
            {
                return false;
            }

            physicalPath = candidate;
            return true;
        }

        public bool TryRead(string physicalPath, out string text)
        {
            try { text = File.ReadAllText(physicalPath); return true; }
            catch (IOException) { text = string.Empty; return false; }
            catch (UnauthorizedAccessException) { text = string.Empty; return false; }
        }

        public long GetStamp(string physicalPath)
        {
            try { return File.GetLastWriteTimeUtc(physicalPath).Ticks; }
            catch (IOException) { return 0; }
        }

        public string ProjectRoot { get; } = string.Empty;
    }

    private static void RealCorpusAcceptance(string[] args)
    {
        Console.WriteLine("################ 10. 真实手写 shader 语料验收（预热 vs 基线）################");

        var projectRoot = args.Length > 1 ? args[1] : "D:/Program Files/U3D/FamiliarWithURP";
        var root = args.Length > 2
            ? args[2]
            : Path.Combine(projectRoot, "Assets/URPshader-Plans/URPshader");

        if (!Directory.Exists(root))
        {
            Console.WriteLine("  [跳过] 目录不存在: " + root);
            return;
        }

        var files = Directory.GetFiles(root, "*.shader", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);

        var source = new ProbeFileSource(projectRoot);
        var cache = new MicroShader.IntelliSenseEngine.IncludeSymbolCache();
        var options = new MicroShader.IntelliSenseEngine.IntelliSenseOptions();
        var baselineService = new MicroShader.IntelliSenseEngine.IntelliSenseService(options);
        var warmedService = new MicroShader.IntelliSenseEngine.IntelliSenseService(
            options,
            new MicroShader.IntelliSenseEngine.IncludeSymbolProvider { Cache = cache, Files = source });

        Console.WriteLine("  工程根: " + projectRoot + "（包 " + source.PackageCount + " 个）");
        Console.WriteLine("  语料  : " + root);
        Console.WriteLine("  文件  : " + files.Length + " 个");
        Console.WriteLine();

        var baseTimes = new List<double>();
        var warmTimes = new List<double>();
        var gainedPoints = new List<string>();
        var totalPoints = 0;
        var baseHit = 0;
        var warmHit = 0;

        // 探针要的是「同步、确定性」预热（见下方注释：不靠 sleep），但预热器默认 RunWorker=true，
        // 后台 worker 会与 Drain() 抢同一队列 → 偶发在预热未完成时就往下走。显式关掉后台 worker，
        // 与自检侧的 Manual 选项同构，保证 Drain() 独占。
        using var warmer = new MicroShader.IntelliSenseEngine.IncludeSymbolWarmer(
            source, cache, new MicroShader.IntelliSenseEngine.IncludeSymbolOptions { RunWorker = false });

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var shortName = Path.GetFileName(file);
            var points = 0;
            var localBase = 0;
            var localWarm = 0;

            foreach (var caret in MemberAccessPoints(text))
            {
                var (line, character) = OffsetToLineChar(text, caret);
                points++;

                var sw = Stopwatch.StartNew();
                var a = CompleteWith(baselineService, text, line, character, file);
                baseTimes.Add(sw.Elapsed.TotalMilliseconds);

                if (a is not null) { localBase++; }

                // 预热一次（同步 Drain，确定性，不靠 sleep），再问同一个点
                warmer.Enqueue(file, file, text);
                warmer.Drain();

                sw.Restart();
                var b = CompleteWith(warmedService, text, line, character, file);
                warmTimes.Add(sw.Elapsed.TotalMilliseconds);

                if (b is not null) { localWarm++; }
            }

            totalPoints += points;
            baseHit += localBase;
            warmHit += localWarm;

            if (localWarm > localBase)
            {
                gainedPoints.Add(shortName + "（点 " + points + "：" + localBase + " → " + localWarm + "）");
            }

            Console.WriteLine("  " + shortName.PadRight(46) + " 补全点 " + points.ToString().PadLeft(3)
                + "  基线命中 " + localBase.ToString().PadLeft(3) + "  预热命中 " + localWarm.ToString().PadLeft(3));
        }

        Console.WriteLine();
        Console.WriteLine("  合计补全点 " + totalPoints + "；基线命中 " + baseHit + "；预热命中 " + warmHit
            + "；净增 " + (warmHit - baseHit));
        ReportTiming("基线补全耗时 (ms)", baseTimes);
        ReportTiming("预热后补全耗时 (ms)", warmTimes);
        Console.WriteLine("  缓存：文件层 " + cache.FileCount + " 个；文档层 " + cache.DocumentCount + " 个");
        Console.WriteLine();
        Console.WriteLine("  预热带来净增的文件：");
        foreach (var line in gainedPoints)
        {
            Console.WriteLine("    · " + line);
        }

        Console.WriteLine();
    }

    /// <summary>找出文档里所有「成员访问」光标点（点号之后）。</summary>
    private static List<int> MemberAccessPoints(string text)
    {
        var points = new List<int>();
        for (var i = 1; i < text.Length; i++)
        {
            if (text[i] != '.')
            {
                continue;
            }

            var caret = i + 1;
            var (line, character) = OffsetToLineChar(text, caret);
            if (MicroShader.IntelliSenseEngine.TriggerContext.Detect(text, line, character).Kind
                == MicroShader.IntelliSenseEngine.TriggerKind.MemberAccess)
            {
                points.Add(caret);
            }
        }

        return points;
    }

    private static string? CompleteWith(
        MicroShader.IntelliSenseEngine.IntelliSenseService service,
        string text,
        int line,
        int character,
        string uri)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            var request = new MicroShader.Domain.PositionRequest(uri, text, line, character);
            if (!service.ProvideCompletions(request, writer))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void ReportTiming(string label, List<double> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        samples.Sort();
        Console.WriteLine("  " + label.PadRight(28) + " 最小 " + samples[0].ToString("F3")
            + " ms   中位 " + samples[samples.Count / 2].ToString("F3") + " ms   (n=" + samples.Count + ")");
    }

    // ───────────── 11. 导航验收（documentLink / documentSymbol / F12 + 3ms 预算）─────────────

    private static void NavigationAcceptance(string[] args)
    {
        Console.WriteLine("################ 11. 导航验收（真实语料）################");

        var projectRoot = args.Length > 1 ? args[1] : "D:/Program Files/U3D/FamiliarWithURP";
        var root = args.Length > 2
            ? args[2]
            : Path.Combine(projectRoot, "Assets/URPshader-Plans/URPshader");

        if (!Directory.Exists(root))
        {
            Console.WriteLine("  [跳过] 目录不存在: " + root);
            return;
        }

        var files = Directory.GetFiles(root, "*.shader", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);

        var source = new ProbeFileSource(projectRoot);
        var cache = new MicroShader.IntelliSenseEngine.IncludeSymbolCache();
        var service = new MicroShader.NavigationEngine.NavigationService(source, cache);

        // 探针要的是「同步、确定性」预热（见下方注释：不靠 sleep），但预热器默认 RunWorker=true，
        // 后台 worker 会与 Drain() 抢同一队列 → 偶发在预热未完成时就往下走。显式关掉后台 worker，
        // 与自检侧的 Manual 选项同构，保证 Drain() 独占。
        using var warmer = new MicroShader.IntelliSenseEngine.IncludeSymbolWarmer(
            source, cache, new MicroShader.IntelliSenseEngine.IncludeSymbolOptions { RunWorker = false });

        var linkCount = 0;
        var linkResolved = 0;
        var symbolCount = 0;
        var containmentViolations = 0;
        var declarations = 0;
        var selfHits = 0;
        var crossFileHits = 0;
        var defLatency = new List<double>();
        var missByKind = new Dictionary<string, int>(StringComparer.Ordinal);
        var missSamples = new List<string>();
        var missAtZeroOffset = 0;

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var uri = source.ToFileUri(file);

            warmer.Enqueue(uri, file.Replace('\\', '/'), text);
            warmer.Drain();

            // ① documentLink（首屏纯词法）
            var links = NavigationJson(service, (s, w) => s.ProvideDocumentLinks(uri, file, text, w));
            if (links is not null)
            {
                using var doc = JsonDocument.Parse(links);
                foreach (var link in doc.RootElement.EnumerateArray())
                {
                    linkCount++;
                    var candidate = link.GetProperty("data").GetProperty("path").GetString() ?? string.Empty;
                    if (candidate.Length > 0 && source.Exists(candidate))
                    {
                        linkResolved++;
                    }
                }
            }

            // ② documentSymbol：统计节点数 + 校验 range 包含关系
            var symbols = NavigationJson(service, (s, w) => s.ProvideDocumentSymbols(uri, file, text, w));
            if (symbols is not null)
            {
                using var doc = JsonDocument.Parse(symbols);
                WalkSymbols(doc.RootElement, ref symbolCount, ref containmentViolations);
            }

            // ③ F12 自洽性：对每个声明的名字本身按 F12，应当指回它自己所在的行
            var parsed = new MicroShader.ShaderLab.ShaderLabParseResult();
            new MicroShader.ShaderLab.ShaderLabStateMachine().Parse(uri, text, parsed);

            var lineStarts = MicroShader.IntelliSenseEngine.LineOffsets.Build(text);

            foreach (var block in parsed.Passes)
            {
                var index = MicroShader.IntelliSenseEngine.HlslDocumentIndex.Build(block.RawHlslBlock.Span);
                foreach (var pair in index.Declarations)
                {
                    foreach (var decl in pair.Value)
                    {
                        if (decl.Kind == MicroShader.IntelliSenseEngine.HlslDeclKind.Field)
                        {
                            continue;
                        }

                        var offset = block.ContentStartOffset + decl.Offset;
                        var line = MicroShader.IntelliSenseEngine.LineOffsets.LineOf(lineStarts, offset);
                        var character = offset - lineStarts[line];

                        declarations++;

                        var sw = Stopwatch.StartNew();
                        var json = NavigationJson(service, (s, w) => s.ProvideDefinition(
                            new MicroShader.Domain.PositionRequest(uri, text, line, character), w));
                        sw.Stop();
                        defLatency.Add(sw.Elapsed.TotalMilliseconds);

                        if (json is null)
                        {
                            Classify(decl, offset, ref missAtZeroOffset, missByKind, missSamples, "无结果");
                            continue;
                        }

                        using var doc = JsonDocument.Parse(json);
                        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                        {
                            Classify(decl, offset, ref missAtZeroOffset, missByKind, missSamples, "空数组");
                            continue;
                        }

                        var first = doc.RootElement[0];
                        var targetLine = first.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();
                        var targetUri = first.TryGetProperty("uri", out var u) ? u.GetString() : first.GetProperty("targetUri").GetString();
                        var same = string.Equals(targetUri, uri, StringComparison.OrdinalIgnoreCase);

                        if (same && targetLine == line)
                        {
                            selfHits++;
                        }
                        else if (!same)
                        {
                            crossFileHits++;
                        }
                        else
                        {
                            Classify(decl, offset, ref missAtZeroOffset, missByKind, missSamples, "跳到别的行");
                        }
                    }
                }
            }
        }

        Console.WriteLine("  文件 " + files.Length + " 个");
        Console.WriteLine("  documentLink  : " + linkCount + " 条；候选物理文件存在 " + linkResolved
            + " 条（" + Percent(linkResolved, linkCount) + "）");
        Console.WriteLine("  documentSymbol: 节点 " + symbolCount + " 个；range 包含违规 " + containmentViolations + " 处");
        Console.WriteLine("  F12 自洽      : 声明点 " + declarations + " 个；指回自身 " + selfHits
            + "（" + Percent(selfHits, declarations) + "）；跨文件命中 " + crossFileHits
            + "；无结果 " + (declarations - selfHits - crossFileHits));
        Summarize("F12 延迟 (ms)", defLatency);

        var within = 0;
        foreach (var value in defLatency)
        {
            if (value <= 3.0)
            {
                within++;
            }
        }

        Console.WriteLine("  3ms 预算内  : " + within + " / " + defLatency.Count + "（" + Percent(within, defLatency.Count) + "）");

        var missTotal = 0;
        foreach (var pair in missByKind)
        {
            missTotal += pair.Value;
        }

        Console.WriteLine();
        Console.WriteLine("  未命中分类（共 " + missTotal + "，其中声明偏移为 0 的 " + missAtZeroOffset + " 个）：");
        foreach (var pair in missByKind.OrderByDescending(p => p.Value))
        {
            Console.WriteLine("    " + pair.Key.PadRight(30) + pair.Value);
        }

        Console.WriteLine("    样例:");
        foreach (var sample in missSamples)
        {
            Console.WriteLine("      · " + sample);
        }

        Console.WriteLine();
    }

    private static void Classify(
        MicroShader.IntelliSenseEngine.HlslDecl decl,
        int offset,
        ref int zeroOffset,
        Dictionary<string, int> byKind,
        List<string> samples,
        string reason)
    {
        if (decl.Offset == 0)
        {
            zeroOffset++;
        }

        var key = reason + " · " + decl.Kind;
        byKind[key] = byKind.TryGetValue(key, out var n) ? n + 1 : 1;

        if (samples.Count < 12)
        {
            samples.Add(key + "  name=" + decl.Name + "  declOffset=" + decl.Offset + "  absOffset=" + offset);
        }
    }

    private static string Percent(int part, int total)
        => total == 0 ? "n/a" : (part * 100.0 / total).ToString("F1") + "%";

    private static string? NavigationJson(
        MicroShader.NavigationEngine.NavigationService service,
        Func<MicroShader.NavigationEngine.NavigationService, Utf8JsonWriter, bool> write)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            if (!write(service, writer))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WalkSymbols(JsonElement array, ref int count, ref int violations)
    {
        foreach (var item in array.EnumerateArray())
        {
            count++;

            if (!item.TryGetProperty("children", out var children))
            {
                continue;
            }

            var outer = item.GetProperty("range");
            foreach (var child in children.EnumerateArray())
            {
                if (!RangeContains(outer, child.GetProperty("range")))
                {
                    violations++;
                }
            }

            WalkSymbols(children, ref count, ref violations);
        }
    }

    private static bool RangeContains(JsonElement outer, JsonElement inner)
    {
        var outerStart = outer.GetProperty("start");
        var outerEnd = outer.GetProperty("end");
        var innerStart = inner.GetProperty("start");
        var innerEnd = inner.GetProperty("end");

        var startOk = outerStart.GetProperty("line").GetInt32() < innerStart.GetProperty("line").GetInt32()
            || (outerStart.GetProperty("line").GetInt32() == innerStart.GetProperty("line").GetInt32()
                && outerStart.GetProperty("character").GetInt32() <= innerStart.GetProperty("character").GetInt32());

        var endOk = outerEnd.GetProperty("line").GetInt32() > innerEnd.GetProperty("line").GetInt32()
            || (outerEnd.GetProperty("line").GetInt32() == innerEnd.GetProperty("line").GetInt32()
                && outerEnd.GetProperty("character").GetInt32() >= innerEnd.GetProperty("character").GetInt32());

        return startOk && endOk;
    }

    private static void Summarize(string label, List<double> values)
    {
        if (values.Count == 0)
        {
            Console.WriteLine("  " + label.PadRight(28) + "        --");
            return;
        }

        var sorted = new List<double>(values);
        sorted.Sort();
        var min = sorted[0];
        var median = sorted[sorted.Count / 2];
        var p95 = sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * 0.95))];
        var max = sorted[sorted.Count - 1];

        Console.WriteLine("  " + label.PadRight(28)
            + min.ToString("F2").PadLeft(12) + median.ToString("F2").PadLeft(12)
            + p95.ToString("F2").PadLeft(12) + max.ToString("F2").PadLeft(12));
    }

    // ───────────── 7. 索引审计：现状顺序 vs 文本顺序 ─────────────

    private static void IndexAudit(string text)
    {
        Console.WriteLine("################ 7. 索引审计（现状合并顺序 vs 按文本顺序）################");

        var blocks = SafeSlice(text);
        var current = BuildMergedIndex(blocks);          // 现状：先 Pass 后 Shared（服务实际行为）
        var textual = BuildMergedIndexTextual(blocks);   // 假设修复：按文本出现顺序（Shared 先）

        string[] names =
        [
            "i", "o", "v", "uv", "albedo", "slope", "var_ColorRT", "pivot",
            "light", "mainLight", "instanceID", "_GrassPositionsBuffer", "nDirCustom", "bladeDirection",
        ];

        Console.WriteLine();
        Console.WriteLine("变量名".PadRight(24) + "现状(Pass→Shared)".PadRight(30) + "按文本顺序(Shared→Pass)");
        foreach (var name in names)
        {
            var a = current.VariablesToType.TryGetValue(name, out var ta) ? ta : "-";
            var b = textual.VariablesToType.TryGetValue(name, out var tb) ? tb : "-";
            var mark = a == b ? string.Empty : "   << 合并顺序影响";
            Console.WriteLine(name.PadRight(24) + a.PadRight(30) + b + mark);
        }

        Console.WriteLine();
        Console.WriteLine("── 登记表全量审计（现状合并顺序）──");
        Console.WriteLine("登记条目数 = " + current.VariablesToType.Count);

        var declared = 0;
        var upperSuspicious = new List<string>();
        var lowerSuspicious = new List<string>();
        var others = new List<string>();

        foreach (var pair in current.VariablesToType.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var type = pair.Value;
            if (current.Structs.ContainsKey(type))
            {
                declared++;
                continue;
            }

            if (type.Length > 0 && char.IsAsciiLetterUpper(type[0]))
            {
                upperSuspicious.Add(pair.Key + " → " + type);
            }
            else if (IsObviousBuiltin(type))
            {
                others.Add(pair.Key + " → " + type);
            }
            else
            {
                lowerSuspicious.Add(pair.Key + " → " + type);
            }
        }

        Console.WriteLine("  结构体字段已声明            : " + declared);
        Console.WriteLine("  类型名大写但未在本文件声明   : " + upperSuspicious.Count + "  ← 大概率是「函数名被当成类型」");
        Console.WriteLine("  小写/下划线开头且非内建类型  : " + lowerSuspicious.Count);
        Console.WriteLine("  内建类型                    : " + others.Count);

        Console.WriteLine();
        Console.WriteLine("  大写未声明的条目（逐条）:");
        foreach (var s in upperSuspicious)
        {
            Console.WriteLine("    " + s);
        }

        if (lowerSuspicious.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  小写/下划线未声明的条目（逐条）:");
            foreach (var s in lowerSuspicious)
            {
                Console.WriteLine("    " + s);
            }
        }

        Console.WriteLine();
        Console.WriteLine("  结构体字段索引（现状）:");
        foreach (var s in current.Structs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            Console.WriteLine("    struct " + s.Key + " { " + string.Join("; ", s.Value.Fields.Select(f => f.Type + " " + f.Name)) + " }");
        }

        Console.WriteLine();
    }

    private static bool IsObviousBuiltin(string type)
    {
        string[] prefixes = ["float", "half", "int", "uint", "bool", "double", "min16", "min10", "vector", "matrix"];
        foreach (var p in prefixes)
        {
            if (type.StartsWith(p, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static HlslDocumentIndex BuildMergedIndexTextual(List<BlockView> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            sb.Append(b.Text).Append(NL);
        }

        return HlslDocumentIndex.Build(sb.ToString().AsSpan());
    }

    // ───────────────────────────── 公共工具 ─────────────────────────────

    private static HlslDocumentIndex BuildMergedIndex(List<BlockView> blocks)
    {
        // 复刻 IntelliSenseService.Merge 的顺序：先所有 Pass 块，再所有共享块。
        var sb = new StringBuilder();
        foreach (var b in blocks.Where(b => b.Label.StartsWith("PASS", StringComparison.Ordinal)))
        {
            sb.Append(b.Text).Append(NL);
        }

        foreach (var b in blocks.Where(b => b.Label.StartsWith("SHARED", StringComparison.Ordinal)))
        {
            sb.Append(b.Text).Append(NL);
        }

        return HlslDocumentIndex.Build(sb.ToString().AsSpan());
    }

    private static List<(string Label, HlslDocumentIndex Index)> BuildPerBlockIndex(List<BlockView> blocks)
    {
        var list = new List<(string, HlslDocumentIndex)>();
        foreach (var b in blocks)
        {
            list.Add((b.Label, HlslDocumentIndex.Build(b.Text.AsSpan())));
        }

        return list;
    }

    private static List<BlockView> SafeSlice(string text)
    {
        var machine = new ShaderLabStateMachine();
        var res = machine.Parse("<probe>", text);
        var views = new List<BlockView>();
        foreach (var shared in res.SharedSnippets)
        {
            views.Add(ToView("SHARED#" + views.Count, "HLSLINCLUDE", shared));
        }

        foreach (var pass in res.Passes)
        {
            views.Add(ToView("PASS#" + pass.PassIndex + ".BLK" + pass.BlockOrdinalInPass, pass.Kind.ToString(), pass));
        }

        return views;
    }

    private static string? Complete(string text, int line, int character)
    {
        var service = new IntelliSenseService();
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            var request = new MicroShader.Domain.PositionRequest("<probe>", text, line, character);
            if (!service.ProvideCompletions(request, writer))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static List<string> ExtractLabels(string json)
    {
        var labels = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("items", out var items))
        {
            return labels;
        }

        foreach (var item in items.EnumerateArray())
        {
            var label = item.GetProperty("label").GetString() ?? string.Empty;
            var detail = item.TryGetProperty("detail", out var d) ? d.GetString() : null;
            var kind = item.TryGetProperty("kind", out var k) ? k.GetInt32() : 0;
            labels.Add(detail is null ? label : label + " (" + detail + ", kind=" + kind + ")");
        }

        return labels;
    }

    private static string Window(string text, int caret, int radius)
    {
        var from = Math.Max(0, caret - radius);
        var to = Math.Min(text.Length, caret + radius);
        var head = text[from..caret];
        var tail = text[caret..to];
        var prefix = from > 0 ? "…" : string.Empty;
        var suffix = to < text.Length ? "…" : string.Empty;
        return prefix + Clean(head) + "<|>" + Clean(tail) + suffix;
    }

    private static string Clean(string s) => s.Replace(NL.ToString(), "<NL>").Replace((char)13, ' ');

    private static string Quote(string s) => Q + s.Replace(NL, ' ') + Q;

    private static (int Line, int Character) OffsetToLineChar(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == NL)
            {
                line++;
                lineStart = i + 1;
            }
        }

        var character = offset - lineStart;
        if (character > 0 && offset - 1 < text.Length && text[offset - 1] == (char)13)
        {
            character--;
        }

        return (line, character);
    }

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static int CountChar(string text, char c)
    {
        var n = 0;
        foreach (var ch in text)
        {
            if (ch == c)
            {
                n++;
            }
        }

        return n;
    }
}
