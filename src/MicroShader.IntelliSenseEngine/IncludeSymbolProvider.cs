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
}
