using MicroShader.ContextEngine;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.Server;

/// <summary>
/// 把 <see cref="ShaderDiagnosticEngine"/> 适配成协商层的分析器契约（模块 8 → 模块 3/4/9 的组合根）。
/// </summary>
/// <remarks>
/// "为什么适配器属于宿主而不属于任何一个引擎"：协商层（模块 8）不能反向引用
/// "MicroShader.DiagnosticEngine"，而诊断引擎也不该知道 LSP 的 "AnalysisOutcome"。
/// 两者的合流是"组合根"的职责，所以它落在服务端宿主工程里。
///
/// "为什么必须把「切片诊断」与「编译器诊断」合流"："ShaderDiagnosticEngine.Analyze"
/// 返回的 "Items" 只含编译器侧诊断（SL0001..SL0005 由模块 1 产出、标签诊断 SL01xx 由模块 9 产出），
/// 而 ADR-024 要求「按 Pass 维护诊断集合、发布时整份替换」。宿主负责把切片诊断归到文件级槽位
/// "-1"，其余按行号归到所在 Pass —— 否则「修好了错误但红线不消失」。
///
/// "串行化的理由（必须承认的取舍）"："ShaderDiagnosticEngine" 内部持有的
/// "ShaderLabStateMachine" 在其自身文档里明确声明「有状态且非线程安全，每个文档分析线程必须
/// 持有自己的实例」。当前宿主只建"一个"引擎实例，因此这里用信号量把分析串行化以保证正确性。
/// 调度层的防抖 + 版本栅栏已经把打字突发大幅合并，实测串行不构成瓶颈；
/// 若将来要吃掉多核，正解是「每线程一个引擎实例」的 PerThread 池（与 ADR-019 同构），而不是去掉这把锁。
/// </remarks>
internal sealed class ShaderDocumentAnalyzer : IDisposable
{
    private readonly ShaderDiagnosticEngine _engine;
    private readonly SemaphoreSlim _gate = new(1, 1);

    [ThreadStatic]
    private static ShaderLabStateMachine? t_machine;

    [ThreadStatic]
    private static ShaderLabParseResult? t_result;

    public ShaderDocumentAnalyzer(ShaderContextRegistry registry, string dxcDirectory, DiagnosticEngineOptions options)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        if (!ShaderDiagnosticEngine.TryCreate(registry, dxcDirectory, out var engine, out var error, options) || engine is null)
        {
            throw new InvalidOperationException("无法创建诊断引擎：" + error);
        }

        _engine = engine;
    }

    public Version DxcVersion => _engine.DxcVersion;

    public async Task<AnalysisOutcome> AnalyzeAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Analyze(request), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _engine.Dispose();
        _gate.Dispose();
    }

    private AnalysisOutcome Analyze(AnalysisRequest request)
    {
        var document = new SourceShaderDocument
        {
            FileUri = request.Uri,
            FilePath = request.FilePath,
            Version = request.Version,
            FullText = request.Text,
        };

        // 线程级复用的切片器与结果对象：本方法在信号量内串行执行，但换线程时仍需各自的实例。
        var machine = t_machine ??= new ShaderLabStateMachine();
        var parse = t_result ??= new ShaderLabParseResult();
        machine.Parse(request.FilePath, request.Text, parse);

        var report = _engine.Analyze(document, request.ProbeLine, request.IsFullScan);

        var slots = new Dictionary<int, List<ShaderDiagnosticItem>>();

        void Add(int slot, ShaderDiagnosticItem item)
        {
            if (!slots.TryGetValue(slot, out var list))
            {
                list = [];
                slots[slot] = list;
            }

            list.Add(item);
        }

        // 切片阶段诊断（UnterminatedBlock / 标签语义等）没有 Pass 归属，落到文件级槽位 -1。
        foreach (var item in parse.Diagnostics)
        {
            Add(-1, item);
        }

        foreach (var item in report.Items)
        {
            Add(SlotOf(parse, item.Line), item);
        }

        // 全量扫描必须覆盖文档当前的全部 Pass 槽位，否则「已消失的 Pass 的旧红线」不会被清掉。
        // 非全量扫描也必须覆盖被编译的 Pass——即使零诊断，否则旧诊断留在槽位里永不消失
        // （用户修好错误后红线不消失的根因）。
        var covered = request.IsFullScan
            ? parse.Passes.Select(static p => p.PassIndex).Distinct().OrderBy(static i => i).ToList()
            : slots.Keys.OrderBy(static i => i).ToList();

        if (request.IsFullScan && parse.SharedSnippets.Count > 0 && !covered.Contains(-1))
        {
            covered.Insert(0, -1);
        }

        // 非全量扫描：把被编译但零诊断的 Pass 也塞进 PassDiagnostics（空列表），
        // 让 PublishAsync 的 SetPass 用空列表覆盖旧诊断。
        if (!request.IsFullScan)
        {
            // report.Items 按行号归属到 Pass；零诊断的 Pass 不在 slots 里。
            // 但 Analyze 知道编译了哪些 Pass——从 report 的 CompiledUnitCount 等无法反推 PassIndex。
            // 正解：SlotOf 能把诊断行号映射到 PassIndex；被编译的 Pass 的行范围是已知的。
            // 这里用 slots 里已有的 Pass 加上被编译但零诊断的 Pass。
            // 被编译的 Pass = 探针行所在的 Pass。
            foreach (var snippet in parse.Passes)
            {
                if (request.ProbeLine >= snippet.StartLineNumber && request.ProbeLine <= snippet.EndLineNumber)
                {
                    if (!slots.ContainsKey(snippet.PassIndex))
                    {
                        slots[snippet.PassIndex] = [];
                    }
                    if (!covered.Contains(snippet.PassIndex))
                    {
                        covered.Add(snippet.PassIndex);
                        covered.Sort();
                    }
                }
            }
        }

        return new AnalysisOutcome
        {
            Uri = request.Uri,
            Version = request.Version,
            Epoch = request.Epoch,
            IsFullScan = request.IsFullScan,
            PassDiagnostics = slots.ToDictionary(static kv => kv.Key, static kv => (IReadOnlyList<ShaderDiagnosticItem>)kv.Value),
            CoveredPasses = covered,
        };
    }

    private static int SlotOf(ShaderLabParseResult parse, int line)
    {
        foreach (var snippet in parse.Passes)
        {
            if (line >= snippet.StartLineNumber && line <= snippet.EndLineNumber)
            {
                return snippet.PassIndex;
            }
        }

        return -1;
    }
}
