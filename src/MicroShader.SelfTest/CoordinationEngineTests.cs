using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using MicroShader.CoordinationEngine.Diagnostics;
using MicroShader.CoordinationEngine.Documents;
using MicroShader.CoordinationEngine.Lsp;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.CoordinationEngine.Transport;
using MicroShader.Domain;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 8（CoordinationEngine：LSP 传输与响应式调度）自检。
/// 全部用例都在无头内存管道上跑，不需要编辑器、不需要 DXC。
/// </summary>
internal static class CoordinationEngineTests
{
    public static void Register()
    {
        TestSuite.Add("Lsp", "Framing", Framing);
        TestSuite.Add("Lsp", "FramingSplit", FramingSplit);
        TestSuite.Add("Lsp", "FramingHeaderVariants", FramingHeaderVariants);
        TestSuite.Add("Lsp", "FramingNeverResyncs", FramingNeverResyncs);
        TestSuite.Add("Lsp", "Envelope", Envelope);
        TestSuite.Add("Lsp", "EnvelopeMultiSegment", EnvelopeMultiSegment);
        TestSuite.Add("Lsp", "UriRoundTrip", UriRoundTrip);
        TestSuite.Add("Lsp", "InitializeCapabilities", InitializeCapabilities);
        TestSuite.Add("Lsp", "NotInitialized", NotInitialized);
        TestSuite.Add("Lsp", "AfterShutdown", AfterShutdown);
        TestSuite.Add("Lsp", "ExitCodes", ExitCodes);
        TestSuite.Add("Lsp", "SaveReasonFilter", SaveReasonFilter);
        TestSuite.Add("Lsp", "CancelRequestNumericId", CancelRequestNumericId);
        TestSuite.Add("Lsp", "CancelRequestNonStringId", CancelRequestNonStringId);
        TestSuite.Add("Lsp", "MalformedPositionSurvives", MalformedPositionSurvives);
        TestSuite.Add("Lsp", "RoundTripFixture", RoundTripFixture);

        TestSuite.Add("Doc", "LineDiff", LineDiffFixture);
        TestSuite.Add("Doc", "TriviaOnlyChanged", TriviaOnlyChanged);
        TestSuite.Add("Doc", "SnapshotImmutable", SnapshotImmutable);
        TestSuite.Add("Doc", "EpochOnReopen", EpochOnReopen);

        TestSuite.Add("Diag", "PerPassMerge", PerPassMerge);
        TestSuite.Add("Diag", "EpochGate", EpochGate);

        TestSuite.Add("Sched", "DebounceCoalesces", DebounceCoalesces);
        TestSuite.Add("Sched", "SaveBypasses", SaveBypasses);
        TestSuite.Add("Sched", "VersionFence", VersionFence);
    }

    // ── 分帧 ───────────────────────────────────────────────────────────────────

    private static void Framing()
    {
        var pipe = new Pipe();
        var channel = new StdioFramingChannel(pipe.Reader);

        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}");
        WriteFrame(pipe, body, "Content-Length: " + body.Length + "\r\n\r\n");

        var frame = channel.ReadFrameAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Check.True(frame.Succeeded, "基本分帧必须成功");
        Check.Equal(body.Length, frame.DeclaredLength, "声明的字节长度");
        Check.Equal(Encoding.UTF8.GetString(body), Encoding.UTF8.GetString(frame.Payload.ToArray()), "报文体内容");
        channel.Complete();

        // Content-Length 按 UTF-8 字节数算：一个中文字符占 3 字节。
        var chinese = Encoding.UTF8.GetBytes("{\"method\":\"中文方法\"}");
        WriteFrame(pipe, chinese, "Content-Length: " + chinese.Length + "\r\n\r\n");
        var frame2 = channel.ReadFrameAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Check.True(frame2.Succeeded, "含中文的帧必须按字节数分帧");
        Check.Equal(chinese.Length, frame2.DeclaredLength, "中文帧的字节长度（不是字符数）");
        channel.Complete();
    }

    private static void FramingSplit()
    {
        var pipe = new Pipe();
        var channel = new StdioFramingChannel(pipe.Reader);

        var body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}");
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");

        var readTask = channel.ReadFrameAsync(CancellationToken.None).AsTask();

        // 先只发头部：分帧状态机必须挂起等待，而不是把半截当一帧。
        pipe.Writer.Write(header);
        pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        Check.True(!readTask.IsCompleted, "只有头部时不得提前完成分帧");

        // 再发报文体的一半，仍然不能完成。
        pipe.Writer.Write(body.AsSpan(0, body.Length / 2));
        pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
        Check.True(!readTask.IsCompleted, "报文体不完整时不得完成分帧");

        pipe.Writer.Write(body.AsSpan(body.Length / 2));
        pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();

        var frame = readTask.GetAwaiter().GetResult();
        Check.True(frame.Succeeded, "补齐后必须成功分帧");
        Check.Equal(Encoding.UTF8.GetString(body), Encoding.UTF8.GetString(frame.Payload.ToArray()), "跨多次读取拼出的报文体");
        channel.Complete();
    }

    private static void FramingHeaderVariants()
    {
        var body = Encoding.UTF8.GetBytes("{\"id\":1}");

        // 未知字段在前、字段名大小写混排、值两侧有空白、结尾用裸 \n。
        var header = "Content-Type: application/vscode-jsonrpc; charset=utf-8\r\n"
            + "CONTENT-LENGTH:   " + body.Length + "  \n\n";

        var pipe = new Pipe();
        var channel = new StdioFramingChannel(pipe.Reader);
        WriteFrame(pipe, body, header);

        var frame = channel.ReadFrameAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Check.True(frame.Succeeded, "必须容忍未知头部、大小写、空白与裸 \\n 分隔");
        Check.Equal(body.Length, frame.DeclaredLength, "在这种头部下也要解出正确的长度");
        channel.Complete();
    }

    private static void FramingNeverResyncs()
    {
        // 1) 非十进制长度 → 不可恢复。
        Check.Equal(
            FramingFailureKind.InvalidContentLength,
            FailureOf("Content-Length: abc\r\n\r\n{}"),
            "非十进制 Content-Length 必须判为不可恢复");

        // 2) 头部里没有 Content-Length → 不可恢复（绝不按字节搜标记）。
        Check.Equal(
            FramingFailureKind.MissingContentLength,
            FailureOf("X-Whatever: 3\r\n\r\n{}"),
            "缺少 Content-Length 必须判为不可恢复");

        // 3) 头部超限 → 不可恢复。
        Check.Equal(
            FramingFailureKind.HeaderTooLarge,
            FailureOf(new string('A', StdioFramingChannel.MaxHeaderBytes + 64)),
            "超长头部必须判为不可恢复");

        // 4) 声明的长度超过 64MB 上限 → 直接拒绝，不去分配。
        Check.Equal(
            FramingFailureKind.PayloadTooLarge,
            FailureOf("Content-Length: 999999999\r\n\r\n"),
            "超大长度必须判为不可恢复");

        // 5) 报文体没凑齐就对端关闭 → 截断，而不是重同步。
        var pipe = new Pipe();
        var channel = new StdioFramingChannel(pipe.Reader);
        pipe.Writer.Write(Encoding.ASCII.GetBytes("Content-Length: 100\r\n\r\nshort"));
        pipe.Writer.Complete();
        var frame = channel.ReadFrameAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Check.Equal(FramingFailureKind.TruncatedPayload, frame.Failure, "截断必须被识别");
        Check.True(frame.IsUnrecoverable, "截断属于不可恢复");

        // 6) 最常见的真实形态：payload 里"字面包含" "Content-Length:"。
        //    这正是设计文档里「按字节滑动重同步」会永久错乱的原因 —— 这里必须完全不理会它。
        var tricky = Encoding.UTF8.GetBytes(
            "{\"method\":\"x\",\"params\":{\"text\":\"// Content-Length: 999999 混淆用\\n//\\r\\n\\r\\n# 与真实分隔符同形\"}}");
        var pipe2 = new Pipe();
        var channel2 = new StdioFramingChannel(pipe2.Reader);
        WriteFrame(pipe2, tricky, "Content-Length: " + tricky.Length + "\r\n\r\n");
        var frame2 = channel2.ReadFrameAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        Check.True(frame2.Succeeded, "payload 内含 Content-Length 字面量时仍必须正确分帧");
        Check.Equal(tricky.Length, frame2.DeclaredLength, "payload 内的伪头部不得影响帧长");
        Check.Equal(
            Encoding.UTF8.GetString(tricky),
            Encoding.UTF8.GetString(frame2.Payload.ToArray()),
            "payload 必须原样取出（长度前缀协议对内容不透明）");
        channel2.Complete();
    }

    // ── 报文解析 ───────────────────────────────────────────────────────────────

    private static void Envelope()
    {
        Check.True(
            LspMessageReader.TryRead(Bytes("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"initialize\",\"params\":{\"a\":1}}"), out var request),
            "请求必须可解析");
        Check.Equal(LspMessageKind.Request, request.Kind, "报文类别");
        Check.True(request.MethodIs(LspMethods.Initialize), "方法名比较（零分配快路径）");
        Check.True(request.HasId && request.HasParams, "带 id 与 params");

        Check.True(
            LspMessageReader.TryRead(Bytes("{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}"), out var notification),
            "通知必须可解析");
        Check.Equal(LspMessageKind.Notification, notification.Kind, "无 id = 通知");
        Check.True(notification.MethodIs(LspMethods.Exit), "exit 方法名");

        Check.True(
            LspMessageReader.TryRead(Bytes("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"result\":null}"), out var response),
            "响应必须可解析");
        Check.Equal(LspMessageKind.Response, response.Kind, "响应类别");
        Check.Equal("abc", Utf8Raw.Unquote(response.Id), "字符串 id 必须原样保留");

        Check.True(
            LspMessageReader.TryRead(Bytes("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"shutdown\",\"params\":{\"nested\":{\"deep\":[1,2,3]}}}"), out var withNested),
            "含嵌套对象的报文");
        Check.True(withNested.MethodIs(LspMethods.Shutdown), "嵌套对象不得打断顶层扫描");

        Check.True(!LspMessageReader.TryRead(Bytes("[1,2,3]"), out var invalid), "非对象必须被拒收");
        Check.Equal(LspMessageKind.Invalid, invalid.Kind, "非对象是 Invalid");
    }

    private static void EnvelopeMultiSegment()
    {
        // 把一条完整报文切成三段，构造真正的多段 ReadOnlySequence。
        var json = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"text\":\"a\\nb\"}}");
        var first = new SequenceSegment(json.AsMemory(0, 5));
        var second = first.Append(json.AsMemory(5, 17));
        var third = second.Append(json.AsMemory(22));

        var sequence = new ReadOnlySequence<byte>(first, 0, third, third.Memory.Length);
        Check.True(!sequence.IsSingleSegment, "构造出的序列必须是多段（否则本用例没有意义）");

        Check.True(LspMessageReader.TryRead(sequence, out var envelope), "多段序列必须能解析（禁止 ToArray 兜底）");
        Check.True(envelope.MethodIs(LspMethods.DidChange), "多段下的方法名");
    }

    private static void UriRoundTrip()
    {
        var path = Path.Combine(Corpus.FixtureDirectory, "SimpleUrp.shader");
        var uri = LspCorpus.UriOf(path);
        Check.True(uri.StartsWith("file:///", StringComparison.Ordinal), "必须是 file:/// 形态");
        Check.Equal(path.Replace('/', '\\'), LspUri.ToPath(uri), "URI 必须能无损还原成物理路径");

        // 中文路径最容易在百分号解码上出错。
        var chinese = LspUri.ToPath("file:///D:/%E4%B8%AD%E6%96%87%E5%B7%A5%E7%A8%8B/Assets/A.shader");
        Check.Equal(@"D:\中文工程\Assets\A.shader", chinese, "中文路径必须按 UTF-8 字节还原");
        Check.True(LspUri.ToPath("http://example.com/x") is null, "非 file scheme 必须返回 null");
    }

    // ── 会话行为 ───────────────────────────────────────────────────────────────

    private static void InitializeCapabilities()
    {
        var analyzer = new FakeAnalyzer();
        using var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        var response = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();

        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        Check.Equal(1, root.GetProperty("id").GetInt32(), "id 必须原样回写");

        var capabilities = root.GetProperty("result").GetProperty("capabilities");

        // LSP 规范原文：textDocumentSync 省略时默认 None —— 不显式声明就收不到 didChange。
        var sync = capabilities.GetProperty("textDocumentSync");
        Check.True(sync.GetProperty("openClose").GetBoolean(), "必须声明 openClose");
        Check.Equal(1, sync.GetProperty("change").GetInt32(), "必须是 Full(1) 同步（ADR-008）");
        Check.True(!sync.TryGetProperty("save", out _), "不得声明 save.includeText（Full 同步下等于传两遍）");

        Check.Equal("utf-16", capabilities.GetProperty("positionEncoding").GetString(), "必须显式声明 utf-16");

        // ADR-015：push-only。声明 diagnosticProvider 会让诊断显示两遍。
        Check.True(!capabilities.TryGetProperty("diagnosticProvider", out _), "绝不能声明 diagnosticProvider（push-only）");
    }

    private static void NotInitialized()
    {
        var analyzer = new FakeAnalyzer();
        using var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":42,\"method\":\"textDocument/hover\",\"params\":{}}")
            .AsTask().GetAwaiter().GetResult();

        var response = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        Check.Equal(
            LspErrorCodes.ServerNotInitialized,
            ErrorCodeOf(response),
            "未 initialize 的请求必须回 ServerNotInitialized(-32002)，而不是静默丢弃");
    }

    private static void AfterShutdown()
    {
        var analyzer = new FakeAnalyzer();
        using var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();

        client.SendAtomicAsync(LspCorpus.Shutdown(2)).AsTask().GetAwaiter().GetResult();
        var shutdownResponse = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        using (var doc = JsonDocument.Parse(shutdownResponse))
        {
            Check.True(doc.RootElement.TryGetProperty("result", out var result), "shutdown 必须回 result");
            Check.Equal(JsonValueKind.Null, result.ValueKind, "shutdown 的 result 必须是 null");
        }

        // shutdown 之后的请求：必须回 InvalidRequest(-32600)，不能静默丢弃。
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"textDocument/hover\",\"params\":{}}")
            .AsTask().GetAwaiter().GetResult();
        Check.Equal(LspErrorCodes.InvalidRequest, ErrorCodeOf(client.ReceiveAsync().AsTask().GetAwaiter().GetResult()),
            "shutdown 之后的请求必须回 InvalidRequest(-32600)");
    }

    private static void ExitCodes()
    {
        // A) shutdown → exit：退出码 0
        {
            var analyzer = new FakeAnalyzer();
            var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));
            client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
            _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();
            client.SendAtomicAsync(LspCorpus.Shutdown(2)).AsTask().GetAwaiter().GetResult();
            _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            client.SendAtomicAsync(LspCorpus.Exit()).AsTask().GetAwaiter().GetResult();
            Check.Equal(0, client.RunTask.Wait(TimeSpan.FromSeconds(5)) ? client.RunTask.Result : -1,
                "收到过 shutdown 再 exit 必须返回退出码 0");
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // B) 直接 exit（没有 shutdown）：退出码 1
        {
            var analyzer = new FakeAnalyzer();
            var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));
            client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
            _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            client.SendAtomicAsync(LspCorpus.Exit()).AsTask().GetAwaiter().GetResult();
            Check.Equal(1, client.RunTask.Result, "没收到 shutdown 就 exit 必须返回退出码 1");
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // C) EOF（宿主直接关闭管道）：同样按是否握过手判定
        {
            var analyzer = new FakeAnalyzer();
            var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));
            client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
            _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            client.SendAtomicAsync(LspCorpus.Shutdown(2)).AsTask().GetAwaiter().GetResult();
            _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            client.Close();
            Check.Equal(0, client.RunTask.Result, "干净握手后 EOF 必须返回 0");
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        // D) 分帧错乱：退出码必须非 0，且绝不重同步
        {
            var analyzer = new FakeAnalyzer();
            var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));
            client.SendRawAsync(Encoding.ASCII.GetBytes("Content-Length: not-a-number\r\n\r\n{}"))
                .AsTask().GetAwaiter().GetResult();
            Check.Equal(1, client.RunTask.Result, "分帧错乱必须返回非 0 退出码");
            Check.Equal(1, client.Session.ProtocolErrors, "必须记录一次协议错误");
            client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void SaveReasonFilter()
    {
        var analyzer = new FakeAnalyzer { PassIndex = 0 };
        var session = new LspServerSession(analyzer.AnalyzeAsync);
        using var client = new LspTestClient(session);

        var file = Path.Combine(Corpus.FixtureDirectory, "SimpleUrp.shader");
        var uri = LspCorpus.UriOf(file);

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.DidOpen(file)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();

        var afterOpen = analyzer.Calls;

        // reason=3 (FocusOut)：切窗口也会触发，必须被过滤掉。
        client.SendAtomicAsync(LspCorpus.DidSave(uri, reason: 3)).AsTask().GetAwaiter().GetResult();
        WaitUntil(() => session.IgnoredSaves == 1, TimeSpan.FromSeconds(3));
        Check.Equal(1, session.IgnoredSaves, "FocusOut 保存必须被忽略");
        Check.Equal(afterOpen, analyzer.Calls, "被忽略的保存不得触发分析");

        // reason=1 (Manual)：必须触发。
        client.SendAtomicAsync(LspCorpus.DidSave(uri, reason: 1)).AsTask().GetAwaiter().GetResult();
        WaitUntil(() => analyzer.Calls > afterOpen, TimeSpan.FromSeconds(3));
        Check.True(analyzer.Calls > afterOpen, "手动保存必须触发全量分析");
        Check.True(analyzer.Last.IsFullScan, "手动保存必须是全量扫描");
    }

    // ── $/cancelRequest 与畸形 token 的回归守护 ──────────────────────────────

    /// <summary>
    /// 数字 id 的 $/cancelRequest 不得杀死服务端（0xC0000409 回归守护）。
    // ── $/cancelRequest 与畸形 token 的回归守护 ──────────────────────────────

    /// <summary>
    /// 数字 id 的 $/cancelRequest 不得杀死服务端（0xC0000409 回归守护）。
    /// </summary>
    private static void CancelRequestNumericId()
    {
        var analyzer = new FakeAnalyzer();
        using var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();

        // VSCode 发的 $/cancelRequest 里 id 是数字 —— 修复前会 FailFast
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":42}}")
            .AsTask().GetAwaiter().GetResult();

        // 服务端必须存活：后续 shutdown 必须能正常回包
        client.SendAtomicAsync(LspCorpus.Shutdown(2)).AsTask().GetAwaiter().GetResult();
        var response = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        using (var doc = JsonDocument.Parse(response))
        {
            Check.Equal(2, doc.RootElement.GetProperty("id").GetInt32(), "shutdown id 必须回写");
            Check.True(doc.RootElement.TryGetProperty("result", out _), "shutdown 必须回 result —— 证明服务端在 $/cancelRequest 后存活");
        }
        client.SendAtomicAsync(LspCorpus.Exit()).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// boolean / null id 的 $/cancelRequest 也不得杀死服务端。
    /// </summary>
    private static void CancelRequestNonStringId()
    {
        var analyzer = new FakeAnalyzer();
        using var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();

        // JSON-RPC 规范允许 id 为 integer|string|null，但绝不该崩
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":true}}")
            .AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":false}}")
            .AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":null}}")
            .AsTask().GetAwaiter().GetResult();

        // 存活判定：shutdown 能回包
        client.SendAtomicAsync(LspCorpus.Shutdown(2)).AsTask().GetAwaiter().GetResult();
        var response = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        using (var doc = JsonDocument.Parse(response))
        {
            Check.Equal(2, doc.RootElement.GetProperty("id").GetInt32(), "shutdown id 必须回写");
        }
        client.SendAtomicAsync(LspCorpus.Exit()).AsTask().GetAwaiter().GetResult();
    }

    /// <summary>
    /// 畸形 position（line=1.5）不得杀死服务端。
    /// 修复前 GetInt32() 遇浮点数抛 FormatException → HandleFrame 兜底 → InternalError。
    /// 修复后 TryGetInt32 降级为 0，服务端照常应答。
    /// </summary>
    private static void MalformedPositionSurvives()
    {
        var analyzer = new FakeAnalyzer();
        using var client = new LspTestClient(new LspServerSession(analyzer.AnalyzeAsync));

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();

        // line=1.5 是合法 JSON 数字但非整数 —— 修复前抛 FormatException
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///test.shader\"},\"position\":{\"line\":1.5,\"character\":0}}}")
            .AsTask().GetAwaiter().GetResult();

        // 服务端必须存活：后续 shutdown 必须能回包（无论 hover 回了什么）
        client.SendAtomicAsync(LspCorpus.Shutdown(2)).AsTask().GetAwaiter().GetResult();

        // 可能收到 hover 回包（id=5）或 shutdown 回包（id=2），不论哪个都证明存活
        var first = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        int firstId;
        using (var doc = JsonDocument.Parse(first))
        {
            firstId = doc.RootElement.GetProperty("id").GetInt32();
            Check.True(firstId == 5 || firstId == 2, "服务端必须存活并回包（收到 id=" + firstId + "）");
        }

        // 如果第一帧是 hover 回包，再收 shutdown
        if (firstId == 5)
        {
            var second = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            using (var doc = JsonDocument.Parse(second))
            {
                Check.Equal(2, doc.RootElement.GetProperty("id").GetInt32(), "shutdown id 必须回写");
            }
        }
        client.SendAtomicAsync(LspCorpus.Exit()).AsTask().GetAwaiter().GetResult();
    }

private static void RoundTripFixture()
    {
        var analyzer = new FakeAnalyzer
        {
            PassIndex = 0,
            Items =
            [
                new ShaderDiagnosticItem
                {
                    Severity = DiagnosticSeverity.Error,
                    Line = 12,
                    Column = 5,
                    Message = "合成诊断：定位校验",
                    Code = "TEST0001",
                },
            ],
        };

        var session = new LspServerSession(analyzer.AnalyzeAsync);
        using var client = new LspTestClient(session);

        var file = Path.Combine(Corpus.FixtureDirectory, "SimpleUrp.shader");
        var uri = LspCorpus.UriOf(file);

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();

        // didOpen → 立即全量诊断（v2.0 第 11 条：首次出现诊断不加延迟）
        client.SendAtomicAsync(LspCorpus.DidOpen(file)).AsTask().GetAwaiter().GetResult();
        var opened = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        using (var doc = JsonDocument.Parse(opened))
        {
            var root = doc.RootElement;
            Check.Equal(LspMethods.PublishDiagnostics, root.GetProperty("method").GetString(), "didOpen 必须立刻推出诊断");
            var parameters = root.GetProperty("params");
            Check.Equal(uri, parameters.GetProperty("uri").GetString(), "诊断必须归属被打的文档");
            Check.Equal(1, parameters.GetProperty("version").GetInt32(), "诊断必须携带版本号");

            var diagnostics = parameters.GetProperty("diagnostics");
            Check.Equal(1, diagnostics.GetArrayLength(), "诊断条数");
            var first = diagnostics[0];
            Check.Equal(11, first.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(), "内部 1-based 行 12 → LSP 0-based 行 11");
            Check.Equal(4, first.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(), "内部 1-based 列 5 → LSP 0-based 列 4");
            Check.Equal(1, first.GetProperty("severity").GetInt32(), "Error = 1");
            Check.Equal("TEST0001", first.GetProperty("code").GetString(), "错误码必须透出（黄金测试靠它比对）");
        }

        // didChange（Full 同步整份替换）→ 版本递增
        var changed = File.ReadAllText(file) + "\n// touched\n";
        client.SendAtomicAsync(LspCorpus.DidChange(uri, changed, 2)).AsTask().GetAwaiter().GetResult();
        WaitUntil(() => session.PublishedCount >= 2, TimeSpan.FromSeconds(5));
        var afterChange = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        using (var doc = JsonDocument.Parse(afterChange))
        {
            Check.Equal(2, doc.RootElement.GetProperty("params").GetProperty("version").GetInt32(), "版本必须来自 didChange");
        }

        Check.True(analyzer.Requests.Any(r => !r.IsFullScan), "didChange 必须走「只算聚焦 Pass」的非全量路径");
        Check.True(analyzer.Last.ProbeLine > 0, "探针行必须由服务端 diff 得出（ADR-011），而不是光标");

        // didClose → 必须先推一份空诊断把红线清干净
        client.SendAtomicAsync(LspCorpus.DidClose(uri)).AsTask().GetAwaiter().GetResult();
        var closed = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        using (var doc = JsonDocument.Parse(closed))
        {
            var parameters = doc.RootElement.GetProperty("params");
            Check.Equal(uri, parameters.GetProperty("uri").GetString(), "清空诊断必须指向同一 URI");
            Check.Equal(0, parameters.GetProperty("diagnostics").GetArrayLength(), "didClose 必须推空数组清红线");
        }

        // 读线程是先推空诊断、再拆状态，客户端收到空诊断时回收可能还没执行到 —— 必须等它落地。
        WaitUntil(() => session.Documents.Count == 0, TimeSpan.FromSeconds(3));
        Check.Equal(0, session.Documents.Count, "didClose 后影子文档必须被回收");
    }

    // ── 文档与差分 ─────────────────────────────────────────────────────────────

    private static void LineDiffFixture()
    {
        Check.Equal(0, LineDiff.FirstChangedLine("abc", "abc"), "完全相同 → 0");

        var file = Path.Combine(Corpus.FixtureDirectory, "SimpleUrp.shader");
        var text = File.ReadAllText(file);
        Check.Equal(0, LineDiff.FirstChangedLine(text, text), "同一份文件与自己无差异");

        // 改动第 1 行
        Check.Equal(1, LineDiff.FirstChangedLine(text, "// " + text), "首行插入");

        // 在第 10 行的行首插入内容
        var lines = text.Split('\n');
        Check.True(lines.Length > 12, "语料必须有足够行数");
        lines[9] = "// INJECTED " + lines[9];
        var modified = string.Join('\n', lines);
        Check.Equal(10, LineDiff.FirstChangedLine(text, modified), "第 10 行改动必须被定位到第 10 行");

        // 尾部追加：首处差异是插入的那个 '\n' 本身，它落在原最后一行的行尾。
        var appended = text + "\n// tail";
        var appendedLine = text.Count(static c => c == '\n') + 1;
        Check.Equal(appendedLine, LineDiff.FirstChangedLine(text, appended), "尾部追加必须定位到插入的换行符所在行");

        // 纯删除中间一行（必须重新切分：上面的 lines 已经被注入过内容，拿它比会先撞上第 10 行）。
        var fresh = text.Split('\n');
        var removed = string.Join('\n', fresh.Where((_, i) => i != 19));
        Check.Equal(20, LineDiff.FirstChangedLine(text, removed), "删除第 20 行必须定位到第 20 行");
    }

    private static void TriviaOnlyChanged()
    {
        // 完全相同 → FirstChangedLine 返回 0，OnlyTriviaChanged 不会调到；这里只是基线。
        Check.True(LineDiff.OnlyTriviaChanged("abc", "abc"), "完全相同 → true");

        // 只加空白
        Check.True(LineDiff.OnlyTriviaChanged("float4 x;", "  float4 x; "), "前后加空格 → true");
        Check.True(LineDiff.OnlyTriviaChanged("int a;\nint b;", "int a;\n\nint b;"), "中间加空行 → true");

        // 只改注释
        Check.True(LineDiff.OnlyTriviaChanged("// old\nfloat4 x;", "// new\nfloat4 x;"), "改行注释内容 → true");
        Check.True(LineDiff.OnlyTriviaChanged("/* a */ float4 x;", "/* b */ float4 x;"), "改块注释内容 → true");
        Check.True(LineDiff.OnlyTriviaChanged("float4 x; // c1", "float4 x; // c2"), "改行尾注释 → true");
        Check.True(LineDiff.OnlyTriviaChanged("float4 x;", "// whole new line\nfloat4 x;"), "加一整行注释 → true");

        // 删注释
        Check.True(LineDiff.OnlyTriviaChanged("// header\nfloat4 x;", "float4 x;"), "删注释 → true");

        // 加块注释
        Check.True(
            LineDiff.OnlyTriviaChanged("float4 x;\nint y;", "/* block */\nfloat4 x;\nint y;"),
            "前面加块注释 → true");

        // 代码变了 → false
        Check.False(LineDiff.OnlyTriviaChanged("float4 x;", "float4 y;"), "改标识符 → false");
        Check.False(LineDiff.OnlyTriviaChanged("int a;", "int2 a;"), "改类型 → false");
        Check.False(LineDiff.OnlyTriviaChanged("return 0;", "return 1;"), "改字面量 → false");
        Check.False(LineDiff.OnlyTriviaChanged("float x;", "float x\n"), "删分号 → false");

        // 字符串内容变了 → false（字符串不是 trivia）
        Check.False(
            LineDiff.OnlyTriviaChanged("Shader \"Foo\"", "Shader \"Bar\""),
            "改字符串内容 → false");
        // 字符串里含 // 或 /* —— 没有字符串状态时会被整段吞成注释（见 LineDiff 的 remarks）
        Check.False(
            LineDiff.OnlyTriviaChanged("#pragma message \"see http://a\"", "#pragma message \"see http://b\""),
            "字符串内含 // 的内容改动 → false");
        Check.True(
            LineDiff.OnlyTriviaChanged("#pragma message \"see http://a\"", "#pragma message \"see http://a\" // tail"),
            "字符串内含 // 时只加行尾注释 → true");
        Check.False(
            LineDiff.OnlyTriviaChanged("#pragma message \"a /* b\"\nfloat4 x;", "#pragma message \"a /* b\"\nfloat4 y;"),
            "字符串内含 /* 时，其后的真实代码改动 → false");
        Check.False(
            LineDiff.OnlyTriviaChanged("Shader \"a b\"", "Shader \"a  b\""),
            "字符串内空白改动 → false（字符串内空白是内容）");
        Check.True(
            LineDiff.OnlyTriviaChanged("x = \"a\\\"b\";", "x = \"a\\\"b\"; // tail"),
            "转义引号字符串 + 新增行尾注释 → true");
        Check.False(
            LineDiff.OnlyTriviaChanged("x = \"a\\\"b\";", "x = \"a\\\"c\";"),
            "转义引号之后的内容改动 → false");

        // 一边有代码、一边没代码
        Check.False(LineDiff.OnlyTriviaChanged("float4 x;", "// only comment"), "旧有代码、新全注释 → false");

        // 两边都只有注释（内容不同）→ true（注释内容不影响诊断）
        Check.True(LineDiff.OnlyTriviaChanged("// a", "// b"), "两边都只注释且内容不同 → true");

        // 两边都空
        Check.True(LineDiff.OnlyTriviaChanged("", ""), "两边都空 → true");
        Check.True(LineDiff.OnlyTriviaChanged("", "  \n  "), "旧空、新纯空白 → true");

        // 行注释未闭合（无换行）→ 跳到末尾，后面没有代码 → true
        Check.True(LineDiff.OnlyTriviaChanged("// unterminated", "// also unterminated"), "两边都未闭合行注释 → true");

        // 块注释未闭合 → 跳到末尾
        Check.True(
            LineDiff.OnlyTriviaChanged("/* unterminated float", "/* different unterminated float"),
            "两边都未闭合块注释 → true（注释内容不影响诊断）");

        // 混合：注释 + 空白都改了，代码不变 → true
        Check.True(
            LineDiff.OnlyTriviaChanged("// c1\n  float4 x;  // tail1", "/* c2 */ float4 x; // tail2"),
            "注释和空白都改、代码不变 → true");
    }

    private static void SnapshotImmutable()
    {
        var store = new DocumentShadowStore();
        var first = store.Open("file:///a.shader", @"C:\a.shader", "line1\nline2\n", 1);
        Check.Equal(1, first.Epoch, "首次打开 epoch=1");

        var second = store.Change("file:///a.shader", "line1\nCHANGED\n", 2);
        Check.NotNull(second, "变更必须返回新快照");

        // 关键性质：拿到手的旧快照绝不能被后续变更改写（torn read 免疫）。
        Check.Equal("line1\nline2\n", first.Text, "旧的快照文本必须在变更后保持不变");
        Check.Equal(1, first.Version, "旧的快照版本号必须保持不变");
        Check.Equal("line1\nCHANGED\n", second!.Text, "新快照必须是新文本");

        Check.True(store.TryGetAtVersion("file:///a.shader", 1, out _) == false, "版本 1 已经不是当前版本");
        Check.True(store.TryGetAtVersion("file:///a.shader", 2, out _), "版本 2 是当前版本");
    }

    private static void EpochOnReopen()
    {
        var store = new DocumentShadowStore();
        var first = store.Open("file:///a.shader", @"C:\a.shader", "x", 7);
        Check.Equal(1, first.Epoch, "首次打开 epoch=1");

        Check.True(store.Close("file:///a.shader"), "关闭必须成功");
        Check.Equal(0, store.Count, "关闭后不应残留");

        // VS Code 行为：重开后 version 回到 1。只靠 version 无法区分新旧会话。
        var second = store.Open("file:///a.shader", @"C:\a.shader", "y", 1);
        Check.Equal(2, second.Epoch, "重开必须换新 epoch");
        Check.Equal(1, second.Version, "重开后版本可以回到 1");

        var diagnostics = new PublishDiagnosticsStore();
        diagnostics.Open("file:///a.shader", second.Epoch, 1);
        Check.True(!diagnostics.SetPass("file:///a.shader", 1, 1, 0, []), "旧 epoch 的迟到诊断必须被拒收");
        Check.True(diagnostics.SetPass("file:///a.shader", second.Epoch, 1, 0, []), "当前 epoch 必须被接受");
    }

    // ── 诊断集合 ───────────────────────────────────────────────────────────────

    private static void PerPassMerge()
    {
        var store = new PublishDiagnosticsStore();
        store.Open("file:///a.shader", 1, 5);

        Check.True(
            store.SetPass("file:///a.shader", 1, 5, 0, [MakeItem(3, "Pass0 的问题")]),
            "写入 Pass 0");
        Check.True(
            store.SetPass("file:///a.shader", 1, 5, 1, [MakeItem(20, "Pass1 的问题")]),
            "写入 Pass 1");

        Check.True(store.TryBuildMerged("file:///a.shader", 1, out var merged), "必须能合并");
        Check.Equal(2, merged!.Items.Count, "两个 Pass 的诊断都必须出现在整份发布里");
        Check.Equal(2, merged.PassCount, "参与合并的 Pass 数");

        // 打字防抖只重算聚焦 Pass：更新 Pass 1 时，Pass 0 的红线"必须保留"（否则用户看到闪烁）。
        Check.True(store.SetPass("file:///a.shader", 1, 6, 1, []), "Pass 1 分析后无诊断");
        Check.True(store.TryBuildMerged("file:///a.shader", 1, out var merged2), "再次合并");
        Check.Equal(1, merged2!.Items.Count, "只应剩下 Pass 0 的诊断");
        Check.Equal("Pass0 的问题", merged2.Items[0].Message, "保留的必须是另一个 Pass 的诊断");

        // 全量重算时，消失的 Pass 必须被清掉。
        Check.True(store.RetainOnly("file:///a.shader", 1, [1]), "只保留 Pass 1");
        Check.True(store.TryBuildMerged("file:///a.shader", 1, out var merged3), "第三次合并");
        Check.Equal(0, merged3!.Items.Count, "被裁掉的 Pass 的诊断不得残留");

        // 共享块重复报出的同一条诊断必须在合并时去重。
        Check.True(store.SetPass("file:///a.shader", 1, 6, 0, [MakeItem(3, "共享块的问题")]), "Pass 0 报共享块");
        Check.True(store.SetPass("file:///a.shader", 1, 6, 1, [MakeItem(3, "共享块的问题")]), "Pass 1 报同一条");
        Check.True(store.TryBuildMerged("file:///a.shader", 1, out var merged4), "第四次合并");
        Check.Equal(1, merged4!.Items.Count, "同一位置的重复诊断必须去重，否则同一行显示两条红线");
    }

    private static void EpochGate()
    {
        var store = new PublishDiagnosticsStore();
        store.Open("file:///a.shader", 3, 1);

        Check.True(!store.SetPass("file:///a.shader", 2, 1, 0, [MakeItem(1, "旧 epoch")]), "epoch 不匹配必须拒收");
        Check.True(!store.SetPass("file:///b.shader", 3, 1, 0, []), "未打开的文档必须拒收");
        Check.True(!store.TryBuildMerged("file:///a.shader", 2, out _), "epoch 不匹配时不得产出载荷");
        Check.True(store.TryBuildMerged("file:///a.shader", 3, out _), "epoch 匹配时必须能产出载荷");

        store.Close("file:///a.shader");
        Check.Equal(0, store.DocumentCount, "关闭后不残留");
    }

    // ── 调度器 ─────────────────────────────────────────────────────────────────

    private static void DebounceCoalesces()
    {
        var analyzer = new FakeAnalyzer();
        using var scheduler = new CoordinationScheduler(
            analyzer.AnalyzeAsync,
            PublishAlways,
            TimeSpan.FromMilliseconds(250));

        Check.True(scheduler.Debounce >= CoordinationScheduler.MinimumDebounce, "防抖时长必须被夹到合法区间下界");
        Check.True(scheduler.Debounce <= CoordinationScheduler.MaximumDebounce, "防抖时长必须被夹到合法区间上界");

        // 12 次连打。第 2..12 次派发必然各自顶掉一个挂起的 timer —— state.Timer 只在「被顶替」
        // 与 Shutdown 时置空，到期回调不碰它 —— 所以「合并 11 次」与 wall-clock 无关，
        // 任何交错下都必须成立。
        const int Bursts = 12;
        var startedTicks = Stopwatch.GetTimestamp();
        for (var i = 0; i < Bursts; i++)
        {
            scheduler.Dispatch(Request("file:///a.shader", version: i + 1), ChangeTrigger.Edit);
        }

        var loopMs = (Stopwatch.GetTimestamp() - startedTicks) * 1000.0 / Stopwatch.Frequency;
        Check.Equal((long)(Bursts - 1), scheduler.ChangesCoalesced, "其余 11 次必须被合并（与时间无关）");

        // 「防抖期内不得启动分析」是 NFR 断言，但它只在循环确实跑完于窗口内时才可断言：
        // timer 最早在首次派发后 Debounce 毫秒到期，留半个窗口的余量即可保证该分支不与到期竞争。
        // 机器被抢占跨越窗口时改为「按窗口数约束上界」——不是取消断言，而是换成能成立的那一条。
        var insideWindow = loopMs + (scheduler.Debounce.TotalMilliseconds / 2) < scheduler.Debounce.TotalMilliseconds;
        TestCaseHelpers.Report(string.Format(
            "12 次连打耗时 {0:F3} ms（防抖 {1:F0} ms，{2}）⇒ 分析 {3} / 合并 {4}",
            loopMs,
            scheduler.Debounce.TotalMilliseconds,
            insideWindow ? "窗口内-严格断言" : "被抢占跨窗-约束上界",
            scheduler.AnalysesStarted,
            scheduler.ChangesCoalesced));
        if (insideWindow)
        {
            Check.Equal(0L, scheduler.AnalysesStarted, "防抖期内不得启动任何分析（NFR：静止之前不创建任务）");
        }

        // 发布发生在 AnalysesStarted 之后（RunAsync 先 ++started 再 publish），
        // 因此必须先等发布再断言，不能像原来那样在 started 刚满 1 时立刻采样（那是既有的采样竞态）。
        WaitUntil(() => scheduler.AnalysesStarted >= 1, TimeSpan.FromSeconds(5));
        WaitUntil(() => scheduler.AnalysesPublished >= 1, TimeSpan.FromSeconds(5));
        Check.True(scheduler.AnalysesPublished >= 1, "合并后的结果必须被发布");

        if (insideWindow)
        {
            Check.Equal(1L, scheduler.AnalysesStarted, "12 次连续打字必须只产生 1 次分析");
            Check.Equal(1L, scheduler.AnalysesPublished, "合并后的结果必须只发布 1 次");
        }
        else
        {
            // 被抢占跨越窗口时只约束上界：每「循环时长 / 窗口」段至多触发一次，再留一段余量。
            // 决定性的不变量是上面那条与时间无关的 ChangesCoalesced == 11（它已覆盖「完全没合并」的退化）。
            var windows = 1 + (long)Math.Ceiling(loopMs / scheduler.Debounce.TotalMilliseconds);
            Check.True(scheduler.AnalysesStarted <= windows + 1,
                string.Format(
                    "防抖应把连打合并到受窗口数约束的分析次数（循环 {0:F3} ms / 窗口 {1:F0} ms ⇒ 上界 {2}），实际 {3}",
                    loopMs,
                    scheduler.Debounce.TotalMilliseconds,
                    windows + 1,
                    scheduler.AnalysesStarted));
        }
    }

    private static void SaveBypasses()
    {
        var analyzer = new FakeAnalyzer();
        using var scheduler = new CoordinationScheduler(
            analyzer.AnalyzeAsync,
            PublishAlways,
            TimeSpan.FromMilliseconds(250));

        scheduler.Dispatch(Request("file:///a.shader", version: 2), ChangeTrigger.Edit);
        // 立刻保存 —— 防抖计时器还没到期就被抢占。
        scheduler.Dispatch(Request("file:///a.shader", version: 3), ChangeTrigger.Save);

        WaitUntil(() => scheduler.AnalysesPublished >= 1, TimeSpan.FromSeconds(5));
        Check.Equal(1L, scheduler.AnalysesStarted, "被保存抢占的防抖任务不得再单独跑");
        Check.Equal(1L, scheduler.ChangesCoalesced, "防抖任务必须被记为已合并");
        Check.Equal(1L, scheduler.AnalysesPublished, "只发布保存的那一次");
    }

    private static void VersionFence()
    {
        var analyzer = new FakeAnalyzer { Delay = TimeSpan.FromMilliseconds(300) };
        using var scheduler = new CoordinationScheduler(
            analyzer.AnalyzeAsync,
            PublishAlways,
            TimeSpan.FromMilliseconds(250));

        // 全量扫描先跑起来（在途 300ms）。
        scheduler.Dispatch(Request("file:///a.shader", version: 1), ChangeTrigger.Open);
        WaitUntil(() => scheduler.AnalysesStarted >= 1, TimeSpan.FromSeconds(2));

        // 在途期间来了保存：generation 递增，在途结果必须在落地前被丢弃。
        scheduler.Dispatch(Request("file:///a.shader", version: 2), ChangeTrigger.Save);

        WaitUntil(() => scheduler.AnalysesPublished >= 1, TimeSpan.FromSeconds(5));
        Check.Equal(1L, scheduler.AnalysesPublished, "只允许最新那次发布");
        Check.True(scheduler.AnalysesDiscarded >= 1, "在途的旧结果必须被版本栅栏丢弃");
        Check.Equal(2L, scheduler.AnalysesStarted, "两次分析都必须真的跑过（第二次无法取消原生编译，只能等）");
    }

    // ── 辅助 ───────────────────────────────────────────────────────────────────

    private static Task<bool> PublishAlways(AnalysisOutcome outcome, CancellationToken cancellationToken)
    {
        _ = outcome;
        _ = cancellationToken;
        return Task.FromResult(true);
    }

    private static AnalysisRequest Request(string uri, int version) => new()
    {
        Uri = uri,
        FilePath = @"C:\a.shader",
        Version = version,
        Epoch = 1,
        Text = "text",
        ProbeLine = 0,
        IsFullScan = true,
    };

    private static ShaderDiagnosticItem MakeItem(int line, string message) => new()
    {
        Severity = DiagnosticSeverity.Error,
        Line = line,
        Column = 1,
        Message = message,
        Code = "T0001",
    };

    private static ReadOnlySequence<byte> Bytes(string json) => new(Encoding.UTF8.GetBytes(json));

    private static int ErrorCodeOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("error").GetProperty("code").GetInt32();
    }

    private static FramingFailureKind FailureOf(string raw)
    {
        var pipe = new Pipe();
        var channel = new StdioFramingChannel(pipe.Reader);
        pipe.Writer.Write(Encoding.ASCII.GetBytes(raw));
        pipe.Writer.Complete();
        return channel.ReadFrameAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult().Failure;
    }

    private static void WriteFrame(Pipe pipe, byte[] body, string headerText)
    {
        var header = Encoding.ASCII.GetBytes(headerText);
        pipe.Writer.Write(header);
        pipe.Writer.Write(body);
        pipe.Writer.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            Thread.Sleep(10);
        }
    }

    /// <summary>多段 <see cref="ReadOnlySequence{T}"/> 的段实现（验证跨段解析）。</summary>
    private sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
    {
        public SequenceSegment(ReadOnlyMemory<byte> memory) => Memory = memory;

        public SequenceSegment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new SequenceSegment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
