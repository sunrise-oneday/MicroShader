using System.Text.Json;

namespace MicroShader.ContextEngine;

/// <summary>读取 "Packages/manifest.json" 与 "Packages/packages-lock.json"。</summary>
public static class PackageManifestReader
{
    public static PackageManifest Read(UnityProjectLayout layout, List<ContextHealthIssue>? issues = null)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var locked = new Dictionary<string, PackageLockEntry>(StringComparer.OrdinalIgnoreCase);

        var manifestValid = TryReadManifest(layout.ManifestPath, declared, out var manifestError);
        var lockValid = TryReadLock(layout.LockFilePath, locked, out var lockError);

        if (!manifestValid)
        {
            // manifest 破损不是致命错误：包来源的第 ①②④⑤ 类都还能从磁盘扫出来，
            // 只有第 ③ 类（file: 本地包）会丢失。
            issues?.Add(new ContextHealthIssue
            {
                Level = ContextHealthLevel.Warning,
                Code = ContextEngineDiagnosticCodes.MalformedManifest,
                Message = "Packages/manifest.json 不可用，已降级为按目录名扫盘推导包拓扑。" + (manifestError is null ? string.Empty : " " + manifestError),
                Path = layout.ManifestPath,
            });
        }

        if (!lockValid && File.Exists(VfsPath.ToNative(layout.LockFilePath)))
        {
            issues?.Add(new ContextHealthIssue
            {
                Level = ContextHealthLevel.Info,
                Code = ContextEngineDiagnosticCodes.MissingLockFile,
                Message = "Packages/packages-lock.json 不可用，包来源判定降级为目录名推导（git 包会被当作 registry 包）。" + (lockError is null ? string.Empty : " " + lockError),
                Path = layout.LockFilePath,
            });
        }

        return new PackageManifest
        {
            DeclaredDependencies = declared,
            LockedDependencies = locked,
            ManifestValid = manifestValid,
            LockValid = lockValid,
            ManifestError = manifestError,
            LockError = lockError,
        };
    }

    private static bool TryReadManifest(string path, Dictionary<string, string> into, out string? error)
    {
        error = null;
        var native = VfsPath.ToNative(path);
        if (!File.Exists(native))
        {
            error = "文件不存在。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(native));
            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                error = "缺少 dependencies 对象。";
                return false;
            }

            foreach (var property in dependencies.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    into[property.Name] = property.Value.GetString() ?? string.Empty;
                }
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryReadLock(string path, Dictionary<string, PackageLockEntry> into, out string? error)
    {
        error = null;
        var native = VfsPath.ToNative(path);
        if (!File.Exists(native))
        {
            error = "文件不存在。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(native));
            if (!document.RootElement.TryGetProperty("dependencies", out var dependencies)
                || dependencies.ValueKind != JsonValueKind.Object)
            {
                error = "缺少 dependencies 对象。";
                return false;
            }

            foreach (var property in dependencies.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var entry = property.Value;
                into[property.Name] = new PackageLockEntry(
                    property.Name,
                    GetString(entry, "version") ?? string.Empty,
                    GetString(entry, "source") ?? string.Empty,
                    GetString(entry, "url"),
                    GetString(entry, "hash"));
            }

            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
