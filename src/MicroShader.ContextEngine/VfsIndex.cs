namespace MicroShader.ContextEngine;

/// <summary>
/// VFS 拓扑索引的"不可变快照"。
/// </summary>
/// <remarks>
/// 查询路径（"#include" 解析、Jump/定义跳转）的全部要求是「零锁、零文件 I/O、零堆分配」，
/// 因此索引构建阶段必须把一切都提前物化：
/// <list type="bullet">
///   <item>包名 → 物理根：一个大小写不敏感的字典（<see cref="Dictionary{TKey,TValue}"/> 支持 "ReadOnlySpan&lt;char&gt;" 查询，不分配）。</item>
///   <item>规则 4 的 "CGIncludes/"："目录级"枚举成文件名集合，避免查询时做目录探测 I/O。</item>
/// </list>
/// 快照一旦发布就不再修改；更新通过整体替换（见 <see cref="ShaderContextRegistry"/>）。
/// </remarks>
public sealed class VfsIndex
{
    private readonly Dictionary<string, PackageEntry> _packages;
    private readonly HashSet<string> _cgIncludeFileNames;

    // .NET 9+ 的 alternative lookup：用 ReadOnlySpan<char> 直接查询 string 键的字典/集合，
    // 命中时零分配。这是替代「把 span 转成 string 再查」的唯一正确做法。
    private readonly Dictionary<string, PackageEntry>.AlternateLookup<ReadOnlySpan<char>> _packageLookup;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _cgIncludeLookup;

    internal VfsIndex(
        UnityProjectLayout layout,
        UnityInstallation? installation,
        PackageManifest manifest,
        Dictionary<string, PackageEntry> packages,
        HashSet<string> cgIncludeFileNames,
        IReadOnlyList<ContextHealthIssue> issues,
        long generation)
    {
        Layout = layout;
        Installation = installation;
        Manifest = manifest;
        _packages = packages;
        _cgIncludeFileNames = cgIncludeFileNames;
        _packageLookup = packages.GetAlternateLookup<ReadOnlySpan<char>>();
        _cgIncludeLookup = cgIncludeFileNames.GetAlternateLookup<ReadOnlySpan<char>>();
        Issues = issues;
        Generation = generation;
    }

    /// <summary>空索引（尚未初始化任何工程时使用）。</summary>
    public static VfsIndex Empty { get; } = new(
        UnityProjectLayout.FromRoot(VfsPath.Normalize(Path.GetTempPath())),
        null,
        new PackageManifest(),
        new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        [],
        0);

    public UnityProjectLayout Layout { get; }

    public UnityInstallation? Installation { get; }

    public PackageManifest Manifest { get; }

    /// <summary>索引构建期发现的降级问题（不是用户着色器错误）。</summary>
    public IReadOnlyList<ContextHealthIssue> Issues { get; }

    /// <summary>快照代号；每次重建 +1。</summary>
    public long Generation { get; }

    public int PackageCount => _packages.Count;

    /// <summary>包名 → 物理根（只读视图）。</summary>
    public IReadOnlyDictionary<string, PackageEntry> Packages => _packages;

    /// <summary>已识别的 "CGIncludes/" 文件名集合（大小写不敏感）。</summary>
    public IReadOnlyCollection<string> CGIncludeFileNames => _cgIncludeFileNames;

    /// <summary>manifest 或 lock 破损导致的降级标记。</summary>
    public bool IsDegraded => !Manifest.ManifestValid || Issues.Count > 0;

    // 以下两个查询方法签名刻意使用 ReadOnlySpan<char>（走 alternate lookup）：
    // 上游给出的 include 路径本来就是 span 切片，命中时一次分配都不产生。

    /// <summary>按包名查询物理根。</summary>
    public bool TryGetPackage(ReadOnlySpan<char> packageName, out PackageEntry? entry)
    {
        if (_packageLookup.TryGetValue(packageName, out var found))
        {
            entry = found;
            return true;
        }

        entry = null;
        return false;
    }

    /// <summary>判断某个裸文件名是否存在于编辑器 "CGIncludes/" 目录（规则 4 的目录级判定）。</summary>
    public bool IsKnownCGInclude(ReadOnlySpan<char> fileName) => _cgIncludeLookup.Contains(fileName);

    // 物理根（归一化）→ 包名。惰性构建；并发下可能重复构建一次，但发布的是一份完整数组/字典，赋值原子，无害。
    private Dictionary<string, string>? _rootToPackage;

    private Dictionary<string, string> RootToPackage => _rootToPackage ??= BuildRootToPackage();

    private Dictionary<string, string> BuildRootToPackage()
    {
        var map = new Dictionary<string, string>(_packages.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in _packages)
        {
            var root = pair.Value.PhysicalRoot;
            if (!string.IsNullOrEmpty(root))
            {
                map[root] = pair.Key;
            }
        }

        return map;
    }

    /// <summary>
    /// 按物理路径反查它属于哪个包：沿路径的"祖先目录链"由深到浅回落，首个命中即最长（最具体）的包根。
    /// </summary>
    /// <remarks>
    /// 用「路径树上的祖先回退 + 哈希查根」取代原来对 "Packages" 的 "O(包数)" 线性扫描
    /// （原扫描每轮还要 "Normalize()" 出一个临时字符串）。这里是 "O(路径深度)" 次字典查询，
    /// 命中时零分配 —— 典型 URP 工程包数 40~80、路径深度 8~14，即 14 次哈希对 80 次字符串比较 + 分配。
    /// </remarks>
    public bool TryMatchPackageRoot(ReadOnlySpan<char> normalizedPath, out string packageName, out int rootLength)
    {
        packageName = string.Empty;
        rootLength = 0;

        if (_packages.Count == 0 || normalizedPath.IsEmpty)
        {
            return false;
        }

        var lookup = RootToPackage.GetAlternateLookup<ReadOnlySpan<char>>();
        var end = normalizedPath.Length;

        while (end > 0)
        {
            if (normalizedPath[end - 1] == '/')
            {
                end--;
                continue;
            }

            var slash = normalizedPath[..end].LastIndexOf('/');
            if (slash <= 0)
            {
                break;
            }

            // 候选 = 该祖先目录；要求目标比它更深一层（存在相对路径），与 VfsPath.IsUnder 的边界语义一致。
            if (slash + 1 < normalizedPath.Length && lookup.TryGetValue(normalizedPath[..slash], out var name))
            {
                packageName = name;
                rootLength = slash;
                return true;
            }

            end = slash;
        }

        return false;
    }

    public bool HasCGIncludesRoot => Installation is { } install && install.HasCGIncludes;
}
