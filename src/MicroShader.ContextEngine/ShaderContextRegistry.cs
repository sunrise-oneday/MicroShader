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
    private ContextSnapshot _snapshot;

    public ShaderContextRegistry(VfsIndex? initial = null)
    {
        _snapshot = new ContextSnapshot(initial ?? VfsIndex.Empty, DynamicShaderContext.Empty, 0);
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
        => IncludeResolver.Resolve(Current.Index, includePath, includingFilePhysicalPath, verifyOnDisk);

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
