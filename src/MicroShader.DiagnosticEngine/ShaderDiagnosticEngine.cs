using System.Diagnostics;
using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine.Native;
using MicroShader.Domain;
using MicroShader.ObservabilityEngine;
using MicroShader.ShaderLab;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 模块文档 §一 的交付接口：给定文档 + 探针行，返回整篇文档的诊断集。
/// </summary>
/// <remarks>
/// <paramref name="probeLine"/> 是"行级 diff 的首个改动行"（ADR-011），不是光标所在行 ——
/// 全量同步不携带光标。打字路径只编译该行所属的 Pass（ADR-003 快路径），保存路径全量扫描。
/// </remarks>
public interface IDiagnosticEngine
{
    Task<DiagnosticReport> AnalyzeDocument(SourceShaderDocument document, int probeLine, bool isSave);
}

public sealed class DiagnosticEngineOptions
{
    public bool IncludeNotes { get; init; }

    public bool FilterPragmaMessages { get; init; } = true;

    public bool AttachRelatedInformation { get; init; } = true;

    /// <summary>全量扫描（保存路径，以及打字时探针行无归属的退化全量）最多编译的单元数，防病态文件把后台线程占满。</summary>
    public int MaxUnitsPerAnalysis { get; init; } = 64;

    /// <summary>
    /// DXC 并行编译的线程上限。&lt;= 0 表示用引擎默认（4）。
    /// 调小可降低 CPU/内存峰值（代价是多 Pass 文件分析变慢）；调大能让大工程更快 ——
    /// 每多一路并发约多占 6 MB 私有内存（实测）。默认 4 是「峰值砍 ~27%、单文件仅 +158ms」的甜点；
    /// 单 Pass 的用户 shader 本就串行（单元数 &lt;= 1），不受本旋钮影响。
    /// </summary>
    public int MaxCompileThreads { get; init; }

    /// <summary>
    /// 标签语义校验快照（模块 9）。默认 <see cref="ShaderTagSchema.Empty"/> —— 空表时校验器整体沉默，
    /// 因此未注入 schema 的宿主行为与本特性引入前完全一致（零行为漂移）。
    /// 宿主应在构建 VFS 索引时用 <see cref="ShaderTagSchemaBuilder"/> 采集后注入。
    /// </summary>
    public ShaderTagSchema TagSchema { get; init; } = ShaderTagSchema.Empty;

    /// <summary>
    /// 按文档解析标签表（给「一个窗口开着多个 Unity 工程」的场景）；为 null 时一律用 <see cref="TagSchema"/>。
    /// </summary>
    public Func<string, ShaderTagSchema>? TagSchemaFor { get; init; }
}

/// <summary>一次文档分析的结果（已归并到源文件坐标、已按位置去重排序）。</summary>
public sealed class DiagnosticReport
{
    public required string FileUri { get; init; }

    public required int Version { get; init; }

    public IReadOnlyList<ShaderDiagnosticItem> Items { get; init; } = Array.Empty<ShaderDiagnosticItem>();

    /// <summary>参与分析的 Pass 单元数（一个 Pass 可能派发多次：顶点/片元各一次）。</summary>
    public int UnitCount { get; init; }

    /// <summary>
    /// 实际派发到原生编译器的阶段单元数，恒等于 "CompiledUnitCount + SuppressedUnitCount + AbortedUnitCount"。
    /// 与 <see cref="UnitCount"/>（Pass 数）不是同一口径，报表里必须分开写，否则会出现「单元 1 / 中止 2」这种读数。
    /// </summary>
    public int DispatchCount => CompiledUnitCount + SuppressedUnitCount + AbortedUnitCount + CachedUnitCount;

    public int CompiledUnitCount { get; init; }

    /// <summary>命中诊断缓存而跳过 DXC 编译的单元数（与编译单元互斥）。</summary>
    public int CachedUnitCount { get; init; }

    public int SuppressedUnitCount { get; init; }

    public int AbortedUnitCount { get; init; }

    /// <summary>
    /// 因单次分析上限（<see cref="DiagnosticEngineOptions.MaxUnitsPerAnalysis"/>）而没有检查的单元数。
    /// 这些单元这次既没编译也没中止 —— 它们的 0 条诊断什么也证明不了，宿主不得当成「干净」发布。
    /// </summary>
    public int SkippedUnitCount { get; init; }

    /// <summary>
    /// 没有检查到的 Pass 序号。宿主发布时要跳过这些槽位（保留上一次的诊断），
    /// 否则「大文件改一处，后半段 Pass 的红线自己消失」。
    /// </summary>
    public IReadOnlyList<int> SkippedPassIndices { get; init; } = Array.Empty<int>();

    public int RawDiagnosticCount { get; init; }

    public int DroppedPragmaMessages { get; init; }

    public int InternalErrors { get; init; }

    public int DegradedColumns { get; init; }

    public long CompilerMicroseconds { get; init; }

    public long ElapsedMicroseconds { get; init; }

    public bool HasErrors
    {
        get
        {
            foreach (var item in Items)
            {
                if (item.Severity == DiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

/// <summary>
/// 模块 4 的门面：切片（模块 1）→ 装配（模块 3）→ 原生编译（本模块网关）→ 诊断归属（本模块映射器）。
/// </summary>
public sealed class ShaderDiagnosticEngine : IDiagnosticEngine, IDisposable
{
    private readonly ShaderContextRegistry _registry;
    private readonly NativeCompilerGateway _gateway;
    private readonly ShaderLabStateMachine _stateMachine = new();
    private readonly VirtualTextAssembler _assembler;
    private readonly DiagnosticAttributor _attributor;
    private readonly ShaderTagValidator _tagValidator;
    private readonly DiagnosticEngineOptions _options;

    // 诊断缓存（模块 6 剩余项）：按装配文本的内容哈希跳过未变化单元的 DXC 重编译。
    // 内容寻址 = 天然失效：源文件或注入宏任一变化，哈希即变。include 文件内容变化不在哈希内
    // （DXC 编译期才读它们），边界是 PackageCache 会话内只读——如实标注。
    /// <summary>缓存单元数上限；满了整体清空（宁可少命中，也不让长会话的驻留内存无上限增长）。</summary>
    private const int MaxCachedUnits = 1024;

    private readonly Dictionary<string, CachedUnit> _unitCache = new(StringComparer.Ordinal);

    // ── 停机时的生命周期计数 ──
    // 释放网关会 FreeLibrary(dxcompiler.dll)，而仍在跑的编译还在通过它的 vtable 调原生函数 ——
    // 那一下是原生崩溃，托管侧的 try/catch 拦不住，也留不下任何可读线索。
    // 因此 Dispose 必须先等在途分析落地，把网关的释放排到最后。
    private readonly object _lifecycleGate = new();
    private readonly ManualResetEventSlim _idle = new(true);

    /// <summary>空闲收缩与网关释放的互斥锁：两者都会碰 COM 实例，交错就是原生崩溃。</summary>
    private readonly object _trimGate = new();
    private int _activeAnalyses;
    private bool _disposeRequested;

    /// <summary>等在途分析落地的上限。关闭路径宁可放弃等待，也不能变成卡死。</summary>
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly record struct CachedUnit(
        ulong Hash,
        IReadOnlyList<ShaderDiagnosticItem> Items,
        int Raw,
        int Pragma,
        int Internal,
        int Degraded);

    // FNV-1a over UTF-16 字符：只为缓存命中判定，不用于安全。
    private static ulong Fnv1a(ReadOnlySpan<char> text)
    {
        ulong hash = 0xCBF29CE484222325UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash = unchecked(hash * 0x100000001B3L);
        }
        return hash;
    }

    public ShaderDiagnosticEngine(
        ShaderContextRegistry registry,
        NativeCompilerGateway gateway,
        DiagnosticEngineOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(gateway);

        _registry = registry;
        _gateway = gateway;
        _options = options ?? new DiagnosticEngineOptions();
        _assembler = new VirtualTextAssembler(registry);
        _tagValidator = new ShaderTagValidator();
        _attributor = new DiagnosticAttributor(new DiagnosticAttributionOptions
        {
            IncludeNotes = _options.IncludeNotes,
            FilterPragmaMessages = _options.FilterPragmaMessages,
            AttachRelatedInformation = _options.AttachRelatedInformation,
        });
    }

    public static bool TryCreate(
        ShaderContextRegistry registry,
        string dxcDirectory,
        out ShaderDiagnosticEngine? engine,
        out string error,
        DiagnosticEngineOptions? options = null)
    {
        engine = null;

        // 默认为 4（内存优化后的默认；实测 8 路并行峰值私存 ~88MB，4 路 ~64MB）。
        // 只有宿主显式给了正数才覆盖。
        var maxThreads = options is { MaxCompileThreads: > 0 } ? options.MaxCompileThreads : 4;
        if (!NativeCompilerGateway.TryCreate(dxcDirectory, out var gateway, out error, maxThreads: maxThreads) || gateway is null)
        {
            return false;
        }

        engine = new ShaderDiagnosticEngine(registry, gateway, options);
        return true;
    }

    public Version DxcVersion => _gateway.DxcVersion;

    /// <summary>
    /// 自检注入点：非 null 时代替原生网关产出编译结果。生产路径恒为 null。
    /// 存在的唯一理由是「DXC 调用本身失败」这条分支在真机上无法稳定复现（需要 HRESULT 非 0），
    /// 而它一旦写错缓存就会让文件永久显示为干净 —— 必须有能锁住它的回归用例。
    /// </summary>
    internal Func<string, IReadOnlyList<string>, DxcCompileResult>? CompileOverride { get; set; }

    /// <summary>
    /// 后台线程执行（模块文档 §四：ThreadPool Worker、同文档同步排他、MTA）。
    /// 排他由调用方（模块 6 的调度中枢）按文档粒度保证。
    /// </summary>
    public Task<DiagnosticReport> AnalyzeDocument(SourceShaderDocument document, int probeLine, bool isSave)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Task.Run(() => Analyze(document, probeLine, isSave));
    }

    /// <summary>同步版本（自检与基准直接用，避免 await 抖动进入被测窗口）。</summary>
    /// <summary>清空单元级诊断缓存。</summary>
    /// <remarks>
    /// **必须在分析器的信号量内调用** —— 缓存字典不支持并发读写，会话侧直接调会和正在读它的
    /// 分析线程撞车。调用点是 <c>ShaderDocumentAnalyzer</c> 的 Analyze（已在 gate 内）。
    /// </remarks>
    public void InvalidateCache() => _unitCache.Clear();

    /// <summary>
    /// 回收空闲的原生编译器实例，只保留 keepAtLeast 个热实例；返回实际释放的数量。
    /// </summary>
    /// <remarks>
    /// 供宿主的「空闲回收」调用，三条兜底由网关落地，这里只说明契约：
    /// 一、至少留一个：keepAtLeast 小于 1 时网关按 1 处理，任何情况下都不清空池。
    /// 清空等于让下一次诊断重新走一遍实例创建，省内存不能拿这个换。
    /// 二、只碰空闲槽：网关用 TryTake 逐个取，取到才算空闲；被编译线程借走的那几枚动不到，
    /// 因此在后台空闲回收里随时调用都不会与在飞编译竞态。
    /// 三、失败只记日志：单槽释放抛异常只留一条痕迹，不会打断调用方。
    /// 引擎自身已进入释放流程时直接返回 0：那之后网关马上被整体释放，再插一手只会和排空撞车。
    /// </remarks>
    public int TrimIdleCompilers(int keepAtLeast)
    {
        lock (_lifecycleGate)
        {
            if (_disposeRequested)
            {
                return 0;
            }
        }

        return _gateway.TrimIdleSlots(keepAtLeast);
    }

    /// <summary>本文件的标签校验表：宿主给了「按文档解析」的委托就用它，否则用固定的那张。</summary>
    private ShaderTagSchema TagSchemaForDocument(SourceShaderDocument document)
        => _options.TagSchemaFor is { } provider ? provider(document.FilePath) : _options.TagSchema;

    public DiagnosticReport Analyze(SourceShaderDocument document, int probeLine, bool isSave)
        => Analyze(
            document,
            _stateMachine.Parse(document.FilePath, document.FullText ?? string.Empty),
            probeLine,
            isSave);

    /// <summary>
    /// 复用宿主已经切好的结果：一份文档在一次分析里只该被切一遍。
    /// </summary>
    /// <remarks>
    /// 宿主为了「把切片诊断按 Pass 归档」必须先切一次，引擎以前又用自己的状态机切了一遍
    /// （两参重载每次还会新建一个结果对象）。打字路径只编译一个 Pass，切片就成了编译之外
    /// 最大的固定开销 —— 直接把宿主那份传进来，省掉整趟扫描与一次分配。
    /// </remarks>
    public DiagnosticReport Analyze(
        SourceShaderDocument document,
        ShaderLabParseResult parse,
        int probeLine,
        bool isSave)
    {
        // 登记为「在途」再进主体：这是 Dispose 等待的依据（见 Dispose 里的注释）。
        EnterAnalysis();
        try
        {
            return AnalyzeCore(document, parse, probeLine, isSave);
        }
        finally
        {
            ExitAnalysis();
        }
    }

    private DiagnosticReport AnalyzeCore(
        SourceShaderDocument document,
        ShaderLabParseResult parse,
        int probeLine,
        bool isSave)
    {
        ArgumentNullException.ThrowIfNull(parse);

        var start = Stopwatch.GetTimestamp();
        var text = document.FullText ?? string.Empty;

        var skippedPasses = new List<int>();
        var units = SelectUnits(parse, probeLine, isSave, skippedPasses);

        var items = new List<ShaderDiagnosticItem>();
        var seen = new HashSet<(int Line, int Column, int Severity, string Message)>();

        // 上限截断绝不能等同于「这些单元是干净的」：DXC 根本没跑过，0 条诊断什么也证明不了。
        // 修复前截断是静默的，宿主又把没检查到的 Pass 当成空诊断集发布，于是大文件改一处之后
        // 后半段 Pass 的红线会自己消失。这里给两层痕迹：
        // ① SkippedPassIndices 进报告，宿主据此既不发布空诊断集，也把它们留在保留集合里 —— 上一次的红线原地不动；
        // ② 文件级留一条 warning，用户才知道「这次没检查到」，否则「没报错」会被读成「没问题」。
        // 取舍：warning 锚在第 1 行（通常落在文件级槽位 -1），而不是第一个被跳过的 Pass 的起始行，
        // 就是为了不去覆盖那个槽位的旧诊断 —— 可见性与「保留上次结果」两件事不能互相打架。
        if (skippedPasses.Count > 0)
        {
            var firstSkippedLine = 1;
            foreach (var snippet in parse.Passes)
            {
                if (snippet.PassIndex == skippedPasses[0])
                {
                    firstSkippedLine = snippet.StartLineNumber;
                    break;
                }
            }

            items.Add(new ShaderDiagnosticItem
            {
                Severity = DiagnosticSeverity.Warning,
                Line = 1,
                Column = 0,
                Message = "本文件有 " + parse.Passes.Count + " 个编译单元，超过单次分析上限 "
                    + _options.MaxUnitsPerAnalysis + "：第 " + firstSkippedLine + " 行起的 " + skippedPasses.Count
                    + " 个 Pass 本次未检查，保留上一次的诊断",
                TargetUri = document.FileUri,
                Code = DiagnosticEngineCodes.AnalysisBudgetExceeded,
            });

            DiagTrace.Mark("分析上限截断：跳过 " + skippedPasses.Count + " 个 Pass（" + string.Join(",", skippedPasses) + "）");
        }

        // DXC 的 include 处理器按「本文件所属工程」的拓扑解析 "Packages/…" —— 与装配阶段同源。
        // 本方法在宿主的信号量内串行执行，静态槽位不会与其它文档打架。
        var index = _registry.IndexForDocument(document.FilePath);
        DxcIncludeHandlerBridge.Index = index;

        int compiled = 0, suppressed = 0, aborted = 0, cachedUnits = 0, raw = 0, droppedPragma = 0, internalErrors = 0, degraded = 0;
        long compilerMicroseconds = 0;

        void Collect(IReadOnlyList<ShaderDiagnosticItem> produced)
        {
            foreach (var item in produced)
            {
                if (seen.Add((item.Line, item.Column, (int)item.Severity, item.Message)))
                {
                    items.Add(item);
                }
            }
        }

        // ── 模块 9：ShaderLab 标签语义校验 ──
        // 位置固定在「切片之后、装配之前」：标签是给渲染管线看的元数据，既不进 HLSL 编译单元，
        // 也不产生任何 DXC 诊断，因此必须独立于编译派发路径上报（DXC 编译被抑制也不例外）。
        // 纯字符串比较 + 有界编辑距离，无诊断时不产生任何托管堆分配。
        _tagValidator.Validate(parse, TagSchemaForDocument(document), items);

        // ── 三阶段派发：串行装配 → 并行编译 → 串行归属 ──
        // 装配（VirtualTextAssembler）非线程安全（含 SourceLineIndex 缓存），必须串行。
        // DXC 编译（NativeCompilerGateway）已按线程池化（ConcurrentBag），可安全并行。
        // 归属（DiagnosticAttributor）和诊断收集非线程安全，串行后处理。
        var toCompile = new List<(AssembledShaderText Text, List<string> Arguments, List<IncludeBinding> Bindings, string UnitKey, ulong Hash, int StartLineNumber)>();

        foreach (var snippet in units)
        {
            var stages = StagesOf(snippet);

            // 未闭合块（模块 1 SL0001）与遗留 CG 抑制都是"既定策略"，模块 1 已经报过，这里绝不重复报。
            if (snippet.IsUnterminated || snippet.SuppressNativeDispatch)
            {
                suppressed += Math.Max(1, stages.Count);
                continue;
            }

            if (stages.Count == 0)
            {
                suppressed++;
                continue;
            }

            // include 绑定表按需算：缓存命中（内容没变的单元）根本用不到它，
            // 而它每条 include 都要探一次盘。保存时 N-1 个未变动的 Pass 会整批命中缓存 ——
            // 把计算挪进来，这些 syscall 就一次都不用做。
            List<IncludeBinding>? bindings = null;

            foreach (var (stage, entry) in stages)
            {
                var assembly = _assembler.Assemble(document, parse, snippet, stage);

                // ★ 装配阶段的诊断（#include 死链等）与编译成败无关，必须无条件上报。
                //   中止分支若直接 continue，这条诊断就会消失 —— 用户看到「零诊断」而 shader 其实是坏的。
                Collect(assembly.Diagnostics);

                if (!assembly.Succeeded || assembly.Text is null)
                {
                    aborted++;
                    continue;
                }

                var assembled = assembly.Text;

                // 模块 6 剩余项（NFR-3b）：装配文本逐字节相同 ⇒ DXC 必然产出相同诊断，
                // 直接复用上次的归属结果，跳过重编译。内容寻址（FNV-1a over UTF-16 字符），
                // 无失效难题：源文件或注入宏任一变化哈希即变。打字流只编译探针行所属单元，
                // 保存流全量——同一份文本在「打字收敛到保存」的链路上至少省掉一次全量重编译。
                var unitKey = document.FileUri + "|" + snippet.PassIndex + ":" + snippet.BlockOrdinalInPass + "|" + stage;
                var hash = Fnv1a(assembled.Span);
                if (_unitCache.TryGetValue(unitKey, out var cached) && cached.Hash == hash)
                {
                    raw += cached.Raw;
                    droppedPragma += cached.Pragma;
                    internalErrors += cached.Internal;
                    degraded += cached.Degraded;
                    Collect(cached.Items);
                    cachedUnits++;
                    assembled.Dispose();
                    continue;
                }

                var arguments = BuildArguments(assembled.PhysicalPath, stage.ToTargetProfile(), entry, assembly.IncludeSearchPaths);
                bindings ??= Bindings(parse, snippet, document.FilePath, index);
                toCompile.Add((assembled, arguments, bindings, unitKey, hash, snippet.StartLineNumber));
            }
        }

        // 单一编译入口：只为自检留一个可替换点，生产路径直接走网关。
        DxcCompileResult CompileUnit(ReadOnlySpan<char> source, IReadOnlyList<string> arguments)
            => CompileOverride is { } hook ? hook(new string(source), arguments) : _gateway.Compile(source, arguments);

        // Phase 2：并行 DXC 编译——并发度受限于实例池上限（MaxThreads），避免空转争抢。
        DiagTrace.Mark("Phase2 并行编译开始 单元数=" + toCompile.Count + " 上限=" + _gateway.MaxThreads);
        var compileResults = new DxcCompileResult[toCompile.Count];
        try
        {
            if (toCompile.Count > 1)
            {
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _gateway.MaxThreads };
                try
                {
                    Parallel.For(0, toCompile.Count, parallelOptions, i =>
                    {
                        DiagTrace.Mark("  unit " + i + " 编译前 thread=" + Environment.CurrentManagedThreadId);
                        compileResults[i] = CompileUnit(toCompile[i].Text.Span, toCompile[i].Arguments);
                        DiagTrace.Mark("  unit " + i + " 编译后 hr=" + compileResults[i].CompileHr);
                    });
                }
                catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
                {
                    // 保持与串行版相同的异常语义：单异常直接抛，不包 AggregateException。
                    throw ex.InnerExceptions[0];
                }
            }
            else if (toCompile.Count == 1)
            {
                DiagTrace.Mark("  unit 0 编译前（串行）thread=" + Environment.CurrentManagedThreadId);
                compileResults[0] = CompileUnit(toCompile[0].Text.Span, toCompile[0].Arguments);
                DiagTrace.Mark("  unit 0 编译后 hr=" + compileResults[0].CompileHr);
            }

            DiagTrace.Mark("Phase2 并行编译结束");

            // Phase 3：串行归属 + 收集 + 缓存写入
            for (var i = 0; i < toCompile.Count; i++)
            {
                var task = toCompile[i];
                var result = compileResults[i];
                compilerMicroseconds += result.ElapsedMicroseconds;

                var attribution = _attributor.Attribute(result, task.Text, task.Bindings, document.FileUri, Math.Max(1, task.StartLineNumber));
                raw += attribution.RawCount;
                droppedPragma += attribution.DroppedPragmaMessages;
                internalErrors += attribution.InternalErrors;
                degraded += attribution.DegradedColumns;

                Collect(attribution.Items);

                // 调用本身失败的结果绝不能进单元缓存：缓存按内容哈希记的是「这个单元是干净的」，
                // 而 DXC 根本没跑起来时那 0 条诊断什么也证明不了 —— 写进去就等于让这个文件在
                // 整个会话里永久显示为干净。这里既不写缓存，也不计为已编译（计入中止），下次重试。
                if (result.InvocationFailed)
                {
                    aborted++;
                    continue;
                }

                // 上限兜底：只写不删会让长会话的驻留内存只增不减（关掉文档也不释放）。
                if (_unitCache.Count >= MaxCachedUnits)
                {
                    _unitCache.Clear();
                }

                _unitCache[task.UnitKey] = new CachedUnit(task.Hash, attribution.Items, attribution.RawCount, attribution.DroppedPragmaMessages, attribution.InternalErrors, attribution.DegradedColumns);
                compiled++;
            }
        }
        finally
        {
            // 确保所有池化缓冲归还，即使归属阶段抛异常。
            foreach (var task in toCompile)
            {
                task.Text.Dispose();
            }
        }

        // 每 Pass 诊断集合并后整体发布（ADR-008/024），顺序必须稳定，否则客户端会闪。
        items.Sort(static (a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line)
            : a.Column != b.Column ? a.Column.CompareTo(b.Column)
            : ((int)a.Severity).CompareTo((int)b.Severity));

        return new DiagnosticReport
        {
            FileUri = document.FileUri,
            Version = document.Version,
            Items = items,
            UnitCount = units.Count,
            CompiledUnitCount = compiled,
            CachedUnitCount = cachedUnits,
            SuppressedUnitCount = suppressed,
            AbortedUnitCount = aborted,
            SkippedUnitCount = skippedPasses.Count,
            SkippedPassIndices = skippedPasses,
            RawDiagnosticCount = raw,
            DroppedPragmaMessages = droppedPragma,
            InternalErrors = internalErrors,
            DegradedColumns = degraded,
            CompilerMicroseconds = compilerMicroseconds,
            ElapsedMicroseconds = (long)((Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency),
        };
    }

    /// <summary>打字路径只取探针行所属单元；保存路径全量（ADR-003）。</summary>
    /// <remarks>
    /// 探针行经常落在「没有编译单元」的位置：文件级共享块（HLSLINCLUDE/CGINCLUDE）、
    /// Shader/Properties/SubShader 这类纯 ShaderLab 行、注释或空行。旧实现对这些位置一律
    /// 返回 0 个单元，编辑后既不重编译也不刷新诊断 —— 红线该出现的不出现、该消失的不消失。
    /// 取舍：共享块按 Unity 语义被拼进每一个同方言 Pass，因此选中「所有引用该共享块的 Pass」
    /// （切片结果里的 ApplicableSharedBlockIndices 就是这份归属）；连归属都没有时（改的是
    /// Shader 名、标签或纯注释行），退化为一律全量扫描 —— 宁可多编译几个单元，也不能让编辑
    /// 完全没有反馈，数量仍受 MaxUnitsPerAnalysis 约束。
    /// </remarks>
    private List<ShaderPassSnippet> SelectUnits(
        ShaderLabParseResult parse, int probeLine, bool isSave, List<int> skippedPasses)
    {
        var selected = new List<ShaderPassSnippet>();

        if (isSave)
        {
            CollectAll(parse, selected, skippedPasses);
            return selected;
        }

        // ① 探针行落在某个程序块内：只编译它（打字快路径，语义与改动前一致）。
        foreach (var snippet in parse.Passes)
        {
            if (probeLine >= snippet.StartLineNumber && probeLine <= snippet.EndLineNumber)
            {
                Select(selected, skippedPasses, snippet);
            }
        }

        // 上限压到 0 时 selected 会是空的，但候选已经全部计入 skipped —— 不能继续往下走，
        // 否则第 ③ 步会把同一批 Pass 再记一遍（截断数量直接翻倍）。
        if (selected.Count > 0 || skippedPasses.Count > 0)
        {
            return selected;
        }

        // ② 探针行落在文件级共享块里：该块被拼进每一个同方言 Pass，等于同时改了它们全部。
        var sharedIndex = SharedBlockAt(parse, probeLine);
        if (sharedIndex >= 0)
        {
            foreach (var snippet in parse.Passes)
            {
                if (snippet.ApplicableSharedBlockIndices.Contains(sharedIndex))
                {
                    Select(selected, skippedPasses, snippet);
                }
            }

            if (selected.Count > 0 || skippedPasses.Count > 0)
            {
                return selected;
            }
        }

        // ③ 无归属（纯 ShaderLab 行 / 注释 / 空行，或没有任何 Pass 引用该共享块）：全量扫描。
        CollectAll(parse, selected, skippedPasses);
        return selected;
    }

    private void CollectAll(ShaderLabParseResult parse, List<ShaderPassSnippet> selected, List<int> skippedPasses)
    {
        foreach (var snippet in parse.Passes)
        {
            Select(selected, skippedPasses, snippet);
        }
    }

    // 超限的单元不是「被丢掉」而是「这次没检查」：必须单独记下来，
    // 否则它和不存在的单元、以及干净的单元在报告里长得一模一样。
    private void Select(List<ShaderPassSnippet> selected, List<int> skippedPasses, ShaderPassSnippet snippet)
    {
        if (selected.Count >= _options.MaxUnitsPerAnalysis)
        {
            skippedPasses.Add(snippet.PassIndex);
            return;
        }

        selected.Add(snippet);
    }

    /// <summary>探针行所在文件级共享块的索引；不在任何共享块内时为 -1。</summary>
    private static int SharedBlockAt(ShaderLabParseResult parse, int probeLine)
    {
        for (var i = 0; i < parse.SharedBlocks.Count; i++)
        {
            var block = parse.SharedBlocks[i];
            if (probeLine >= block.StartLineNumber && probeLine <= block.EndLineNumber)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>单元要编译的阶段与入口点：一个 Pass 可能同时有顶点/片元两套宏集，必须分别编译。</summary>
    private static List<(ShaderStage Stage, string Entry)> StagesOf(ShaderPassSnippet snippet)
    {
        var stages = new List<(ShaderStage, string)>(2);
        if (snippet.VertexEntry is { Length: > 0 } vertex)
        {
            stages.Add((ShaderStage.Vertex, vertex));
        }

        if (snippet.FragmentEntry is { Length: > 0 } fragment)
        {
            stages.Add((ShaderStage.Fragment, fragment));
        }

        if (snippet.KernelEntry is { Length: > 0 } kernel)
        {
            stages.Add((ShaderStage.Compute, kernel));
        }

        return stages;
    }

    private static List<IncludeBinding> Bindings(ShaderLabParseResult parse, ShaderPassSnippet snippet, string documentPath, VfsIndex index)
    {
        var bindings = new List<IncludeBinding>(snippet.Includes.Count + 4);
        Add(snippet.Includes);

        foreach (var sharedIndex in snippet.ApplicableSharedBlockIndices)
        {
            if (sharedIndex >= 0 && sharedIndex < parse.SharedSnippets.Count)
            {
                Add(parse.SharedSnippets[sharedIndex].Includes);
            }
        }

        return bindings;

        void Add(List<IncludeDirective> includes)
        {
            foreach (var include in includes)
            {
                var resolution = IncludeResolver.Resolve(index, include.Path.AsSpan(), documentPath.AsSpan(), verifyOnDisk: true);
                bindings.Add(new IncludeBinding(include.LineNumber, include.Column, include.Path, resolution.Found ? resolution.PhysicalPath : null));
            }
        }
    }

    /// <summary>
    /// DXC 参数表。约定 "arguments[0]" 是源文件名（位置参数）——
    /// 它同时决定诊断文件名与"相对 include 的基准目录"；"-HV 2018" 对齐 Unity 2022.3 的 DXC 档位。
    /// 绝不传 "-Wall"/"-WX"（URP 的私有 #pragma 会刷满噪声，ADR-018/清单第 16 条）。
    /// </summary>
    internal static List<string> BuildArguments(
        string physicalPath,
        string targetProfile,
        string entryPoint,
        IReadOnlyList<string> includeSearchPaths)
    {
        var arguments = new List<string>(8 + (includeSearchPaths.Count * 2))
        {
            physicalPath,
            "-T", targetProfile,
            "-E", entryPoint,
            "-HV", "2018",
            "-Zpc",
        };

        foreach (var directory in includeSearchPaths)
        {
            if (string.IsNullOrEmpty(directory))
            {
                continue;
            }

            arguments.Add("-I");
            arguments.Add(directory);
        }

        return arguments;
    }

    /// <summary>
    /// 释放引擎：先等在途分析落地（有上限），最后才释放原生网关。
    /// </summary>
    /// <remarks>
    /// 顺序是硬要求而不是风格：网关释放会卸载 dxcompiler.dll，正在跑的编译一旦在卸载之后
    /// 再调它的 vtable，进程直接原生崩溃（无托管异常、无堆栈、AOT 下只剩一个访问冲突）。
    /// 等待有上限，超时就继续释放 —— 关闭路径上「宁可放弃一次排空」也「不能永久卡住」，
    /// 这与出站排空那条链路的取舍一致。
    /// </remarks>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposeRequested)
            {
                return;
            }

            // 先立标志再等待：这样在等待期间新来的分析会被直接拒掉，不会又挤进「在途」。
            _disposeRequested = true;
        }

        if (!_idle.Wait(DisposeWaitTimeout))
        {
            Log.Warn(
                LogCategories.Compiler,
                "释放诊断引擎时仍有分析在途，等待已超过 " + (int)DisposeWaitTimeout.TotalSeconds + " 秒，继续释放");
        }

        // 与 TrimIdleCompilers 互斥（见那里的注释）：等在飞的空闲收缩做完，再卸载实例池与 dll。
        lock (_trimGate)
        {
            _gateway.Dispose();
        }

        // 刻意不释放 _idle：超时后可能还有分析在跑，它退场时会调 Set，对已释放的信号量调用
        // 又是一次崩溃。这个对象随进程一起消失，泄漏一个句柄远好过在关机路上再炸一次。
    }

    /// <summary>把一次分析登记为在途；引擎已进入释放流程时拒绝新分析。</summary>
    private void EnterAnalysis()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _activeAnalyses++;
            _idle.Reset();
        }
    }

    /// <summary>在途分析退场；归零时唤醒等待中的 Dispose。</summary>
    private void ExitAnalysis()
    {
        lock (_lifecycleGate)
        {
            if (--_activeAnalyses == 0)
            {
                _idle.Set();
            }
        }
    }
}
