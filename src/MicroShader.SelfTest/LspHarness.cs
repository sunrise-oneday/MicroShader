using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using MicroShader.CoordinationEngine.Documents;
using MicroShader.CoordinationEngine.Lsp;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.CoordinationEngine.Transport;
using MicroShader.Domain;

namespace MicroShader.SelfTest;

/// <summary>
/// 无头 LSP 回环测试台：用一对内存 <see cref="Pipe"/> 把客户端与会话直接对接，
/// 客户端侧复用被测的 <see cref="StdioFramingChannel"/> 来解帧。
/// </summary>
internal sealed class LspTestClient : IAsyncDisposable, IDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _fromServer = new();
    private readonly StdioFramingChannel _reader;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

    public LspTestClient(LspServerSession session)
    {
        _reader = new StdioFramingChannel(_fromServer.Reader);
        RunTask = session.RunAsync(_toServer.Reader, _fromServer.Writer, _cts.Token);
        Session = session;
    }

    public LspServerSession Session { get; }

    /// <summary>会话读循环任务；完成后即为退出码。</summary>
    public Task<int> RunTask { get; }

    /// <summary>以"一次" "WriteAsync" 发送一帧（单段读路径）。</summary>
    public async ValueTask SendAtomicAsync(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = BuildFrame(body);
        await _toServer.Writer.WriteAsync(frame, _cts.Token);
    }

    /// <summary>头部与报文体分两次写（"故意制造跨读分帧"，验证挂起等待路径）。</summary>
    public async ValueTask SendSplitAsync(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
        await _toServer.Writer.WriteAsync(header, _cts.Token);
        await Task.Delay(20);
        await _toServer.Writer.WriteAsync(body, _cts.Token);
    }

    /// <summary>发送完全自定义的原始字节（畸形帧用）。</summary>
    public async ValueTask SendRawAsync(byte[] bytes) => await _toServer.Writer.WriteAsync(bytes, _cts.Token);

    /// <summary>接收一帧并解析成 JSON 文本（测试用，允许分配）。</summary>
    public async ValueTask<string> ReceiveAsync()
    {
        var frame = await _reader.ReadFrameAsync(_cts.Token);
        if (!frame.Succeeded)
        {
            throw new AssertionException("客户端解帧失败: " + StdioFramingChannel.Describe(frame.Failure));
        }

        try
        {
            return Encoding.UTF8.GetString(frame.Payload.ToArray());
        }
        finally
        {
            _reader.Complete();
        }
    }

    /// <summary>尝试接收一帧；超时或对端未发则返回 "null"。</summary>
    public async ValueTask<string?> TryReceiveAsync(TimeSpan timeout)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var frame = await _reader.ReadFrameAsync(timeoutCts.Token);
            if (!frame.Succeeded) return null;
            try
            {
                return Encoding.UTF8.GetString(frame.Payload.ToArray());
            }
            finally
            {
                _reader.Complete();
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>模拟宿主关闭标准输入。</summary>
    public void Close() => _toServer.Writer.Complete();

    /// <summary>同步释放（自检用例体是同步的，用不了 "await using"）。</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        Close();
        try
        {
            await RunTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }

        _cts.Dispose();
        await _toServer.Reader.CompleteAsync();
        await _fromServer.Reader.CompleteAsync();
    }

    private static byte[] BuildFrame(byte[] body)
    {
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);
        return frame;
    }
}

/// <summary>把自检语料（tests/Fixtures/*.shader）包装成 LSP 消息与 JSON-RPC 报文。</summary>
internal static class LspCorpus
{
    /// <summary>枚举全部固定用例 .shader（真实管道语料）。</summary>
    public static IReadOnlyList<string> FixtureFiles() =>
        Directory.GetFiles(Corpus.FixtureDirectory, "*.shader").OrderBy(static p => p, StringComparer.Ordinal).ToArray();

    public static string UriOf(string filePath) => FileUri.FromPath(filePath);

    /// <summary>构造 "initialize" 请求。</summary>
    public static string Initialize(int id) =>
        "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"initialize\",\"params\":{\"processId\":1234,"
        + "\"rootUri\":null,\"capabilities\":{}}}";

    public static string Initialized() => "{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}";

    public static string Shutdown(int id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"shutdown\"}";

    public static string Exit() => "{\"jsonrpc\":\"2.0\",\"method\":\"exit\"}";

    /// <summary>构造 "textDocument/didOpen"，正文来自指定 .shader 文件。</summary>
    public static string DidOpen(string filePath, int version = 1)
    {
        var uri = UriOf(filePath);
        var text = File.ReadAllText(filePath);
        return "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{"
            + "\"uri\":" + JsonString(uri) + ",\"languageId\":\"shaderlab\",\"version\":" + version
            + ",\"text\":" + JsonString(text) + "}}}";
    }

    /// <summary>构造 "textDocument/didChange"（Full 同步：整份替换）。</summary>
    public static string DidChange(string uri, string text, int version) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{"
        + "\"textDocument\":{\"uri\":" + JsonString(uri) + ",\"version\":" + version + "},"
        + "\"contentChanges\":[{\"text\":" + JsonString(text) + "}]}}";

    /// <summary>构造 "textDocument/didSave"；<paramref name="reason"/> 为 null 表示不带 reason。</summary>
    public static string DidSave(string uri, int? reason = 1) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didSave\",\"params\":{\"textDocument\":{\"uri\":"
        + JsonString(uri) + "}" + (reason is null ? string.Empty : ",\"reason\":" + reason) + "}}";

    public static string DidClose(string uri) =>
        "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":"
        + JsonString(uri) + "}}}";

    /// <summary>把任意文本包装成 JSON 字符串字面量（含控制字符转义）。</summary>
    public static string JsonString(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < ' ') builder.Append("\\u").Append(((int)ch).ToString("x4"));
                    else builder.Append(ch);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }
}

/// <summary>可编排的假分析器：协议层用例用它，不依赖 DXC。</summary>
internal sealed class FakeAnalyzer
{
    private readonly List<AnalysisRequest> _requests = [];

    public string? Uri { get; set; }

    public int PassIndex { get; set; }

    public IReadOnlyList<ShaderDiagnosticItem> Items { get; set; } = [];

    /// <summary>每次调用前的额外等待（用于制造「在途分析」）。</summary>
    public TimeSpan Delay { get; set; }

    public int Calls => _requests.Count;

    public IReadOnlyList<AnalysisRequest> Requests => _requests;

    public AnalysisRequest Last
    {
        get
        {
            lock (_requests) return _requests[^1];
        }
    }

    public async Task<AnalysisOutcome> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        lock (_requests) _requests.Add(request);

        if (Delay > TimeSpan.Zero)
        {
            await Task.Delay(Delay, cancellationToken).ConfigureAwait(false);
        }

        return new AnalysisOutcome
        {
            Uri = request.Uri,
            Version = request.Version,
            Epoch = request.Epoch,
            IsFullScan = request.IsFullScan,
            PassDiagnostics = new Dictionary<int, IReadOnlyList<ShaderDiagnosticItem>> { [PassIndex] = Items },
            CoveredPasses = [PassIndex],
        };
    }
}
