using System.Buffers;
using System.Text.Json;
using MicroShader.Domain;

namespace MicroShader.CoordinationEngine.Scheduling;

/// <summary>
/// 客户端在 "initialize" 里声明的导航相关能力快照。
/// </summary>
/// <remarks>
/// 详设 v2.0 第 1 条：本模块对外返回三种不同 LSP 形状，若把返回类型写死成
/// "LocationLink[]"，等于把「只支持两家客户端」静默焊死。因此把能力快照
/// 从 "initialize" 一路送到引擎，由它决定回 "LocationLink[]" 还是 "Location[]"、
/// 回层级 "DocumentSymbol[]" 还是扁平 "SymbolInformation[]"。
/// </remarks>
/// <param name="LinkSupport">"textDocument.definition.linkSupport"。</param>
/// <param name="HierarchicalDocumentSymbol">"textDocument.documentSymbol.hierarchicalDocumentSymbolSupport"。</param>
/// <param name="SymbolKindValueSet">客户端允许的 "SymbolKind" 取值；空数组 = 不限制。</param>
public readonly record struct ClientNavigationCapabilities(
    bool LinkSupport,
    bool HierarchicalDocumentSymbol,
    int[] SymbolKindValueSet)
{
    /// <summary>默认（未收到 initialize 或客户端未声明）：按最保守的形状回。</summary>
    public static ClientNavigationCapabilities Default => new(false, false, []);
}

/// <summary>
/// 「正被编辑器打开的文档」查询（导航的脏文件跳转用）。
/// </summary>
/// <remarks>
/// 定义在这里（而不是组合根）是因为实现方是 "LspServerSession" —— 它持有影子仓储；
/// 而 CoordinationEngine 不能反向依赖组合根，组合根只负责把它接上。
/// </remarks>
public interface IOpenDocumentLookup
{
    /// <summary>
    /// 该物理文件是否正被打开；命中时给出"客户端原始 URI"（可能是 "untitled:"）与内存文本。
    /// </summary>
    bool TryGetOpenDocument(string physicalPath, out string uri, out string text);
}

/// <summary>按文档查询的写出器（"textDocument/documentLink" / "documentSymbol"）。</summary>
public delegate bool NavigationDocumentWriter(string uri, string filePath, string text, Utf8JsonWriter writer);

/// <summary>
/// "textDocument/documentLink/resolve" 的写出器：拿到的仍是"原始 params 字节"（零 DTO）。
/// </summary>
/// <remarks>
/// resolve 的入参是客户端把服务端先前给出的那条 link 原样回传，我们只需要自己塞进
/// "data" 的字段。为此单独定义 "data" 形状会引入 DTO，与 ADR-026 冲突；
/// 直接把原始字节交给引擎，由它用 <see cref="System.Text.Json"/> 取所需字段。
/// </remarks>
public delegate bool NavigationResolveWriter(ReadOnlySequence<byte> parameters, Utf8JsonWriter writer);

/// <summary>
/// 导航能力集：由宿主（组合根）注入。"null" 表示该能力整体不启用。
/// </summary>
/// <remarks>
/// 与 <see cref="IntelliSenseProvider"/> 同一结构："声明与实现在同一处把关" ——
/// <see cref="LspServerSession"/> 只在对应委托非 null 时才在 "initialize" 里宣告该能力，
/// 因此不可能出现「宣告了却不会响应」或「实现了却没宣告（客户端根本不调用）」的错配。
/// </remarks>
public sealed class NavigationProvider
{
    /// <summary>"textDocument/definition"（F12）；"null" = 不宣告 "definitionProvider"。</summary>
    public IntelliSenseWriter? Definition { get; init; }

    /// <summary>"textDocument/documentLink"（"#include" 超链接）；"null" = 不宣告。</summary>
    public NavigationDocumentWriter? DocumentLinks { get; init; }

    /// <summary>
    /// "textDocument/documentLink/resolve"：把首屏的纯词法结果补全成可点击目标。
    /// </summary>
    /// <remarks>
    /// 详设 v2.0 第 4 条：声明 "resolveProvider: true" 后，客户端只在用户真正悬停/点击时发 resolve。
    /// 一份含 200 个 include 的 .shader 因此把 200 次 VFS 查询降到 0~1 次。
    /// </remarks>
    public NavigationResolveWriter? ResolveDocumentLink { get; init; }

    /// <summary>"textDocument/documentSymbol"（大纲/面包屑）；"null" = 不宣告。</summary>
    public NavigationDocumentWriter? DocumentSymbols { get; init; }

    /// <summary>"textDocument/foldingRange"（折叠范围）；"null" = 不宣告。</summary>
    /// <remarks>
    /// 除编辑器左侧的折叠之外，粘滞滚动窗口的 foldingProviderModel 也读这份数据，
    /// 且对范围不做任何过滤 —— 粘滞里显示什么完全由引擎产出的范围决定。
    /// </remarks>
    public NavigationDocumentWriter? FoldingRanges { get; init; }

    /// <summary>
    /// 客户端能力快照的回调（"initialize" 时触发一次）。
    /// </summary>
    /// <remarks>
    /// 为什么是回调而不是构造参数：组合根在"收到 initialize 之前"就已构造好服务，
    /// 而能力快照只有 initialize 才知道。回调让「形状决策」仍然发生在引擎内部，
    /// 同时不必让会话反向依赖引擎类型。
    /// </remarks>
    public Action<ClientNavigationCapabilities>? CapabilitiesReceived { get; init; }

    /// <summary>是否至少启用了一项能力。</summary>
    public bool IsEnabled => Definition is not null || DocumentLinks is not null || DocumentSymbols is not null
        || FoldingRanges is not null;
}
