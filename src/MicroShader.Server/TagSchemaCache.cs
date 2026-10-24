using MicroShader.ContextEngine;
using MicroShader.ObservabilityEngine;
using MicroShader.ShaderLab;

namespace MicroShader.Server;

/// <summary>
/// 按文档所属工程提供标签校验表（SL010x 语义校验与标签值补全共用）。
/// </summary>
/// <remarks>
/// "为什么不能只有一个全局表"：合法标签值来自工程自己装的渲染管线包与工程 C# 里的
/// "new ShaderTagId(…)" —— 一个窗口里开着两个工程时，用另一个工程的表去校验只会
/// 产生误报或漏报。按工程采集一次并缓存，与按文档定位 VFS 索引同一口径。
/// </remarks>
internal sealed class TagSchemaCache
{
    private const int MaxCachedProjects = 4;

    private readonly VfsIndexLocator _locator;

    private readonly ShaderTagSchema _primary;

    private readonly object _sync = new();

    private readonly Dictionary<string, ShaderTagSchema> _byRoot = new(StringComparer.OrdinalIgnoreCase);

    public TagSchemaCache(VfsIndexLocator locator, ShaderTagSchema primary)
    {
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(primary);

        _locator = locator;
        _primary = primary;
    }

    /// <summary>该文档所属工程的标签表；定位不到工程（或就是主工程）时用主表。</summary>
    public ShaderTagSchema For(string? documentPhysicalPath)
    {
        if (string.IsNullOrEmpty(documentPhysicalPath))
        {
            return _primary;
        }

        var root = _locator.RootFor(documentPhysicalPath);
        if (root is null || string.Equals(root, _locator.PrimaryRoot, StringComparison.OrdinalIgnoreCase))
        {
            return _primary;
        }

        lock (_sync)
        {
            if (_byRoot.TryGetValue(root, out var cached))
            {
                return cached;
            }
        }

        // 采集要扫渲染管线包与工程 C#，属文件 I/O；放在锁外，每个新工程只做一次。
        var schema = TagSchemaHarvester.Harvest(_locator.ForFile(documentPhysicalPath), root);

        lock (_sync)
        {
            if (_byRoot.Count >= MaxCachedProjects)
            {
                _byRoot.Clear();
            }

            _byRoot[root] = schema;
        }

        return schema;
    }
}
