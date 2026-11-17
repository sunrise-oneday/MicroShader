using System.Buffers;
using System.Text.Json;
using MicroShader.FormatEngine;

namespace MicroShader.CoordinationEngine.Scheduling;

/// <summary>
/// textDocument/formatting 的写出器。
/// </summary>
/// <returns>有编辑返回 true；无编辑返回 false，调用方据此回 null。</returns>
public delegate bool FormatDocumentWriter(
    string uri,
    string filePath,
    string text,
    FormatOptions options,
    Utf8JsonWriter writer);

/// <summary>
/// 格式化能力集：由宿主（组合根）注入。
/// </summary>
/// <remarks>
/// 与 NavigationProvider 同构，声明与实现在同一处把关：
/// 委托为空即视为能力未启用，会话据此回 MethodNotFound，绝不静默丢弃。
/// </remarks>
public sealed class FormatProvider
{
    /// <summary>整文档格式化写出器。</summary>
    public FormatDocumentWriter? Formatting { get; init; }

    /// <summary>
    /// 括号风格。来自启动参数 --brace-style，进程启动时固定。
    /// </summary>
    /// <remarks>
    /// 与补全、导航不同，格式化参数里的 options.tabSize 等是客户端逐请求下发的
    /// （见 FormatDocumentWriter 的 FormatOptions 参数），
    /// 但括号风格是用户希望整个文件统一成哪种的长期偏好，随进程启动固定更合适：
    /// 它决定的是拆行还是并行，属于文件级结构，不该在一次请求里被临时改写。
    /// </remarks>
    public BraceStyle BraceStyle { get; init; } = BraceStyle.Auto;

    /// <summary>能力是否启用。</summary>
    public bool IsEnabled => Formatting is not null;
}
