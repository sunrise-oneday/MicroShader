namespace MicroShader.ContextEngine;

/// <summary>
/// 工程拓扑热更新回路：监听到白名单文件变化后整份重建索引、原子置换注册表快照，再通知宿主重算。
/// </summary>
/// <remarks>
/// 监视范围只有四个文件：Packages/manifest.json、Packages/packages-lock.json、
/// ProjectSettings/ProjectVersion.txt、Library/MicroShader/endpoint.json，另加周期心跳与
/// 文件系统缓冲区溢出兜底。新增或删除 .hlsl 头文件不触发任何重建：include 由 DXC 直接读盘，
/// 不经过 VFS，去 watch 整个 Assets 或 Library 只会把 CPU 耗在包解压与资源导入风暴上。
/// endpoint.json 只说明 Unity 桥接上线或下线，与包拓扑无关（桥接自己会读它），
/// 因此该原因进不了重建。
/// 重建一次要遍历包目录与 PackageCache，耗时几十到几百毫秒，所以同一时刻只允许一轮在跑：
/// 期间到达的后续请求合并成「跑完再跑一轮」，不会排队几十次，也不会并发抢扫同一批目录。
/// 重建失败时旧快照原样继续服务，只记一条告警 —— 拓扑扫描失败不该让语言服务停摆。
/// </remarks>
public sealed class VfsTopologyRefresher : IDisposable
{
    private readonly ShaderContextRegistry _registry;
    private readonly Action<VfsChangeReason> _onRebuilt;
    private readonly Action<string>? _onWarning;
    private readonly Func<long, VfsIndex> _buildIndex;
    private readonly VfsWatcher _watcher;

    /// <summary>是否有待跑的一轮（1 表示有）。</summary>
    private int _pending;

    /// <summary>是否已有线程在跑（1 表示在跑）。</summary>
    private int _running;

    /// <summary>最近一次请求的原因，真正开跑时用它填日志与宿主通知。</summary>
    private int _reason;

    /// <param name="projectRoot">工程根（归一路径）；调用方负责保证它成立。</param>
    /// <param name="registry">接收新快照的注册表。</param>
    /// <param name="onRebuilt">新拓扑置换之后调用，宿主在这里让已打开文档全量重算。</param>
    /// <param name="watcherOptions">去抖与心跳，测试可调小。</param>
    /// <param name="buildOptions">索引构建选项；必须与启动那次保持同一套（尤其是 Unity 安装根）。</param>
    /// <param name="buildIndex">重建实现，默认走 VfsIndexBuilder；仅测试注入用。</param>
    /// <param name="onWarning">告警出口，默认无。宿主接到日志旁路。</param>
    public VfsTopologyRefresher(
        string projectRoot,
        ShaderContextRegistry registry,
        Action<VfsChangeReason> onRebuilt,
        VfsWatcherOptions? watcherOptions = null,
        VfsIndexBuildOptions? buildOptions = null,
        Func<long, VfsIndex>? buildIndex = null,
        Action<string>? onWarning = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _onRebuilt = onRebuilt ?? throw new ArgumentNullException(nameof(onRebuilt));
        _onWarning = onWarning;

        var options = buildOptions ?? new VfsIndexBuildOptions();
        _buildIndex = buildIndex ?? (generation => VfsIndexBuilder.Build(projectRoot, generation, options));

        _watcher = new VfsWatcher(UnityProjectLayout.FromRoot(projectRoot), OnChanged, watcherOptions);
    }

    /// <summary>开始监视。构造之后必须调用一次。</summary>
    public void Start() => _watcher.Start();

    /// <summary>请求重建一次，可来自任意线程。</summary>
    /// <remarks>已有重建在跑时只置位「再跑一次」，不排队、不阻塞调用方。</remarks>
    public void RequestRebuild(VfsChangeReason reason)
    {
        Interlocked.Exchange(ref _reason, (int)reason);
        Interlocked.Exchange(ref _pending, 1);

        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            RunLoop();
        }
    }

    private void OnChanged(VfsChangeReason reason)
    {
        if (reason == VfsChangeReason.EndpointChanged)
        {
            // 桥接上线只影响动态宏，不影响包拓扑；拿它触发一次全量扫盘纯属白烧 CPU。
            return;
        }

        RequestRebuild(reason);
    }

    private void RunLoop()
    {
        try
        {
            while (true)
            {
                Interlocked.Exchange(ref _pending, 0);
                Rebuild((VfsChangeReason)Volatile.Read(ref _reason));
                Volatile.Write(ref _running, 0);

                // 这里有个空窗：若新请求恰好落在「判断无待办」与「交出在跑标记」之间，
                // 它看到 running=1 就直接返回了，没有别的线程会接手 —— 必须自己再抢一次。
                if (Volatile.Read(ref _pending) == 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                {
                    return;   // 已被其它线程接手
                }
            }
        }
        catch
        {
            // 只有「异常逃出循环」时本线程才仍然持有标记：正常退出路径要么已在循环内交出，
            // 要么是把所有权交接给了别的线程（下面 CAS 失败那条）。这里若无条件清标记，
            // 会把别人的在跑标记一起清掉 —— 第三个线程随后就能与正在跑的那一轮并发扫盘，
            // 两轮各自 Publish，后完成的旧构建还会覆盖先完成的新构建。
            Volatile.Write(ref _running, 0);
            throw;
        }
    }

    private void Rebuild(VfsChangeReason reason)
    {
        var current = _registry.Current.Index.Generation;

        // 代际号必须递增：读端靠它判断自己手里的是不是新拓扑。
        var generation = current + 1;

        VfsIndex built;
        try
        {
            built = _buildIndex(generation);
        }
        catch (Exception ex)
        {
            // 失败保留旧快照：读端期间拿到的始终是上一份完整拓扑，不会出现半新半旧。
            _onWarning?.Invoke("VFS 拓扑重建失败，继续沿用旧快照（generation 保持 " + current + "）：" + ex.Message);
            return;
        }

        // 置换前先做一次健全性判定：Build 内部会把 JsonException / IOException 吞掉，
        // 转成「manifest 无效」的索引而不是抛异常。Unity 装包时 manifest 与 lock 是先后两次写，
        // 去抖窗口落在半写状态就会得到一份「包全没了」的索引 —— 若照原样发布，
        // 紧接着的全量重算会把所有包判成不在拓扑中，而且这个错误结论会一直留到下一次拓扑事件，
        // 比「需要重启」更糟。所以：旧快照是好的、新快照坏了，就拒绝置换。
        // 反向（坏 → 好）不在判定范围内：修好 manifest 之后必须能恢复，那是正常置换。
        var previous = _registry.Current.Index;
        if (built.Manifest.ManifestValid == false && previous.Manifest.ManifestValid)
        {
            _onWarning?.Invoke(
                "VFS 拓扑重建拿到无效 manifest（多半是 Unity 正写到一半），保留旧快照（generation 保持 "
                + current + "）：" + (built.Manifest.ManifestError ?? "未知原因"));
            return;
        }
        else if (previous.PackageCount > 0 && built.PackageCount == 0)
        {
            _onWarning?.Invoke(
                "VFS 拓扑重建后包数从 " + previous.PackageCount + " 掉到 0（多半是写到一半），"
                + "保留旧快照（generation 保持 " + current + "）");
            return;
        }

        // 先置换再通知：宿主收到通知时新拓扑已经可见，它触发的重算必然用上新拓扑。
        _registry.Publish(built);

        try
        {
            _onRebuilt(reason);
        }
        catch (Exception ex)
        {
            _onWarning?.Invoke("VFS 拓扑重建后的重算通知失败：" + ex.Message);
        }
    }

    public void Dispose() => _watcher.Dispose();
}
