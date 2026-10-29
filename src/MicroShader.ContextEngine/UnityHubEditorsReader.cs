using System.Text.Json;

namespace MicroShader.ContextEngine;

/// <summary>Unity Hub 已安装编辑器列表里的一条记录。</summary>
public sealed record UnityHubEditor(string Version, string? InstallRoot, string? Executable);

/// <summary>
/// 读取 Unity Hub 的 "%APPDATA%/UnityHub/editors-v2.json"。
/// </summary>
/// <remarks>
/// "必须大小写敏感解析。" 本机实测该文件同时存在 "preSelected" 与 "preselected" 两个仅大小写不同的键，
/// 任何大小写不敏感的解析路径都会直接失败（PowerShell 的 "ConvertFrom-Json" 报
/// 「无法转换 JSON 字符串，因为它包含具有不同大小写的键」）。
/// 因此这里用 <see cref="JsonDocument"/> 手工取值，且绝不开启
/// "PropertyNameCaseInsensitive"；同时用 <see cref="JsonDocumentOptions"/> 默认值（不允许尾随逗号、注释）。
/// 另外还必须是 "容错" 的：Hub 配置被写坏不能拖垮整个 LSP。
/// </remarks>
public static class UnityHubEditorsReader
{
    /// <summary>默认配置文件位置。</summary>
    public static string DefaultConfigPath => VfsPath.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnityHub/editors-v2.json");

    /// <summary>读取编辑器列表；文件不存在或解析失败时返回空列表。</summary>
    public static IReadOnlyList<UnityHubEditor> Read(out string? error, string? configPath = null)
    {
        error = null;
        var path = VfsPath.ToNative(configPath ?? DefaultConfigPath);

        if (!File.Exists(path))
        {
            error = $"未找到 Unity Hub 配置：{path}";
            return [];
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            return Parse(document, out error);
        }
        catch (JsonException ex)
        {
            error = $"Unity Hub 配置不是合法 JSON：{ex.Message}";
            return [];
        }
        catch (IOException ex)
        {
            error = $"读取 Unity Hub 配置失败：{ex.Message}";
            return [];
        }
    }

    internal static IReadOnlyList<UnityHubEditor> Parse(JsonDocument document, out string? error)
    {
        error = null;
        var list = new List<UnityHubEditor>();

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            error = "Unity Hub 配置缺少 data 数组。";
            return list;
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var version = GetString(entry, "version");
            if (string.IsNullOrEmpty(version))
            {
                continue;
            }

            // folderPath 是安装根；location[0] 是 Unity.exe。两者任一缺失都还能从另一个推导。
            var folderPath = GetString(entry, "folderPath");
            var executable = GetFirstArrayString(entry, "location");

            var installRoot = folderPath;
            if (string.IsNullOrEmpty(installRoot) && !string.IsNullOrEmpty(executable))
            {
                // <install>/Editor/Unity.exe -> <install>
                installRoot = VfsPath.GetDirectoryName(VfsPath.GetDirectoryName(VfsPath.Normalize(executable!)));
            }

            list.Add(new UnityHubEditor(
                version!,
                string.IsNullOrEmpty(installRoot) ? null : VfsPath.Normalize(installRoot),
                string.IsNullOrEmpty(executable) ? null : VfsPath.Normalize(executable)));
        }

        if (list.Count == 0)
        {
            error = "Unity Hub 配置中没有可用的编辑器记录。";
        }

        return list;
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? GetFirstArrayString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                return item.GetString();
            }
        }

        return null;
    }
}
