using MicroShader.Domain;
using MicroShader.ObservabilityEngine;
using MicroShader.ShaderLab;

namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// include 链符号预热器：后台把现场包（URP 头）里的结构体定义扫进
/// <see cref="IncludeSymbolCache"/>，让补全请求永远不必做磁盘 I/O。
/// </summary>
/// <remarks>
/// "为什么必须有它"：成员补全是在 LSP 读循环里同步写响应的，绝不能在里面扫盘。
/// URP 一条 pass 的 include 链有 69 个文件、单文件最大 300 KB，同步全扫约 300 ms —— 那是卡顿，
/// 不是补全。所以扫描离线做，补全侧只读缓存。
/// "失败模式是安全的"：预热没赶上就查不到 → 引擎保持现有的「降级为空」行为，
/// 只会「暂时少给候选」，绝不会变慢。
/// "只收结构体"：include 链里的局部变量若一并灌进变量表，会重演 R1 那类误登记
/// （函数调用实参被当成声明），因此这里刻意只取 "Structs"。
/// </remarks>
public sealed class IncludeSymbolWarmer : IDisposable
{
    private readonly IIncludeFileSource _source;
    private readonly IncludeSymbolCache _cache;
    private readonly IncludeSymbolOptions _options;
    private readonly Dictionary<string, Pending> _queue = new(StringComparer.Ordinal);

    /// <summary>每个文档最近一次的 include 目标集合（去抖判据：未变则闭包必然一致，跳过重扫）。</summary>
    private readonly Dictionary<string, string> _lastIncludes = new(StringComparer.Ordinal);

    /// <summary>每个文档最近一次的内容快照（重扫入口用：磁盘变化后据此重新排队）。</summary>
    private readonly Dictionary<string, Pending> _latest = new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _worker;

    private readonly record struct Pending(string ShaderPath, string Text);

    public IncludeSymbolWarmer(IIncludeFileSource source, IncludeSymbolCache cache, IncludeSymbolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(cache);

        _source = source;
        _cache = cache;
        _options = options ?? new IncludeSymbolOptions();

        // 手动模式（RunWorker=false）不起后台消费者：否则 worker 会与 Drain() 抢同一把 _sync 与同一个 _queue，
        // 谁先 Clear() 谁拿走批次，Drain() 拿到空队列就立刻 return，调用方以为「预热已完成」。
        _worker = _options.RunWorker ? Task.Run(LoopAsync) : Task.CompletedTask;
    }

    private readonly Lock _sync = new();

    /// <summary>排一次预热（同一文档只保留最新一次请求，天然去抖）。非阻塞。</summary>
    /// <remarks>
    /// "include 集合未变则跳过"：预热结果只取决于文档的 include 目标集合（闭包图）
    /// 与各头文件内容，与文档正文的其余部分无关 —— 逐键 didChange 时集合几乎从不变化，
    /// 跳过让每次按键的成本降到一趟 O(文档长度) 的 include 行扫描（微秒级）。
    /// "为什么跳过也安全"：集合相同意味着 BFS 会走到同一批文件，仍在队列里的旧任务
    /// 产出的闭包与本次必然一致；头文件在磁盘上变化由 RescanAll / 写入戳比对兜住。
    /// </remarks>
    public void Enqueue(string documentUri, string shaderPath, string text)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var includesKey = string.Join('\n', IncludeLineScanner.Scan(text));

        lock (_sync)
        {
            _latest[documentUri] = new Pending(shaderPath, text);

            if (_lastIncludes.TryGetValue(documentUri, out var previous)
                && previous.Equals(includesKey, StringComparison.Ordinal))
            {
                DiagTrace.Mark("预热跳过（include 集合未变）uri=" + documentUri);
                return;
            }

            DiagTrace.Mark("预热入队 uri=" + documentUri);
            _lastIncludes[documentUri] = includesKey;
            _queue[documentUri] = new Pending(shaderPath, text);
        }

        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    /// <summary>
    /// 磁盘上相关文件可能变过（didChangeWatchedFiles）：把所有「预热过」的文档重新排队。
    /// </summary>
    /// <remarks>
    /// 重扫成本有界：WarmOne 对每个文件先比写入戳，未变的文件不重建符号；
    /// 真正多出来的只是「重新走一遍 BFS + 读文本扫边」——与一次文档打开同量级。
    /// 这里不比对 include 集合判据：变化的可能正是头文件内容而非文档文本。
    /// </remarks>
    public void RescanAll()
    {
        if (!_options.Enabled)
        {
            return;
        }

        lock (_sync)
        {
            foreach (var pair in _latest)
            {
                _queue[pair.Key] = pair.Value;
            }
        }

        if (_signal.CurrentCount == 0)
        {
            _signal.Release();
        }
    }

    /// <summary>工程拓扑重建后的回收入口：清空缓存并把预热过的文档重新排队。</summary>
    //
    // 什么时候清：只在拓扑重建成功之后（装包、升级包、删包、改 manifest / packages-lock、
    // 切 Unity 版本）。包目录与版本一变，文件层快照里的声明偏移与 include 边可能指向已经
    // 不存在的目录，留着它 F12 会跳到不存在的行、补全可能拿到旧版本的结构体。
    // 清完第一次请求会慢多少：直连 include 由导航 / 补全既有的同步兜底现场补扫（受
    // SyncFallbackFiles 预算限制，一次最多两个直连 include，几十微秒到几毫秒），扫到的文件
    // 同时写回缓存；更深一层与文档层由这里重新排队后的后台预热补齐（一次文档打开的预热量级），
    // 这段窗口只可能暂时少给候选，不会给出错误候选。
    // 去抖表必须一起清：预热按「include 集合未变」跳过，留着旧键会让重建整批落空，
    // 缓存清完就再也没人填回来。
    public void ReclaimForTopologyChange()
    {
        _cache.Clear();

        lock (_sync)
        {
            _lastIncludes.Clear();
        }

        // 重新排队已预热过的文档：不清的话，要等下一次文档打开或磁盘变化才会重建文件层。
        RescanAll();
    }

    /// <summary>文档关闭：丢弃其文档层条目与去抖/重扫状态。</summary>
    public void Invalidate(string documentUri)
    {
        _cache.InvalidateDocument(documentUri);

        lock (_sync)
        {
            _lastIncludes.Remove(documentUri);
            _latest.Remove(documentUri);
        }
    }

    /// <summary>
    /// 同步跑完当前排队的全部预热。仅用于自检 —— 让测试不必靠 sleep 等后台线程。
    /// </summary>
    public void Drain()
    {
        while (true)
        {
            List<KeyValuePair<string, Pending>> batch;
            lock (_sync)
            {
                if (_queue.Count == 0)
                {
                    return;
                }

                batch = [.. _queue];
                _queue.Clear();
            }

            foreach (var item in batch)
            {
                WarmOne(item.Key, item.Value);
            }
        }
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            DiagTrace.Mark("预热 worker 唤醒");

            List<KeyValuePair<string, Pending>> batch;
            lock (_sync)
            {
                batch = [.. _queue];
                _queue.Clear();
            }

            foreach (var item in batch)
            {
                if (_cts.IsCancellationRequested)
                {
                    return;
                }

                try
                {
                    WarmOne(item.Key, item.Value);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    DiagTrace.Mark("!! WarmOne 异常（已忽略，预热线程继续）: " + ex.GetType().Name + ": " + ex.Message);
                }
                await Task.Yield();
            }
        }
    }

    private void WarmOne(string documentUri, Pending work)
    {
        DiagTrace.Mark("WarmOne 开始 uri=" + documentUri + " shaderPath=" + work.ShaderPath);

        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Physical, int Depth)>();

        queue.Enqueue((work.ShaderPath, 0));

        var files = 0;
        var bytes = 0;

        while (queue.Count > 0)
        {
            var (physical, depth) = queue.Dequeue();
            if (!visited.Add(physical) || files >= _options.MaxFiles || bytes >= _options.MaxBytes)
            {
                continue;
            }

            string content;
            if (depth == 0)
            {
                content = work.Text;   // 文档自身：直接用已给的文本，省一次 I/O
            }
            else if (!_source.TryRead(physical, out content!))
            {
                continue;
            }

            files++;
            bytes += content.Length * 2;
            DiagTrace.Mark("  预热文件 depth=" + depth + " " + physical);

            if (depth > 0)
            {
                var stamp = _source.GetStamp(physical);

                // 跳过判据是"已有完整快照"：只写了结构体表的条目（补全同步兜底写的）
                // 不算完成 —— 否则该文件永远补不上声明表与行首表，导航侧每次都要现场补扫。
                if (!_cache.HasFullSnapshot(physical, stamp))
                {
                    // 一次扫描同时产出结构体、声明表、宏事件流与行首表，导航请求路径上才能完全不读盘。
                    // 字符串去重直接用缓存自己那张表，不另开私有表：StringInterner 是并发字典，
                    // 命中走无锁读、只有首次登记才取桶锁，预热线程整篇写入不会拖慢会话线程的补全 / F12；
                    // 共用省掉的是 include 缓存自己那份词表（它与块索引本来就是两张独立的表，不跨用）。
                    _cache.PutFile(physical, IncludeSymbolCache.FileSymbols.Build(content, stamp, _cache.Interner));
                }
            }

            ordered.Add(physical);

            if (depth >= _options.MaxDepth)
            {
                continue;
            }

            foreach (var target in IncludeLineScanner.Scan(content))
            {
                if (_source.TryResolve(physical, target, out var resolved)
                    || _source.TryResolveRelative(physical, target, out resolved))
                {
                    queue.Enqueue((resolved, depth + 1));
                }
            }
        }

        DiagTrace.Mark("WarmOne 结束 文件数=" + files + " 字节=" + bytes);
        _cache.SetDocument(documentUri, ordered);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _signal.Release();

        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 关闭路径：预热失败不影响会话退出
        }

        _signal.Dispose();
        _cts.Dispose();
    }
}
