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

    private readonly object _sync = new();

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

        var includesKey = string.Join('\n', ScanIncludes(text));

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
                if (!_cache.TryGetFile(physical, stamp, out _))
                {
                    // 一次扫描同时产出结构体、声明表、宏事件流与行首表，导航请求路径上才能完全不读盘。
                    _cache.PutFile(physical, IncludeSymbolCache.FileSymbols.Build(content, stamp, _cache.Interner));
                }
            }

            ordered.Add(physical);

            if (depth >= _options.MaxDepth)
            {
                continue;
            }

            foreach (var target in ScanIncludes(content))
            {
                if (_source.TryResolve(target, out var resolved)
                    || _source.TryResolveRelative(physical, target, out resolved))
                {
                    queue.Enqueue((resolved, depth + 1));
                }
            }
        }

        DiagTrace.Mark("WarmOne 结束 文件数=" + files + " 字节=" + bytes);
        _cache.SetDocument(documentUri, ordered);
    }
    /// <summary>
    /// 扫出 "#include" / "#include_with_pragmas" 的目标路径（原样，不做归一化）。
    /// </summary>
    /// <remarks>
    /// 刻意只做「路径收集」这一件事，Unity 的指令语义（降级重写、条件包含）仍归模块 1/3 负责；
    /// 这里漏收的后果只是「少预热一个文件」，不会给出错误候选。
    /// </remarks>
    private static List<string> ScanIncludes(string text)
    {
        var found = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var nl = text.IndexOf((char)10, start);
            var end = nl < 0 ? text.Length : nl;
            var line = text.AsSpan(start, end - start).TrimStart();

            if (line.StartsWith("#include", StringComparison.Ordinal))
            {
                var quote = line.IndexOf('"');
                var open = line.IndexOf('<');
                var from = quote >= 0 ? quote + 1 : open + 1;
                if (from > 0)
                {
                    var rest = line[from..];
                    var close = quote >= 0 ? rest.IndexOf('"') : rest.IndexOf('>');
                    if (close > 0)
                    {
                        found.Add(rest[..close].ToString());
                    }
                }
            }

            if (nl < 0)
            {
                break;
            }

            start = nl + 1;
        }

        return found;
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
