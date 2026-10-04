using System.Text.Json;
using MicroShader.ContextEngine;
using MicroShader.CoordinationEngine.Lsp;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 把真实的 <see cref="ShaderDiagnosticEngine"/> 适配成协商层的分析器契约。
/// </summary>
/// <remarks>
/// "集成时发现的一处契约缺口"："ShaderDiagnosticEngine.Analyze" 返回的
/// "DiagnosticReport.Items" 只包含"编译器侧"诊断，并"不"包含模块 1 切片阶段产出的
/// SL0001..SL0005。而 ADR-024 要求「按 Pass 维护诊断集合并合并后整份发布」—— 所以把两者合流
/// 是宿主（本适配器）的职责：切片诊断归到文件级槽位 "-1"，编译器诊断按行号归到所在 Pass。
/// 为了让语料用例可复现，这里对引擎调用做了串行化（协商层只保证「同一文档串行」，
/// 不同文档仍可能并发；引擎的 PerThread 池本身支持并发，但自检不需要压这个）。
/// </remarks>
internal sealed class ShaderEngineAnalyzer : IDisposable
{
    private readonly ShaderDiagnosticEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ShaderEngineAnalyzer(string projectRoot, string dxcDirectory)
    {
        var index = VfsIndexBuilder.Build(projectRoot);
        var registry = new ShaderContextRegistry(index);

        if (!ShaderDiagnosticEngine.TryCreate(registry, dxcDirectory, out var engine, out var error) || engine is null)
        {
            throw new SkipException("dxcompiler.dll 不可用: " + error);
        }

        _engine = engine;
        PackageCount = index.PackageCount;
    }

    public int PackageCount { get; }

    public async Task<AnalysisOutcome> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Analyze(request), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _gate.Dispose();
    }

    private AnalysisOutcome Analyze(AnalysisRequest request)
    {
        var document = new SourceShaderDocument
        {
            FileUri = request.Uri,
            FilePath = request.FilePath,
            Version = request.Version,
            FullText = request.Text,
        };

        var parse = new ShaderLabStateMachine().Parse(request.FilePath, request.Text);
        var report = _engine.Analyze(document, request.ProbeLine, request.IsFullScan);

        var slots = new Dictionary<int, List<ShaderDiagnosticItem>>();

        void Add(int slot, ShaderDiagnosticItem item)
        {
            if (!slots.TryGetValue(slot, out var list))
            {
                list = [];
                slots[slot] = list;
            }

            list.Add(item);
        }

        // 切片阶段诊断（模块 1）没有 Pass 归属，落到文件级槽位 -1。
        foreach (var item in parse.Diagnostics) Add(-1, item);

        foreach (var item in report.Items) Add(SlotOf(parse, item.Line), item);

        // 全量扫描必须覆盖文档当前的全部 Pass 槽位，否则已消失的 Pass 的旧红线不会被清掉。
        var covered = request.IsFullScan
            ? parse.Passes.Select(static p => p.PassIndex).Distinct().OrderBy(static i => i).ToList()
            : slots.Keys.OrderBy(static i => i).ToList();

        if (request.IsFullScan && parse.SharedSnippets.Count > 0 && !covered.Contains(-1)) covered.Insert(0, -1);

        return new AnalysisOutcome
        {
            Uri = request.Uri,
            Version = request.Version,
            Epoch = request.Epoch,
            IsFullScan = request.IsFullScan,
            PassDiagnostics = slots.ToDictionary(static kv => kv.Key, static kv => (IReadOnlyList<ShaderDiagnosticItem>)kv.Value),
            CoveredPasses = covered,
        };
    }

    private static int SlotOf(ShaderLabParseResult parse, int line)
    {
        foreach (var snippet in parse.Passes)
        {
            if (line >= snippet.StartLineNumber && line <= snippet.EndLineNumber) return snippet.PassIndex;
        }

        return -1;
    }
}

/// <summary>
/// 用 "tests/Fixtures" 里的全部测试着色器驱动完整 LSP 回环（真实 DXC 编译）。
/// </summary>
internal static class LspCorpusTests
{
    private static string? _projectRoot;
    private static ShaderEngineAnalyzer? _analyzer;

    public static void Configure(string? projectRoot) => _projectRoot = projectRoot ?? DefaultProject();

    public static void Register()
    {
        TestSuite.Add("LspCorpus", "EveryFixtureRoundTrip", EveryFixtureRoundTrip);
        TestSuite.Add("LspCorpus", "BrokenUrpDeadInclude", BrokenUrpDeadInclude);
        TestSuite.Add("LspCorpus", "SliceDiagnosticsMerged", SliceDiagnosticsMerged);
        TestSuite.Add("LspCorpus", "MarkersInCommentsAreClean", MarkersInCommentsAreClean);
        TestSuite.Add("LspCorpus", "EditThenSaveClearsError", EditThenSaveClearsError);
    }

    // ── 用例 ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// 把每一个固定用例 .shader 都真的通过 LSP 协议喂进去，断言每个文件都能拿到
    /// 一份归属正确、带版本号的 publishDiagnostics。
    /// </summary>
    private static void EveryFixtureRoundTrip()
    {
        var files = LspCorpus.FixtureFiles();
        Check.True(files.Count >= 15, "固定用例语料至少应有 15 个 .shader，实际 " + files.Count);

        var analyzer = GetAnalyzer();
        var session = new LspServerSession(analyzer.AnalyzeAsync);
        using var client = new LspTestClient(session);

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();

        var totalDiagnostics = 0;
        var filesWithErrors = 0;

        foreach (var file in files)
        {
            var uri = LspCorpus.UriOf(file);
            client.SendAtomicAsync(LspCorpus.DidOpen(file)).AsTask().GetAwaiter().GetResult();

            var publish = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(publish);
            var root = document.RootElement;

            Check.Equal(
                LspMethods.PublishDiagnostics,
                root.GetProperty("method").GetString(),
                Path.GetFileName(file) + " 必须收到 publishDiagnostics");

            var parameters = root.GetProperty("params");
            Check.Equal(uri, parameters.GetProperty("uri").GetString(), Path.GetFileName(file) + " 诊断归属 URI");
            Check.Equal(1, parameters.GetProperty("version").GetInt32(), Path.GetFileName(file) + " 诊断必须带版本号");

            var diagnostics = parameters.GetProperty("diagnostics");
            totalDiagnostics += diagnostics.GetArrayLength();

            var hasError = false;
            foreach (var item in diagnostics.EnumerateArray())
            {
                if (item.GetProperty("severity").GetInt32() == 1) hasError = true;

                // 每条诊断都必须能被还原成一个合法的 LSP 位置。
                var start = item.GetProperty("range").GetProperty("start");
                Check.True(start.GetProperty("line").GetInt32() >= 0, "行号必须非负（LSP 是 0-based）");
                Check.True(start.GetProperty("character").GetInt32() >= 0, "列号必须非负");
            }

            if (hasError) filesWithErrors++;

            client.SendAtomicAsync(LspCorpus.DidClose(uri)).AsTask().GetAwaiter().GetResult();
            var cleared = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            using (var clearedDocument = JsonDocument.Parse(cleared))
            {
                Check.Equal(
                    0,
                    clearedDocument.RootElement.GetProperty("params").GetProperty("diagnostics").GetArrayLength(),
                    Path.GetFileName(file) + " didClose 必须清空诊断");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"        · 语料回环：{files.Count} 个 .shader 全部完成 didOpen→publishDiagnostics→didClose");
        Console.WriteLine($"        · 累计诊断 {totalDiagnostics} 条，其中 {filesWithErrors} 个文件含 Error");
        Console.WriteLine($"        · 调度统计：分析发布 {session.AnalysesPublished} 次、合并 {session.ChangesCoalesced} 次、丢弃 {session.AnalysesDiscarded} 次");
        Console.WriteLine($"        · 影子文档在全部 didClose 后剩余 {session.Documents.Count} 个");

        Check.Equal(0, session.Documents.Count, "全部 didClose 之后不得残留影子文档");
        Check.Equal(0, session.ProtocolErrors, "整轮语料回环不得出现协议错误");
    }

    /// <summary>BrokenUrp.shader 的包名拼错必须被翻成一条挂在 include 行上的 Error。</summary>
    private static void BrokenUrpDeadInclude()
    {
        var file = Path.Combine(Corpus.FixtureDirectory, "BrokenUrp.shader");
        Check.True(File.Exists(file), "缺少语料 BrokenUrp.shader");

        var diagnostics = OpenAndCollect(file, out var uri);
        Check.True(diagnostics.Count > 0, "故意埋错的语料必须报出至少一条诊断");

        var missing = diagnostics.Where(static d => d.Code == "MS0001").ToList();
        Check.Equal(1, missing.Count, "必须恰好报出一条 MS0001 依赖缺失");
        Check.Equal(1, missing[0].Severity, "依赖缺失必须是 Error");
        Check.Equal(32, missing[0].Line, "必须挂在第 32 行的 #include 上");
        Check.True(
            missing[0].Message.Contains("universial", StringComparison.Ordinal),
            "文案必须指出具体是哪个包解析不到");

        Console.WriteLine($"        · BrokenUrp.shader → {uri} 共 {diagnostics.Count} 条诊断，MS0001 定位 {missing[0].Line}:{(missing[0].Character + 1)}");
    }

    /// <summary>切片阶段的诊断（模块 1 的 SL 码）必须与编译诊断合并后一起发布。</summary>
    private static void SliceDiagnosticsMerged()
    {
        var unterminated = OpenAndCollect(Path.Combine(Corpus.FixtureDirectory, "UnterminatedBlock.shader"), out _);
        Check.True(
            unterminated.Any(static d => d.Code == "SL0001"),
            "未闭合代码块必须报出 SL0001（切片阶段诊断必须被合并进发布集合）");

        var blockComment = OpenAndCollect(Path.Combine(Corpus.FixtureDirectory, "UnterminatedBlockComment.shader"), out _);
        Check.True(
            blockComment.Any(static d => d.Code == "SL0004"),
            "未闭合块注释必须报出 SL0004");

        var stringLiteral = OpenAndCollect(Path.Combine(Corpus.FixtureDirectory, "UnterminatedString.shader"), out _);
        Check.True(
            stringLiteral.Any(static d => d.Code == "SL0005"),
            "未闭合字符串必须报出 SL0005");

        Console.WriteLine(
            $"        · 切片诊断：UnterminatedBlock {unterminated.Count} 条 / "
            + $"UnterminatedBlockComment {blockComment.Count} 条 / UnterminatedString {stringLiteral.Count} 条");
    }

    /// <summary>
    /// MarkersInCommentsAndStrings.shader 里，HLSLPROGRAM/ENDHLSL 之类标记只出现在注释与字符串中 ——
    /// 管线不得因此产生任何 Error（否则整条链路的词法层就是坏的）。
    /// </summary>
    private static void MarkersInCommentsAreClean()
    {
        var diagnostics = OpenAndCollect(Path.Combine(Corpus.FixtureDirectory, "MarkersInCommentsAndStrings.shader"), out _);
        var errors = diagnostics.Where(static d => d.Severity == 1).ToList();
        Check.Equal(0, errors.Count, "注释/字符串里的块标记不得导致任何 Error，实际: "
            + string.Join("; ", errors.Select(static e => e.Line + ":" + e.Message)));

        // 未闭合块 / 结束标记不匹配这两类切片诊断也一条都不该有。
        Check.True(
            !diagnostics.Any(static d => d.Code is "SL0001" or "SL0003"),
            "注释里的块标记不得被当成真标记（SL0001/SL0003 必须为零）");

        var clean = OpenAndCollect(Path.Combine(Corpus.FixtureDirectory, "SimpleUrp.shader"), out _);
        Check.Equal(
            0,
            clean.Count(static d => d.Severity == 1),
            "SimpleUrp.shader 是合法 URP 着色器，不得有任何 Error");

        Console.WriteLine($"        · MarkersInCommentsAndStrings：{diagnostics.Count} 条诊断（0 Error）；SimpleUrp：{clean.Count} 条（0 Error）");
    }

    /// <summary>完整编辑闭环：didOpen（坏）→ didChange（修好）→ didSave → 红线必须消失。</summary>
    private static void EditThenSaveClearsError()
    {
        var file = Path.Combine(Corpus.FixtureDirectory, "BrokenUrp.shader");
        var text = File.ReadAllText(file);
        var uri = LspCorpus.UriOf(file);

        var analyzer = GetAnalyzer();
        var session = new LspServerSession(analyzer.AnalyzeAsync);
        using var client = new LspTestClient(session);

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();

        // 1) 打开坏文件 → 必须有 MS0001
        client.SendAtomicAsync(LspCorpus.DidOpen(file)).AsTask().GetAwaiter().GetResult();
        var opened = ParsePublish(client.ReceiveAsync().AsTask().GetAwaiter().GetResult());
        Check.True(opened.Items.Any(static d => d.Code == "MS0001"), "打开时必须有依赖缺失");

        // 2) 把包名拼写改正（这就是用户在编辑器里敲的那一下）
        var fixedText = text.Replace("universial", "universal", StringComparison.Ordinal);
        Check.True(!string.Equals(fixedText, text, StringComparison.Ordinal), "语料里必须真的存在 universial 这个拼写");
        client.SendAtomicAsync(LspCorpus.DidChange(uri, fixedText, 2)).AsTask().GetAwaiter().GetResult();

        // 3) 立刻保存 → 走全量穿透
        client.SendAtomicAsync(LspCorpus.DidSave(uri, reason: 1)).AsTask().GetAwaiter().GetResult();

        WaitUntil(
            () => session.PublishedCount >= 2 && session.AnalysesPublished >= 2,
            TimeSpan.FromSeconds(20));

        // 收帧：只认最后一份（版本 2）的发布，中途的防抖结果按版本栅栏可能被丢弃。
        PublishSnapshot latest = default;
        for (var i = 0; i < 4; i++)
        {
            var frame = client.TryReceiveAsync(TimeSpan.FromSeconds(20)).AsTask().GetAwaiter().GetResult();
            if (frame is null) break;
            var parsed = ParsePublish(frame);
            latest = parsed;
            if (parsed.Version >= 2) break;
        }

        Check.True(latest.Version >= 2, "必须收到版本 ≥ 2 的发布，实际 " + latest.Version);
        Check.True(
            !latest.Items.Any(static d => d.Code == "MS0001"),
            "修好包名并保存后，依赖缺失的红线必须消失（实际仍有: "
            + string.Join("; ", latest.Items.Where(static d => d.Code == "MS0001").Select(static d => d.Message)) + "）");

        Console.WriteLine();
        Console.WriteLine($"        · 编辑闭环：打开 → {opened.Items.Count} 条诊断（含 MS0001）");
        Console.WriteLine($"        · 改正包名 + 保存 → {latest.Items.Count} 条诊断，MS0001 已消除");
        Console.WriteLine($"        · 调度统计：发布 {session.AnalysesPublished} 次、合并 {session.ChangesCoalesced} 次、丢弃 {session.AnalysesDiscarded} 次");
    }

    // ── 辅助 ───────────────────────────────────────────────────────────────────

    private readonly record struct PublishedDiagnostic(
        string? Code,
        int Severity,
        int Line,
        int Character,
        string Message);

    private readonly record struct PublishSnapshot(int Version, List<PublishedDiagnostic> Items);

    private static PublishSnapshot ParsePublish(string json)
    {
        using var document = JsonDocument.Parse(json);
        var parameters = document.RootElement.GetProperty("params");
        var version = parameters.GetProperty("version").GetInt32();
        var items = new List<PublishedDiagnostic>();
        foreach (var item in parameters.GetProperty("diagnostics").EnumerateArray())
        {
            var start = item.GetProperty("range").GetProperty("start");
            items.Add(new PublishedDiagnostic(
                item.TryGetProperty("code", out var code) ? code.GetString() : null,
                item.GetProperty("severity").GetInt32(),
                start.GetProperty("line").GetInt32(),
                start.GetProperty("character").GetInt32(),
                item.GetProperty("message").GetString() ?? string.Empty));
        }

        return new PublishSnapshot(version, items);
    }

    /// <summary>打开一个语料文件，收下第一份发布，返回其诊断（行号已换算回 1-based）。</summary>
    private static List<PublishedDiagnostic> OpenAndCollect(string file, out string uri)
    {
        uri = LspCorpus.UriOf(file);
        var analyzer = GetAnalyzer();
        var session = new LspServerSession(analyzer.AnalyzeAsync);
        using var client = new LspTestClient(session);

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.DidOpen(file)).AsTask().GetAwaiter().GetResult();

        var publish = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        var snapshot = ParsePublish(publish);

        // LSP 是 0-based，测试断言按人读的 1-based 行号来。
        return snapshot.Items
            .Select(static d => d with { Line = d.Line + 1 })
            .ToList();
    }

    private static ShaderEngineAnalyzer GetAnalyzer()
    {
        if (_analyzer is not null) return _analyzer;

        _projectRoot ??= DefaultProject();
        if (_projectRoot is null || !Directory.Exists(_projectRoot))
        {
            throw new SkipException("未找到 Unity 工程（用 MICROSHADER_UNITY_PROJECT 指定）");
        }

        var dxc = DxcGatewayTests.DxcDirectory();
        if (dxc is null) throw new SkipException("未找到 dxcompiler.dll");

        _analyzer = new ShaderEngineAnalyzer(_projectRoot, dxc);
        return _analyzer;
    }

    private static string? DefaultProject()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT");
        if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv)) return fromEnv;

        return Directory.Exists(@"D:\Program Files\U3D\NewWorld") ? @"D:\Program Files\U3D\NewWorld" : null;
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(20);
        }
    }
}
