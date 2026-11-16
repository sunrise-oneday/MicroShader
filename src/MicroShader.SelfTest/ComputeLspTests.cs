using System.Text.Json;
using MicroShader.CoordinationEngine.Lsp;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.DiagnosticEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// compute 文件的 LSP 端到端回环：didOpen → publishDiagnostics。
/// </summary>
/// <remarks>
/// 锁的行为：单元测试各自验证了切片、派发与归属，但没有一条经过协商层。
/// .compute 走的是「无 ShaderLab 标记」的裸 HLSL 路径，一旦宿主按扩展名过滤文档
/// （或语言 id 不认），整条 compute 支持就在真实编辑器里完全不可见 ——
/// 而所有单元测试仍然全绿。这条用例用真实 stdio 帧把这条路走一遍。
/// 语料直接落临时目录：样本不带 #include，不需要工程上下文。
/// </remarks>
internal static class ComputeLspTests
{
    private const string Suite = "ComputeLsp";

    public static void Register()
    {
        TestSuite.Add(Suite, "DidOpenBrokenCompute", DidOpenBrokenCompute);
        TestSuite.Add(Suite, "DidOpenNoKernelCompute", DidOpenNoKernelCompute);
        TestSuite.Add(Suite, "CompletionAfterPragmaKernel", CompletionAfterPragmaKernel);
    }

    // ── 1. 打开有错的 compute：两个错都要经 LSP 送到客户端 ──

    private static void DidOpenBrokenCompute()
    {
        var text = Corpus.Read("sample-compute-broken.compute");
        var uri = OpenTempCompute("Broken.compute", text, out var diagnostics);

        var weighLine = DiagnosticsLine(text, "p.weigh");
        var missingLine = DiagnosticsLine(text, "missingValue");

        Check.True(
            diagnostics.Any(d => d.Line == weighLine),
            $"p.weigh 的错必须经 LSP 发到第 {weighLine} 行，实际：" + Describe(diagnostics));
        Check.True(
            diagnostics.Any(d => d.Line == missingLine),
            $"missingValue 的错必须经 LSP 发到第 {missingLine} 行，实际：" + Describe(diagnostics));
        Check.True(
            !diagnostics.Any(d => d.Code == DiagnosticEngineCodes.ComputeMissingKernel),
            "有 #pragma kernel 的文件不得报 " + DiagnosticEngineCodes.ComputeMissingKernel);
        Check.True(
            diagnostics.Any(d => d.Severity == 1),
            "未声明标识符必须是 Error 级别，实际：" + Describe(diagnostics));

        TestCaseHelpers.Report($"broken.compute → {uri} 共 {diagnostics.Count} 条诊断");
    }

    // ── 2. 打开缺 kernel 的 compute：CS0101 必须经 LSP 送到客户端 ──

    private static void DidOpenNoKernelCompute()
    {
        var text = Corpus.Read("sample-compute-nokernel.compute");
        _ = OpenTempCompute("NoKernel.compute", text, out var diagnostics);

        var missing = diagnostics.Where(d => d.Code == DiagnosticEngineCodes.ComputeMissingKernel).ToList();
        Check.Equal(1, missing.Count, "缺 #pragma kernel 必须经 LSP 报出恰好一条 CS0101，实际：" + Describe(diagnostics));
        Check.Equal(2, missing[0].Severity, "CS0101 是 Warning（LSP severity 2）");
        Check.True(
            missing[0].Message.Contains("pragma kernel", StringComparison.Ordinal),
            "CS0101 文案要点明缺的是 #pragma kernel（实际：" + missing[0].Message + "）");

        TestCaseHelpers.Report("nokernel.compute → CS0101：" + missing[0].Message);
    }

    // ── 3. `#pragma kernel ` 后的补全必须经 LSP 送到客户端 ──

    /// <remarks>
    /// 这个位置没有标识符前缀，TriggerContext 返回 None —— 补全完全依赖
    /// IntelliSenseService 里那条独立的 pragma 入口路径。单元测试盖住了引擎，
    /// 但只有经过会话分发才能证明「该位置会真的发到引擎」（不会在 Session 里被提前拦掉）。
    /// </remarks>
    private static void CompletionAfterPragmaKernel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "microshader-compute-lsp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "Completion.compute");
        File.WriteAllText(file, CompletionSource);

        try
        {
            using var analyzer = CreateAnalyzer(directory);
            var intelliSense = new MicroShader.IntelliSenseEngine.IntelliSenseService();
            var session = new LspServerSession(
                analyzer.AnalyzeAsync,
                intelliSense: new IntelliSenseProvider
                {
                    Completion = intelliSense.ProvideCompletions,
                    Hover = intelliSense.ProvideHover,
                });
            using var client = new LspTestClient(session);

            client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
            _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();
            client.SendAtomicAsync(LspCorpus.DidOpen(file)).AsTask().GetAwaiter().GetResult();
            _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();

            var openUri = LspCorpus.UriOf(file);
            client.SendAtomicAsync(CompletionRequest(2, openUri, line: 0, character: PragmaPrefixLength))
                .AsTask().GetAwaiter().GetResult();

            var response = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            Check.Equal(2, root.GetProperty("id").GetInt32(), "响应必须对上请求 id");
            Check.False(root.TryGetProperty("error", out _), "补全不得返回错误：" + response);

            var labels = CompletionLabels(root.GetProperty("result"));
            Check.Contains(labels, "CSMain", "`#pragma kernel ` 后必须补出 CSMain，实际：" + string.Join(",", labels));
            Check.DoesNotContain(labels, "Payload", "结构体不是入口函数");

            TestCaseHelpers.Report("LSP 补全候选 " + labels.Count + " 条，含 CSMain（uri " + openUri + "）");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── 辅助 ──
    private readonly record struct PublishedDiagnostic(string? Code, int Severity, int Line, string Message);

    /// <summary>把正文写到临时目录、经真实 stdio 帧 didOpen，收下第一份发布。</summary>
    /// <remarks>
    /// LspCorpus.DidOpen 从磁盘读正文，所以必须先把语料落盘。
    /// 语言 id 保持 "shaderlab"：客户端也是按这一个 id 注册的（.compute 借 hlsl 高亮）。
    /// </remarks>
    private static string OpenTempCompute(string fileName, string text, out List<PublishedDiagnostic> diagnostics)
    {
        var directory = Path.Combine(Path.GetTempPath(), "microshader-compute-lsp", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, fileName);
        File.WriteAllText(file, text);

        var analyzer = CreateAnalyzer(directory);
        var session = new LspServerSession(analyzer.AnalyzeAsync);
        using var client = new LspTestClient(session);
        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.DidOpen(file)).AsTask().GetAwaiter().GetResult();

        var publish = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(publish);
        var root = document.RootElement;
        Check.Equal(LspMethods.PublishDiagnostics, root.GetProperty("method").GetString(), "必须收到 publishDiagnostics");

        var parameters = root.GetProperty("params");
        var uri = parameters.GetProperty("uri").GetString()!;
        Check.Equal(LspCorpus.UriOf(file), uri, "诊断必须挂在这份 .compute 的 URI 上");

        diagnostics = [];
        foreach (var item in parameters.GetProperty("diagnostics").EnumerateArray())
        {
            var start = item.GetProperty("range").GetProperty("start");

            // LSP 是 0-based，用例断言按 1-based 行号写。
            diagnostics.Add(new PublishedDiagnostic(
                item.TryGetProperty("code", out var code) ? code.GetString() : null,
                item.GetProperty("severity").GetInt32(),
                start.GetProperty("line").GetInt32() + 1,
                item.GetProperty("message").GetString() ?? string.Empty));
        }

        try
        {
            client.SendAtomicAsync(LspCorpus.DidClose(uri)).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        return uri;
    }

    /// <summary>
    /// 在语料目录上建一个真实分析器。
    /// </summary>
    /// <remarks>
    /// 不复用 LspCorpusTests 的共享实例：那个实例绑在 Unity 工程根上，
    /// 而 compute 样本不带 #include，用一个空工程目录就够，也不受 --selftest 的工程参数影响。
    /// </remarks>
    private static ShaderEngineAnalyzer CreateAnalyzer(string projectRoot)
    {
        var dxc = DxcGatewayTests.DxcDirectory();
        if (dxc is null) throw new SkipException("未找到 dxcompiler.dll");
        return new ShaderEngineAnalyzer(projectRoot, dxc);
    }

    // ── 语料 ──

    /// <summary>补全语料：第 0 行光标停在 "#pragma kernel " 之后。</summary>
    private const string CompletionSource = """
        #pragma kernel 
        RWStructuredBuffer<float4> outputBuffer;

        struct Payload
        {
            float4 color;
        };

        [numthreads(1, 1, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            outputBuffer[id.x] = float4(1, 0, 0, 1);
        }
        """;

    /// <summary>"#pragma kernel " 这段前缀的长度 = 第 0 行光标列号。</summary>
    private const int PragmaPrefixLength = 15;

    // ── 辅助 ──

    private static string CompletionRequest(int id, string uri, int line, int character) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":" + LspCorpus.JsonString(LspMethods.Completion)
        + ",\"params\":{\"textDocument\":{\"uri\":" + LspCorpus.JsonString(uri) + "},\"position\":{\"line\":"
        + line + ",\"character\":" + character + "}}}";

    /// <summary>从 completion 结果里抽 label（兼容 CompletionList 与裸数组两种形状）。</summary>
    private static List<string> CompletionLabels(JsonElement result)
    {
        var labels = new List<string>();
        if (result.ValueKind != JsonValueKind.Object && result.ValueKind != JsonValueKind.Array)
        {
            return labels;
        }

        var items = result.ValueKind == JsonValueKind.Array
            ? result
            : result.TryGetProperty("items", out var list) ? list : default;

        if (items.ValueKind != JsonValueKind.Array)
        {
            return labels;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
            {
                labels.Add(label.GetString()!);
            }
        }

        return labels;
    }

    private static int DiagnosticsLine(string text, string needle)    {
        var line = Corpus.LineNumberContaining(text, needle);
        Check.True(line > 0, "语料缺少 " + needle);
        return line;
    }

    private static string Describe(List<PublishedDiagnostic> diagnostics)
    {
        if (diagnostics.Count == 0) return "（0 条诊断）";
        return string.Join(" | ", diagnostics.Select(d => $"{d.Code ?? d.Severity.ToString()}@{d.Line}:{d.Message}"));
    }
}