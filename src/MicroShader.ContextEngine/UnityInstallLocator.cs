using System.Text.Json;

namespace MicroShader.ContextEngine;

/// <summary>
/// 定位 Unity 编辑器安装目录。按优先级：Unity Hub 配置 → Library/EditorInstance.json → 注册表。
/// </summary>
/// <remarks>
/// 找不到安装目录并不是致命错误：只影响"规则 4"（裸名兜底到 "CGIncludes/"）与"包来源 ⑤"（BuiltInPackages）。
/// 工程内 "Assets/" 与 "Packages/" 的解析完全不依赖它，因此必须优雅降级而不是整体失败。
/// </remarks>
public static class UnityInstallLocator
{
    /// <summary>按优先级定位；全部失败时返回 null 并填充 <paramref name="issues"/>。</summary>
    public static UnityInstallation? Locate(
        UnityProjectLayout layout,
        List<ContextHealthIssue>? issues = null,
        string? hubConfigPath = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (TryFromHub(layout.UnityVersion, hubConfigPath, out var installation))
        {
            return installation;
        }

        if (TryFromEditorInstance(layout, out installation))
        {
            return installation;
        }

        if (TryFromRegistry(layout.UnityVersion, out installation))
        {
            return installation;
        }

        issues?.Add(new ContextHealthIssue
        {
            Level = ContextHealthLevel.Warning,
            Code = ContextEngineDiagnosticCodes.UnityInstallNotFound,
            Message = layout.UnityVersion is null
                ? "未能定位 Unity 编辑器安装目录：ProjectVersion.txt 里没有 m_EditorVersion，且 Hub/EditorInstance/注册表均无匹配。"
                : $"未能定位 Unity {layout.UnityVersion} 的安装目录：Hub 配置、Library/EditorInstance.json、注册表三层全部未命中。",
            Path = layout.ProjectRoot,
        });

        return null;
    }

    private static bool TryFromHub(string? version, string? hubConfigPath, out UnityInstallation? installation)
    {
        installation = null;

        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        var editors = UnityHubEditorsReader.Read(out _, hubConfigPath);
        foreach (var editor in editors)
        {
            if (!string.Equals(editor.Version, version, StringComparison.Ordinal) || editor.InstallRoot is null)
            {
                continue;
            }

            installation = Create(editor.InstallRoot, editor.Executable, UnityInstallLocatorKind.UnityHub);
            return true;
        }

        return false;
    }

    private static bool TryFromEditorInstance(UnityProjectLayout layout, out UnityInstallation? installation)
    {
        installation = null;

        var path = VfsPath.ToNative(VfsPath.Combine(layout.LibraryDirectory, "EditorInstance.json"));
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            // 编辑器保证原子写，但仍做最小容错：空文件（编辑器已退出且未清理）视为不可用。
            using var stream = File.OpenRead(path);
            if (stream.Length == 0)
            {
                return false;
            }

            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("app_contents_path", out var contents)
                || contents.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var editorDataRoot = contents.GetString();
            if (string.IsNullOrEmpty(editorDataRoot))
            {
                return false;
            }

            // app_contents_path = <install>/Editor/Data
            var installRoot = VfsPath.GetDirectoryName(VfsPath.GetDirectoryName(VfsPath.Normalize(editorDataRoot)));
            var executable = VfsPath.Combine(installRoot, "Editor/Unity.exe");
            installation = Create(installRoot, executable, UnityInstallLocatorKind.EditorInstanceJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static bool TryFromRegistry(string? version, out UnityInstallation? installation)
    {
        installation = null;

        if (string.IsNullOrEmpty(version))
        {
            return false;
        }

        // 键名带完整版本号，例如 "Unity 2021.3.45f2c1"；本机实测 HKCU 与 HKLM 上可能各有一部分。
        var subKey = "Software\\Unity Technologies\\Installer\\Unity " + version;

        foreach (var root in (ReadOnlySpan<uint>)[WindowsRegistry.HkeyCurrentUser, WindowsRegistry.HkeyLocalMachine])
        {
            var location = WindowsRegistry.ReadString(root, subKey, "Location x64");
            if (string.IsNullOrEmpty(location))
            {
                continue;
            }

            var installRoot = VfsPath.Normalize(location);
            installation = Create(installRoot, VfsPath.Combine(installRoot, "Editor/Unity.exe"), UnityInstallLocatorKind.Registry);
            return true;
        }

        return false;
    }

    private static UnityInstallation Create(string installRoot, string? executable, UnityInstallLocatorKind kind)
        => new()
        {
            InstallRoot = VfsPath.Normalize(installRoot),
            EditorDataRoot = VfsPath.Combine(installRoot, "Editor/Data"),
            EditorExecutable = executable,
            LocatedBy = kind,
        };
}
