using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.NavigationEngine;

namespace MicroShader.Server;

/// <summary>
/// <see cref="INavigationHost"/> 的宿主实现：文件源委托给 <see cref="VfsIncludeFileSource"/>，
/// 另加「脏文件查询」与「路径 → URI」两件导航专属能力。
/// </summary>
/// <remarks>
/// 
/// "依赖方向"：接口在引擎层，实现在组合根；"MicroShader.NavigationEngine"
/// 不会反向依赖工程布局知识。
/// 
/// "脏文件查询为什么是可变属性"：它由 "LspServerSession" 实现，而会话又要靠
/// 本宿主构造出来的导航服务 —— 构造顺序上必然成环。用「先建宿主 → 建服务 → 建会话 → 回填会话」
/// 破环，比让引擎持有一张自己维护的影子副本安全得多（后者会出现两份真相）。
/// </remarks>
internal sealed class VfsNavigationHost : INavigationHost
{
    private readonly VfsIncludeFileSource _files;

    public VfsNavigationHost(VfsIncludeFileSource files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
    }

    /// <summary>会话建好之后回填；"null" = 拿不到开文档信息（脏文件跳转退化为按磁盘跳）。</summary>
    public IOpenDocumentLookup? OpenDocuments { get; set; }

    public bool TryResolve(string virtualPath, out string physicalPath)
        => _files.TryResolve(virtualPath, out physicalPath);

    public bool TryResolveRelative(string currentPhysicalPath, string target, out string physicalPath)
        => _files.TryResolveRelative(currentPhysicalPath, target, out physicalPath);

    public bool TryRead(string physicalPath, out string text)
        => _files.TryRead(physicalPath, out text);

    public long GetStamp(string physicalPath) => _files.GetStamp(physicalPath);

    /// <summary>
    /// 文档 URI → 物理路径。
    /// </summary>
    /// <remarks>
    /// "不要求文件存在"：脏文件（未存盘的新建 ".hlsl"）在磁盘上没有对应物，
    /// 但它作为相对 include 的基准目录依然有效 —— 详设 v2.0 第 3 条要求支持「两个未存盘的脏文件互跳」。
    /// </remarks>
    public bool TryGetPhysicalPath(string uri, out string physicalPath)
    {
        physicalPath = VfsIncludeFileSource.UriToPath(uri);
        return physicalPath.Length > 0;
    }

    public bool TryGetOpenDocument(string physicalPath, out string uri, out string text)
    {
        if (OpenDocuments is not null)
        {
            return OpenDocuments.TryGetOpenDocument(physicalPath, out uri, out text);
        }

        uri = string.Empty;
        text = string.Empty;
        return false;
    }

    /// <summary>
    /// 物理路径 → "file:///" URI。
    /// </summary>
    /// <remarks>
    /// 详设 v2.0 第 9 条：必须走 <see cref="Uri"/> 构造做 percent-encode。
    /// 字符串拼接在真实路径上会坏 —— 本机样本就是 "D:/Program Files/U3D/…"（含空格），
    /// 再加上可能的中文目录，拼出来的 URI 编辑器打不开。
    /// </remarks>
    public bool Exists(string physicalPath)
        => !string.IsNullOrEmpty(physicalPath) && File.Exists(physicalPath);

    public string ToFileUri(string physicalPath)
    {
        if (string.IsNullOrEmpty(physicalPath))
        {
            return string.Empty;
        }

        var normalized = physicalPath.Replace('\\', '/');

        // Uri 构造需要绝对路径；盘符形态（D:/…）与 UNC（//server/share）都能识别。
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            return uri.AbsoluteUri;
        }

        return "file:///" + normalized.TrimStart('/');
    }
}
