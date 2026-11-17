namespace MicroShader.ContextEngine;

/// <summary>"packages-lock.json" 里一条被锁定的依赖。</summary>
public sealed record PackageLockEntry(string Name, string Version, string Source, string? Url, string? Hash)
{
    /// <summary>"source" 是否为 git。</summary>
    public bool IsGit => string.Equals(Source, "git", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 是否为工程内联/本地包。
    /// </summary>
    /// <remarks>
    /// 本机实测："Packages/com.farlocus.locus" 在 lock 里的
    /// "source" 是 "embedded" 而 "version" 是 "file:com.farlocus.locus"。
    /// 也就是说 "file:" 本地包"不一定"标为 "local"，两种都算。
    /// </remarks>
    public bool IsEmbeddedOrLocal =>
        string.Equals(Source, "embedded", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Source, "local", StringComparison.OrdinalIgnoreCase)
        || Version.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// "Packages/manifest.json" + "Packages/packages-lock.json" 的容错读取结果。
/// </summary>
/// <remarks>
/// 两份文件都是可选增强：任何一份破损都不应让 VFS 整体失败，而是降级为「按目录名扫盘推导包名」。
/// </remarks>
public sealed class PackageManifest
{
    /// <summary>manifest 的 "dependencies"：包名 → 版本声明（可能是 "1.2.3" / "file:.." / git URL）。</summary>
    public IReadOnlyDictionary<string, string> DeclaredDependencies { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>lock 的 "dependencies"：包名 → 锁定记录。</summary>
    public IReadOnlyDictionary<string, PackageLockEntry> LockedDependencies { get; init; } =
        new Dictionary<string, PackageLockEntry>(StringComparer.OrdinalIgnoreCase);

    public bool ManifestValid { get; init; }

    public bool LockValid { get; init; }

    public string? ManifestError { get; init; }

    public string? LockError { get; init; }
}
