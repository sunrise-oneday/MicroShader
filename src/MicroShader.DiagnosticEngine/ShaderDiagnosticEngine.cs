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

    /// <summary>保存路径全量扫描时最多编译的单元数（防病态文件把后台线程占满）。</summary>
    public int MaxUnitsPerAnalysis { get; init; } = 64;

    /// <summary>
    /// DXC 并行编译的线程上限。&lt;= 0 表示用引擎默认（8）。
    /// 调小可降低 CPU 峰值（代价是分析变慢），调大能让大工程更快 ——
    /// 默认行为与引入本旋钮前逐字节一致（见服务端 --max-compile-threads）。
    /// </summary>
    public int MaxCompileThreads { get; init; }

    /// <summary>
    /// 标签语义校验快照（模块 9）。默认 <see cref="ShaderTagSchema.Empty"/> —— 空表时校验器整体沉默，
    /// 因此未注入 schema 的宿主行为与本特性引入前完全一致（零行为漂移）。
    /// 宿主应在构建 VFS 索引时用 <see cref="ShaderTagSchemaBuilder"/> 采集后注入。
    /// </summary>
    public ShaderTagSchema TagSchema { get; init; } = ShaderTagSchema.Empty;
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
    private readonly Dictionary<string, CachedUnit> _unitCache = new(StringComparer.Ordinal);

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

        // 默认仍是 8（与引入旋钮前完全一致）；只有宿主显式给了正数才覆盖。
        var maxThreads = options is { MaxCompileThreads: > 0 } ? options.MaxCompileThreads : 8;
        if (!NativeCompilerGateway.TryCreate(dxcDirectory, out var gateway, out error, maxThreads: maxThreads) || gateway is null)
        {
            return false;
        }

        engine = new ShaderDiagnosticEngine(registry, gateway, options);
        return true;
    }

    public Version DxcVersion => _gateway.DxcVersion;

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

    public DiagnosticReport Analyze(SourceShaderDocument document, int probeLine, bool isSave)
    {
        var start = Stopwatch.GetTimestamp();
        var text = document.FullText ?? string.Empty;

        var parse = _stateMachine.Parse(document.FilePath, text);
        var units = SelectUnits(parse, probeLine, isSave);

        var items = new List<ShaderDiagnosticItem>();
        var seen = new HashSet<(int Line, int Column, int Severity, string Message)>();
        var index = _registry.Current.Index;
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
        _tagValidator.Validate(parse, _options.TagSchema, items);

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

            var bindings = Bindings(parse, snippet, document.FilePath, index);

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
                toCompile.Add((assembled, arguments, bindings, unitKey, hash, snippet.StartLineNumber));
            }
        }

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
                        compileResults[i] = _gateway.Compile(toCompile[i].Text.Span, toCompile[i].Arguments);
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
                compileResults[0] = _gateway.Compile(toCompile[0].Text.Span, toCompile[0].Arguments);
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
            RawDiagnosticCount = raw,
            DroppedPragmaMessages = droppedPragma,
            InternalErrors = internalErrors,
            DegradedColumns = degraded,
            CompilerMicroseconds = compilerMicroseconds,
            ElapsedMicroseconds = (long)((Stopwatch.GetTimestamp() - start) * 1_000_000L / Stopwatch.Frequency),
        };
    }

    /// <summary>打字路径只取探针行所属单元；保存路径全量（ADR-003）。</summary>
    private List<ShaderPassSnippet> SelectUnits(ShaderLabParseResult parse, int probeLine, bool isSave)
    {
        var selected = new List<ShaderPassSnippet>();
        foreach (var snippet in parse.Passes)
        {
            if (isSave || (probeLine >= snippet.StartLineNumber && probeLine <= snippet.EndLineNumber))
            {
                selected.Add(snippet);
                if (selected.Count >= _options.MaxUnitsPerAnalysis)
                {
                    break;
                }
            }
        }

        return selected;
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

    public void Dispose() => _gateway.Dispose();
}
