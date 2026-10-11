using MicroShader.CoordinationEngine.Documents;

namespace MicroShader.CoordinationEngine.Session;

/// <summary>
/// 协商引擎对外契约（规格书模块 8 §七 的交付清单）。
/// </summary>
/// <remarks>
/// "与原文签名的两处偏离"：
/// <list type="number">
///   <item>"RunAsync" 返回 "Task&lt;int&gt;" 而不是 "Task" —— v2.0 第 5 条要求
///         退出码语义化："收到过 shutdown → 0；否则 → 1"。原设计的
///         「断开即 "Environment.Exit(0)"」既有退出码错误，也会与未 flush 的 PipeWriter
///         和 DXC COM 对象竞争甚至死锁；现在改为「置原子标志 + 取消 CTS，让 RunAsync 正常返回，
///         由 "Main" 决定退出码」，"Environment.Exit" 只留作 watchdog 兜底。</item>
///   <item>"EnqueueDiagnosticsAsync" 不是公开方法 —— 诊断回传由注入的分析器与内部发布器
///         完成（分析器由宿主提供，协商层不反向引用 "MicroShader.DiagnosticEngine"）。
///         对外只暴露「读当前活跃文档快照」两个查询。</item>
/// </list>
/// </remarks>
public interface ILspCoordinationEngine
{
    /// <summary>启动标准输入/输出上的服务循环，返回进程退出码。</summary>
    Task<int> RunAsync(Stream inputStream, Stream outputStream, CancellationToken cancellationToken);

    /// <summary>取当前处于活跃编辑焦点的文档快照（启发式，见 README 裁定 3）。</summary>
    bool TryGetActiveDocument(out DocumentSnapshot? document);

    /// <summary>按 URI + 版本取快照（版本不吻合即判定为过期）。</summary>
    bool TryGetDocumentAtVersion(string uri, int version, out DocumentSnapshot? document);
}
