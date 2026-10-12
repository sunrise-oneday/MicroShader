using MicroShader.ContextEngine;
using MicroShader.IntelliSenseEngine;

namespace MicroShader.Server;

/// <summary>
/// 用工程的 VFS 索引把虚拟 include 路径解析成物理路径 —— <see cref="IIncludeFileSource"/> 的宿主实现。
/// </summary>
/// <remarks>
/// "依赖方向"：实现落在组合根（Server），引擎层只认接口，不会反向依赖工程布局知识。
/// "与模块 3 的关系"：模块 3 的 "VirtualTextAssembler" 也做同一件事（把 include 目标
/// 改成物理路径）。这里没有直接复用它的解析器，是为了不把「诊断装配」的内部类型泄漏到补全链路上；
/// 两边都以 <see cref="VfsIndex"/> 为唯一事实源（包根 "PhysicalRoot"），因此不会漂移。
/// </remarks>
internal sealed class VfsIncludeFileSource : IIncludeFileSource
{
    private const string PackagesPrefix = "Packages/";

    private const string AssetsPrefix = "Assets/";

    private readonly VfsIndex _index;

    private readonly string _projectRootNormalized;

    public VfsIncludeFileSource(VfsIndex index, string projectRoot)
    {
        ArgumentNullException.ThrowIfNull(index);

        _index = index;
        _projectRootNormalized = projectRoot.Replace('\\', '/').TrimEnd('/');
    }

    /// <summary>把 "Packages/&lt;包名&gt;/..." / "Assets/..." 解析成物理路径。</summary>
    public bool TryResolve(string virtualPath, out string physicalPath)
    {
        physicalPath = string.Empty;

        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            return false;
        }

        var normalized = virtualPath.Replace('\\', '/').TrimStart('/');

        if (normalized.StartsWith(PackagesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var rest = normalized[PackagesPrefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                return false;
            }

            var packageName = rest[..slash];
            if (!_index.TryGetPackage(packageName, out var entry) || entry is null)
            {
                return false;
            }

            physicalPath = entry.PhysicalRoot.TrimEnd('/') + "/" + rest[(slash + 1)..];
            return true;
        }

        if (normalized.StartsWith(AssetsPrefix, StringComparison.OrdinalIgnoreCase) && _projectRootNormalized.Length > 0)
        {
            physicalPath = _projectRootNormalized + "/" + normalized;
            return true;
        }

        // 裸名 CG include（如 "UnityCG.cginc"）：不匹配 Packages/ 或 Assets/ 前缀，
        // 但 VFS 在构建时已枚举了编辑器 CGIncludes 目录的文件名集合（规则 4）。
        // 如果裸名在这个集合里，拼出 CGIncludesRoot/<裸名> 的物理路径。
        if (normalized.IndexOf('/') < 0
            && _index.Installation is { HasCGIncludes: true } install
            && _index.IsKnownCGInclude(normalized))
        {
            physicalPath = install.CGIncludesRoot.TrimEnd('/') + "/" + normalized;
            return true;
        }

        return false;
    }

    /// <summary>相对 include：按「包含方所在目录」解析（URP 头里大量使用，如 "#include "Common.hlsl""）。</summary>
    public bool TryResolveRelative(string currentPhysicalPath, string target, out string physicalPath)
    {
        physicalPath = string.Empty;

        if (string.IsNullOrWhiteSpace(currentPhysicalPath) || string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(currentPhysicalPath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(directory, target));
        if (!File.Exists(candidate))
        {
            return false;
        }

        physicalPath = candidate;
        return true;
    }

    public bool TryRead(string physicalPath, out string text)
    {
        try
        {
            text = File.ReadAllText(physicalPath);
            return true;
        }
        catch (IOException)
        {
            text = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>写入戳用于缓存失效；取不到返回 0（0 表示「永远视为变化」，宁可重扫也不给错数据）。</summary>
    public long GetStamp(string physicalPath)
    {
        try
        {
            return File.GetLastWriteTimeUtc(physicalPath).Ticks;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>把 LSP 的 "file:///" URI 还原成物理路径（供相对 include 定位与预热日志用）。</summary>
    public static string UriToPath(string uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return string.Empty;
        }

        var text = uri;
        if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
        {
            text = text["file:///".Length..];
        }
        else if (text.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            text = text["file://".Length..];
        }

        text = Uri.UnescapeDataString(text);

        // Windows 盘符形态：/C:/... → C:/...
        if (text.Length > 2 && text[0] == '/' && text[2] == ':')
        {
            text = text[1..];
        }

        return text.Replace('/', Path.DirectorySeparatorChar);
    }
}
