namespace MicroShader.CoordinationEngine.Scheduling;

/// <summary>变更事件的来源，决定是否旁路防抖。</summary>
public enum ChangeTrigger
{
    /// <summary>didOpen：首次全量诊断，"不加任何延迟"（v2.0 第 11 条硬约束）。</summary>
    Open,

    /// <summary>didChange：走防抖合并。</summary>
    Edit,

    /// <summary>didSave(reason = Manual)：立即穿透并抢占在途任务。</summary>
    Save,
}

/// <summary>
/// 响应式调度仲裁器：消灭打字并发竞争，仲裁「防抖 / 保存穿透」的时序。
/// </summary>
/// <remarks>
/// "与设计文档的重大偏离（已记入 README）"：原文要求用 "Cysharp/R3"。
/// 本实现用 BCL 的 <see cref="Timer"/> + <see cref="SemaphoreSlim"/> + <see cref="CancellationTokenSource"/>
/// 自行实现同等语义，理由三条，全部来自项目级约束：
/// <list type="number">
///   <item>本仓库 "NuGet.config" 清空了包源 ——「零第三方依赖、构建机可完全离线」是硬约束，
///         R3 根本拉不下来（模块 2 放弃 MemoryPack、模块 7 放弃 ZString 是同一条约束的同一后果）；</item>
///   <item>v2.0 修正清单第 10 条自己写明：R3 的 "R3.csproj" 既没有 "IsAotCompatible"
///         也没有 "IsTrimmable"，README 全文 0 次提到 AOT —— 而本项目 "IsAotCompatible=true"
///         且 ADR-030 要求第三方库走三步 AOT 验收。把一条未声明 AOT 支持的响应式框架放在全系统
///         最热的调度路径上，正是这个项目反复拒绝的那类风险；</item>
///   <item>R3 的能力里我们真正用到的只有「可重置的延迟 + 取消」，几十行 BCL 代码即可等价实现，
///         而语义可以 100% 被无头自检覆盖（R3 的 "AwaitOperation" 有 6 个值且默认
///         "Sequential"，反而更容易踩坑）。</item>
/// </list>
/// "防抖时长"：v2.0 第 11 条把原文的 2.5 秒判为离群值（clangd 自适配硬顶 500ms、
/// rust-analyzer 事件合并 50ms、Sublime LSP 立即发 + 取消旧请求）→ 默认 "300ms"，
/// 合法区间 [200ms, 500ms]，构造时自动夹取。
/// "版本栅栏"：每个文档维护一个只增不减的 generation。事件到达即 ++；
/// 分析前后各校验一次，任何时刻发现 generation 变了就放弃 —— 这就是 v2.0 第 2 / 9 条要求的
/// 「保存与防抖死斗」结局：迟到的防抖结果一律进垃圾桶，编辑器只会看到全量保存的结果。
/// </remarks>
public sealed class CoordinationScheduler : IDisposable
{
    /// <summary>默认防抖时长。</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(300);

    /// <summary>允许的最短防抖时长。</summary>
    public static readonly TimeSpan MinimumDebounce = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// 允许的最长防抖时长。2026-09-30 由 500ms 放宽到 5000ms：**默认值仍是 <see cref="DefaultDebounce"/>（300ms）**，
    /// 放宽只是把「更激进地合并打字突发」变成宿主/用户可选的旋钮
    /// （见服务端 --debounce / MICROSHADER_ANALYSIS_DEBOUNCE_MS）。
    /// </summary>
    public static readonly TimeSpan MaximumDebounce = TimeSpan.FromMilliseconds(5000);

    private sealed class DocState
    {
        public long Generation;
        public Timer? Timer;
        public CancellationTokenSource? InFlight;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private sealed class PendingRun
    {
        public required CoordinationScheduler Scheduler { get; init; }

        public required DocState State { get; init; }

        public required long Generation { get; init; }

        public required AnalysisRequest Request { get; init; }
    }

    private readonly DocumentAnalyzer _analyze;
    private readonly OutcomePublisher _publish;
    private readonly TimeSpan _debounce;
    private readonly Dictionary<string, DocState> _states = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();

    private long _analysesStarted;
    private long _analysesPublished;
    private long _analysesDiscarded;
    private long _changesCoalesced;
    private int _disposed;

    public CoordinationScheduler(DocumentAnalyzer analyze, OutcomePublisher publish, TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(analyze);
        ArgumentNullException.ThrowIfNull(publish);

        var value = debounce ?? DefaultDebounce;
        if (value < MinimumDebounce) value = MinimumDebounce;
        if (value > MaximumDebounce) value = MaximumDebounce;

        _analyze = analyze;
        _publish = publish;
        _debounce = value;
    }

    /// <summary>实际生效的防抖时长。</summary>
    public TimeSpan Debounce => _debounce;

    /// <summary>已启动的分析次数。</summary>
    public long AnalysesStarted => Interlocked.Read(ref _analysesStarted);

    /// <summary>真正推送出去的分析次数。</summary>
    public long AnalysesPublished => Interlocked.Read(ref _analysesPublished);

    /// <summary>被版本栅栏丢弃的迟到结果数。</summary>
    public long AnalysesDiscarded => Interlocked.Read(ref _analysesDiscarded);

    /// <summary>被防抖合并掉（计时器被新事件顶替，从未单独派发）的变更数。</summary>
    public long ChangesCoalesced => Interlocked.Read(ref _changesCoalesced);

    /// <summary>分析器抛异常时的回调（诊断链路自身故障不能杀死会话）。</summary>
    public event Action<string, Exception>? AnalysisFailed;

    /// <summary>派发一次分析。</summary>
    public void Dispatch(AnalysisRequest request, ChangeTrigger trigger)
    {
        ArgumentNullException.ThrowIfNull(request.Uri);

        DocState state;
        long generation;
        lock (_gate)
        {
            if (!_states.TryGetValue(request.Uri, out var found))
            {
                found = new DocState();
                _states[request.Uri] = found;
            }

            state = found;
            state.Generation++;
            generation = state.Generation;

            if (state.Timer is not null)
            {
                // 计时器被顶替 = 这次变更被合并掉了，永远不会单独分析。
                state.Timer.Dispose();
                state.Timer = null;
                Interlocked.Increment(ref _changesCoalesced);
            }

            // 保存事件的「最高政治权利」：立即撤销在途的防抖任务。
            state.InFlight?.Cancel();
        }

        if (trigger == ChangeTrigger.Edit)
        {
            var pending = new PendingRun
            {
                Scheduler = this,
                State = state,
                Generation = generation,
                Request = request,
            };

            lock (_gate)
            {
                state.Timer = new Timer(
                    static captured =>
                    {
                        var run = (PendingRun)captured!;
                        _ = run.Scheduler.RunAsync(run.State, run.Generation, run.Request, ChangeTrigger.Edit);
                    },
                    pending,
                    _debounce,
                    Timeout.InfiniteTimeSpan);
            }

            return;
        }

        _ = RunAsync(state, generation, request, trigger);
    }

    /// <summary>文档关闭：丢弃其调度状态。</summary>
    public void Forget(string uri)
    {
        lock (_gate)
        {
            if (!_states.Remove(uri, out var state)) return;
            state.Timer?.Dispose();
            state.InFlight?.Cancel();
        }
    }

    /// <summary>停止一切在途与排队任务（shutdown 路径）。</summary>
    public void Shutdown()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();

        lock (_gate)
        {
            foreach (var state in _states.Values)
            {
                state.Timer?.Dispose();
                state.Timer = null;
                state.InFlight?.Cancel();
            }
            _states.Clear();
        }
    }

    public void Dispose()
    {
        Shutdown();
        _shutdown.Dispose();
    }

    private async Task RunAsync(DocState state, long generation, AnalysisRequest request, ChangeTrigger trigger)
    {
        _ = trigger;

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (state.Generation != generation) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            state.InFlight = cts;
        }

        var gateTaken = false;
        try
        {
            // 文档级互斥：同一文档任何时刻只允许一个分析在跑（规格书四组线程的 Thread 3 约束）。
            await state.Gate.WaitAsync(cts.Token).ConfigureAwait(false);
            gateTaken = true;

            // 拿到互斥后再判一次：等待期间可能已有更新的保存事件把我们作废。
            lock (_gate)
            {
                if (state.Generation != generation) return;
            }

            Interlocked.Increment(ref _analysesStarted);
            var outcome = await _analyze(request, cts.Token).ConfigureAwait(false);

            // 分析后最后一次校验 —— 版本栅栏的落点。
            lock (_gate)
            {
                if (state.Generation != generation)
                {
                    Interlocked.Increment(ref _analysesDiscarded);
                    return;
                }
            }

            if (await _publish(outcome, cts.Token).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _analysesPublished);
            }
            else
            {
                Interlocked.Increment(ref _analysesDiscarded);
            }
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _analysesDiscarded);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AnalysisFailed?.Invoke(request.Uri, ex);
        }
        finally
        {
            if (gateTaken) state.Gate.Release();

            lock (_gate)
            {
                if (ReferenceEquals(state.InFlight, cts)) state.InFlight = null;
            }

            cts.Dispose();
        }
    }
}
