using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Text.Json;
using MicroShader.CoordinationEngine.Diagnostics;
using MicroShader.CoordinationEngine.Documents;
using MicroShader.CoordinationEngine.Lsp;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Transport;
using MicroShader.Domain;
using MicroShader.ObservabilityEngine;

namespace MicroShader.CoordinationEngine.Session;

/// <summary>服务器进程全局生命周期状态（规格书 §四 状态机）。</summary>
public enum ServerState
{
    Uninitialized = 0,
    Initializing,
    ActiveServing,
    ShuttingDown,
    Terminated,
}

/// <summary>
/// LSP 服务端会话：把标准输入输出的字节流、文档影子仓储、响应式调度器与单写者出站通道
/// 串成一个可无头运行的闭环。
/// </summary>
public sealed class LspServerSession : ILspCoordinationEngine, IOpenDocumentLookup
{
    private readonly DocumentShadowStore _shadow = new();
    private readonly PublishDiagnosticsStore _diagnostics = new();

    // IntelliSense 能力集（宿主注入）。null 或全部为 null 时，对应能力既不宣告也不响应 ——
    // 「宣告」与「实现」由同一个字段把关，从结构上排除两者错配（详设 v2.0 第 11 条）。
    private readonly IntelliSenseProvider? _intelliSense;
    private readonly NavigationProvider? _navigation;
    private readonly CoordinationScheduler _scheduler;
    private readonly LspServerOptions _options;
    private readonly object _gate = new();

    private OutboundDrainSink? _outbound;
    private Task? _drainTask;
    private ServerState _state = ServerState.Uninitialized;
    private bool _shutdownReceived;
    private bool _exitRequested;
    private int _disconnected;
    private int _protocolErrors;
    private int _cancelRequests;
    private int _ignoredSaves;
    private int _triviaSkipped;
    private int _publishedCount;

    // 最后一次「派发分析 / 发布诊断」的时刻（Environment.TickCount64 毫秒）。
    // 初值取构造时刻：会话刚建好还没干活，不该一上来就被当成「闲了很久」。
    private long _lastActivityTicks = Environment.TickCount64;

    public LspServerSession(
        DocumentAnalyzer analyzer,
        LspServerOptions? options = null,
        IntelliSenseProvider? intelliSense = null,
        NavigationProvider? navigation = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _options = options ?? LspServerOptions.Default;
        _intelliSense = intelliSense is { IsEnabled: true } enabled ? enabled : null;
        _navigation = navigation is { IsEnabled: true } navEnabled ? navEnabled : null;

        _scheduler = new CoordinationScheduler(analyzer, PublishAsync, _options.Debounce);
        _scheduler.AnalysisFailed += static (uri, ex) =>
            Log.Error(LogCategories.Transport, "分析失败 uri=" + uri + " : " + ex.Message);
    }

    /// <summary>当前生命周期状态。</summary>
    public ServerState State => _state;

    /// <summary>已发布的诊断份数。</summary>
    public int PublishedCount => Volatile.Read(ref _publishedCount);

    /// <summary>协议层错误计数（分帧失败、无法解析的报文）。</summary>
    public int ProtocolErrors => Volatile.Read(ref _protocolErrors);

    /// <summary>收到的 "$/cancelRequest" 数量。</summary>
    public int CancelRequests => Volatile.Read(ref _cancelRequests);

    /// <summary>被 reason 过滤掉的 didSave 数量（切窗口会触发 FocusOut 保存，v2.0 第 13 条）。</summary>
    public int IgnoredSaves => Volatile.Read(ref _ignoredSaves);

    /// <summary>因"仅空白/注释差异"跳过分析的 didChange 数量。</summary>
    public int TriviaSkipped => Volatile.Read(ref _triviaSkipped);

    /// <summary>防抖合并计数（自检断言用）。</summary>
    public long ChangesCoalesced => _scheduler.ChangesCoalesced;

    /// <summary>版本栅栏丢弃计数（自检断言用）。</summary>
    public long AnalysesDiscarded => _scheduler.AnalysesDiscarded;

    /// <summary>已发布分析计数。</summary>
    public long AnalysesPublished => _scheduler.AnalysesPublished;

    /// <summary>距离上一次分析活动（派发或发布）过去了多久。</summary>
    /// <remarks>
    /// 派发与发布都算活动：只盯发布会把一次长编译当成「用户停手了」，
    /// 只盯派发又会在防抖后的长尾里提前判闲。
    /// </remarks>
    public TimeSpan SinceLastActivity
        => TimeSpan.FromMilliseconds(Environment.TickCount64 - Volatile.Read(ref _lastActivityTicks));

    /// <summary>当前是否空闲：调度器里既没有在飞的分析，也没有排队等防抖的一轮。</summary>
    public bool IsIdle => _scheduler.IsIdle;

    /// <summary>文档影子仓储（自检直接查询）。</summary>
    public DocumentShadowStore Documents => _shadow;

    /// <summary>按 Pass 维护的诊断集合（自检断言 per-Pass 合并）。</summary>
    public PublishDiagnosticsStore DiagnosticsStore => _diagnostics;

    /// <inheritdoc />
    public bool TryGetActiveDocument([NotNullWhen(true)] out DocumentSnapshot? document)
    {
        var uri = _shadow.ActiveDocumentUri;
        if (uri is null)
        {
            document = null;
            return false;
        }
        return _shadow.TryGet(uri, out document);
    }

    /// <inheritdoc />
    public bool TryGetDocumentAtVersion(string uri, int version, [NotNullWhen(true)] out DocumentSnapshot? document)
        => _shadow.TryGetAtVersion(uri, version, out document);

    /// <summary>在标准输入/输出流上启动读循环（生产路径）。</summary>
    /// <remarks>
    /// 必须用 "Console.OpenStandardInput()/OpenStandardOutput()" 拿到原始流再传进来 ——
    /// "Console.In/Out" 是 "TextReader"/"TextWriter"，"根本没有 BaseStream 属性"
    /// （v2.0 第 3 条已实测），而且它们的自带缓冲会打乱字节边界与顺序。
    /// 同时"禁止"在此之后再用 "Console.In/Out" 读同一路数据。
    /// </remarks>
    public async Task<int> RunAsync(Stream inputStream, Stream outputStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(outputStream);

        // ★ leaveOpen: true —— 传输层"不拥有"调用方传来的流。
        // 默认的 PipeReader.Create / PipeWriter.Create 会在排空结束时 Dispose 掉这两个流；
        // 而在 stdio 宿主里 outputStream 就是"进程的标准输出"。关掉它之后，出站排空的收尾
        // flush 会在已关闭的 ConsolePal.WindowsConsoleStream 上抛 ObjectDisposedException；
        // NativeAOT 下该异常被 fail-fast 成 0xC0000409，语言服务器在退出时直接崩溃。
        // 自检的 LspTestClient 走的是 Pipe 重载，所以这条路径此前从未被覆盖。
        var exit = await RunAsync(
            PipeReader.Create(inputStream, new StreamPipeReaderOptions(leaveOpen: true)),
            PipeWriter.Create(outputStream, new StreamPipeWriterOptions(leaveOpen: true)),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await outputStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // 对端已死，flush 失败无所谓。
        }
        catch (ObjectDisposedException)
        {
            // 宿主自己关掉了输出流。语言服务器绝不能因为收尾 flush 而崩溃。
        }

        return exit;
    }

    /// <summary>在既有管道上启动读循环（自检/嵌入式宿主路径）。</summary>
    public async Task<int> RunAsync(PipeReader reader, PipeWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);

        var outbound = new OutboundDrainSink(writer);
        _outbound = outbound;
        outbound.Disconnected += reason =>
        {
            Volatile.Write(ref _disconnected, 1);
            Log.Warn(LogCategories.Transport, "出站断开: " + reason);
        };

        // 排空循环刻意不接停机令牌：令牌一响它就抛 OperationCanceledException，
        // 队列里剩下的帧会被当残留直接归还 —— 那样「取消」就等于「丢帧」。
        // 结束条件改由 CompleteInput（下面的 finally 必然调用）触发，等待时长仍受
        // DrainShutdownTimeout 约束，因此不会变成无限等。
        var drain = outbound.DrainAsync(CancellationToken.None);
        _drainTask = drain;
        var framing = new StdioFramingChannel(reader);

        // ★ 停机信号：stdin 上的阻塞读只有「有数据 / EOF」才会完成，令牌叫不醒它
        //   （Windows 上 Console.OpenStandardInput 的 ReadAsync 甚至是同步读套壳）。
        //   所以每次读都得和停机信号抢一次：令牌一响就放弃这次读并退出循环，
        //   否则 Ctrl+C 之后进程会永远挂在读这一行上（出站队列也永远排不完）。
        var stopSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            stopSignal);

        Log.Info(LogCategories.Transport, "服务循环启动，等待 initialize");

        try
        {
            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref _disconnected) == 0)
            {
                var pending = framing.ReadFrameAsync(cancellationToken).AsTask();
                if (!pending.IsCompleted
                    && await Task.WhenAny(pending, stopSignal.Task).ConfigureAwait(false) != pending)
                {
                    // 这次读永远不会返回结果（底层读还挂在管道上），绝不能 await 它。
                    // 放弃前把可能的异常观察掉：未观察异常在 Native AOT 下会变成进程级故障。
                    _ = pending.ContinueWith(
                        static t => _ = t.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    break;
                }

                var frame = await pending.ConfigureAwait(false);

                if (!frame.Succeeded)
                {
                    if (frame.IsEndOfStream)
                    {
                        Log.Info(LogCategories.Transport, "标准输入 EOF，宿主已关闭");
                    }
                    else
                    {
                        Interlocked.Increment(ref _protocolErrors);

                        // 关键裁定（v2.0 第 2 条）：分帧失败"不可恢复"，绝不重同步。
                        Log.Error(
                            LogCategories.Transport,
                            "分帧失败（不可恢复，按协议终止）: " + StdioFramingChannel.Describe(frame.Failure));
                    }

                    break;
                }

                try
                {
                    HandleFrame(frame.Payload);
                }
                finally
                {
                    framing.Complete();
                }

                if (_exitRequested) break;
            }
        }
        catch (OperationCanceledException)
        {
            Log.Info(LogCategories.Transport, "服务循环被取消");
        }
        catch (IOException ex)
        {
            Interlocked.Increment(ref _protocolErrors);
            Log.Error(LogCategories.Transport, "标准输入读取失败: " + ex.Message);
        }
        finally
        {
            _state = ServerState.Terminated;
            _scheduler.Shutdown();
            outbound.CompleteInput();

            try
            {
                await drain.WaitAsync(_options.DrainShutdownTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                Log.Warn(LogCategories.Transport, "出站排空超时，放弃剩余帧");
            }

            await outbound.DisposeAsync().ConfigureAwait(false);

            try
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // 强制停机时上面主动放弃了一次仍在挂着的读，管道还停在进行中的读状态，
                // 此时收尾会被拒。这条路径本来就在离场路上，读端留给进程回收即可；
                // 让它抛出去只会把一次「正常停机」变成「RunAsync 以异常结束」。
                Log.Warn(LogCategories.Transport, "收尾时输入管道仍在读，跳过关闭: " + ex.Message);
            }

            Log.Info(LogCategories.Transport, "服务循环退出");
        }

        return _shutdownReceived ? 0 : 1;
    }

    /// <summary>
    /// 尽力把出站队列排空，返回是否在时限内排完。给「读循环叫不醒」的强制停机路径用。
    /// </summary>
    /// <remarks>
    /// 只做收尾，不结束进程：调用方排完之后自己决定要不要 Environment.Exit。
    /// 这里不等读循环 —— 它此刻可能正卡在标准输入的一次同步读上，等它就等于永不返回。
    /// 停机请求之前已经投递的帧仍然会写出去；此后的新帧一律被拒（通道已关闭），
    /// 这正是强制停机该有的取舍：先把已经承诺给客户端的东西发完，剩下的不再产生。
    /// </remarks>
    public async Task<bool> DrainOutboundAsync(TimeSpan timeout)
    {
        var outbound = _outbound;
        var drain = _drainTask;
        if (outbound is null || drain is null)
        {
            return true;   // 服务循环还没起来，没有任何出站积压
        }

        outbound.CompleteInput();

        try
        {
            await drain.WaitAsync(timeout, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    // ── 报文处理 ────────────────────────────────────────────────────────────────

    private void HandleFrame(in ReadOnlySequence<byte> payload)
    {
        // ★ 单条报文兜底（两层）：先兜「解析」，再兜「处理」，任何异常都不允许逃出本方法。
        //   Native AOT 下读循环线程上的未处理异常 = 进程 FailFast（0xC0000409），
        //   客户端只会看到「服务端崩了」，拿不到任何线索。
        //   2026-09-29 的事故（$/cancelRequest 数字 id 让整个服务消失）正出在「解析之后、处理之中」。
        LspEnvelope envelope;
        try
        {
            if (!LspMessageReader.TryRead(payload, out envelope))
            {
                Interlocked.Increment(ref _protocolErrors);
                Log.Warn(LogCategories.Transport, "无法解析的 JSON-RPC 报文: " + (envelope.Malformed ?? "未知"));
                return;
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _protocolErrors);
            var malformed = default(LspEnvelope);
            ReportMessageFault(in malformed, ex);
            return;
        }

        try
        {
            // exit 是唯一在 ShuttingDown 之后仍然必须被处理的报文。
            if (envelope.MethodIs(LspMethods.Exit))
            {
                _exitRequested = true;
                return;
            }

            // 临时追踪：把客户端实际发来的每个方法落盘（用于回答「编辑器到底问了什么」）。
            DiagTrace.Mark((envelope.Kind == LspMessageKind.Request ? "→req    " : "→notify ") + Utf8Raw.Unquote(envelope.Method));

            if (envelope.Kind == LspMessageKind.Request)
            {
                HandleRequest(in envelope);
                return;
            }

            HandleNotification(in envelope);
        }
        catch (Exception ex)
        {
            ReportMessageFault(in envelope, ex);
        }
    }

    /// <summary>
    /// 单条报文处理失败时的兜底：记日志，并给「带 id 的请求」回 InternalError，避免客户端挂在等不到回包上。
    /// </summary>
    /// <remarks>
    /// 「为什么必须在这里兜」：服务端对单条畸形/意外报文的最佳反应是「报错并继续服务」，
    /// 而不是把整个进程带走。2026-09-29 的事故正是一条 $/cancelRequest（数字 id）让整个语言服务器消失，
    /// 表现为「补全一条都不出」，排查代价极高。
    /// </remarks>
    private void ReportMessageFault(in LspEnvelope envelope, Exception ex)
    {
        // ★ 兜底的兜底：本方法自身也不允许抛（例如日志 sink 恰在此时失效），
        //   否则异常会从 catch 块里逃出 HandleFrame，又回到「进程 FailFast」的老路。
        try
        {
            var method = Utf8Raw.Unquote(envelope.Method);
            var text = "处理报文失败（已忽略该条）: " + (method.Length == 0 ? "<无方法名>" : method)
                + " —— " + ex.GetType().Name + ": " + ex.Message;

            // ★ 落盘留痕：修好「不再崩」之后必须同时保住「可发现」。
            //   只写 Log.Error 是不够的 —— 它只进内存环形缓冲，而黑匣子在正常退出时并不落盘，
            //   客户端那侧又有限流；少了这一行，系统性故障会退化成「补全就是不出」且查无实据。
            DiagTrace.Mark("!! " + text);
            Log.Error(LogCategories.Transport, text);

            if (envelope.Kind == LspMessageKind.Request && envelope.HasId)
            {
                try
                {
                    SendError(in envelope, LspErrorCodes.InternalError, "服务端内部错误: " + ex.Message);
                }
                catch
                {
                    // 连错误回包都发不出去（例如管道已断）就只能放弃这条。
                }
            }
        }
        catch
        {
            // 兜底路径绝不二次抛出。
        }
    }

    private void HandleRequest(in LspEnvelope envelope)
    {
        if (envelope.MethodIs(LspMethods.Initialize))
        {
            if (_state != ServerState.Uninitialized)
            {
                SendError(in envelope, LspErrorCodes.InvalidRequest, "initialize 只能发送一次");
                return;
            }

            _state = ServerState.Initializing;

            // 客户端能力快照：导航的返回形状（LocationLink 或 Location、层级或扁平大纲）
            // 必须由它决定。解析失败时按最保守形状走，绝不猜。
            if (_navigation?.CapabilitiesReceived is not null
                && LspParamsReader.TryReadNavigationCapabilities(envelope.Params, out var capabilities))
            {
                _navigation.CapabilitiesReceived(capabilities);
            }

            SendResult(in envelope, WriteInitializeResult);
            return;
        }

        if (_shutdownReceived)
        {
            // v2.0 第 5 条：shutdown 之后的请求回 InvalidRequest，"不能静默丢弃"
            // （否则客户端会永远挂在一个等不到回包的请求上）。
            SendError(in envelope, LspErrorCodes.InvalidRequest, "服务器已收到 shutdown");
            return;
        }

        if (_state == ServerState.Uninitialized)
        {
            SendError(in envelope, LspErrorCodes.ServerNotInitialized, "服务器尚未收到 initialize");
            return;
        }

        if (envelope.MethodIs(LspMethods.Shutdown))
        {
            _shutdownReceived = true;
            _state = ServerState.ShuttingDown;
            _scheduler.Shutdown();
            SendResult(in envelope, writeResult: null);
            return;
        }

        if (envelope.MethodIs(LspMethods.Completion))
        {
            HandlePositionRequest(in envelope, _intelliSense?.Completion);
            return;
        }

        if (envelope.MethodIs(LspMethods.Hover))
        {
            HandlePositionRequest(in envelope, _intelliSense?.Hover);
            return;
        }

        if (envelope.MethodIs(LspMethods.Definition))
        {
            HandlePositionRequest(in envelope, _navigation?.Definition);
            return;
        }

        if (envelope.MethodIs(LspMethods.DocumentLink))
        {
            HandleDocumentRequest(in envelope, _navigation?.DocumentLinks);
            return;
        }

        if (envelope.MethodIs(LspMethods.DocumentSymbol))
        {
            HandleDocumentRequest(in envelope, _navigation?.DocumentSymbols);
            return;
        }

        if (envelope.MethodIs(LspMethods.FoldingRange))
        {
            HandleDocumentRequest(in envelope, _navigation?.FoldingRanges);
            return;
        }

        if (envelope.MethodIs(LspMethods.DocumentLinkResolve))
        {
            HandleResolveRequest(in envelope, _navigation?.ResolveDocumentLink);
            return;
        }

        SendError(
            in envelope,
            LspErrorCodes.MethodNotFound,
            "不支持的方法: " + Utf8Raw.Unquote(envelope.Method));
    }

    /// <summary>
    /// 处理「按位置查询」类请求（completion / hover）。
    /// </summary>
    /// <remarks>
    /// 三条硬约束：
    /// <list type="number">
    /// <item>"能力未启用时回 MethodNotFound，绝不静默丢弃" —— 客户端会一直挂在一个等不到回包的请求上；</item>
    /// <item>"文档未打开时回 null"（合法结果，客户端理解为「无补全」），而不是报错；</item>
    /// <item>"provider 直接写响应 writer"，零 DTO、零反射序列化（ADR-026）；
    /// 它返回 "false" 时兜底写 null，保证任何情况下都是合法 JSON。</item>
    /// </list>
    /// 补全/悬停"不进防抖队列"（详设 §五）：这里是同步直接算完就回，
    /// 与后台的 DXC 编译完全解耦，所以打字补全不会被编译阻塞。
    /// </remarks>
    private void HandlePositionRequest(in LspEnvelope envelope, IntelliSenseWriter? writer)
    {
        if (writer is null)
        {
            SendError(
                in envelope,
                LspErrorCodes.MethodNotFound,
                "该能力未启用: " + Utf8Raw.Unquote(envelope.Method));
            return;
        }

        if (!LspParamsReader.TryReadPositionRequest(envelope.Params, out var uri, out var line, out var character))
        {
            SendError(in envelope, LspErrorCodes.InvalidParams, "缺少 textDocument.uri 或 position.line/character");
            return;
        }

        if (!_shadow.TryGet(uri, out var snapshot))
        {
            // 文档未打开 → 合法的「无结果」。
            SendResult(in envelope, static w => w.WriteNullValue());
            return;
        }

        var request = new PositionRequest(uri, snapshot.Text, line, character);

        SendResult(in envelope, w =>
        {
            if (!writer(request, w))
            {
                w.WriteNullValue();
            }
        });
    }

    /// <summary>
    /// 处理「按文档查询」类请求（documentLink / documentSymbol）。
    /// </summary>
    /// <remarks>
    /// 与 HandlePositionRequest 同一套约束：能力未启用回 MethodNotFound，绝不静默丢弃，
    /// 否则客户端会永远挂着等回包；文档未打开回 null（合法的「无结果」）；provider 直接写响应 writer。
    /// </remarks>
    private void HandleDocumentRequest(in LspEnvelope envelope, NavigationDocumentWriter? writer)
    {
        if (writer is null)
        {
            SendError(
                in envelope,
                LspErrorCodes.MethodNotFound,
                "该能力未启用: " + Utf8Raw.Unquote(envelope.Method));
            return;
        }

        if (!LspParamsReader.TryReadDocumentUri(envelope.Params, out var uri))
        {
            SendError(in envelope, LspErrorCodes.InvalidParams, "缺少 textDocument.uri");
            return;
        }

        if (!_shadow.TryGet(uri, out var snapshot))
        {
            SendResult(in envelope, static w => w.WriteNullValue());
            return;
        }

        SendResult(in envelope, w =>
        {
            if (!writer(snapshot.Uri, snapshot.FilePath, snapshot.Text, w))
            {
                w.WriteNullValue();
            }
        });
    }

    /// <summary>
    /// 处理 textDocument/documentLink/resolve。
    /// </summary>
    /// <remarks>
    /// 原样转发 params 字节：入参是客户端把服务端先前给出的那条 link 回传，
    /// 其中只有 data 是我们自己塞的。为它建 DTO 会破坏零反射约束，
    /// 因此这里不做任何解析，直接交给引擎。
    /// </remarks>
    private void HandleResolveRequest(in LspEnvelope envelope, NavigationResolveWriter? writer)
    {
        if (writer is null)
        {
            SendError(
                in envelope,
                LspErrorCodes.MethodNotFound,
                "该能力未启用: " + Utf8Raw.Unquote(envelope.Method));
            return;
        }

        var parameters = envelope.Params;

        SendResult(in envelope, w =>
        {
            if (!writer(parameters, w))
            {
                w.WriteNullValue();
            }
        });
    }

    private void HandleNotification(in LspEnvelope envelope)
    {
        // 未初始化时，除 exit 与 initialized 外一律静默忽略（规范允许）。
        if (_state == ServerState.Uninitialized) return;

        if (envelope.MethodIs(LspMethods.CancelRequest))
        {
            Interlocked.Increment(ref _cancelRequests);
            if (LspParamsReader.TryReadCancelId(envelope.Params, out var id))
            {
                Log.Info(LogCategories.Transport, "收到 $/cancelRequest id=" + Utf8Raw.Unquote(id));
            }
            return;
        }

        if (envelope.MethodIs(LspMethods.Initialized))
        {
            _state = ServerState.ActiveServing;
            Log.Info(LogCategories.Transport, "会话进入 ActiveServing");
            return;
        }

        if (_shutdownReceived) return;

        if (envelope.MethodIs(LspMethods.DidOpen))
        {
            HandleDidOpen(in envelope);
            return;
        }

        if (envelope.MethodIs(LspMethods.DidChange))
        {
            HandleDidChange(in envelope);
            return;
        }

        if (envelope.MethodIs(LspMethods.DidSave))
        {
            HandleDidSave(in envelope);
            return;
        }

        if (envelope.MethodIs(LspMethods.DidClose))
        {
            HandleDidClose(in envelope);
            return;
        }

        if (envelope.MethodIs(LspMethods.DidChangeWatchedFiles))
        {
            HandleDidChangeWatchedFiles();
            return;
        }

        if (envelope.MethodIs(LspMethods.ActiveDocument) && LspParamsReader.TryReadDocumentUri(envelope.Params, out var activeUri))
        {
            _shadow.SetActiveDocument(activeUri);
        }
    }

    private void HandleDidOpen(in LspEnvelope envelope)
    {
        if (!LspParamsReader.TryReadDidOpen(envelope.Params, out var parameters))
        {
            Log.Warn(LogCategories.Transport, "didOpen 参数非法");
            return;
        }

        var path = LspUri.ToPath(parameters.Uri) ?? parameters.Uri;
        var snapshot = _shadow.Open(parameters.Uri, path, parameters.Text, parameters.Version);
        _diagnostics.Open(parameters.Uri, snapshot.Epoch, snapshot.Version);

        Log.Info(
            LogCategories.Transport,
            "didOpen " + path + " v" + snapshot.Version + " epoch" + snapshot.Epoch + " (" + snapshot.LineCount + " 行)");

        Dispatch(Request(snapshot, probeLine: AnalysisRequest.NoProbeLine, full: true), ChangeTrigger.Open);

        // 现场包符号预热：非阻塞（宿主内部排队后立刻返回），绝不拖慢读循环。
        _intelliSense?.DocumentOpened?.Invoke(parameters.Uri, parameters.Text);
    }

    private void HandleDidChange(in LspEnvelope envelope)
    {
        if (!LspParamsReader.TryReadDidChange(envelope.Params, out var parameters))
        {
            Log.Warn(LogCategories.Transport, "didChange 参数非法");
            return;
        }

        _shadow.TryGet(parameters.Uri, out var previous);

        // 与影子文本逐字相同的 didChange 一律当作空操作：不建快照、不派发、不计数。
        // 影子仓储的 Change 无条件发布新快照，同文本会让版本号凭空往前跳一格，
        // 随后一整条链路都以为「有新内容」——探针行算不出来（退化成 0）、
        // 防抖白跑一轮、在途那一轮还会被版本栅栏判成过期。
        // 编辑器（尤其是「格式化后回写同一份文本」和自动保存）确实会发这种通知。
        if (previous is not null && string.Equals(previous.Text, parameters.Text, StringComparison.Ordinal))
        {
            Log.Info(LogCategories.Transport, "didChange 与影子文本相同，忽略 v" + parameters.Version);
            return;
        }

        var snapshot = _shadow.Change(parameters.Uri, parameters.Text, parameters.Version);
        if (snapshot is null)
        {
            Log.Warn(LogCategories.Transport, "didChange 指向未打开的文档: " + parameters.Uri);
            return;
        }

        // include 目标集合预热：非阻塞（宿主内部按「集合未变则跳过」去抖后立刻返回）。
        // 刻意放在 trivia 早退之前：去抖判据本身是微秒级的，不值得为省它而让本调用
        // 依赖 OnlyTriviaChanged 的语义细节（trivia 判定变化时这里也不会漏）。
        _intelliSense?.DocumentChanged?.Invoke(parameters.Uri, snapshot.Text);

        // ADR-011：探针行 = 服务端自己 diff 出的首个改动行（协议里根本没有光标通知）。
        // FirstChangedLine 的 0 是「两文本逐字相同」，不是「第 0 行」；这种通知上面已经早退。
        // 拿不到探针行时用 NoProbeLine 显式表示，绝不能拿 0 顶替 —— 引擎对「0 行」查不到
        // 任何归属，会退化成整篇扫描，而发布侧仍按非全量处理（不做槽位回收），
        // 局部结果就会把文件级诊断盖掉。
        var probeLine = previous is null
            ? AnalysisRequest.NoProbeLine
            : LineDiff.FirstChangedLine(previous.Text, snapshot.Text);

        // 注意：这条路径上 probeLine 恒为正（首次打开在上面的 Change 处早退，逐字相同的 didChange
        // 在最前面就 return），所以这里不再有「探针行退化 → 自动升全量」的分支 —— 留着只会让下一个人
        // 以为那层保护存在。NoProbeLine 只在 didOpen / didSave 这些显式无探针的入口使用。

        // 空白/注释编辑跳过分析：只改了空白或注释时诊断结果不变，不 Dispatch。
        // 首次打开（previous == null）不跳过——需要建立基线诊断。
        //
        // 判据就是 OnlyTriviaChanged 本身，不再拿 probeLine 当门槛：FirstChangedLine 只在
        // 两文本"逐字相同"时才返回 0，而那种情况在上面已经早退 —— 把早退挂在一个永不成立的
        // 判据上，等于这道优化从未生效：敲一个空格也会跑一遍切片 + 编译 + 发布。
        if (previous is not null && LineDiff.OnlyTriviaChanged(previous.Text, snapshot.Text))
        {
            Interlocked.Increment(ref _triviaSkipped);
            Log.Info(LogCategories.Transport, "didChange 仅空白/注释差异，跳过分析 v" + snapshot.Version);

            // 跳过分析不等于把在途那一轮作废：影子文档已经推进到本次快照，而排队中的请求
            // 还带着旧版本号，出队时会按「版本已过期」丢弃，之后再没有新请求 —— 最新文本
            // 就永远不会被分析。这里让调度器把那一轮重挂到最新快照上（或在其已开跑、
            // 无法改写时告知我们补一轮）。
            var request = Request(snapshot, probeLine, full: false);
            if (!_scheduler.TryPreserveInFlightRound(request))
            {
                Dispatch(request, ChangeTrigger.Edit);
            }

            return;
        }

        Dispatch(Request(snapshot, probeLine, full: false), ChangeTrigger.Edit);
    }

    private void HandleDidSave(in LspEnvelope envelope)
    {
        if (!LspParamsReader.TryReadDidSave(envelope.Params, out var parameters))
        {
            Log.Warn(LogCategories.Transport, "didSave 参数非法");
            return;
        }

        // v2.0 第 13 条：SaveReason 含 FocusOut(3)，切窗口也会触发保存；
        // 只处理 Manual(1)。没带 reason 的客户端按 Manual 处理。
        if (parameters.HasReason && parameters.Reason != LspEnums.SaveReasonManual)
        {
            Interlocked.Increment(ref _ignoredSaves);
            Log.Info(LogCategories.Transport, "忽略非手动保存 reason=" + parameters.Reason);
            return;
        }

        if (!_shadow.TryGet(parameters.Uri, out var snapshot))
        {
            Log.Warn(LogCategories.Transport, "didSave 指向未打开的文档: " + parameters.Uri);
            return;
        }

        Dispatch(Request(snapshot, probeLine: AnalysisRequest.NoProbeLine, full: true), ChangeTrigger.Save);

        // 增量预热：保存后内容已稳定，若作者新写了 #include，这一步才需要重扫 include 链。
        _intelliSense?.DocumentSaved?.Invoke(parameters.Uri, snapshot.Text);
    }

    private void HandleDidClose(in LspEnvelope envelope)
    {
        if (!LspParamsReader.TryReadDocumentUri(envelope.Params, out var uri)) return;

        // 先把版本号取出来（拆完状态就查不到了），然后先拆、再发 —— 顺序不能反。
        // publishDiagnostics 是整份替换语义，空数组必须推出去，否则客户端会一直留着这个文件
        // 最后一批红线；但空数组若先于拆状态发出，一次恰好落在「版本校验已通过、帧还没入队」
        // 的那一轮就会把诊断贴回已关闭的文档（幽灵条目）。
        // 拆完再发，配合 PublishAsync 入队前的「文档仍在打开集合里」复查，这条路径才是闭环的。
        var version = _diagnostics.VersionOf(uri);

        _shadow.Close(uri);
        _diagnostics.Close(uri);
        _scheduler.Forget(uri);
        _intelliSense?.DocumentClosed?.Invoke(uri);

        SendNotification(LspMethods.PublishDiagnostics, writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WriteNumber("version", version);
            writer.WritePropertyName("diagnostics");
            writer.WriteStartArray();
            writer.WriteEndArray();
            writer.WriteEndObject();
        });

        Log.Info(LogCategories.Transport, "didClose " + uri);
    }

    /// <summary>
    /// IOpenDocumentLookup: 按物理路径查正被打开的文档（脏文件跳转用）。
    /// </summary>
    bool IOpenDocumentLookup.TryGetOpenDocument(string physicalPath, out string uri, out string text)
    {
        // 遍历影子仓储里的已打开文档，找匹配物理路径的。
        // 路径必须不区分大小写地比：Windows 上同一个文件会以 "D:\…" 与 "d:\…" 两种形态进来
        // （客户端 URI 的盘符/目录大小写、VFS 侧登记的写法各不相同），序数比较认不出同一份文件，
        // 脏文件跳转就退回按磁盘跳、落在旧行号上。
        foreach (var (docUri, snapshot) in _shadow.EnumerateOpen())
        {
            if (string.Equals(snapshot.FilePath, physicalPath, StringComparison.OrdinalIgnoreCase))
            {
                uri = docUri;
                text = snapshot.Text;
                return true;
            }
        }

        uri = string.Empty;
        text = string.Empty;
        return false;
    }

    // ── 发布 ───────────────────────────────────────────────────────────────────

    private Task<bool> PublishAsync(AnalysisOutcome outcome, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // 显式不变量（v2.0 第 9 条）："出队即校验版本"。
        // 快照必须仍是该版本、且 epoch 未变；否则是迟到的旧结果，直接丢弃。
        if (!_shadow.TryGetAtVersion(outcome.Uri, outcome.Version, out var snapshot)
            || snapshot.Epoch != outcome.Epoch)
        {
            Log.Info(
                LogCategories.Transport,
                "丢弃过期分析结果 " + outcome.Uri + " v" + outcome.Version + " epoch" + outcome.Epoch);
            return Task.FromResult(false);
        }

        if (outcome.IsFullScan)
        {
            // 全量重算：清掉已消失的 Pass 槽位，避免旧红线永久残留。
            _diagnostics.RetainOnly(outcome.Uri, outcome.Epoch, outcome.CoveredPasses);
        }

        foreach (var pair in outcome.PassDiagnostics)
        {
            // 文件级槽位（-1）在非全量分析里报空，不等于「文件级问题都没了」：
            // 这一轮只重算了探针行所在的单元，标签校验这类覆盖整篇的结论它根本没有完整重算。
            // 照着写空会把那些红线连诊断一起抹掉，要等下一次全量（保存）才回来 ——
            // 用户看到的就是「改了一处代码，另一个地方的红线自己消失」。
            // 全量扫描不受此限：它本来就有资格清空任何槽位。
            if (!outcome.IsFullScan && pair.Key < 0 && pair.Value.Count == 0)
            {
                continue;
            }

            _diagnostics.SetPass(outcome.Uri, outcome.Epoch, outcome.Version, pair.Key, pair.Value);
        }

        if (!_diagnostics.TryBuildMerged(outcome.Uri, outcome.Epoch, out var payload) || payload is null)
        {
            return Task.FromResult(false);
        }

        // ★ 入队前最后确认一次文档仍在打开集合里。上面那道「出队即校验版本」与这里之间
        //   还有一个窗口：didClose 在另一条线程上把文档摘掉之后，这一轮才轮到写帧。
        //   照发的话客户端会把红线贴回一个已经关闭的文档 —— 问题面板里的幽灵条目就是这么来的。
        //   丢弃时返回 false，调度器据此记一次「被丢弃」（既有统计）。
        if (!_shadow.TryGet(outcome.Uri, out var alive) || alive.Epoch != outcome.Epoch)
        {
            Log.Info(
                LogCategories.Transport,
                "丢弃迟到的分析结果（文档已关闭） " + outcome.Uri + " v" + outcome.Version);
            return Task.FromResult(false);
        }

        SendNotification(
            LspMethods.PublishDiagnostics,
            writer => WriteDiagnostics(writer, payload));

        Interlocked.Increment(ref _publishedCount);

        // 发布也算活动：宿主靠这个时刻判断「真的停手了」，而不是「编译刚好在跑」。
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);

        return Task.FromResult(true);
    }

    private static void WriteDiagnostics(Utf8JsonWriter writer, PublishPayload payload)
    {
        writer.WriteStartObject();
        writer.WriteString("uri", payload.Uri);
        writer.WriteNumber("version", payload.Version);
        writer.WritePropertyName("diagnostics");
        writer.WriteStartArray();

        foreach (var item in payload.Items)
        {
            WriteDiagnostic(writer, item);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteDiagnostic(Utf8JsonWriter writer, ShaderDiagnosticItem item)
    {
        // 内部契约是 1-based UTF-16 code unit；LSP 是 0-based。
        var line = Math.Max(0, item.Line - 1);
        var start = Math.Max(0, item.Column - 1);
        var end = item.Column > 0 ? start + 1 : start;

        writer.WriteStartObject();
        WriteRange(writer, line, start, line, end);
        writer.WriteNumber("severity", (int)item.Severity);
        if (!string.IsNullOrEmpty(item.Code)) writer.WriteString("code", item.Code);
        writer.WriteString("source", "MicroShader");
        writer.WriteString("message", item.Message);

        if (item.RelatedFilePath is not null)
        {
            var relatedLine = Math.Max(0, (item.RelatedLine ?? 1) - 1);
            writer.WritePropertyName("relatedInformation");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WritePropertyName("location");
            writer.WriteStartObject();
            writer.WriteString("uri", FileUri.FromPath(item.RelatedFilePath));
            WriteRange(writer, relatedLine, 0, relatedLine, 0);
            writer.WriteEndObject();
            writer.WriteString("message", "该诊断来自外部头文件");
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteRange(Utf8JsonWriter writer, int startLine, int startChar, int endLine, int endChar)
    {
        writer.WritePropertyName("range");
        writer.WriteStartObject();

        writer.WritePropertyName("start");
        writer.WriteStartObject();
        writer.WriteNumber("line", startLine);
        writer.WriteNumber("character", startChar);
        writer.WriteEndObject();

        writer.WritePropertyName("end");
        writer.WriteStartObject();
        writer.WriteNumber("line", endLine);
        writer.WriteNumber("character", endChar);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    // ── 出站 ───────────────────────────────────────────────────────────────────

    private void SendNotification(string method, Action<Utf8JsonWriter>? writeParams)
        => _outbound?.SendNotification(method, writeParams);

    /// <summary>
    /// 向客户端推一条 "window/logMessage"（模块 7 的 "LspChannelThrottledSink" 的落点）。
    /// </summary>
    /// <remarks>
    /// "为什么必须由会话转发而不是让汇自己写 stdout"：出站只有"一个"写者
    /// （"OutboundDrainSink" 的排空线程）。任何绕过它直接写 stdout 的代码都会与
    /// 诊断帧、响应帧在字节层交错，产出损坏的 LSP 帧 —— 那正是模块 8 用「单写者出站」要杜绝的。
    /// <paramref name="lspMessageType"/> 用 LSP 的 "MessageType" 取值
    /// （1=Error / 2=Warning / 3=Info / 4=Log）。
    /// 返回 "false" 表示出站队列已满或已断开（日志属可丢数据，绝不因此阻断）。
    /// </remarks>
    public bool TrySendLogMessage(int lspMessageType, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        var text = message.Length > MaxLogMessageChars ? message[..MaxLogMessageChars] : message;
        return _outbound?.SendNotification(LspMethods.LogMessage, writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("type", lspMessageType);
            writer.WriteString("message", text);
            writer.WriteEndObject();
        }) ?? false;
    }

    /// <summary>单条 "window/logMessage" 的字符上限（超长在<strong>此处</strong>截断，避免把输出面板刷爆）。</summary>
    private const int MaxLogMessageChars = 2048;

    private void SendResult(in LspEnvelope envelope, Action<Utf8JsonWriter>? writeResult)
        => _outbound?.SendResult(envelope.Id, writeResult);

    private void SendError(in LspEnvelope envelope, int code, string message)
        => _outbound?.SendError(envelope.Id, code, message);

    /// <summary>
    /// 服务端能力声明。
    /// </summary>
    /// <remarks>
    /// "两处必须显式写出来的东西"：
    /// <list type="number">
    ///   <item>"textDocumentSync" 必须显式声明 —— LSP 规范原文：
    ///         "If omitted it defaults to "TextDocumentSyncKind.None""，不写就永远收不到 "didChange"。
    ///         这里写 "{openClose:true, change:1, save:true}"（Full 同步，ADR-008）。
    ///         "save" 必须写 true 才收得到 "didSave"（CTRL+S 全量复检走的就是这条通知），
    ///         但不声明 "includeText"：Full 同步下那等于把同一份文本传两遍；</item>
    ///   <item>"不声明 "diagnosticProvider"" —— 本项目是 push-only（ADR-015），
    ///         一旦声明，客户端会同时期待 pull 结果，造成同一条诊断显示两遍。</item>
    /// </list>
    /// "positionEncoding" 显式回 "utf-16"（两端客户端都是 UTF-16；
    /// 注意与 DXC 的 UTF-8 字节列不是一回事）。
    /// </remarks>
    private void WriteInitializeResult(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();

        writer.WritePropertyName("capabilities");
        writer.WriteStartObject();

        writer.WritePropertyName("textDocumentSync");
        writer.WriteStartObject();
        writer.WriteBoolean("openClose", true);
        writer.WriteNumber("change", LspEnums.TextDocumentSyncFull);

        // save 只给 true 不给 SaveOptions：规范里 true 等价于「保存后发通知、不带文本」，
        // 正是我们需要的；开 includeText 会把同一份全文再传一遍（Full 同步已经传过）。
        // 不声明的话标准客户端一次 didSave 都不会发，HandleDidSave 整条链路就是死码。
        writer.WriteBoolean("save", true);
        writer.WriteEndObject();

        // ── IntelliSense 能力：只在宿主真的注入了实现时才宣告 ──
        // 详设 v2.0 第 4 条：triggerCharacters 必须克制，只宣告 '.'。
        // 宣告 '(' 或双字符会显著抬高请求量，且 '(' 场景（签名帮助）本版不实现。
        // resolveProvider 恒为 false：第 3 条限定 completionItem/resolve 只能补文档字段，
        // 本服务把文档直接塞进 detail 里，无需二次往返。
        if (_intelliSense?.Completion is not null)
        {
            writer.WritePropertyName("completionProvider");
            writer.WriteStartObject();

            writer.WritePropertyName("triggerCharacters");
            writer.WriteStartArray();
            writer.WriteStringValue(".");
            writer.WriteEndArray();

            writer.WriteBoolean("resolveProvider", false);
            writer.WriteEndObject();
        }

        if (_intelliSense?.Hover is not null)
        {
            writer.WriteBoolean("hoverProvider", true);
        }

        // 导航能力：同样只在宿主真的注入了实现时才宣告。
        if (_navigation?.Definition is not null)
        {
            writer.WriteBoolean("definitionProvider", true);
        }

        if (_navigation?.DocumentLinks is not null)
        {
            writer.WritePropertyName("documentLinkProvider");
            writer.WriteStartObject();

            // resolveProvider 必须如实反映是否真的实现了 resolve：首屏只给 range 和 data、
            // 刻意不做路径解析，若这里报 false，客户端不会发 resolve，链接就永远没有 target、点不动。
            writer.WriteBoolean("resolveProvider", _navigation.ResolveDocumentLink is not null);
            writer.WriteBoolean("tooltipSupport", true);
            writer.WriteEndObject();
        }

        if (_navigation?.DocumentSymbols is not null)
        {
            writer.WriteBoolean("documentSymbolProvider", true);
        }

        if (_navigation?.FoldingRanges is not null)
        {
            writer.WriteBoolean("foldingRangeProvider", true);
        }

        writer.WriteString("positionEncoding", "utf-16");
        writer.WriteEndObject();

        writer.WritePropertyName("serverInfo");
        writer.WriteStartObject();
        writer.WriteString("name", _options.ServerName);
        writer.WriteString("version", _options.ServerVersion);
        writer.WriteEndObject();

        writer.WriteEndObject();
    }

    /// <summary>
    /// 把所有已打开文档整篇重算；磁盘头文件变化与工程拓扑重建共用这一条路径。
    /// </summary>
    /// <param name="reason">触发原因，只用于日志（谁让这一轮重算发生）。</param>
    /// <remarks>
    /// **这是最小可用版，刻意不做 include 精确匹配。** 精确匹配需要在分析时记下每个文档的
    /// 直接 include 物理路径、再在会话里维护反查表（跨诊断引擎与会话两层）。本版反过来选：
    /// 宁可多算也不漏 —— 漏算的症状（「改了头文件，红线不消失」）看起来像工具坏了，而多算
    /// 只是一次可被防抖合并的重算。
    ///
    /// 为什么对外公开：工程拓扑（包目录、Unity 版本、lock 内容）换了之后，注册表里已缓存的分析
    /// 单元仍是按旧拓扑算出来的，不整篇作废就永远看到旧结果。宿主那条拓扑热更新回路跑在
    /// watcher 的计时器线程上，不能走 didChangeWatchedFiles 这条客户端通知路径，于是把两边的
    /// 共同部分抽到这里，不复制两份。
    ///
    /// 代价与边界：
    /// - 只重算**已打开**的文档，不碰没打开的文件 —— 不会因为一次保存引发全工程扫描。
    /// - 走 ChangeTrigger.Edit，与打字共用同一套防抖 + 版本栅栏，因此连续的文件变化会合并。
    /// - 通知内容（具体哪个文件变了）本版不解析：反正都要重算已打开文档，解析只是让日志更好看。
    ///   若将来发现「无关文件变化也触发重算」代价明显，再补 include 精确匹配那一层。
    /// - 任何线程可调：影子仓储与调度器各自加锁。
    /// </remarks>
    public void RequestFullReanalysis(string reason)
    {
        // 预热缓存也要跟上：被 include 的头文件变了之后，补全的符号快照同样会陈旧
        // （诊断侧靠 forceRecompile 兜住，符号侧此前没有对应通道）。宿主按写入戳重扫，
        // 未变的文件不会重建，代价与「打开一次文档」同量级。
        _intelliSense?.WatchedFilesChanged?.Invoke();

        var uris = _shadow.Uris();
        if (uris.Length == 0) return;

        var dispatched = 0;
        foreach (var uri in uris)
        {
            if (!_shadow.TryGet(uri, out var snapshot)) continue;
            // forceRecompile：被 include 的文件或工程拓扑变了，缓存里的单元结果就是旧的，
            // 必须让它失效，否则会原样收到上一次的旧诊断（这正是「改了头文件红线不消失」的成因）。
            Dispatch(Request(snapshot, probeLine: AnalysisRequest.NoProbeLine, full: true, forceRecompile: true), ChangeTrigger.Edit);
            dispatched++;
        }

        Log.Info(
            LogCategories.Transport,
            reason + " → 重算已打开文档 " + dispatched + " 个（防抖合并中）");
    }

    /// <summary>workspace/didChangeWatchedFiles 的入口：与拓扑热更新走同一段逻辑。</summary>
    private void HandleDidChangeWatchedFiles() => RequestFullReanalysis("watchedFiles 变化");

    /// <summary>派发一次分析，同时把「最后一次活动」推到此刻。</summary>
    /// <remarks>
    /// 所有派发都必须走这里：只在一两处刷新会把「刚打开文件」这段时间错算成静止。
    /// </remarks>
    private void Dispatch(AnalysisRequest request, ChangeTrigger trigger)
    {
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
        _scheduler.Dispatch(request, trigger);
    }

    private static AnalysisRequest Request(DocumentSnapshot snapshot, int probeLine, bool full, bool forceRecompile = false) => new()
    {
        Uri = snapshot.Uri,
        FilePath = snapshot.FilePath,
        Version = snapshot.Version,
        Epoch = snapshot.Epoch,
        Text = snapshot.Text,
        ProbeLine = probeLine,
        IsFullScan = full,
        ForceRecompile = forceRecompile,
    };
}
