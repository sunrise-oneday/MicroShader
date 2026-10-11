using System.Text;
using MicroShader.ContextEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 零分配预处理与 "#line" 坐标锚定（模块 3 的 LineCalibrator 主体）。
/// </summary>
/// <remarks>
/// "一个编译单元 = 文件级共享块（同方言，可多个）+ 本 Pass 的程序块"。
/// 共享块先出，各带独立 "#line" 锚；Pass 块最后出。
/// "渲染文本布局"（注入段 → 锚定段）：
///
/// // MicroShader 注入段…（注释 + 平台宏 + 阶段宏 + 关键字矩阵）
/// #line &lt;共享块首行&gt; "&lt;虚拟路径&gt;"
/// …共享块源行（逐字节照抄，仅 include 行被重写）…
/// #line &lt;Pass 块首行&gt; "&lt;虚拟路径&gt;"
/// …Pass 块源行…
///
/// "对 ADR-023 不变量 I1 的一处明确收窄"：文档写「注入段只在文件头且以 #line 收尾」。
/// 但 v2.0 清单第 3 条又要求 "HLSLINCLUDE" 注入进每个同类程序块，而它的内容是真实源行、
/// 必须有自己的 "#line" 才能锚回正确行号 —— 于是「文件中间不会再出现注入内容」不可能成立。
/// 本实现把 I1 落地为可验证的等价形式："除文件头那一段外"，注入段恰好 1 行且必须是 "#line" 指令，
/// 且其后紧跟一个锚定段"；任何非 "#line" 的注入行出现在锚定段之后都判为违反。
/// 这样做保住了 I1 真正要防的事故："在 "#line" 之后悄悄多出几行，导致之后所有诊断整体偏移且编译器不报错"。
/// "c 里的绝对路径 + 虚拟路径"：DXC 的 "pSourceName" 应传"物理路径"，
/// 这样它解析 "#include "…"" 的相对基准就是真实目录（与 Unity 一致）；
/// 而报告出去的文件身份由我们注入的 "#line" 虚拟路径统一。
/// </remarks>
public sealed class VirtualTextAssembler
{
    private readonly ShaderContextRegistry _registry;
    private readonly AssemblerOptions _options;
    private readonly object _platformGate = new();
    private PlatformMacroSet? _platformMacros;

    // SourceLineIndex 跨 unit 缓存：同一文档的多个编译单元共享同一份行索引，
    // 避免对 437K 文本重复扫描 7+ 次。用 ReferenceEquals 判定——字符串不可变，
    // didChange 产生新对象即 cache miss，无需手动失效。
    private string? _cachedSourceText;
    private SourceLineIndex? _cachedLineIndex;

    public VirtualTextAssembler(ShaderContextRegistry registry, AssemblerOptions? options = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? new AssemblerOptions();
    }

    /// <summary>
    /// 本工程解算出的平台宏集合（首次访问时解算并缓存）。
    /// </summary>
    /// <remarks>
    /// 解算要读 "Editor.log" / "ProjectSettings.asset"，属文件 I/O，
    /// "绝不能"出现在每次按键的热路径上 —— 因此按装配器实例缓存，工程设置变化时显式调
    /// <see cref="InvalidatePlatformMacros"/>。
    /// </remarks>
    public PlatformMacroSet PlatformMacros
    {
        get
        {
            if (_options.PlatformMacros is { } forced)
            {
                return forced;
            }

            var cached = Volatile.Read(ref _platformMacros);
            if (cached is not null)
            {
                return cached;
            }

            lock (_platformGate)
            {
                cached = _platformMacros;
                if (cached is null)
                {
                    cached = ResolvePlatformMacros();
                    Volatile.Write(ref _platformMacros, cached);
                }
            }

            return cached;
        }
    }

    /// <summary>丢弃平台宏缓存，下次访问重新解算。</summary>
    public void InvalidatePlatformMacros() => Volatile.Write(ref _platformMacros, null);

    /// <summary>装配单个编译单元。</summary>
    public AssemblyResult Assemble(
        SourceShaderDocument document,
        ShaderLabParseResult parse,
        ShaderPassSnippet snippet,
        ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(snippet);

        if (snippet.IsUnterminated)
        {
            // EOF 熔断：编译抑制。诊断已由模块 1 在开启标记行给出（SL0001），
            // 这里"刻意不重复产出" —— 同一处错误显示两遍是 ADR-015/024 明令禁止的。
            return AssemblyResult.Fail(
                AssemblyFailureKind.UnterminatedBlock,
                $"程序块未闭合（开启标记在第 {snippet.StartLineNumber} 行），按 EOF 熔断策略不做原生编译；诊断由切片层给出。",
                stage);
        }

        if (snippet.SuppressNativeDispatch)
        {
            return AssemblyResult.Fail(
                AssemblyFailureKind.SuppressedNativeDispatch,
                snippet.SuppressReason ?? "该程序块按策略抑制原生派发。",
                stage);
        }

        var source = document.FullText;
        if (source.Length == 0 || snippet.ContentEndOffset <= snippet.ContentStartOffset)
        {
            return AssemblyResult.Fail(AssemblyFailureKind.NoContent, "程序块内容为空，没有可编译的内容。", stage);
        }

        var stageInfo = new ShaderStageInfo(stage);
        var sourceSpan = source.AsSpan();
        SourceLineIndex lineIndex;
        if (ReferenceEquals(_cachedSourceText, source) && _cachedLineIndex is { } cached)
        {
            lineIndex = cached;
        }
        else
        {
            lineIndex = SourceLineIndex.Build(sourceSpan);
            _cachedSourceText = source;
            _cachedLineIndex = lineIndex;
        }
        var physicalPath = VfsPath.Normalize(document.FilePath);
        var virtualPath = _options.ForcedVirtualPath ?? VirtualPath.For(physicalPath, _registry.Current.Index);

        var pieces = BuildPieces(parse, snippet);
        var searchPaths = BuildSearchPaths(physicalPath);

        var diagnostics = new List<ShaderDiagnosticItem>();
        var deadIncludes = new List<ShaderDiagnosticItem>();

        var plan = new ShaderKeywordPlan();
        var dynamicContext = _options.IncludeDynamicMacros ? _registry.Current.Dynamic : DynamicShaderContext.Empty;
        if (_options.IncludeKeywordMatrix)
        {
            ShaderKeywordMatrixBuilder.Build(
                CollectVariants(parse, snippet),
                stage,
                plan,
                dynamicContext.IsLive ? dynamicContext.ActiveKeywords.AsSpan() : default);
        }

        var estimated = (snippet.ContentEndOffset - snippet.ContentStartOffset) + 4096;
        var writer = new RenderedTextWriter(estimated);
        try
        {
            WritePrologue(writer, pieces[0].AnchorLine, virtualPath, stageInfo, plan, dynamicContext);

            for (var i = 0; i < pieces.Count; i++)
            {
                var piece = pieces[i];

                if (i > 0)
                {
                    // 文件中间的注入段只允许是这一行 #line（见类注释里对 I1 的收窄说明）。
                    writer.BeginSegment(SegmentKind.Injected);
                    writer.WriteLine(FormatLineAnchor(piece.AnchorLine, virtualPath));
                    writer.EndSegment();
                }

                writer.BeginSegment(SegmentKind.Anchored, piece.AnchorLine, piece.From, piece.SharedBlockIndex);
                EmitSourceRange(writer, sourceSpan, lineIndex, piece, document, diagnostics, deadIncludes);
                writer.EndSegment(piece.To - piece.From);
            }

            if (deadIncludes.Count > 0 && _options.AbortOnUnresolvedInclude)
            {
                return AssemblyResult.Fail(
                    AssemblyFailureKind.MissingInclude,
                    $"编译单元内存在 {deadIncludes.Count} 条无法解析的 #include，按「绝不把虚拟路径喂给 DXC」策略中止本单元装配。",
                    stage,
                    deadIncludes);
            }

            var assembled = writer.Build(physicalPath, virtualPath, stageInfo);
            diagnostics.AddRange(deadIncludes);
            return AssemblyResult.Success(assembled, diagnostics, searchPaths, plan, stage);
        }
        finally
        {
            // Build 成功时缓冲所有权已移交，这里是幂等空操作。
            writer.Dispose();
        }
    }

    /// <summary>装配整篇文档的全部程序块（同一阶段），并按位置去重诊断。</summary>
    public DocumentAssemblyReport AssembleDocument(
        SourceShaderDocument document,
        ShaderLabParseResult parse,
        ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parse);

        var units = new List<AssemblyResult>(parse.Passes.Count);
        var diagnostics = new List<ShaderDiagnosticItem>();
        var seen = new HashSet<(int Line, string? Code, string Message)>();
        var aborted = 0;

        foreach (var snippet in parse.Passes)
        {
            var result = Assemble(document, parse, snippet, stage);
            units.Add(result);
            if (!result.Succeeded)
            {
                aborted++;
            }

            foreach (var diagnostic in result.Diagnostics)
            {
                if (seen.Add((diagnostic.Line, diagnostic.Code, diagnostic.Message)))
                {
                    diagnostics.Add(diagnostic);
                }
            }
        }

        return new DocumentAssemblyReport(units, diagnostics, aborted);
    }

    private static List<string> BuildSearchPaths(string physicalPath)
    {
        var paths = new List<string>(2);
        var directory = VfsPath.GetDirectoryName(physicalPath);
        if (directory.Length != 0)
        {
            paths.Add(directory);
        }

        return paths;
    }

    private PlatformMacroSet ResolvePlatformMacros()
    {
        var index = _registry.Current.Index;
        return PlatformMacroResolver.Resolve(index.Layout, index.Installation, index.Layout.UnityVersion, _options.Platform);
    }

    private static List<Piece> BuildPieces(ShaderLabParseResult parse, ShaderPassSnippet snippet)
    {
        var pieces = new List<Piece>(snippet.ApplicableSharedBlockIndices.Count + 1);

        foreach (var sharedIndex in snippet.ApplicableSharedBlockIndices)
        {
            if (sharedIndex < 0 || sharedIndex >= parse.SharedBlocks.Count)
            {
                continue;
            }

            var block = parse.SharedBlocks[sharedIndex];
            if (block.Kind != snippet.Kind || block.ContentEndOffset <= block.ContentStartOffset)
            {
                continue;
            }

            pieces.Add(new Piece(sharedIndex, block.StartLineNumber + 1, block.ContentStartOffset, block.ContentEndOffset));
        }

        pieces.Add(new Piece(-1, snippet.StartLineNumber + 1, snippet.ContentStartOffset, snippet.ContentEndOffset));
        return pieces;
    }

    private static IReadOnlyList<ShaderVariantDeclaration> CollectVariants(ShaderLabParseResult parse, ShaderPassSnippet snippet)
    {
        if (snippet.ApplicableSharedBlockIndices.Count == 0)
        {
            return snippet.Variants;
        }

        var merged = new List<ShaderVariantDeclaration>(snippet.Variants);
        foreach (var sharedIndex in snippet.ApplicableSharedBlockIndices)
        {
            if (sharedIndex >= 0 && sharedIndex < parse.SharedSnippets.Count)
            {
                merged.AddRange(parse.SharedSnippets[sharedIndex].Variants);
            }
        }

        return merged;
    }

    private void WritePrologue(
        RenderedTextWriter writer,
        int firstAnchorLine,
        string virtualPath,
        ShaderStageInfo stageInfo,
        ShaderKeywordPlan plan,
        DynamicShaderContext dynamicContext)
    {
        writer.BeginSegment(SegmentKind.Injected);
        writer.WriteLine("// MicroShader 注入段：本段由工具生成，不属于用户代码；紧随其后的 #line 会把行号锚回源文件。");
        writer.WriteLine("// 阶段: " + (stageInfo.Macro.Length == 0
            ? "<未指定>"
            : stageInfo.Macro + " → " + stageInfo.TargetProfile));

        if (_options.IncludePlatformMacros)
        {
            var platform = PlatformMacros;
            writer.WriteLine("// " + platform.Describe());
            foreach (var note in platform.Notes)
            {
                writer.WriteLine("// " + note);
            }

            foreach (var macro in platform.Macros)
            {
                writer.WriteLine(FormatDefine(macro));
            }
        }

        if (_options.IncludeKeywordMatrix)
        {
            foreach (var line in plan.Lines)
            {
                writer.WriteLine(line);
            }
        }

        if (_options.IncludeDynamicMacros)
        {
            WriteDynamicMacroBlock(writer, dynamicContext);
        }

        writer.WriteLine(FormatLineAnchor(firstAnchorLine, virtualPath));
        writer.EndSegment();
    }

    /// <summary>
    /// 模块 11B：把 Unity 编辑器实时上报的全局宏状态写进注入段。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>**离线零漂移**：桥接不在线、或没上报任何宏时，一个字节也不写（下面的早退就是契约本身）。</item>
    /// <item>**先 #undef 再 #define**：直接重复定义会触发 C4005「宏重定义」告警，
    /// 那是我们自己的注入造出来的假告警，绝不能被归到用户代码上。</item>
    /// <item>顺序在平台宏**之后**：桥接数据是实测权威，平台宏里同名的推定项必须被它盖掉。</item>
    /// <item>宏名/宏值都做字符白名单校验：上报内容跨进程而来，绝不允许把换行、注释、
    /// <c>#line</c> 之类的结构字节直接拼进渲染文本（ADR-023 的结构不变量不允许被外部数据打破）。</item>
    /// </list>
    /// </remarks>
    private static void WriteDynamicMacroBlock(RenderedTextWriter writer, DynamicShaderContext dynamicContext)
    {
        if (!dynamicContext.IsLive)
        {
            return;
        }

        if (dynamicContext.DefinedMacros.IsEmpty && dynamicContext.UndefinedMacros.IsEmpty)
        {
            return;
        }

        const int MaxEmitted = 64;
        var emitted = 0;

        foreach (var name in dynamicContext.UndefinedMacros)
        {
            if (emitted >= MaxEmitted || !IsValidMacroName(name))
            {
                continue;
            }

            if (emitted == 0)
            {
                WriteDynamicHeader(writer, dynamicContext);
            }

            writer.WriteLine("#undef " + name);
            emitted++;
        }

        foreach (var entry in dynamicContext.DefinedMacros)
        {
            if (emitted >= MaxEmitted)
            {
                break;
            }

            var (name, value) = SplitMacroEntry(entry);
            if (!IsValidMacroName(name) || !IsValidMacroValue(value))
            {
                continue;
            }

            if (emitted == 0)
            {
                WriteDynamicHeader(writer, dynamicContext);
            }

            writer.WriteLine("#ifdef " + name);
            writer.WriteLine("#undef " + name);
            writer.WriteLine("#endif");
            writer.WriteLine(value.Length == 0 ? "#define " + name + " 1" : "#define " + name + " " + value);
            emitted++;
        }
    }

    private static void WriteDynamicHeader(RenderedTextWriter writer, DynamicShaderContext dynamicContext)
    {
        writer.WriteLine("// Unity 桥接动态宏（编辑器实时上报，覆盖上面的平台宏推断；已定义 "
            + dynamicContext.DefinedMacros.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 项 / 显式未定义 "
            + dynamicContext.UndefinedMacros.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " 项）");
    }

    /// <summary>拆分契约 2 里的宏项："X" 或 "X=1"。缺失时值按 ADR-018 取 1（裸 define 会让 URP 的 <c>#if K</c> 报 expected value）。</summary>
    private static (string Name, string Value) SplitMacroEntry(string entry)
    {
        if (entry.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        var separator = entry.IndexOf('=');
        if (separator < 0)
        {
            return (entry.Trim(), string.Empty);
        }

        return (entry[..separator].Trim(), entry[(separator + 1)..].Trim());
    }

    /// <summary>宏名白名单：<c>[A-Za-z_][A-Za-z0-9_]*</c>。</summary>
    private static bool IsValidMacroName(string name)
    {
        if (name.Length == 0 || name.Length > 128)
        {
            return false;
        }

        var first = name[0];
        if (!char.IsAsciiLetter(first) && first != '_')
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            var ch = name[i];
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>宏值白名单：只允许单行、且不含反斜杠（反斜杠可以把下一行续进来）。</summary>
    private static bool IsValidMacroValue(string value)
    {
        if (value.Length > 128)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch == '\\' || ch == '\n' || ch == '\r' || ch < 0x20)
            {
                return false;
            }
        }

        return true;
    }

    private void EmitSourceRange(
        RenderedTextWriter writer,
        ReadOnlySpan<char> source,
        SourceLineIndex lineIndex,
        in Piece piece,
        SourceShaderDocument document,
        List<ShaderDiagnosticItem> diagnostics,
        List<ShaderDiagnosticItem> deadIncludes)
    {
        var firstLine = lineIndex.LineOfOffset(piece.From);
        var lastLine = lineIndex.LineOfOffset(piece.To - 1);

        for (var line = firstLine; line <= lastLine; line++)
        {
            var start = Math.Max(lineIndex.LineStart(line), piece.From);
            var end = Math.Min(lineIndex.LineEnd(source, line, piece.To), piece.To);
            if (end <= start)
            {
                writer.WriteLine(ReadOnlySpan<char>.Empty, line);
                continue;
            }

            var lineText = source[start..end];
            if (!IncludeLineParser.TryParse(lineText, out var include))
            {
                writer.WriteLine(lineText, line);
                continue;
            }

            EmitIncludeLine(writer, lineText, line, include, document, diagnostics, deadIncludes);
        }
    }

    private void EmitIncludeLine(
        RenderedTextWriter writer,
        ReadOnlySpan<char> lineText,
        int line,
        in IncludeLine include,
        SourceShaderDocument document,
        List<ShaderDiagnosticItem> diagnostics,
        List<ShaderDiagnosticItem> deadIncludes)
    {
        var resolution = _registry.ResolveInclude(include.Path, document.FilePath, _options.VerifyIncludesOnDisk);

        // 只重写规则 1（Packages/ 虚拟前缀）与规则 2（Assets/ 前缀）：这两种写法 DXC 自己解析不了。
        // 规则 3/4（裸名 / 相对路径）一律保持原样交给 DXC —— 它以「当前文件所在目录 + 我们给的 -I」解析，
        // 语义与 Unity 完全一致；我们抢先重写成 <文档目录>名字 反而会把 CGIncludes 兜底弄丢。
        string? renderedPath = null;
        if (resolution.Found &&
            (resolution.Rule == IncludeRuleKind.PackageVirtualPrefix || resolution.Rule == IncludeRuleKind.AssetsPrefix))
        {
            renderedPath = resolution.PhysicalPath;
        }

        var dead = !resolution.Found &&
                   (resolution.Miss == IncludeMissKind.UnknownPackage || _options.VerifyIncludesOnDisk);
        if (dead)
        {
            var message = resolution.DescribeMissing(include.Path.AsSpan());
            if (resolution.Miss == IncludeMissKind.UnknownPackage)
            {
                message += " 该包不在当前工程的包拓扑中时，其全部 include 都无法解析（依赖扫描层结论）。";
            }

            deadIncludes.Add(new ShaderDiagnosticItem
            {
                Severity = DiagnosticSeverity.Error,
                Line = line,
                Column = include.PathStartIndex + 1,
                Code = DiagnosticEngineCodes.MissingInclude,
                TargetUri = document.FileUri,
                Message = message + " 该程序块不做原生编译，以免把虚拟路径喂给 DXC 产出二次误报。",
            });
        }

        var kindChanged = include.IsWithPragmas;
        var pathChanged = renderedPath is not null &&
                          !lineText.Slice(include.PathStartIndex, include.Path.Length)
                                   .SequenceEqual(renderedPath.AsSpan());

        if (!kindChanged && !pathChanged)
        {
            writer.WriteLine(lineText, line);
            return;
        }

        // 自我校验：切片器/解析器对同一行给出的列必须真的指向路径文字。
        var consistent = include.PathStartIndex + include.Path.Length <= lineText.Length &&
                         lineText.Slice(include.PathStartIndex, include.Path.Length)
                                  .SequenceEqual(include.Path.AsSpan());

        if (!consistent)
        {
            diagnostics.Add(new ShaderDiagnosticItem
            {
                Severity = DiagnosticSeverity.Warning,
                Line = line,
                Column = include.PathStartIndex + 1,
                Code = DiagnosticEngineCodes.InvariantViolation,
                TargetUri = document.FileUri,
                Message = "组装器在 include 行上未能把列号与路径文字对齐，已按兜底路径处理（仍会把 include_with_pragmas 降级为 include，但不做路径重写）。",
            });

            writer.WriteLine(kindChanged ? StripWithPragmas(lineText) : lineText.ToString(), line);
            return;
        }

        var sourcePrefix = lineText[..include.PathStartIndex];
        var sourceTail = lineText[(include.PathStartIndex + include.Path.Length)..];
        var renderedPrefix = kindChanged ? StripWithPragmas(sourcePrefix) : sourcePrefix.ToString();
        var targetPath = renderedPath ?? include.Path;

        var rendered = string.Concat(renderedPrefix, targetPath, sourceTail);

        var rewrite = new LineRewrite
        {
            RenderedLine = writer.NextLineNumber,
            SourceLine = line,
            Kind = kindChanged
                ? pathChanged ? LineRewriteKind.IncludeWithPragmasAndPath : LineRewriteKind.IncludeWithPragmasDowngrade
                : LineRewriteKind.IncludePathToPhysical,
            SourcePrefixLength = include.PathStartIndex,
            RenderedPrefixLength = renderedPrefix.Length,
            CommonPrefixLength = SharedPrefixLength(sourcePrefix, renderedPrefix.AsSpan()),
            SourcePathLength = include.Path.Length,
            RenderedPathLength = targetPath.Length,
            SourcePath = include.Path,
            RenderedPath = targetPath,
        };

        writer.WriteLine(rendered, line, writer.AddRewrite(rewrite));
    }

    /// <summary>两段前缀的最长公共前缀长度。</summary>
    private static int SharedPrefixLength(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var limit = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < limit && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private static string FormatLineAnchor(int line, string virtualPath) =>
        "#line " + line.ToString(System.Globalization.CultureInfo.InvariantCulture) + " \"" + virtualPath + "\"";

    private static string FormatDefine(in PlatformMacro macro) =>
        macro.Value.Length == 0 ? "#define " + macro.Name : "#define " + macro.Name + " " + macro.Value;

    /// <summary>把 "include_with_pragmas" 降级成 "include"（保留缩进与尖括号形态）。</summary>
    private static string StripWithPragmas(ReadOnlySpan<char> prefix)
    {
        var at = prefix.IndexOf("include_with_pragmas".AsSpan(), StringComparison.Ordinal);
        if (at < 0)
        {
            return prefix.ToString();
        }

        // 截取 "include" 部分 + 尾部，跳过 "_with_pragmas"，一次 Concat 出结果。
        var includeEnd = at + "include".Length;
        var tailStart = at + "include_with_pragmas".Length;
        return string.Concat(prefix[..includeEnd], prefix[tailStart..]);
    }

    /// <summary>编译单元的一段：文件级共享块（<see cref="SharedBlockIndex"/> ≥ 0）或 Pass 块（-1）。</summary>
    private readonly struct Piece
    {
        public Piece(int sharedBlockIndex, int anchorLine, int from, int to)
        {
            SharedBlockIndex = sharedBlockIndex;
            AnchorLine = anchorLine;
            From = from;
            To = to;
        }

        /// <summary>共享块索引；Pass 块为 -1。</summary>
        public int SharedBlockIndex { get; }

        /// <summary>"#line" 要声明的首行行号（= 开启标记行 + 1）。</summary>
        public int AnchorLine { get; }

        /// <summary>源文本起始偏移。</summary>
        public int From { get; }

        /// <summary>源文本结束偏移（独占）。</summary>
        public int To { get; }
    }
}

/// <summary>整篇文档的装配报告。</summary>
public sealed class DocumentAssemblyReport
{
    internal DocumentAssemblyReport(
        List<AssemblyResult> units,
        List<ShaderDiagnosticItem> diagnostics,
        int abortedUnits)
    {
        Units = units;
        Diagnostics = diagnostics;
        AbortedUnitCount = abortedUnits;
    }

    /// <summary>各编译单元的装配结果（含 "Text"，调用方负责 Dispose）。</summary>
    public IReadOnlyList<AssemblyResult> Units { get; }

    /// <summary>去重后的诊断。</summary>
    public IReadOnlyList<ShaderDiagnosticItem> Diagnostics { get; }

    /// <summary>未能装配出文本的单元数。</summary>
    public int AbortedUnitCount { get; }

    /// <summary>成功装配的单元数。</summary>
    public int SucceededUnitCount => Units.Count - AbortedUnitCount;

    /// <summary>释放全部单元持有的池化缓冲。</summary>
    public void DisposeAll()
    {
        foreach (var unit in Units)
        {
            unit.Text?.Dispose();
        }
    }
}
