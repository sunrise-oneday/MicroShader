namespace MicroShader.ContextEngine;

/// <summary>
/// "#include" 的四规则解析器。
/// </summary>
/// <remarks>
/// "规则 1" "Packages/&lt;pkg&gt;/..."：URP 14.0.12 包内 779 条 include 中有 727 条走这条（实测）。
/// "规则 2" "Assets/..."：相对工程根。
/// "规则 3" 裸名或相对路径：相对"包含方文件所在目录"（URP 包内 52 条，例如
/// "ShaderLibrary/Shadows.hlsl:7" 的 "#include "Core.hlsl""）。
/// "规则 4" 裸名兜底："&lt;Editor&gt;/Data/CGIncludes/&lt;裸名&gt;"（目录级判定，本机实测 56 个文件）。
/// 
/// "为什么 <paramref name="verifyOnDisk"/> 默认 false"：查询路径不允许有文件 I/O。
/// 但规则 4 与规则 3 的优先级只能靠「磁盘上到底有没有」来裁决，所以分两种模式：
/// <list type="bullet">
///   <item>"编译热路径（false）"：只做纯路径演算，规则 3 永远优先。这与 DXC 自身的 """" 搜索语义一致；
///         规则 4 由编译器侧通过 "-I &lt;CGIncludesRoot&gt;" 交给 DXC 完成，结论等价。</item>
///   <item>"依赖扫描上报层（true）"：规则 3 先探盘，未命中再回落 CGIncludes 目录集合，最后才判「依赖缺失」。</item>
/// </list>
/// 
/// 
/// "不做任何猜测"：未命中一律返回明确失败，绝不静默丢弃、也绝不「碰巧解析到某个同名文件」。
/// 
/// </remarks>
public static class IncludeResolver
{
    private const string PackagesPrefix = "Packages/";
    private const string AssetsPrefix = "Assets/";

    /// <summary>解析一条 include（<paramref name="includePath"/> 可为带引号/尖括号的原文）。</summary>
    /// <param name="index">VFS 快照。</param>
    /// <param name="includePath">include 里的路径原文。</param>
    /// <param name="includingFilePhysicalPath">包含方文件的物理路径（可为空：表示没有上下文，只有规则 1/2 可用）。</param>
    /// <param name="verifyOnDisk">是否做磁盘存在性校验（上报层用 true，编译热路径用 false）。</param>
    public static IncludeResolution Resolve(
        VfsIndex index,
        ReadOnlySpan<char> includePath,
        ReadOnlySpan<char> includingFilePhysicalPath,
        bool verifyOnDisk = false)
    {
        ArgumentNullException.ThrowIfNull(index);

        var trimmed = TrimDecorations(includePath);
        if (trimmed.Length == 0)
        {
            return new IncludeResolution { Found = false, Miss = IncludeMissKind.NotFound };
        }

        // 统一分隔符：Unity 生态里两种写法都存在。
        var normalized = NormalizeSeparators(trimmed);

        if (StartsWith(normalized, PackagesPrefix))
        {
            return ResolvePackagesPrefix(index, normalized[PackagesPrefix.Length..], verifyOnDisk);
        }

        if (StartsWith(normalized, AssetsPrefix))
        {
            // 同样避免 normalized.ToString() 造成的额外分配。
            var candidate = VfsPath.Combine(index.Layout.ProjectRoot.AsSpan(), normalized);
            return Finish(candidate, IncludeRuleKind.AssetsPrefix, verifyOnDisk);
        }

        return ResolveRelative(index, normalized, includingFilePhysicalPath, verifyOnDisk);
    }

    private static IncludeResolution ResolvePackagesPrefix(VfsIndex index, ReadOnlySpan<char> rest, bool verifyOnDisk)
    {
        if (rest.Length == 0)
        {
            return new IncludeResolution { Found = false, Miss = IncludeMissKind.NotFound };
        }

        var slash = rest.IndexOf('/');
        var packageName = slash < 0 ? rest : rest[..slash];
        var tail = slash < 0 ? ReadOnlySpan<char>.Empty : rest[(slash + 1)..];

        if (!index.TryGetPackage(packageName, out var entry) || entry is null)
        {
            return new IncludeResolution
            {
                Found = false,
                Miss = IncludeMissKind.UnknownPackage,
                MissingPackageName = packageName.ToString(),
            };
        }

        // 走 span 版 Combine：包根是构建期就归一化好的，tail 直接来自输入 span，
        // 因此整个规则 1 只产生"一次"分配（拼出来的物理路径本身）。
        // 早期写法 tail.ToString() + 字符串拼接 + Normalize 会产生三次分配（实测 600 B/次）。
        var path = tail.Length == 0
            ? entry.PhysicalRoot
            : VfsPath.Combine(entry.PhysicalRoot.AsSpan(), tail);

        return Finish(path, IncludeRuleKind.PackageVirtualPrefix, verifyOnDisk);
    }

    private static IncludeResolution ResolveRelative(
        VfsIndex index,
        ReadOnlySpan<char> normalized,
        ReadOnlySpan<char> includingFilePhysicalPath,
        bool verifyOnDisk)
    {
        if (includingFilePhysicalPath.Length == 0)
        {
            // 没有包含方上下文：规则 3/4 都无从谈起，只能判缺失。
            return new IncludeResolution { Found = false, Miss = IncludeMissKind.NotFound };
        }

        // 调用方传进来的包含方路径通常是索引/布局产出的已归一化路径，因此这里走 span 版本，
        // 只有遇到 Windows 原生反斜杠路径时才付出一次归一化分配。
        var includingNormalized = VfsPath.IsNormalizedFast(includingFilePhysicalPath)
            ? includingFilePhysicalPath
            : VfsPath.Normalize(includingFilePhysicalPath.ToString()).AsSpan();

        var baseDirectory = VfsPath.GetDirectoryName(includingNormalized);
        if (baseDirectory.IsEmpty)
        {
            return new IncludeResolution { Found = false, Miss = IncludeMissKind.NotFound };
        }

        var relativeCandidate = VfsPath.Combine(baseDirectory, normalized);

        if (!verifyOnDisk)
        {
            // 编译热路径：纯路径演算。规则 4 由编译器侧的 -I <CGIncludesRoot> 交给 DXC 完成，语义等价。
            return new IncludeResolution
            {
                Found = true,
                PhysicalPath = relativeCandidate,
                Rule = IncludeRuleKind.RelativeToIncludingFile,
            };
        }

        if (File.Exists(VfsPath.ToNative(relativeCandidate)))
        {
            return new IncludeResolution
            {
                Found = true,
                PhysicalPath = relativeCandidate,
                Rule = IncludeRuleKind.RelativeToIncludingFile,
            };
        }

        // 规则 4：裸名兜底。"目录级"判定 —— 查的是索引里预先枚举好的文件名集合，不做额外 I/O。
        if (IsBareName(normalized) && index.Installation is { } install && index.IsKnownCGInclude(normalized))
        {
            return new IncludeResolution
            {
                Found = true,
                PhysicalPath = VfsPath.Combine(install.CGIncludesRoot, normalized.ToString()),
                Rule = IncludeRuleKind.CGIncludesFallback,
            };
        }

        return new IncludeResolution
        {
            Found = false,
            Miss = IncludeMissKind.NotFound,
            Rule = IncludeRuleKind.RelativeToIncludingFile,
        };
    }

    private static IncludeResolution Finish(string candidate, IncludeRuleKind rule, bool verifyOnDisk)
    {
        if (verifyOnDisk && !File.Exists(VfsPath.ToNative(candidate)))
        {
            return new IncludeResolution
            {
                Found = false,
                Miss = IncludeMissKind.NotFound,
                Rule = rule,
            };
        }

        return new IncludeResolution { Found = true, PhysicalPath = candidate, Rule = rule };
    }

    private static bool IsBareName(ReadOnlySpan<char> path) => path.IndexOf('/') < 0;

    private static bool StartsWith(ReadOnlySpan<char> value, string prefix)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static ReadOnlySpan<char> NormalizeSeparators(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            buffer[i] = value[i] == '\\' ? '/' : value[i];
        }

        return buffer;
    }

    /// <summary>去掉首尾空白、包裹引号以及尖括号形式。</summary>
    private static ReadOnlySpan<char> TrimDecorations(ReadOnlySpan<char> value)
    {
        var span = value.Trim();
        if (span.Length >= 2)
        {
            var first = span[0];
            var last = span[^1];
            if ((first == '"' && last == '"') || (first == '<' && last == '>'))
            {
                span = span[1..^1].Trim();
            }
        }

        while (span.StartsWith("./", StringComparison.Ordinal))
        {
            span = span[2..];
        }

        return span;
    }
}
