namespace MicroShader.ContextEngine;

/// <summary>
/// 无锁只读上下文注册表：静态 VFS 拓扑 + 动态宏的单一入口。
/// </summary>
/// <remarks>
/// "为什么不用锁"：查询发生在每一次 "#include" 解析上（URP 单个包内实测 779 条），
/// 要求数十纳秒级；而更新只发生在「文件变化 / Unity 推送宏」这类低频事件上。
/// 典型的读多写极少 —— 用 CAS 整体置换快照，读者只做一次 "Volatile.Read"。
/// 
/// "契约（源自详细设计的交付接口，已按零分配要求调整返回形态）"：
/// 原始接口声明 "out ReadOnlySpan&lt;char&gt;" —— 从注册表这种可被并发置换的对象里
/// 返回指向内部字符串的 span 是危险的（读者可能在持有 span 期间经历一次置换）。
/// 这里改为返回 "string"：物理路径字符串本身早已存在于索引中，返回它"不产生任何分配"，
/// 语义与性能等价而生命周期安全。
/// 
/// </remarks>
public sealed class ShaderContextRegistry
{
    private readonly VfsIndexLocator _locator;

    private ContextSnapshot _snapshot;

    /// <summary>最近一次「按文档定位工程」的结果（单槽备忘，见 <see cref="IndexForDocument"/>）。</summary>
    private DocumentIndex? _memo;

    public ShaderContextRegistry(VfsIndex? initial = null, VfsIndexLocator? locator = null)
    {
        _locator = locator ?? new VfsIndexLocator(initial);
        _snapshot = new ContextSnapshot(_locator.Primary, DynamicShaderContext.Empty, 0);
    }

    /// <summary>当前主工程根（归一化）；未定位到时为空串。</summary>
    public string PrimaryProjectRoot => _locator.PrimaryRoot;

    /// <summary>
    /// 该文档所属工程的索引。
    /// </summary>
    /// <remarks>
    /// "为什么按文档取而不是全局一个"：一个语言服务器窗口可能开着多个 Unity 工程的文件
    /// （典型情形是把装着若干工程的父目录当成工作区）。工程拓扑是「文件属于谁」的函数，
    /// 让每个文件用自己工程的拓扑，才能既不误报「包不在拓扑中」，也不把相邻工程的包当成自己的。
    /// "为什么有单槽备忘"：同一次分析里每一行 include 都要问一次，而答案只取决于文档路径；
    /// 命中路径 span 比对（零分配）即返回。分析由信号量串行，备忘命中率接近 100%。
    /// </remarks>
    public VfsIndex IndexForDocument(string? documentPhysicalPath)
    {
        if (string.IsNullOrEmpty(documentPhysicalPath))
        {
            return Current.Index;
        }

        var memo = _memo;
        if (memo is not null && documentPhysicalPath.AsSpan().SequenceEqual(memo.Path))
        {
            return memo.Index;
        }

        var index = _locator.ForFile(documentPhysicalPath);
        _memo = new DocumentIndex(documentPhysicalPath, index);
        return index;
    }

    /// <summary>
    /// <see cref="IndexForDocument(string?)"/> 的 span 版本：include 解析热路径用它比对备忘，
    /// 命中时零分配（路径字符串早已存在，不必再拷一份）。
    /// </summary>
    public VfsIndex IndexForDocument(ReadOnlySpan<char> documentPhysicalPath)
    {
        var memo = _memo;
        if (memo is not null && documentPhysicalPath.SequenceEqual(memo.Path))
        {
            return memo.Index;
        }

        return IndexForDocument(documentPhysicalPath.ToString());
    }

    /// <summary>读取当前快照（一次 volatile 读，无锁无分配）。</summary>
    public ContextSnapshot Current => Volatile.Read(ref _snapshot);

    /// <summary>Unity 桥接是否在线。</summary>
    public bool IsUnityLiveConnected => Current.Dynamic.IsLive;

    /// <summary>当前激活的全局宏。</summary>
    public IReadOnlyList<string> GetActiveDefines() => Current.GetActiveDefines();

    /// <summary>替换静态拓扑，返回新快照版本号。</summary>
    public long Publish(VfsIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        _locator.ReplacePrimary(index);   // 主索引换了，按文档定位的「命中主工程」分支要跟着换。
        return Advance(static (current, state) => new ContextSnapshot(state, current.Dynamic, current.Version + 1), index);
    }

    /// <summary>替换 Unity 动态上下文，返回新快照版本号。</summary>
    public long PublishDynamic(DynamicShaderContext dynamicContext)
    {
        ArgumentNullException.ThrowIfNull(dynamicContext);
        return Advance(static (current, state) => new ContextSnapshot(current.Index, state, current.Version + 1), dynamicContext);
    }

    /// <summary>
    /// 解析一条 "#include"（编译热路径：不做磁盘 I/O）。
    /// </summary>
    public IncludeResolution ResolveInclude(
        ReadOnlySpan<char> includePath,
        ReadOnlySpan<char> includingFilePhysicalPath,
        bool verifyOnDisk = false)
        => IncludeResolver.Resolve(IndexForDocument(includingFilePhysicalPath), includePath, includingFilePhysicalPath, verifyOnDisk);

    /// <summary>
    /// 契约入口：把虚拟 include 路径解析成物理路径。
    /// </summary>
    /// <returns>成功时为 true；<paramref name="physicalPath"/> 不产生分配（字符串来自索引内部）。</returns>
    public bool TryResolveVirtualPath(
        ReadOnlySpan<char> virtualIncludePath,
        ReadOnlySpan<char> includingFilePhysicalPath,
        out string? physicalPath,
        out IncludeRuleKind rule)
    {
        var resolution = ResolveInclude(virtualIncludePath, includingFilePhysicalPath);
        physicalPath = resolution.PhysicalPath;
        rule = resolution.Rule;
        return resolution.Found;
    }

    /// <summary>「文档路径 → 该文档所属工程的索引」的单槽备忘（整体替换，读者只见新旧之一）。</summary>
    private sealed record DocumentIndex(string Path, VfsIndex Index);

    private long Advance<TState>(Func<ContextSnapshot, TState, ContextSnapshot> factory, TState state)
    {
        while (true)
        {
            var current = Volatile.Read(ref _snapshot);
            var next = factory(current, state);
            if (Interlocked.CompareExchange(ref _snapshot, next, current) == current)
            {
                return next.Version;
            }
        }
    }
}
