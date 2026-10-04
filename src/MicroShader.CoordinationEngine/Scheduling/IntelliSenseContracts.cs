using System.Text.Json;
using MicroShader.Domain;

namespace MicroShader.CoordinationEngine.Scheduling;

/// <summary>
/// IntelliSense 结果写出器：把查询结果直接写进响应报文的 JSON writer。
/// </summary>
/// <returns>
/// "true" = 已经写出"一个完整的 JSON 值"（对象/数组/null 均可）；
/// "false" = 什么都没写（例如光标处不构成触发场景）——
/// 此时调用方会兜底写 "null"。"返回 false 时绝不能写出半截 JSON。"
/// </returns>
/// <remarks>
/// 采用「写进调用方给的 writer」而不是「返回 DTO 再序列化」，是为了守住
/// ADR-026 的零反射约束：不存在任何可被 System.Text.Json 反射式序列化的 DTO，
/// 因此 AOT 下不可能因为缺元数据而在运行期失败。
/// </remarks>
public delegate bool IntelliSenseWriter(PositionRequest request, Utf8JsonWriter writer);

/// <summary>
/// IntelliSense 能力集：由宿主（组合根）注入。"null" 表示该能力整体不启用。
/// </summary>
/// <remarks>
/// 详设 v2.0 第 11 条要求「capabilities 必须成套声明」："声明与实现在同一处把关" ——
/// 这里就是那个「同一处」。<see cref="LspServerSession"/> 只在对应委托非 null 时才在
/// "initialize" 里宣告该项能力，从而不可能出现「宣告了却不会响应」或
/// 「实现了却没宣告（客户端根本不调用）」的错配。
/// </remarks>
public sealed class IntelliSenseProvider
{
    /// <summary>"textDocument/completion" 的实现；"null" = 不宣告 "completionProvider"。</summary>
    public IntelliSenseWriter? Completion { get; init; }

    /// <summary>"textDocument/hover" 的实现；"null" = 不宣告 "hoverProvider"。</summary>
    public IntelliSenseWriter? Hover { get; init; }

    /// <summary>
    /// 文档打开回调（"uri", "text"）。宿主用它做现场包符号预热；"null" = 不预热。
    /// </summary>
    /// <remarks>
    /// 放在这里而不是新开一个注入点，是为了维持「能力集与实现在同一处把关」的既有结构：
    /// 会话只按注入情况调用，宿主只按需要实现，两边不会错配。
    /// 回调必须是"非阻塞"的（会话在读循环里调用它）。
    /// </remarks>
    public Action<string, string>? DocumentOpened { get; init; }

    /// <summary>
    /// 文档内容已稳定（保存）时的回调（"uri", "text"）。宿主用它做"增量预热"：
    /// 作者在块里新写了一个 "#include" 时，只有到这一步才需要重新扫链。
    /// </summary>
    public Action<string, string>? DocumentSaved { get; init; }

    /// <summary>文档关闭回调（"uri"）。宿主用它丢弃该文档的预热结果；"null" = 不处理。</summary>
    public Action<string>? DocumentClosed { get; init; }

    /// <summary>是否至少启用了一项能力（否则整个 provider 视为未注入）。</summary>
    public bool IsEnabled => Completion is not null || Hover is not null;
}
