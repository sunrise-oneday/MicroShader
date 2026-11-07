namespace MicroShader.Domain;

/// <summary>
/// 一次「按位置查询」请求（completion / hover / signatureHelp 共用）。
/// </summary>
/// <remarks>
/// "为什么放在 Domain 而不是任何一侧引擎里"：补全引擎需要它、LSP 会话也需要它，
/// 而分层上「引擎不得反向依赖协商层、协商层不得反向依赖具体引擎」。把它放进中立的契约程序集，
/// 两侧各自引用即可，宿主（组合根）负责把它们接起来。
/// <paramref name="Line"/> / <paramref name="Character"/> 是 LSP 的 "0-based" 坐标，
/// 列号单位是 "UTF-16 码元"（"positionEncoding: utf-16"）——
/// 与 DXC 的 1-based "UTF-8 字节"列"不是"一回事，两者之间必须经 "Utf8Column" 换算，
/// 绝不能直接互换。
/// <paramref name="Text"/> 随请求一起给出（Full 同步下快照本就持有全文），
/// 这样 provider 不需要反向依赖协商层的影子仓储。
/// </remarks>
public readonly record struct PositionRequest(string Uri, string Text, int Line, int Character);
