using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;
using MicroShader.ShaderLab;

namespace MicroShader.NavigationEngine;

/// <summary>
/// 导航服务：把 definition / documentLink / documentSymbol 请求变成写进 LSP 响应 writer 的 JSON。
/// </summary>
/// <remarks>
/// 
/// "零 DTO、零反射序列化"（ADR-026）：结果直接写进调用方给的 <see cref="Utf8JsonWriter"/>，
/// 因此 AOT 下不存在「缺序列化元数据」的失败模式。
/// 
/// "三种形状按客户端能力分派"（详设 v2.0 第 1、8 条）：definition 回
/// "LocationLink[]" 或 "Location[]"（由 <see cref="NavigationOptions.LinkSupport"/> 决定）；
/// documentSymbol 回 "DocumentSymbol[]" 或扁平的 "SymbolInformation[]"
/// （由 <see cref="NavigationOptions.HierarchicalDocumentSymbol"/> 决定）。
/// 
/// "不进防抖队列"（详设 §五）：F12 与 Ctrl+点击都直接同步算完就回，
/// 与后台 DXC 编译完全解耦 —— 跳转手感不能被编译阻塞。
/// </remarks>
public sealed class NavigationService
{
    private readonly INavigationHost _host;
    private readonly IncludeSymbolCache _cache;
    private readonly NavigationOptions _options;
    private readonly DefinitionTracer _tracer;
    private readonly HlslBlockIndexCache _blockIndexCache;

    // 复用的解析器与解析结果（与模块 9 同一模式）：Parse(text, into) 会把 snippet 归还池，
    // 避免每次请求都新建一份解析结果与状态机。导航的请求都在会话读循环上串行执行，无并发。
    private readonly ShaderLabStateMachine _machine = new();
    private readonly ShaderLabParseResult _parseResult = new();

    /// <summary>客户端能力快照（"initialize" 之后由宿主回填）；默认按最保守形状回。</summary>
    private bool _linkSupport;
    private bool _hierarchicalSymbols;
    private int[] _symbolKindValueSet = [];

    public NavigationService(
        INavigationHost host,
        IncludeSymbolCache cache,
        NavigationOptions? options = null,
        HlslBlockIndexCache? blockIndexCache = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(cache);

        _host = host;
        _cache = cache;
        _options = options ?? new NavigationOptions();
        _blockIndexCache = blockIndexCache ?? new HlslBlockIndexCache();
        _tracer = new DefinitionTracer(host, cache, _options, _blockIndexCache);

        _linkSupport = _options.LinkSupport;
        _hierarchicalSymbols = _options.HierarchicalDocumentSymbol;
        _symbolKindValueSet = _options.SymbolKindValueSet;
    }

    /// <summary>
    /// 回填客户端能力快照（会话在 "initialize" 时调用一次）。
    /// </summary>
    /// <remarks>
    /// 详设 v2.0 第 1 条：返回形状必须由客户端能力决定。写成构造参数是不可能的 ——
    /// 组合根构造服务时还没收到 "initialize"；写成会话侧分支则会让「形状决策」
    /// 漏到传输层。这里的做法是在引擎内保存快照，由会话转达。
    /// </remarks>
    public void ApplyCapabilities(bool linkSupport, bool hierarchicalDocumentSymbols, int[] symbolKindValueSet)
    {
        _linkSupport = linkSupport;
        _hierarchicalSymbols = hierarchicalDocumentSymbols;
        _symbolKindValueSet = symbolKindValueSet ?? [];
    }

    // ───────────────────────────── F12 ─────────────────────────────

    /// <summary>"textDocument/definition"。</summary>
    public bool ProvideDefinition(PositionRequest request, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var candidates = _tracer.Trace(request.Uri, request.Text, request.Line, request.Character);
        if (candidates.Count == 0)
        {
            return false;   // 会话兜底写 null —— 「找不到定义」的合法表达
        }

        var lineStarts = LineOffsets.Build(request.Text);
        SymbolAtCursor.TryFind(
            request.Text, lineStarts, request.Line, request.Character, out var originName, out var originOffset);

        var origin = (Start: originOffset, End: originOffset + originName.Length);

        writer.WriteStartArray();

        foreach (var candidate in candidates)
        {
            writer.WriteStartObject();

            if (_linkSupport)
            {
                writer.WritePropertyName("originSelectionRange");
                WriteRange(writer, lineStarts, origin.Start, origin.End);

                writer.WriteString("targetUri", candidate.Uri);

                writer.WritePropertyName("targetRange");
                WriteRange(writer, candidate.Line, 0, candidate.Line, int.MaxValue);

                writer.WritePropertyName("targetSelectionRange");
                WriteRange(writer, candidate.Line, candidate.Character, candidate.Line, candidate.EndCharacter);
            }
            else
            {
                writer.WriteString("uri", candidate.Uri);

                writer.WritePropertyName("range");
                WriteRange(writer, candidate.Line, candidate.Character, candidate.Line, candidate.EndCharacter);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        return true;
    }

    // ───────────────────── #include 超链接（两段式）─────────────────────

    /// <summary>
    /// "textDocument/documentLink" 首屏："纯词法"扫描，不算路径、不碰磁盘。
    /// </summary>
    /// <remarks>
    /// 详设 v2.0 第 4 条：只给 range + tooltip + data，"target" 留给 resolve 期。
    /// 含 200 个 include 的文件因此把 200 次 VFS 查询降到 0 次。
    /// </remarks>
    public bool ProvideDocumentLinks(string uri, string filePath, string text, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var links = DocumentLinkScanner.Scan(text);
        if (links.Count == 0)
        {
            return false;
        }

        var lineStarts = LineOffsets.Build(text);

        writer.WriteStartArray();

        foreach (var link in links)
        {
            writer.WriteStartObject();

            writer.WritePropertyName("range");
            WriteRange(writer, lineStarts, link.RangeStart, link.RangeEnd);

            writer.WriteString("tooltip", "打开 " + link.Target);

            writer.WritePropertyName("data");
            writer.WriteStartObject();
            writer.WriteString("target", link.Target);

            // 候选物理路径在首屏算好（纯字符串运算，不做存在性检查）：
            // 这样 resolve 期只剩一次 File.Exists / 影子查询，仍是详设要求的「廉价 resolve」。
            writer.WriteString("path", DocumentLinkResolver.CandidatePath(_host, uri, link));
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        return true;
    }

    /// <summary>
    /// "textDocument/documentLink/resolve"：补全 "target"。
    /// </summary>
    /// <remarks>
    /// 入参是客户端把服务端先前给出的那条 link "原样回传"，因此这里要把它重新读出来
    /// （range / tooltip / data）再补上 target。目标解析不到或文件不存在 → 返回 "false"，
    /// 会话写 "null"：规范允许，客户端表现为该链接不可点击 ——
    /// 而不是点了之后弹一个 "File not found"。
    /// </remarks>
    public bool ResolveDocumentLink(ReadOnlySequence<byte> parameters, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (!TryReadLink(parameters, out var link))
        {
            return false;
        }

        var target = DocumentLinkResolver.Resolve(_host, link.Path);
        if (target is null)
        {
            return false;
        }

        writer.WriteStartObject();

        writer.WritePropertyName("range");
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WritePosition(writer, link.StartLine, link.StartCharacter);
        writer.WritePropertyName("end");
        WritePosition(writer, link.EndLine, link.EndCharacter);
        writer.WriteEndObject();

        writer.WriteString("target", target);
        writer.WriteString("tooltip", link.Tooltip.Length > 0 ? link.Tooltip : link.Target);

        writer.WritePropertyName("data");
        writer.WriteStartObject();
        writer.WriteString("target", link.Target);
        writer.WriteString("path", link.Path);
        writer.WriteEndObject();

        writer.WriteEndObject();
        return true;
    }

    private readonly record struct LinkPayload(
        int StartLine,
        int StartCharacter,
        int EndLine,
        int EndCharacter,
        string Target,
        string Path,
        string Tooltip);

    private static bool TryReadLink(ReadOnlySequence<byte> parameters, out LinkPayload link)
    {
        link = default;

        // 这里用 JsonDocument 而不是 Utf8JsonReader：resolve 是"用户悬停/点击才发生"的稀有请求
        // （详设 v2.0 第 4 条），把健壮性放在省一次解析分配之上 —— 手写单遍 reader 很容易
        // 在「data 对象要整块跳过、range 的四个数字又要逐个取出」这种混合结构上写错。
        // 注意这与 ADR-026 不冲突：被禁止的是"反射式序列化"，不是 System.Text.Json 本身。
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(parameters);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            var target = string.Empty;
            var path = string.Empty;
            var tooltip = string.Empty;

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("target", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    target = t.GetString() ?? string.Empty;
                }

                if (data.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    path = p.GetString() ?? string.Empty;
                }
            }

            if (root.TryGetProperty("tooltip", out var tip) && tip.ValueKind == JsonValueKind.String)
            {
                tooltip = tip.GetString() ?? string.Empty;
            }

            var startLine = 0;
            var startCharacter = 0;
            var endLine = 0;
            var endCharacter = 0;

            if (root.TryGetProperty("range", out var range) && range.ValueKind == JsonValueKind.Object)
            {
                ReadPoint(range, "start", out startLine, out startCharacter);
                ReadPoint(range, "end", out endLine, out endCharacter);
            }

            if (path.Length == 0 && target.Length == 0)
            {
                return false;
            }

            link = new LinkPayload(startLine, startCharacter, endLine, endCharacter, target, path, tooltip);
            return true;
        }
    }

    private static void ReadPoint(JsonElement range, string name, out int line, out int character)
    {
        line = 0;
        character = 0;

        if (!range.TryGetProperty(name, out var point) || point.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (point.TryGetProperty("line", out var l) && l.ValueKind == JsonValueKind.Number)
        {
            if (l.TryGetInt32(out var li)) line = li;
        }

        if (point.TryGetProperty("character", out var c) && c.ValueKind == JsonValueKind.Number)
        {
            if (c.TryGetInt32(out var ci)) character = ci;
        }
    }

    // ───────────────────────────── 大纲 ─────────────────────────────

    /// <summary>"textDocument/documentSymbol"。</summary>
    public bool ProvideDocumentSymbols(string uri, string filePath, string text, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        _machine.Parse(uri, text, _parseResult);

        var root = SymbolOutlineBuilder.Build(text, _parseResult, _symbolKindValueSet, _blockIndexCache);
        if (root.Children.Count == 0)
        {
            return false;
        }

        writer.WriteStartArray();

        if (_hierarchicalSymbols)
        {
            WriteHierarchical(writer, root, uri);
        }
        else
        {
            WriteFlat(writer, root, uri, root.Name);
        }

        writer.WriteEndArray();
        return true;
    }

    // ───────────────────────────── 折叠 ─────────────────────────────

    /// <summary>"textDocument/foldingRange"。</summary>
    /// <remarks>
    /// 折叠范围只依赖文本本身（大括号结构 + 预处理器 + 程序块），不读 include、不碰磁盘，
    /// 因此与 F12 一样不进防抖队列，同步算完就回。
    /// 这份数据同时是粘滞滚动窗口 foldingProviderModel 的条目来源，收录与排除的取舍见
    /// <see cref="FoldingRangeBuilder"/>。
    /// </remarks>
    public bool ProvideFoldingRanges(string uri, string filePath, string text, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        return FoldingRangeBuilder.TryWrite(text, writer);
    }

    private static void WriteHierarchical(Utf8JsonWriter writer, OutlineNode node, string uri)
    {
        writer.WriteStartObject();
        writer.WriteString("name", node.Name);

        if (node.Detail.Length > 0)
        {
            writer.WriteString("detail", node.Detail);
        }

        writer.WriteNumber("kind", node.Kind);

        writer.WritePropertyName("range");
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WritePosition(writer, node.StartLine, node.StartChar);
        writer.WritePropertyName("end");
        WritePosition(writer, node.EndLine, node.EndChar);
        writer.WriteEndObject();

        writer.WritePropertyName("selectionRange");
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WritePosition(writer, node.Line, node.SelStart);
        writer.WritePropertyName("end");
        WritePosition(writer, node.Line, node.SelEnd);
        writer.WriteEndObject();

        if (node.Children.Count > 0)
        {
            writer.WritePropertyName("children");
            writer.WriteStartArray();

            foreach (var child in node.Children)
            {
                WriteHierarchical(writer, child, uri);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteFlat(Utf8JsonWriter writer, OutlineNode node, string uri, string containerName)
    {
        foreach (var child in node.Children)
        {
            writer.WriteStartObject();
            writer.WriteString("name", child.Name);
            writer.WriteNumber("kind", child.Kind);

            // containerName 始终写：符号无层级时它就是顶层容器（Shader 名），
            // 客户端据此把它归到正确的大纲分组里；留空会让顶层符号散在外面。
            writer.WriteString("containerName", containerName);

            writer.WritePropertyName("location");
            writer.WriteStartObject();
            writer.WriteString("uri", uri);
            writer.WritePropertyName("range");
            writer.WriteStartObject();
            writer.WritePropertyName("start");
            WritePosition(writer, child.Line, child.SelStart);
            writer.WritePropertyName("end");
            WritePosition(writer, child.Line, child.SelEnd);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();

            WriteFlat(writer, child, uri, child.Name);
        }
    }

    // ───────────────────────────── JSON 基元 ─────────────────────────────

    private static void WritePosition(Utf8JsonWriter writer, int line, int character)
    {
        writer.WriteStartObject();
        writer.WriteNumber("line", line);
        writer.WriteNumber("character", character);
        writer.WriteEndObject();
    }

    private static void WriteRange(Utf8JsonWriter writer, int[] lineStarts, int startOffset, int endOffset)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WritePosition(writer, LineOffsets.LineOf(lineStarts, startOffset), LineOffsets.CharacterOf(lineStarts, startOffset));
        writer.WritePropertyName("end");
        WritePosition(writer, LineOffsets.LineOf(lineStarts, endOffset), LineOffsets.CharacterOf(lineStarts, endOffset));
        writer.WriteEndObject();
    }

    private static void WriteRange(Utf8JsonWriter writer, int startLine, int startCharacter, int endLine, int endCharacter)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("start");
        WritePosition(writer, startLine, startCharacter);
        writer.WritePropertyName("end");
        WritePosition(writer, endLine, endCharacter);
        writer.WriteEndObject();
    }
}
