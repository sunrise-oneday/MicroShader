using System.Text;

namespace MicroShader.ContextEngine;

/// <summary>
/// Unity 工程布局：把「工程根」翻译成所有已知路径。
/// </summary>
/// <remarks>
/// 调用方拿到的通常是一个 .shader 文件路径（可能来自磁盘、也可能来自 LSP 的 URI），
/// 因此需要从任意深层文件反推工程根。判定锚点是 "ProjectSettings/ProjectVersion.txt"，
/// 而不是 "Assets/" 或 "Packages/" —— 只有 ProjectVersion.txt 是每个 Unity 工程必有的。
/// </remarks>
public sealed class UnityProjectLayout
{
    private UnityProjectLayout(string projectRoot)
    {
        ProjectRoot = projectRoot;
    }

    /// <summary>工程根绝对路径（归一化，正斜杠）。</summary>
    public string ProjectRoot { get; }

    public string PackagesDirectory => VfsPath.Combine(ProjectRoot, "Packages");

    public string AssetsDirectory => VfsPath.Combine(ProjectRoot, "Assets");

    public string LibraryDirectory => VfsPath.Combine(ProjectRoot, "Library");

    public string PackageCacheDirectory => VfsPath.Combine(LibraryDirectory, "PackageCache");

    /// <summary>
    /// 端点发现目录。"不是" "Temp/" —— Unity 官方把 Temp/ 定义为关闭编辑器即清空。
    /// </summary>
    public string MicroShaderDirectory => VfsPath.Combine(LibraryDirectory, "MicroShader");

    public string EndpointFilePath => VfsPath.Combine(MicroShaderDirectory, "endpoint.json");

    public string ManifestPath => VfsPath.Combine(PackagesDirectory, "manifest.json");

    public string LockFilePath => VfsPath.Combine(PackagesDirectory, "packages-lock.json");

    public string ProjectVersionPath => VfsPath.Combine(ProjectRoot, "ProjectSettings/ProjectVersion.txt");

    /// <summary>
    /// Unity 判活锁文件。注意："Library/" 可能被 Unity 重建甚至删除，因此读取端必须容忍它消失。
    /// </summary>
    public string UnityLockFilePath => VfsPath.Combine(ProjectRoot, "Temp/UnityLockfile");

    /// <summary>"m_EditorVersion"，例如 "2022.3.62f3c1"。</summary>
    public string? UnityVersion { get; private init; }

    /// <summary>"m_EditorVersionWithRevision"，例如 "2022.3.62f3c1 (1623fc0bbb97)"。</summary>
    public string? UnityVersionWithRevision { get; private init; }

    /// <summary>由已知工程根构造（不校验磁盘）。</summary>
    public static UnityProjectLayout FromRoot(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        var normalized = VfsPath.Normalize(projectRoot);
        var layout = new UnityProjectLayout(normalized);
        return layout.ReadVersionFile();
    }

    /// <summary>
    /// 从工程内任意路径向上找工程根。
    /// </summary>
    /// <param name="anyPathInsideProject">工程内任意文件/目录路径（可以是 .shader 文件、也可以是包内 hlsl）。</param>
    /// <param name="layout">定位成功时的布局。</param>
    /// <param name="error">失败原因。</param>
    public static bool TryLocate(string anyPathInsideProject, out UnityProjectLayout? layout, out string? error)
    {
        layout = null;
        error = null;

        if (string.IsNullOrWhiteSpace(anyPathInsideProject))
        {
            error = "路径为空，无法定位 Unity 工程根。";
            return false;
        }

        var current = VfsPath.Normalize(anyPathInsideProject);

        // 若给的是文件路径，从它的目录开始。
        if (!Directory.Exists(VfsPath.ToNative(current)))
        {
            current = VfsPath.GetDirectoryName(current);
        }

        var visited = 0;
        while (!string.IsNullOrEmpty(current) && visited++ < 64)
        {
            var candidate = VfsPath.Combine(current, "ProjectSettings/ProjectVersion.txt");
            if (File.Exists(VfsPath.ToNative(candidate)))
            {
                layout = FromRoot(current);
                return true;
            }

            var parent = VfsPath.GetDirectoryName(current);
            if (parent == current || parent.Length == 0)
            {
                break;
            }

            current = parent;
        }

        error = $"未能在 '{anyPathInsideProject}' 的任何祖先目录中找到 ProjectSettings/ProjectVersion.txt。";
        return false;
    }

    private UnityProjectLayout ReadVersionFile()
    {
        var path = VfsPath.ToNative(ProjectVersionPath);
        if (!File.Exists(path))
        {
            return this;
        }

        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (IOException)
        {
            return this;
        }
        catch (UnauthorizedAccessException)
        {
            return this;
        }

        string? version = null;
        string? withRevision = null;

        foreach (var line in text.AsSpan().EnumerateLines())
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("m_EditorVersionWithRevision:", StringComparison.Ordinal))
            {
                withRevision = trimmed[28..].Trim().ToString();
            }
            else if (trimmed.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
            {
                version = trimmed[16..].Trim().ToString();
            }
        }

        return new UnityProjectLayout(ProjectRoot)
        {
            UnityVersion = version,
            UnityVersionWithRevision = withRevision,
        };
    }
}
