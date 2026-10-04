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

    /// <summary>探针行（1-based）。"0" 表示全量。</summary>
    public required int ProbeLine { get; init; }

    /// <summary>"true" = 全文件所有 Pass；"false" = 只算聚焦 Pass。</summary>
    public required bool IsFullScan { get; init; }
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
