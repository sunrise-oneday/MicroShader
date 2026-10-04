using MicroShader.Domain;

namespace MicroShader.DiagnosticEngine;

/// <summary>本编译单元参与编译的一条 "#include"：源文件里的位置 + 解析出的物理路径。</summary>
public readonly record struct IncludeBinding(int Line, int Column, string IncludePath, string? PhysicalPath);

public sealed class DiagnosticAttributionOptions
{
    /// <summary>是否保留 note 级诊断（默认丢弃：note 基本指向头文件内部声明，对 ShaderLab 作者是噪声）。</summary>
    public bool IncludeNotes { get; init; }

    /// <summary>是否过滤 "#pragma message" 产生的 warning（默认过滤；按源码行文本判定，不靠消息文案猜）。</summary>
    public bool FilterPragmaMessages { get; init; } = true;

    /// <summary>是否把外部头文件位置放进 relatedInformation。</summary>
    public bool AttachRelatedInformation { get; init; } = true;
}

/// <summary>一次归属映射的统计（用于黄金测试与排障，不面向用户）。</summary>
public sealed class DiagnosticAttribution
{
    public List<ShaderDiagnosticItem> Items { get; } = new();

    public int RawCount { get; internal set; }

    public int DroppedNotes { get; internal set; }

    public int DroppedPragmaMessages { get; internal set; }

    /// <summary>诊断落在装配注入段里 —— 说明宏矩阵/装配自身有缺陷，必须当工具缺陷暴露。</summary>
    public int InternalErrors { get; internal set; }

    /// <summary>既不属于主文件也不属于任何已知 include 的诊断，只能挂到单元起始行。</summary>
    public int Unmapped { get; internal set; }

    public int DegradedColumns { get; internal set; }
}

/// <summary>
/// 诊断归属映射器：把 DXC 的原始诊断（渲染文本坐标 + UTF-8 字节列）映射回源文件坐标。
///
/// 坐标链路（三段，各自只做一件事）：
///   ① DXC 列 = 渲染行内的 1-based UTF-8 字节偏移 → UTF-16 列（<see cref="Utf8Column"/>）；
///   ② 若渲染行被重写（include 路径改写 / with_pragmas 降级）→ 按公共前缀分段映射回源列（<see cref="LineRewrite"/>）；
///   ③ 渲染行 → 源行（<see cref="RenderedLineMap.SourceLineOf"/>，由 "#line" 锚定保证）。
///
/// 外部头文件（含深层依赖）不走 "#line" 锚定，而是按「谁 #include 了它」反查归属：
///   直接包含 → 红线挂在主文件的那条 "#include" 行；
///   深层包含 → 挂在主文件包含其祖先目录的那条 "#include" 行，消息里带真实文件与行。
/// </summary>
public sealed class DiagnosticAttributor
{
    private readonly DiagnosticAttributionOptions _options;

    public DiagnosticAttributor(DiagnosticAttributionOptions? options = null)
        => _options = options ?? new DiagnosticAttributionOptions();

    public DiagnosticAttribution Attribute(
        DxcCompileResult result,
        AssembledShaderText? text,
        IReadOnlyList<IncludeBinding> includes,
        string targetUri,
        int unitStartSourceLine)
    {
        var attribution = new DiagnosticAttribution();
        if (result.DiagnosticText.Length == 0)
        {
            return attribution;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        // include 归属索引：本单元内所有外部诊断共用一份（惰性构建）。
        // 原实现是「每条外部诊断都对整个 include 列表做线性反查，且每个 binding 每次都要 Normalize 一次」，
        // 即 O(诊断数 x include 数) 次字符串分配。
        IncludeIndex? includeIndex = null;

        foreach (var raw in DxcDiagnosticParser.Parse(result.DiagnosticText))
        {
            attribution.RawCount++;

            if (raw.Severity == DiagnosticSeverity.Information && !_options.IncludeNotes)
            {
                attribution.DroppedNotes++;
                continue;
            }

            if (_options.FilterPragmaMessages
                && raw.Severity == DiagnosticSeverity.Warning
                && text is not null
                && raw.Line >= 1
                && text.LineMap.TryMapSourceLineToRendered(raw.Line, out var pragmaRenderedLine)
                && IsPragmaMessageLine(text.GetLine(pragmaRenderedLine)))
            {
                attribution.DroppedPragmaMessages++;
                continue;
            }

            ShaderDiagnosticItem item;
            if (text is not null && IsSamePath(raw.File, text.VirtualPath))
            {
                item = MapMainFile(raw, text, targetUri, unitStartSourceLine, attribution);
            }
            else if (text is null || raw.File.Length == 0 || IsSamePath(raw.File, text.PhysicalPath) || IsDxcPlaceholderName(raw.File))
            {
                // 头部注入段（宏矩阵）发生在第一条 #line 之前，DXC 只能拿源名/默认名报出来。
                attribution.InternalErrors++;
                item = new ShaderDiagnosticItem
                {
                    Severity = raw.Severity,
                    Line = unitStartSourceLine,
                    Column = 0,
                    Message = "装配注入段报错（工具内部缺陷）: " + raw.Message,
                    TargetUri = targetUri,
                    Code = DiagnosticEngineCodes.AssemblyPrologue,
                };
            }
            else
            {
                item = MapExternalFile(raw, includeIndex ??= new IncludeIndex(includes), targetUri, unitStartSourceLine, attribution);
            }

            var key = item.Line + ":" + item.Column + ":" + (int)item.Severity + ":" + item.Message;
            if (seen.Add(key))
            {
                attribution.Items.Add(item);
            }
        }

        return attribution;
    }

    private ShaderDiagnosticItem MapMainFile(
        DxcDiagnostic raw,
        AssembledShaderText text,
        string targetUri,
        int unitStartSourceLine,
        DiagnosticAttribution attribution)
    {
        var map = text.LineMap;

        // 关键：DXC 报出的行号已经是"源文件"行号 —— 那正是我们下发 #line 的目的。
        // 因此这里绝不能再去查 SourceLineOf（那是二次映射，会把坐标顶回去）。
        // 渲染行只用于一件事：把 DXC 的 UTF-8 字节列换算成字符列。
        if (!map.TryMapSourceLineToRendered(raw.Line, out var renderedLine))
        {
            attribution.InternalErrors++;
            return new ShaderDiagnosticItem
            {
                Severity = raw.Severity,
                Line = unitStartSourceLine,
                Column = 0,
                Message = "装配注入段报错（工具内部缺陷）: " + raw.Message,
                TargetUri = targetUri,
                Code = DiagnosticEngineCodes.AssemblyPrologue,
            };
        }

        var sourceLine = raw.Line;
        if (raw.Column <= 0)
        {
            return new ShaderDiagnosticItem
            {
                Severity = raw.Severity,
                Line = sourceLine,
                Column = 0,
                Message = raw.Message,
                TargetUri = targetUri,
            };
        }

        var utf16 = Utf8Column.ToUtf16Column(text.GetLine(renderedLine), raw.Column, out var exact);
        if (!exact)
        {
            attribution.DegradedColumns++;
        }
        else if (map.TryGetRewrite(renderedLine, out var rewrite))
        {
            utf16 = rewrite.MapRenderedColumnToSource(utf16);
        }

        return new ShaderDiagnosticItem
        {
            Severity = raw.Severity,
            Line = sourceLine,
            Column = exact ? utf16 : 0,
            Message = raw.Message,
            TargetUri = targetUri,
            Code = exact ? null : DiagnosticEngineCodes.ColumnDegraded,
        };
    }

    private ShaderDiagnosticItem MapExternalFile(
        DxcDiagnostic raw,
        IncludeIndex includes,
        string targetUri,
        int unitStartSourceLine,
        DiagnosticAttribution attribution)
    {
        var exact = includes.TryMatchExact(raw.File, out var exactBinding) ? exactBinding : (IncludeBinding?)null;
        var line = unitStartSourceLine;
        var column = 0;
        var message = raw.Message;

        if (exact is { } binding)
        {
            line = binding.Line;
            column = binding.Column;
        }
        else
        {
            var ancestor = includes.TryMatchAncestor(raw.File, out var ancestorBinding) ? ancestorBinding : (IncludeBinding?)null;
            if (ancestor is { } deep)
            {
                line = deep.Line;
                column = deep.Column;
                message = $"深层依赖错误: [{FileName(raw.File)}:{raw.Line}] {raw.Message} (通过 {FileName(deep.PhysicalPath!)} 引入)";
            }
            else
            {
                attribution.Unmapped++;
                message = $"外部头文件错误: [{raw.File}:{raw.Line}] {raw.Message}";
            }
        }

        return new ShaderDiagnosticItem
        {
            Severity = raw.Severity,
            Line = line,
            Column = column,
            Message = message,
            TargetUri = targetUri,
            RelatedFilePath = _options.AttachRelatedInformation ? Normalize(raw.File) : null,
            RelatedLine = _options.AttachRelatedInformation ? raw.Line : null,
        };
    }

    private sealed class IncludeIndex
    {
        private readonly Dictionary<string, IncludeBinding> _byPhysicalPath;
        private readonly (string Prefix, IncludeBinding Binding)[] _ancestorPrefixes;

        public IncludeIndex(IReadOnlyList<IncludeBinding> includes)
        {
            _byPhysicalPath = new Dictionary<string, IncludeBinding>(includes.Count, StringComparer.OrdinalIgnoreCase);
            var ancestors = new List<(string, IncludeBinding)>(includes.Count);

            foreach (var binding in includes)
            {
                if (binding.PhysicalPath is not { Length: > 0 } physical)
                {
                    continue;
                }

                // 归一化只在构建期做一次（原实现是每条外部诊断 x 每条 include 都做）。
                var normalized = Normalize(physical);
                _byPhysicalPath[normalized] = binding;

                var slash = normalized.LastIndexOf('/');
                if (slash > 0)
                {
                    ancestors.Add((normalized[..(slash + 1)], binding));
                }
            }

            // 目录前缀长度降序：首个命中即最长前缀，与原来的 bestLength 扫描语义等价，但不做任何分配。
            ancestors.Sort(static (a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
            _ancestorPrefixes = ancestors.ToArray();
        }

        /// <summary>精确命中：某条直接 include 解析出的物理路径就是报到诊断的那个文件。</summary>
        public bool TryMatchExact(string file, out IncludeBinding binding)
            => _byPhysicalPath.TryGetValue(Normalize(file), out binding);

        /// <summary>深层依赖：诊断文件位于某条直接 include 所在目录之下，取最长的那个目录前缀。</summary>
        public bool TryMatchAncestor(string file, out IncludeBinding binding)
        {
            var target = Normalize(file);
            foreach (var (prefix, candidate) in _ancestorPrefixes)
            {
                if (target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    binding = candidate;
                    return true;
                }
            }

            binding = default;
            return false;
        }
    }

    private static bool IsPragmaMessageLine(ReadOnlySpan<char> line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("#pragma", StringComparison.Ordinal)
            && trimmed["#pragma".Length..].TrimStart().StartsWith("message", StringComparison.Ordinal);
    }

    private static bool IsDxcPlaceholderName(string file)
        => file.Equals("hlsl.hlsl", StringComparison.OrdinalIgnoreCase);

    internal static string Normalize(string path) => path.Replace('\\', '/');

    internal static bool IsSamePath(string a, string b)
        => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string FileName(string path)
    {
        var slash = path.LastIndexOfAny(['/', '\\']);
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
