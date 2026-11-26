namespace MicroShader.ContextEngine;

/// <summary>Unity 编辑器安装目录的定位来源（按优先级排列）。</summary>
public enum UnityInstallLocatorKind : byte
{
    Unknown = 0,

    /// <summary>① Unity Hub 的 "%APPDATA%/UnityHub/editors-v2.json"。</summary>
    UnityHub = 1,

    /// <summary>② 工程内 "Library/EditorInstance.json"（编辑器正在运行时才有效）。</summary>
    EditorInstanceJson = 2,

    /// <summary>③ 注册表 "HKCU|HKLM\Software\Unity Technologies\Installer\Unity &lt;version&gt;" 的 "Location x64"。</summary>
    Registry = 3,
}

/// <summary>一次成功的 Unity 编辑器安装定位结果。</summary>
public sealed class UnityInstallation
{
    /// <summary>安装根，例如 "D:/Program Files/U3DSgame/2022.3.62f3c1"。</summary>
    public required string InstallRoot { get; init; }

    /// <summary>"&lt;InstallRoot&gt;/Editor/Data"（即 Unity 的 "applicationContentsPath"）。</summary>
    public required string EditorDataRoot { get; init; }

    /// <summary>编辑器可执行文件路径（可能未知）。</summary>
    public string? EditorExecutable { get; init; }

    /// <summary>定位来源。</summary>
    public UnityInstallLocatorKind LocatedBy { get; init; }

    /// <summary>"&lt;EditorDataRoot&gt;/CGIncludes"：规则 4 的裸名兜底目录。</summary>
    public string CGIncludesRoot => VfsPath.Combine(EditorDataRoot, "CGIncludes");

    /// <summary>"&lt;EditorDataRoot&gt;/Resources/PackageManager/BuiltInPackages"：包来源 ⑤。</summary>
    public string BuiltInPackagesRoot =>
        VfsPath.Combine(EditorDataRoot, "Resources/PackageManager/BuiltInPackages");

    /// <summary>
    /// 该目录是否存在（规则 4 的裸名兜底目录）。
    /// </summary>
    /// <remarks>
    /// "为什么必须缓存"：这两个结论被 include 解析与虚拟路径计算用到 ——
    /// 而查询热路径的契约是零磁盘 I/O。目录是否存在在一个会话里不会变，
    /// 因此首次探测后记住结论（探测本身幂等，并发下重复探测也只是同一答案）。
    /// </remarks>
    public bool HasCGIncludes => Probed(ref _cgIncludesState, CGIncludesRoot);

    public bool HasBuiltInPackages => Probed(ref _builtInPackagesState, BuiltInPackagesRoot);

    /// <summary>0 = 还没探测；1 = 存在；2 = 不存在（int 而非 bool?，保证写入原子可见）。</summary>
    private int _cgIncludesState;

    private int _builtInPackagesState;

    private static bool Probed(ref int state, string root)
    {
        var current = Volatile.Read(ref state);
        if (current != 0)
        {
            return current == 1;
        }

        var exists = Directory.Exists(VfsPath.ToNative(root));
        Volatile.Write(ref state, exists ? 1 : 2);
        return exists;
    }

    public override string ToString() => $"{InstallRoot} ({LocatedBy})";
}
