namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// include 链符号能力的注入包。"null" = 整体不启用（引擎行为与注入前逐字节一致）。
/// </summary>
/// <remarks>
/// 把「缓存 + 文件源 + 参数」打包成一个可选参数注入，是为了让
/// <see cref="IntelliSenseService"/> 的构造函数保持单一可选参数形态：
/// 不注入时没有 "任何" 新代码路径被走到 —— 这一点由自检直接断言。
/// </remarks>
public sealed class IncludeSymbolProvider
{
    /// <summary>文档层 / 文件层符号缓存（预热器与补全共用同一实例）。</summary>
    public required IncludeSymbolCache Cache { get; init; }

    /// <summary>文件源；"null" = 关闭 B 兜底（纯 C：缓存未命中一律降级）。</summary>
    public IIncludeFileSource? Files { get; init; }

    /// <summary>可调参数。</summary>
    public IncludeSymbolOptions Options { get; init; } = new();

    /// <summary>
    /// LSP 文档 URI → 物理路径（宿主注入；引擎层不做 URI 方案判断）。
    /// </summary>
    /// <remarks>
    /// 用途是「让引擎知道当前文档真正在磁盘上的哪里」：虚拟路径解析、「按文档所属工程」
    /// 的标签表都要它。为 null 时引擎只能退回伪文档路径（行为与注入前一致）。
    /// </remarks>
    public Func<string, string>? PathFromUri { get; init; }
}
