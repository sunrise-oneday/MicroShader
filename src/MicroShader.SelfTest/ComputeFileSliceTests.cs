using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// compute 文件切片兜底：裸 .compute 整篇当单个编译单元的形态契约。
/// </summary>
/// <remarks>
/// 锁的行为：.compute 里没有任何 ShaderLab 结构标记（没有 SubShader / Pass / HLSLPROGRAM），
/// 切片器原先只把「有 program block 的 .shader」切成块，于是 compute 文件切出 0 个块、
/// 诊断引擎无事可做 —— 表现为「compute 文件永远一条诊断都没有」，看起来像工具根本没生效。
/// 兜底必须只对 .compute 扩展名生效，否则 .hlsl 头文件会被独立当成编译单元报一堆错
/// （它们本来就只该在 include 它的 .shader 里被诊断）。
/// </remarks>
internal static class ComputeFileSliceTests
{
    private const string Suite = "ComputeSlice";

    public static void Register()
    {
        TestSuite.Add(Suite, "SingleBlock", SingleBlock);
        TestSuite.Add(Suite, "SnippetShape", SnippetShape);
        TestSuite.Add(Suite, "PragmaVisible", PragmaVisible);
        TestSuite.Add(Suite, "NoKernel", NoKernel);
        TestSuite.Add(Suite, "HlslNotSliced", HlslNotSliced);
        TestSuite.Add(Suite, "ShaderBlockUnchanged", ShaderBlockUnchanged);
    }

    private static ShaderLabParseResult ParseFixture(string fileName)
        => new ShaderLabStateMachine().Parse(Corpus.PathOf(fileName), Corpus.Read(fileName));

    private static void SingleBlock()
    {
        var parse = ParseFixture("sample-compute-broken.compute");

        Check.Equal(1, parse.Passes.Count, ".compute 必须兜底切出恰好 1 个伪 snippet");
        Check.Equal(0, parse.SubShaderCount, ".compute 不该被认出 SubShader");
        Check.Equal(0, parse.SharedBlocks.Count, ".compute 不该有共享块");
        Check.False(parse.HasUnterminatedBlock, ".compute 不该被判未闭合");
        TestCaseHelpers.Report($"compute 伪 snippet {parse.Passes.Count} 个，SubShader {parse.SubShaderCount} 个");
    }

    private static void SnippetShape()
    {
        var text = Corpus.Read("sample-compute-broken.compute");
        var parse = ParseFixture("sample-compute-broken.compute");
        var snippet = parse.Passes[0];

        Check.Equal(ShaderBlockKind.ComputeFile, snippet.Kind, "块类型必须是 ComputeFile");
        Check.False(snippet.IsSharedBlock, "伪 snippet 不是共享块");
        Check.True(snippet.IsOrphan, "伪 snippet 不属于任何 Pass，必须标 IsOrphan");
        Check.Equal(-1, snippet.PassIndex, "伪 snippet 没有 Pass 序号");
        Check.Equal(-1, snippet.PassStartLineNumber, "伪 snippet 没有 Pass 起始行");

        // 覆盖整篇：起点 0、终点末行（0-based 闭区间），内容偏移覆盖全文。
        // 末行下标恒等于正文里的换行符个数：有尾换行时多出来的空行是最后一个「行」。
        Check.Equal(0, snippet.StartLineNumber, "伪 snippet 从第 0 行开始");
        var totalLines = text.Count(c => c == '\n');
        Check.Equal(totalLines, snippet.EndLineNumber, "伪 snippet 终点是末行（0-based）");
        Check.Equal(0, snippet.ContentStartOffset, "伪 snippet 内容从偏移 0 开始");
        Check.Equal(text.Length, snippet.ContentEndOffset, "伪 snippet 内容覆盖到全文末尾");
        Check.Equal(text.Length, snippet.RawHlslBlock.Length, "RawHlslBlock 就是全文");
        TestCaseHelpers.Report($"伪 snippet 行区间 [0, {snippet.EndLineNumber}]，正文 {snippet.RawHlslBlock.Length} 字符");
    }

    private static void PragmaVisible()
    {
        var parse = ParseFixture("sample-compute-broken.compute");
        var snippet = parse.Passes[0];

        Check.Equal("CSMain", snippet.KernelEntry, "KernelEntry 必须取到 CSMain");
        Check.SequenceEqual(["CSMain"], snippet.KernelEntries, "KernelEntries 必须与单 kernel 一致");
        TestCaseHelpers.Report("kernel 列表：" + string.Join(",", snippet.KernelEntries));
    }

    private static void NoKernel()
    {
        var parse = ParseFixture("sample-compute-nokernel.compute");

        Check.Equal(1, parse.Passes.Count, "缺 #pragma kernel 的 .compute 同样要兜底成 1 块");
        var snippet = parse.Passes[0];
        Check.True(snippet.KernelEntry is null, "没有 #pragma kernel 时 KernelEntry 必须为 null");
        Check.Equal(0, snippet.KernelEntries.Count, "没有 #pragma kernel 时 KernelEntries 必须为空");
        TestCaseHelpers.Report("无 kernel 样本：KernelEntry=" + (snippet.KernelEntry ?? "<null>"));
    }

    /// <summary>
    /// 不回归：.hlsl 头文件不得被兜底成编译单元。
    /// </summary>
    /// <remarks>
    /// 这是兜底条件里 `filePath.EndsWith(".compute")` 的门禁。少了它，URP 的每个头文件
    /// 都会独立产出诊断（它们本来就只在 include 它的 .shader 里被编译）。
    /// </remarks>
    private static void HlslNotSliced()
    {
        var text = Corpus.Read("PureProbe.hlsl");
        var path = Corpus.PathOf("PureProbe.hlsl");
        var parse = new ShaderLabStateMachine().Parse(path, text);

        Check.Equal(0, parse.Passes.Count, ".hlsl 头文件不得被兜底成编译单元");
        Check.Equal(0, parse.SubShaderCount, ".hlsl 头文件里没有 SubShader");
        TestCaseHelpers.Report(".hlsl 样本切片：Pass " + parse.Passes.Count + " 个（必须为 0）");
    }

    /// <summary>
    /// 不回归：真实 .shader 的切片数量与块类型不受兜底影响。
    /// </summary>
    private static void ShaderBlockUnchanged()
    {
        var parse = ParseFixture("MixedBlocks.shader");

        Check.True(parse.Passes.Count > 0, "MixedBlocks.shader 必须仍然切出 Pass");
        foreach (var snippet in parse.Passes)
        {
            Check.True(
                snippet.Kind is ShaderBlockKind.Hlsl or ShaderBlockKind.Cg,
                "真实 .shader 的 Pass 块类型只能是 Hlsl/Cg，不得是 ComputeFile");
        }

        TestCaseHelpers.Report("MixedBlocks.shader：Pass " + parse.Passes.Count + " 个，块类型均非 ComputeFile");
    }
}