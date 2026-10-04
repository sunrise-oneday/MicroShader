using System.Text.Json;

namespace MicroShader.ContextEngine;

/// <summary>索引构建选项（主要用于测试注入）。</summary>
public sealed record VfsIndexBuildOptions
{
    /// <summary>是否尝试定位 Unity 编辑器安装目录（影响包来源 ⑤ 与规则 4）。</summary>
    public bool LocateUnityInstall { get; init; } = true;

    /// <summary>覆盖 Unity Hub 配置文件路径（测试用）。</summary>
    public string? HubConfigPath { get; init; }

    /// <summary>直接指定编辑器安装根，跳过三层定位（测试/桥接在线时使用）。</summary>
    public string? ForcedUnityInstallRoot { get; init; }
}

/// <summary>
/// 把「一个 Unity 工程目录」扫成 <see cref="VfsIndex"/> 快照。
/// </summary>
/// <remarks>
/// "包来源 5 类与优先级"（数字越小越优先，后者覆盖前者）：
/// <list type="number">
///   <item>"Packages/" 实体目录（Embedded）—— 开发者可能魔改过 URP，必须以它为准。</item>
///   <item>"manifest.json" 的 "file:" 本地包。</item>
///   <item>git 包（lock 的 "source: git"）。</item>
///   <item>"Library/PackageCache/"（registry 包）。</item>
///   <item>"&lt;Editor&gt;/Data/Resources/PackageManager/BuiltInPackages/"。</item>
/// </list>
/// 
/// 落地方式是"按优先级从低到高插入字典"（后写覆盖先写），而不是「扫盘后丢弃同名包」——
/// Unity 本身是解析期择优，不是扫盘后择优。本机实测 BuiltInPackages 与 PackageCache 都含
/// "com.unity.render-pipelines.core"，正确的赢家是 PackageCache。
/// 
/// 
/// "不允许用 "@版本号" 正则识别包名"：PackageCache 里 git 包是 "name@ec0f32df12" 形式。
/// 因此从目录名推导时一律按"最后一个 "@"" 切分，并优先采用 lock 的 "source" 字段。
/// 
/// </remarks>
public static class VfsIndexBuilder
{
    public static VfsIndex Build(string projectRoot, long generation = 1, VfsIndexBuildOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        options ??= new VfsIndexBuildOptions();

        var layout = UnityProjectLayout.FromRoot(projectRoot);
        var issues = new List<ContextHealthIssue>();

        var installation = ResolveInstallation(layout, options, issues);
        var manifest = PackageManifestReader.Read(layout, issues);

        var packages = new Dictionary<string, PackageEntry>(StringComparer.OrdinalIgnoreCase);

        // ⑤ BuiltInPackages —— 最低优先级，先插入。
        ScanBuiltInPackages(installation, packages, issues);

        // ② / ④ PackageCache（registry + git）—— 覆盖 BuiltIn 同名包。
        ScanPackageCache(layout, manifest, packages, issues);

        // ③ file: 本地包 —— 覆盖 PackageCache。
        ResolveFilePackages(layout, manifest, packages, issues);

        // ① Packages/ 实体目录 —— 最高优先级。
        ScanEmbeddedPackages(layout, packages, issues);

        var cgIncludes = EnumerateCGIncludes(installation);

        return new VfsIndex(layout, installation, manifest, packages, cgIncludes, issues, generation);
    }

    private static UnityInstallation? ResolveInstallation(
        UnityProjectLayout layout,
        VfsIndexBuildOptions options,
        List<ContextHealthIssue> issues)
    {
        if (!string.IsNullOrEmpty(options.ForcedUnityInstallRoot))
        {
            var root = VfsPath.Normalize(options.ForcedUnityInstallRoot);
            return new UnityInstallation
            {
                InstallRoot = root,
                EditorDataRoot = VfsPath.Combine(root, "Editor/Data"),
                EditorExecutable = VfsPath.Combine(root, "Editor/Unity.exe"),
                LocatedBy = UnityInstallLocatorKind.Unknown,
            };
        }

        return options.LocateUnityInstall
            ? UnityInstallLocator.Locate(layout, issues, options.HubConfigPath)
            : null;
    }

    private static void ScanBuiltInPackages(
        UnityInstallation? installation,
        Dictionary<string, PackageEntry> packages,
        List<ContextHealthIssue> issues)
    {
        if (installation is not { } install || !install.HasBuiltInPackages)
        {
            return;
        }

        foreach (var directory in SafeEnumerateDirectories(install.BuiltInPackagesRoot))
        {
            var name = VfsPath.GetFileName(directory).ToString();
            if (name.Length == 0 || name[0] == '.')
            {
                continue;
            }

            packages[name] = new PackageEntry
            {
                Name = name,
                Source = PackageSourceKind.BuiltIn,
                PhysicalRoot = directory,
                ContainerRoot = install.BuiltInPackagesRoot,
            };
        }
    }

    private static void ScanPackageCache(
        UnityProjectLayout layout,
        PackageManifest manifest,
        Dictionary<string, PackageEntry> packages,
        List<ContextHealthIssue> issues)
    {
        foreach (var directory in SafeEnumerateDirectories(layout.PackageCacheDirectory))
        {
            var directoryName = VfsPath.GetFileName(directory).ToString();
            if (directoryName.Length == 0 || directoryName[0] == '.')
            {
                continue;
            }

            var (name, version) = SplitAtLastAt(directoryName);
            if (name.Length == 0)
            {
                issues.Add(new ContextHealthIssue
                {
                    Level = ContextHealthLevel.Info,
                    Code = ContextEngineDiagnosticCodes.UnrecognizedNamePackageDirectory,
                    Message = $"PackageCache 目录名 '{directoryName}' 无法推导出包名，已按原样登记。",
                    Path = directory,
                });
                name = directoryName;
            }

            // lock 的 source 字段是权威判定：git 包与 registry 包在磁盘上目录形态完全一样。
            var lockEntry = manifest.LockedDependencies.TryGetValue(name, out var locked) ? locked : null;

            packages[name] = new PackageEntry
            {
                Name = name,
                VersionSpec = manifest.DeclaredDependencies.TryGetValue(name, out var declared) ? declared : string.Empty,
                LockVersion = lockEntry?.Version ?? version,
                Source = lockEntry is { IsGit: true } ? PackageSourceKind.Git : PackageSourceKind.PackageCache,
                PhysicalRoot = directory,
                ContainerRoot = layout.PackageCacheDirectory,
            };
        }
    }

    private static void ResolveFilePackages(
        UnityProjectLayout layout,
        PackageManifest manifest,
        Dictionary<string, PackageEntry> packages,
        List<ContextHealthIssue> issues)
    {
        // 收集所有「file:」型依赖：manifest 的声明里带 file: 的，以及 lock 里 source=local/embedded 的。
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, spec) in manifest.DeclaredDependencies)
        {
            if (spec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                candidates[name] = spec[5..];
            }
        }

        foreach (var (name, locked) in manifest.LockedDependencies)
        {
            if (!locked.IsEmbeddedOrLocal)
            {
                continue;
            }

            var relative = locked.Version.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? locked.Version[5..]
                : name;

            candidates.TryAdd(name, relative);
        }

        foreach (var (name, relative) in candidates)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                continue;
            }

            // Unity 官方文档对 file: 的相对基准自相矛盾（Packages/ 还是工程根），因此两个都试。
            // 顺序取「先 Packages/ 后工程根」：本机实测 Packages/com.farlocus.locus 走的是前者。
            var viaPackages = VfsPath.Combine(layout.PackagesDirectory, relative);
            var viaRoot = VfsPath.Combine(layout.ProjectRoot, relative);

            var physical = Directory.Exists(VfsPath.ToNative(viaPackages))
                ? viaPackages
                : Directory.Exists(VfsPath.ToNative(viaRoot))
                    ? viaRoot
                    : null;

            if (physical is null)
            {
                issues.Add(new ContextHealthIssue
                {
                    Level = ContextHealthLevel.Info,
                    Code = ContextEngineDiagnosticCodes.PackageDirectoryMissing,
                    Message = $"manifest/lock 声明了本地包 '{name}'（file:{relative}），但磁盘上不存在对应目录。",
                    Path = viaPackages,
                });
                continue;
            }

            packages[name] = new PackageEntry
            {
                Name = name,
                VersionSpec = manifest.DeclaredDependencies.TryGetValue(name, out var declared) ? declared : $"file:{relative}",
                LockVersion = manifest.LockedDependencies.TryGetValue(name, out var locked) ? locked.Version : string.Empty,
                Source = PackageSourceKind.LocalFile,
                PhysicalRoot = physical,
                ContainerRoot = layout.PackagesDirectory,
            };
        }
    }

    private static void ScanEmbeddedPackages(
        UnityProjectLayout layout,
        Dictionary<string, PackageEntry> packages,
        List<ContextHealthIssue> issues)
    {
        foreach (var directory in SafeEnumerateDirectories(layout.PackagesDirectory))
        {
            var directoryName = VfsPath.GetFileName(directory).ToString();
            if (directoryName.Length == 0 || directoryName[0] == '.')
            {
                continue;
            }

            // package.json 的 name 是权威包名（目录名在理论上可以不同）。
            var name = ReadPackageJsonName(directory) ?? directoryName;

            packages[name] = new PackageEntry
            {
                Name = name,
                VersionSpec = "file:" + directoryName,
                Source = PackageSourceKind.Embedded,
                PhysicalRoot = directory,
                ContainerRoot = layout.PackagesDirectory,
            };
        }
    }

    private static HashSet<string> EnumerateCGIncludes(UnityInstallation? installation)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (installation is not { } install || !install.HasCGIncludes)
        {
            return set;
        }

        // 只枚举顶层：规则 4 的形态就是 <CGIncludes>/<裸名>，裸名不含分隔符。
        foreach (var file in SafeEnumerateFiles(install.CGIncludesRoot))
        {
            set.Add(VfsPath.GetFileName(file).ToString());
        }

        return set;
    }

    private static string? ReadPackageJsonName(string packageDirectory)
    {
        var path = VfsPath.ToNative(VfsPath.Combine(packageDirectory, "package.json"));
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (document.RootElement.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                var value = name.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        return null;
    }

    /// <summary>按"最后一个" "@" 切分包目录名（git 短 hash 与语义化版本都适用）。</summary>
    internal static (string Name, string Version) SplitAtLastAt(string directoryName)
    {
        var index = directoryName.LastIndexOf('@');
        if (index <= 0)
        {
            return (directoryName, string.Empty);
        }

        return (directoryName[..index], directoryName[(index + 1)..]);
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root)
    {
        var native = VfsPath.ToNative(root);
        if (!Directory.Exists(native))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateDirectories(native).Select(VfsPath.Normalize).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var native = VfsPath.ToNative(root);
        if (!Directory.Exists(native))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(native).Select(VfsPath.Normalize).ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
