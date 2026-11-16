using MicroShader.Domain;

namespace MicroShader.CoordinationEngine.Scheduling;

/// <summary>一次诊断派发请求（调度器 → 分析器）。</summary>
public readonly struct AnalysisRequest
{
    public required string Uri { get; init; }

    public required string FilePath { get; init; }

    public required int Version { get; init; }

    public required int Epoch { get; init; }

    public required string Text { get; init; }

    /// <summary>
    /// 没有探针行时的取值：这一轮做的是整篇扫描，发布侧据此回收已消失的槽位。
    /// 1-based 的行号从 1 起，因此 -1 不可能与真实探针行撞号
    /// （旧口径拿 "0" 当「全量」，会被引擎当成「哪一行都不是」而悄悄退化成一整篇扫描）。
    /// </summary>
    public const int NoProbeLine = -1;

    /// <summary>探针行（1-based）；没有探针行时取 NoProbeLine。</summary>
    public required int ProbeLine { get; init; }

    /// <summary>"true" = 全文件所有 Pass；"false" = 只算聚焦 Pass。</summary>
    public required bool IsFullScan { get; init; }

    /// <summary>
    /// "true" = 本次分析前先清空诊断缓存。
    /// 用于「被 include 的文件在磁盘上变了」这一情形：缓存按装配文本的内容哈希寻址，
    /// 而 include 的内容不在哈希内（DXC 编译期才读它们），不清就会原样返回上一次的旧诊断。
    /// </summary>
    public bool ForceRecompile { get; init; }
}

/// <summary>分析结果："按 Pass" 给出诊断集合（ADR-024）。</summary>
public sealed class AnalysisOutcome
{
    public required string Uri { get; init; }

    public required int Version { get; init; }

    public required int Epoch { get; init; }

    public required bool IsFullScan { get; init; }

    /// <summary>Pass 槽位 → 诊断集合。"-1" 表示文件级（切片层 / 共享块）。</summary>
    public required IReadOnlyDictionary<int, IReadOnlyList<ShaderDiagnosticItem>> PassDiagnostics { get; init; }

    /// <summary>本次分析实际覆盖的 Pass 槽位（全量扫描时等于文档当前全部 Pass）。</summary>
    public required IReadOnlyList<int> CoveredPasses { get; init; }
}

/// <summary>诊断分析器契约。协商层不引用具体引擎实现（"MicroShader.DiagnosticEngine" 由宿主注入）。</summary>
public delegate Task<AnalysisOutcome> DocumentAnalyzer(AnalysisRequest request, CancellationToken cancellationToken);

/// <summary>发布契约：返回 "true" 表示结果真的推给了客户端（版本门控放行）。</summary>
public delegate Task<bool> OutcomePublisher(AnalysisOutcome outcome, CancellationToken cancellationToken);
