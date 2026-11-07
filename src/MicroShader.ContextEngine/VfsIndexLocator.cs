namespace MicroShader.ContextEngine;

/// <summary>
/// 按「文件自己所在的 Unity 工程」提供 VFS 索引。
/// </summary>
/// <remarks>
/// "为什么需要它"：工程根不能是全局单值 —— 打开一个「装着多个 Unity 工程的父目录」时，
/// 全局那一个根要么定位不到（空索引：所有 "Packages/..." 都报「不在当前工程的包拓扑中」），
/// 要么定到相邻工程上（拓扑与真相不符）。而「这个文件属于哪个工程」从文件自己的路径向上找
/// 就必然知道，不需要用户声明任何东西。
/// "成本"：向上找根只在首次见到某个目录时发生，之后是字典命中；每个新工程的索引只构建一次。
/// "与主索引的关系"：启动时由 "--project" / 当前目录定的那个工程仍叫主索引 —— 命中主工程
/// 时直接返回它（它可能被整体替换过，不能另建一份），只有「文件属于别的工程」才另建。
/// </remarks>
public sealed class VfsIndexLocator
{
    /// <summary>工程根标记文件（相对工程根，正斜杠）。</summary>
    private const string ProjectMarker = "ProjectSettings/ProjectVersion.txt";

    /// <summary>同时记住的工程数上限（窗口里同时打开的工程通常 1~2 个）。</summary>
    private const int MaxCachedProjects = 4;

    /// <summary>目录 → 工程根 的缓存上限；满了整体清空（路径查找本身很便宜）。</summary>
    private const int MaxCachedDirectories = 512;

    private readonly Lock _sync = new();

    /// <summary>工程根 → 索引。写时整体替换（查询路径零锁，见 <see cref="ForFile"/>）。</summary>
    private Dictionary<string, VfsIndex> _indexByRoot = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>目录 → 所属工程根（找不到记为 null）。写时整体替换。</summary>
    private Dictionary<string, string?> _rootByDirectory = new(StringComparer.OrdinalIgnoreCase);

    private readonly Action<string>? _onWarn;

    private VfsIndex _primary;

    public VfsIndexLocator(VfsIndex? primary = null, string? primaryRoot = null, Action<string>? onWarn = null)
    {
        _primary = primary ?? VfsIndex.Empty;
        PrimaryRoot = VfsPath.Normalize(primaryRoot);
        _onWarn = onWarn;
    }

    /// <summary>主索引（启动时定位到的工程；未定位到时为空索引）。</summary>
    public VfsIndex Primary => Volatile.Read(ref _primary);

    /// <summary>主工程根（归一化：正斜杠、无尾分隔符）；未定位到时为空串。</summary>
    public string PrimaryRoot { get; }

    /// <summary>替换主索引（VFS 变化时与注册表同步更换）。</summary>
    public void ReplacePrimary(VfsIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        Volatile.Write(ref _primary, index);
    }

    /// <summary>
    /// 该文件所属工程的索引；定位不到工程（游离文件）或恰是主工程时返回主索引。
    /// </summary>
    public VfsIndex ForFile(string? physicalFilePath)
    {
        var primary = Primary;
        if (string.IsNullOrEmpty(physicalFilePath))
        {
            return primary;
        }

        var root = RootFor(physicalFilePath);
        if (root is null || string.Equals(root, PrimaryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return primary;
        }

        // 快路径：读一次快照（写侧整体替换，因此这里零锁）。命中即返回。
        if (Volatile.Read(ref _indexByRoot).TryGetValue(root, out var cached))
        {
            return cached;
        }

        // 构建放在锁外：首次遇到一个新工程要扫包（与启动建索引同量级），
        // 不该让其它文档的解析被挡住。并发重复构建最多发生一次，结果等价。
        var index = Build(root);

        lock (_sync)
        {
            var current = Volatile.Read(ref _indexByRoot);
            var next = current.Count >= MaxCachedProjects
                ? new Dictionary<string, VfsIndex>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, VfsIndex>(current, StringComparer.OrdinalIgnoreCase);

            next[root] = index;
            Volatile.Write(ref _indexByRoot, next);
            return next[root];
        }
    }

    /// <summary>
    /// 该文件所属的 Unity 工程根（归一化）；找不到返回 null。
    /// </summary>
    public string? RootFor(string? physicalFilePath)
    {
        if (string.IsNullOrEmpty(physicalFilePath))
        {
            return null;
        }

        var normalized = VfsPath.Normalize(physicalFilePath);
        var slash = normalized.LastIndexOf('/');
        if (slash <= 0)
        {
            return null;
        }

        var directory = normalized[..slash];

        // 快路径：读一次快照（写侧整体替换），命中即返回 —— 查询路径零锁。
        var snapshot = Volatile.Read(ref _rootByDirectory);
        if (snapshot.TryGetValue(directory, out var known))
        {
            return known;
        }

        // 慢路径：向上逐个试探（有磁盘 I/O），整条链一次补齐后才写回快照。
        var probe = directory;
        List<string>? visited = null;
        string? root = null;

        while (probe.Length > 0)
        {
            if (snapshot.TryGetValue(probe, out var cached))
            {
                root = cached;
                break;
            }

            // 磁盘探测放在锁外：这是唯一会碰到文件系统的地方，绝不能挡住别人的查询。
            if (File.Exists(probe + "/" + ProjectMarker))
            {
                root = probe;
                break;
            }

            (visited ??= []).Add(probe);

            var parentSlash = probe.LastIndexOf('/');
            if (parentSlash <= 0)
            {
                break;
            }

            probe = probe[..parentSlash];
        }

        RememberChain(visited, root);
        if (root is not null)
        {
            Remember(root, root);
        }

        return root;
    }

    /// <summary>
    /// 把「这一趟走过的目录」一次性写回快照（整条链映射到同一个结论）。
    /// 只做一次复制，避免逐条写入导致的重复拷贝。
    /// </summary>
    private void RememberChain(List<string>? visited, string? root)
    {
        if (visited is null || visited.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            var current = Volatile.Read(ref _rootByDirectory);
            var next = current.Count + visited.Count >= MaxCachedDirectories
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(current, StringComparer.OrdinalIgnoreCase);

            foreach (var directory in visited)
            {
                next[directory] = root;
            }

            Volatile.Write(ref _rootByDirectory, next);
        }
    }

    private void Remember(string directory, string? root)
    {
        lock (_sync)
        {
            var current = Volatile.Read(ref _rootByDirectory);
            var next = current.Count >= MaxCachedDirectories
                ? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string?>(current, StringComparer.OrdinalIgnoreCase);

            next[directory] = root;
            Volatile.Write(ref _rootByDirectory, next);
        }
    }

    private VfsIndex Build(string root)
    {
        try
        {
            return VfsIndexBuilder.Build(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            // 索引坏了不该让诊断停摆：退回主索引（最坏情况与修复前一致）。
            _onWarn?.Invoke($"工程 '{root}' 的 VFS 索引构建失败，已退回主索引：{ex.Message}");
            return Primary;
        }
    }
}
