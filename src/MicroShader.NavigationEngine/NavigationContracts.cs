using MicroShader.IntelliSenseEngine;

namespace MicroShader.NavigationEngine;

/// <summary>
/// 导航引擎需要的宿主能力：文件源（继承自 IntelliSense 引擎）+ 三个导航专属查询。
/// </summary>
/// <remarks>
/// "为什么继承 IIncludeFileSource"：它已经提供了
/// 「虚拟路径 → 物理路径 / 相对解析 / 读文本 / 写入戳」四件套，
/// 而导航的后三个成员是它的严格超集场景。拆成两个接口会让宿主实现两遍同一份路径解析。
/// </remarks>
public interface INavigationHost : IIncludeFileSource
{
    /// <summary>
    /// 文档 URI → 物理路径（用于相对 include 的基准目录）。
    /// 不要求文件存在：脏文件在磁盘上没有对应物，但作为基准目录依然有效。
    /// </summary>
    bool TryGetPhysicalPath(string uri, out string physicalPath);

    /// <summary>
    /// 该物理文件是否正被编辑器打开；命中时给出"客户端原始 URI"与内存文本。
    /// 用于"脏文件跳转"——必须原样回传客户端 didOpen 时的 URI。
    /// </summary>
    bool TryGetOpenDocument(string physicalPath, out string uri, out string text);

    /// <summary>物理路径 → "file:///" URI（percent-encoded）。</summary>
    string ToFileUri(string physicalPath);

    /// <summary>物理路径对应的文件是否存在。</summary>
    bool Exists(string physicalPath);
}

/// <summary>
/// 导航行为选项：由构造方注入，决定回出的 JSON 形状。
/// </summary>
/// <remarks>
/// "为什么不直接读 ClientNavigationCapabilities"：能力快照在 initialize 之后才知道，
/// 而服务在构造时就需要默认形状。选项是"初始默认值"，能力快照是"运行时覆盖"。
/// </remarks>
public sealed record NavigationOptions
{
    /// <summary>是否回 LocationLink（VS Code 默认）；false = 回 Location[]。</summary>
    public bool LinkSupport { get; init; }

    /// <summary>是否回层级 DocumentSymbol[]；false = 回扁平 SymbolInformation[]。</summary>
    public bool HierarchicalDocumentSymbol { get; init; }

    /// <summary>客户端允许的 SymbolKind 取值；空数组 = 不限制。</summary>
    public int[] SymbolKindValueSet { get; init; } = [];

    /// <summary>跨文件补扫的上限（防止预热未命中时读太多文件）。</summary>
    public int MaxCrossFileProbes { get; init; } = 8;

    /// <summary>宏别名解包的最大深度（实测别名链最长 3 层，上限 8 防环）。</summary>
    public int MaxMacroUnwrapDepth { get; init; } = 8;

    /// <summary>返回候选的上限（超出则截断）。</summary>
    public int MaxCandidates { get; init; } = 32;
}
