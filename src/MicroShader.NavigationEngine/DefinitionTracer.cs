using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;
using MicroShader.ObservabilityEngine;
using MicroShader.ShaderLab;

namespace MicroShader.NavigationEngine;

/// <summary>一个定义候选（已换算成 LSP 位置）。</summary>
/// <param name="Offset">声明在其所属文本里的绝对偏移；仅本地候选参与「最近优先」排序。</param>
/// <param name="IsLocal">是否来自本文件（本地候选之间按离光标距离排序）。</param>
internal sealed record DefinitionCandidate(
    string Uri,
    int Line,
    int Character,
    int EndCharacter,
    int Kind,
    string Detail,
    string Container,
    string Rank,
    int Offset = 0,
    bool IsLocal = false);

/// <summary>
/// "textDocument/definition"（F12）的穿透回溯器。
/// </summary>
/// <remarks>
/// 
/// "三级来源，按优先级"：
/// ① 本文件当前块 + 其生效的 "HLSLINCLUDE" 共享块（Unity 语义：共享块属于每个 Pass，
/// 所以声明在 "HLSLINCLUDE" 里的 "VertexOutput" 对 Pass 内的光标可见）；
/// ② include 闭包里的头文件（按预热时的 BFS 顺序，即「越近越优先」）；
/// ③ 宏别名解包（"#define A B"：同时给「宏定义处」与「展开后的 B 的实现处」两个候选，
/// 见详设 v2.0 第 6 条 —— 静默替换会让使用者永远看不到宏定义本身）。
/// 
/// "1→N"：HLSL/URP 大量重载（实测函数名跨文件重复 41/984、宏名 177/1162），
/// 本回溯器保留全部候选并按「近 → 远」排序，交由客户端弹 Peek 列表，绝不「取第一条」。
/// 
/// "不返回失败也不返回错行"（详设 v2.0 第 3 条）：缓存里若有陈旧条目，先比对写入戳，
/// 不一致就"现场补扫该文件"（上限 <see cref="NavigationOptions.MaxCrossFileProbes"/> 个），
/// 补扫预算用完时直接跳过该文件 —— 两种做法都不是拿旧行号去跳。
/// 
/// "已知边界"：不实现「符号级作用域」——同一块内同名的局部变量与全局变量都算候选择。
/// 也不做条件编译求值（"#if" 分支里的声明照收），与索引器的口径一致。
/// </remarks>
internal sealed class DefinitionTracer
{
    private readonly INavigationHost _host;
    private readonly IncludeSymbolCache _cache;
    private readonly NavigationOptions _options;
    private readonly HlslBlockIndexCache _blockIndexCache;

    // 复用的解析器与解析结果：F12 与大纲共用同一套会话线程上的实例，Parse(text, into)
    // 会重置并复用 snippet 池，省掉每次请求 new ShaderLabStateMachine/ParseResult 的分配。
    private readonly ShaderLabStateMachine _machine;
    private readonly ShaderLabParseResult _parseResult;

    // 写入点扫描的复用缓冲：F12 连按时不再为每个块 new 一个 List。
    private readonly List<StructMemberWrites.Site> _writeBuffer = new(8);

    public DefinitionTracer(
        INavigationHost host,
        IncludeSymbolCache cache,
        NavigationOptions options,
        HlslBlockIndexCache blockIndexCache,
        ShaderLabStateMachine machine,
        ShaderLabParseResult parseResult)
    {
        _host = host;
        _cache = cache;
        _options = options;
        _blockIndexCache = blockIndexCache;
        _machine = machine;
        _parseResult = parseResult;
    }

    /// <summary>
    /// 追踪光标处的定义候选。<paramref name="lineStarts"/> 与 <c>origin*</c> 回传给
    /// 调用方复用：这些量本来就是本方法内部算出来的，外部再算一遍等于把整篇文本
    /// 的扫描与行首表构建重复一次。
    /// </summary>
    public List<DefinitionCandidate> Trace(
        string uri,
        string text,
        int[] lineStarts,
        int line,
        int character,
        out string originName,
        out int originOffset)
    {
        var found = new List<DefinitionCandidate>();

        if (!SymbolAtCursor.TryFind(text, lineStarts, line, character, out originName, out originOffset))
        {
            return found;
        }

        // 复用解析器与解析结果（与 NavigationService 的大纲路径同一模式）。
        var parsed = _machine.Parse(uri, text, _parseResult);

        var name = originName;
        var cursorOffset = lineStarts[Math.Min(Math.Max(line, 0), lineStarts.Length - 1)] + Math.Max(character, 0);

        var localBlocks = LocalScope(parsed, cursorOffset);
        var localMacro = false;
        foreach (var block in localBlocks)
        {
            localMacro |= CollectFromBlock(found, uri, text, lineStarts, block, name, "local", "本文件", _blockIndexCache);
        }

        // 成员访问的写入点：片元里读 IN.positionWS，真正要看的往往是顶点里写它的那一行。
        // 必须在下面的早退之前做 —— 字段声明通常就在本地块里，早退之后就到不了这里。
        CollectMemberWrites(found, uri, text, lineStarts, localBlocks, name, originOffset);

        // 本地作用域一旦命中就返回：既符合「文件内优先」的语义，也是 F12 能守住 3ms 的原因
        // （详设 §五）。继续扫 include 链只会把它自己的声明再报一遍，还平白读盘。
        if (found.Count > 0)
        {
            // 唯一的例外：本地命中的是个宏。此时「定义处」只是半份答案 —— 契约要求
            // 宏别名同时给出展开后的实现位置（详设 v2.0 第 6 条），而写在 HLSLINCLUDE
            // 里的「#define A B」正是最常见的那种。非宏的本地命中照旧直接返回。
            if (localMacro)
            {
                ExpandMacroAliases(found, uri, text, lineStarts, parsed, localBlocks, IncludeClosure(uri, text, parsed), name);
            }

            return Finish(found, cursorOffset);
        }

        var files = IncludeClosure(uri, text, parsed);
        var probed = 0;

        foreach (var file in files)
        {
            // 陈旧检测在这里与宏别名、宏可见性共用一个入口：写入戳变了说明磁盘上的文本已经改过，
            // 快照里那些行号对应的是旧文本；预算还有就现场补扫，耗尽就整份跳过。
            // 宁可少给一条候选（客户端表现为跳不到），也不给错位置：错的位置比没有更让人迷惑。
            if (!TryGetFreshSnapshot(file, ref probed, out var symbols))
            {
                continue;
            }

            CollectFromSymbols(found, file, symbols, name, "include", Path.GetFileName(file));
        }

        ExpandMacroAliases(found, uri, text, lineStarts, parsed, localBlocks, files, name);

        return Finish(found, cursorOffset);
    }

    // ───────────────────────── 本地作用域 ─────────────────────────

    /// <summary>
    /// 光标所在块，外加它生效的 "HLSLINCLUDE" 共享块。
    /// </summary>
    /// <remarks>
    /// "为什么共享块要算进本地"：Unity 语义下 "HLSLINCLUDE" 的正文会被拼进本文件内
    /// "每一个" "HLSLPROGRAM" 块的头部，所以它声明的结构体对 Pass 内的光标是本文件可见的。
    /// 漏掉这一步，用户那份手写 shader（结构体全写在 "HLSLINCLUDE" 里）按 F12 会跳不动。
    /// </remarks>
    private static List<ShaderPassSnippet> LocalScope(ShaderLabParseResult parsed, int cursorOffset)
    {
        var scope = new List<ShaderPassSnippet>();
        ShaderPassSnippet? current = null;

        foreach (var pass in parsed.Passes)
        {
            if (cursorOffset >= pass.ContentStartOffset && cursorOffset <= pass.ContentEndOffset)
            {
                current = pass;
                break;
            }
        }

        if (current is not null)
        {
            scope.Add(current);
            foreach (var index in current.ApplicableSharedBlockIndices)
            {
                if (index >= 0 && index < parsed.SharedSnippets.Count)
                {
                    scope.Add(parsed.SharedSnippets[index]);
                }
            }

            return scope;
        }

        // 光标不在任何程序块内（例如停在 ShaderLab 的 Properties 段）：
        // 只有文件级共享块可能相关。
        scope.AddRange(parsed.SharedSnippets);
        return scope;
    }

    /// <summary>
    /// 收集一个块里的同名声明；返回是否收进了宏（宏还要再走一次别名解包）。
    /// </summary>
    private static bool CollectFromBlock(
        List<DefinitionCandidate> found,
        string uri,
        string text,
        int[] lineStarts,
        ShaderPassSnippet block,
        string name,
        string rank,
        string container,
        HlslBlockIndexCache blockIndexCache)
    {
        var index = blockIndexCache.GetOrBuild(block.RawHlslBlock);
        if (!index.Declarations.TryGetValue(name, out var declarations))
        {
            return false;
        }

        var hasMacro = false;
        foreach (var decl in declarations)
        {
            hasMacro |= decl.Kind == HlslDeclKind.Macro;
            var offset = block.ContentStartOffset + decl.Offset;
            var (lineNumber, character) = ToPosition(lineStarts, offset);
            found.Add(new DefinitionCandidate(
                uri,
                lineNumber,
                character,
                character + decl.Length,
                LspKindOf(decl.Kind),
                DetailOf(decl),
                container,
                rank + ":" + lineNumber,
                offset,
                IsLocal: true));
        }

        return hasMacro;
    }

    // ───────────────────────── 成员写入点 ─────────────────────────

    /// <summary>
    /// 光标停在「接收者.字段」这种成员访问上时，补上同类型变量对该字段的写入点。
    /// </summary>
    /// <remarks>
    /// 典型场景：片元里读 IN.positionWS，顶点里写 o.positionWS = …。字段声明回答「它是什么类型」，
    /// 写入点回答「值从哪来」—— 后者才是作者按 F12 时要找的那一行，所以写入点排在声明之前。
    ///
    /// 只在接收者的类型能从本文件解析出来时给：类型未知（结构体定义在 include 里、或接收者是
    /// 嵌套成员）就整段跳过，保持原行为，绝不按字段名跨类型乱配。
    /// </remarks>
    private void CollectMemberWrites(
        List<DefinitionCandidate> found,
        string uri,
        string text,
        int[] lineStarts,
        List<ShaderPassSnippet> scope,
        string field,
        int originOffset)
    {
        if (!StructMemberWrites.TryFindReceiver(text, originOffset, out var receiver))
        {
            return;
        }

        var typeName = ResolveReceiverType(scope, receiver, field);
        if (typeName is null)
        {
            return;
        }

        foreach (var block in scope)
        {
            var index = _blockIndexCache.GetOrBuild(block.RawHlslBlock);

            _writeBuffer.Clear();
            StructMemberWrites.ScanWrites(block.RawHlslBlock.Span, index.VariablesToType, typeName, field, _writeBuffer);

            foreach (var site in _writeBuffer)
            {
                var offset = block.ContentStartOffset + site.Offset;
                if (offset == originOffset)
                {
                    continue;   // 光标本来就停在这条写入上，不必把它再报一遍
                }

                var (lineNumber, character) = ToPosition(lineStarts, offset);
                found.Add(new DefinitionCandidate(
                    uri,
                    lineNumber,
                    character,
                    character + site.Length,
                    SymbolKinds.Field,
                    typeName + "." + field + " 写入",
                    "本文件",
                    "write:" + lineNumber,
                    offset,
                    IsLocal: true));
            }
        }
    }

    /// <summary>接收者的类型名；类型未知、或该类型的结构体里没有这个字段时返回 null。</summary>
    /// <remarks>
    /// 类型与结构体分两步查：变量常常声明在 Pass 里、结构体却写在 HLSLINCLUDE 里（Unity 的
    /// 共享块会拼进每个 Pass），要求两者同块就会整类漏掉。查变量时按 scope 顺序（光标所在块在前），
    /// 取第一个说得出类型的块。
    /// </remarks>
    private string? ResolveReceiverType(List<ShaderPassSnippet> scope, string receiver, string field)
    {
        string? typeName = null;

        foreach (var block in scope)
        {
            var index = _blockIndexCache.GetOrBuild(block.RawHlslBlock);
            if (index.VariablesToType.TryGetValue(receiver, out var type))
            {
                typeName = type;
                break;
            }
        }

        if (typeName is null)
        {
            return null;
        }

        foreach (var block in scope)
        {
            var index = _blockIndexCache.GetOrBuild(block.RawHlslBlock);
            if (index.Structs.TryGetValue(typeName, out var declared) && HasField(declared, field))
            {
                return typeName;
            }
        }

        return null;
    }

    private static bool HasField(HlslStruct declared, string field)
    {
        foreach (var item in declared.Fields)
        {
            if (item.Name.Equals(field, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // ───────────────────────── include 闭包 ─────────────────────────

    /// <summary>
    /// 待检索的头文件清单（按「越近越优先」排序，已排除文档自身）。
    /// </summary>
    /// <remarks>
    /// "权威作用域是 include 闭包，不是块类型"（详设 v2.0 第 12 条）：真实工程里
    /// "CGPROGRAM" 也会 include URP 头文件，用块类型硬隔离必然误伤。
    /// 预热命中时直接取 BFS 顺序；未预热（刚打开文档、刚敲下新 include）时退回
    /// 「当前块 + 共享块的直连 include」——这是详设第 3 条要求的「廉价、近乎同步」那一档。
    /// </remarks>
    private List<string> IncludeClosure(string uri, string text, ShaderLabParseResult parsed)
    {
        var files = new List<string>();

        // 预热表里的第 0 项是"文档自己"（BFS 起点，用物理路径登记），因此必须拿物理路径比对，
        // 只比 URI 会漏掉它 —— 后果是「本文件里的声明」被当成 include 命中回一份 file:/// 的候选，
        // 与本地作用域那份重复，且 URI 还可能是错的（脏文件本应是 untitled:）。
        _host.TryGetPhysicalPath(uri, out var selfPath);

        if (_cache.TryGetDocumentFiles(uri, out var warmed))
        {
            foreach (var file in warmed)
            {
                if (IsSelf(file, uri, selfPath))
                {
                    continue;
                }

                files.Add(file);
            }

            if (files.Count > 0)
            {
                return files;
            }
        }

        foreach (var target in DirectIncludes(parsed))
        {
            if (_host.TryResolve(selfPath, target, out var physical)
                || (selfPath.Length > 0 && _host.TryResolveRelative(selfPath, target, out physical)))
            {
                if (!files.Contains(physical, StringComparer.OrdinalIgnoreCase))
                {
                    files.Add(physical);
                }
            }
        }

        return files;
    }

    private static bool IsSelf(string file, string uri, string selfPath)
        => string.Equals(file, uri, StringComparison.OrdinalIgnoreCase)
        || (selfPath.Length > 0 && string.Equals(file, selfPath, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> DirectIncludes(ShaderLabParseResult parsed)
    {
        foreach (var snippet in parsed.Passes)
        {
            foreach (var include in snippet.Includes)
            {
                yield return include.Path;
            }
        }

        foreach (var snippet in parsed.SharedSnippets)
        {
            foreach (var include in snippet.Includes)
            {
                yield return include.Path;
            }
        }
    }

    /// <summary>
    /// 按真实 include 文本顺序（深度优先）给出宏求值要用的文件序列，已排除文档自身。
    /// </summary>
    /// <remarks>
    /// 预处理器是深度优先：A 里先 include B 再 include C、B 里又 include D 时，文本顺序是
    /// B、D、C；而预热给的 BFS 顺序是 B、C、D。同名宏「后者覆盖前者」在两种顺序下结论相反，
    /// 别名解包就会指向另一个定义。
    /// 同一文件只取第一次出现：既防环，也与预处理器只展开一次一致。
    /// 全程只用已有快照，不为此读盘 —— 缺快照的文件自然钻不下去，但会留一条追踪，
    /// 可见性因它下降时必须可查，不能静默跳过。
    /// </remarks>
    private List<string> IncludeTextOrder(string uri, string text, List<string> fallback)
    {
        var ordered = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        _host.TryGetPhysicalPath(uri, out var selfPath);
        if (selfPath.Length > 0)
        {
            visited.Add(selfPath);
        }

        Walk(IncludeLineScanner.Scan(text), selfPath);

        // 一个都解析不出来（宿主没给路径、或 include 目标靠宏拼出来）：退回预热的 BFS 表。
        // 那份顺序不是文本顺序，但总比「一个头文件宏都看不见」好，且与修复前的行为一致。
        // 退回顺序本身也是一种可见性下降，同样留痕，免得宏解包结果对不上时无从查起。
        if (ordered.Count == 0)
        {
            DiagTrace.Mark("宏可见性下降：include 文本顺序解析为空，退回预热顺序 uri=" + uri);
            return fallback;
        }

        if (missing.Count > 0)
        {
            DiagTrace.Mark(
                "宏可见性下降：include 链上有 " + missing.Count + " 个文件没有符号快照，已跳过，首个为 "
                + missing[0] + " uri=" + uri);
        }

        return ordered;

        void Walk(List<string> targets, string basePath)
        {
            foreach (var target in targets)
            {
                if (!_host.TryResolve(basePath, target, out var physical)
                    && !(basePath.Length > 0 && _host.TryResolveRelative(basePath, target, out physical)))
                {
                    // 承诺过「可见性下降必须可查」：解析不出来的目标同样是一条下降，不能只覆盖缺快照那种。
                    DiagTrace.Mark("宏可见性下降：include 目标解析不出物理路径 " + target + " from " + basePath);
                    continue;
                }

                if (!visited.Add(physical))
                {
                    continue;
                }

                ordered.Add(physical);

                if (!_cache.TryGetFileByPath(physical, out var symbols))
                {
                    missing.Add(physical);
                    continue;
                }

                Walk(symbols.Includes, physical);
            }
        }
    }

    /// <summary>
    /// 取一份「与磁盘一致」的文件快照。预算耗尽且快照陈旧时返回 false。
    /// </summary>
    /// <remarks>
    /// 三处取快照的地方（主 include 循环、宏别名展开、宏可见性求值）必须走同一套规则，
    /// 否则「预算耗尽不再用旧行号」只在主循环成立，宏别名这条最常见的路径仍会跳错。
    /// 宁可少给一条候选（客户端表现为跳不到），也不给错位置。
    /// </remarks>
    private bool TryGetFreshSnapshot(string file, ref int probed, out IncludeSymbolCache.FileSymbols symbols)
    {
        if (!_cache.TryGetFileByPath(file, out symbols))
        {
            if (probed < _options.MaxCrossFileProbes && _host.TryRead(file, out var content))
            {
                probed++;
                symbols = IncludeSymbolCache.FileSymbols.Build(content, _host.GetStamp(file), _cache.Interner);
                _cache.PutFile(file, symbols);
                return true;
            }

            symbols = null!;
            return false;
        }

        if (_host.GetStamp(file) == symbols.Stamp)
        {
            return true;
        }

        if (probed >= _options.MaxCrossFileProbes)
        {
            DiagTrace.Mark("陈旧快照且补扫预算已用尽，跳过该文件（不给旧行号）：" + file);
            symbols = null!;
            return false;
        }

        probed++;
        var refreshed = Probe(file, symbols);
        if (refreshed is null)
        {
            symbols = null!;
            return false;
        }

        symbols = refreshed;
        return true;
    }

    /// <returns>重扫成功返回新快照；读不到（文件被删或被独占）返回 null —— 调用方据此「本次拿不到」。</returns>
    private IncludeSymbolCache.FileSymbols? Probe(string file, IncludeSymbolCache.FileSymbols stale)
    {
        _ = stale;

        if (!_host.TryRead(file, out var content))
        {
            // 读不到时不能沿用旧表：那正是「行号可能偏」的来源，与本类「不返回错行」的承诺冲突。
            return null;
        }

        var rebuilt = IncludeSymbolCache.FileSymbols.Build(content, _host.GetStamp(file), _cache.Interner);
        _cache.PutFile(file, rebuilt);
        return rebuilt;
    }

    private void CollectFromSymbols(
        List<DefinitionCandidate> found,
        string file,
        IncludeSymbolCache.FileSymbols symbols,
        string name,
        string rank,
        string container)
    {
        // URI 走宿主：目标若正被编辑器打开（脏文件），必须原样回传客户端的原始 URI ——
        // VS Code 里未存盘的新文件是 untitled: 方案，自己拼 file:/// 会让它打开一个不存在的文件
        // （详设 v2.0 第 10 条）。
        var uri = _host.ToFileUri(file);

        if (_host.TryGetOpenDocument(file, out var openUri, out var openText))
        {
            uri = openUri;

            // 脏文件：编辑器里那份缓冲区才是用户看到的内容。缓存里的快照是按磁盘文本扫的，
            // 它给出的行表与声明偏移在同一份磁盘文本里才自洽 —— 头文件改过没存盘时行数已经变了，
            // 拿磁盘行表换算内存文本的位置必然落错行（甚至落进不存在的那一行）。
            // 因此这里按内存文本重扫一份（不读盘），行表与偏移同源。
            // 刻意不写回缓存：那份文本属于「未存盘」状态，写进按磁盘戳索引的缓存会污染
            // 关闭文件之后的跳转。
            symbols = IncludeSymbolCache.FileSymbols.Build(openText, symbols.Stamp, _cache.Interner);
        }

        if (!symbols.Declarations.TryGetValue(name, out var declarations) || symbols.LineStarts.Length == 0)
        {
            return;
        }

        // 结构体字段照收：当初跳过它，是因为字段偏移错用了「相对结构体正文」的基准，
        // 收进来会跳到别的行；那个基准已在索引器里改成文件内绝对偏移，跨文件字段现在能正确定位。
        // 重复候选由 Finish 按 Uri|Line|Char|Kind 去重兜住，与结构体名也不会互相干扰。
        foreach (var decl in declarations)
        {
            var line = LineOffsets.LineOf(symbols.LineStarts, decl.Offset);
            var character = decl.Offset - symbols.LineStarts[line];

            found.Add(new DefinitionCandidate(
                uri,
                line,
                character,
                character + decl.Length,
                LspKindOf(decl.Kind),
                DetailOf(decl),
                container,
                rank + ":" + line));
        }
    }

    // ───────────────────────── 宏别名解包 ─────────────────────────

    /// <summary>
    /// 宏别名解包：给「宏定义处」补上「展开后的真实实现处」候选。
    /// </summary>
    /// <remarks>
    /// 
    /// 规则（详设 v2.0 第 6 条）：只解"对象式"宏、宏体必须是"单个标识符"、
    /// 必须迭代到定点（实测别名链最长 3 层，上限 8 + visited 防环）、
    /// 终点若是内建或类型名而无源码位置 → "停在宏定义处并说明"，绝不返回空。
    /// 
    /// 候选"不替换"宏定义处：静默替换会让使用者永远看不到宏定义本身。
    /// </remarks>
    private void ExpandMacroAliases(
        List<DefinitionCandidate> found,
        string uri,
        string text,
        int[] lineStarts,
        ShaderLabParseResult parsed,
        List<ShaderPassSnippet> localBlocks,
        List<string> files,
        string name)
    {
        // 宏求值走真实 include 文本顺序：候选收集那份是「越近越优先」的 BFS 顺序，
        // 拿它算可见性，同名宏谁覆盖谁会和预处理器相反。
        var macros = MacroVisibility(localBlocks, IncludeTextOrder(uri, text, files));
        if (!macros.TryGetValue(name, out var macro) || macro.IsFunctionLike || macro.Body.Length == 0)
        {
            return;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { name };
        var current = macro.Body.Trim();

        for (var depth = 0; depth < _options.MaxMacroUnwrapDepth; depth++)
        {
            if (!IsPlainIdentifier(current) || !visited.Add(current))
            {
                return;
            }

            // 先当宏继续解包
            if (macros.TryGetValue(current, out var next) && !next.IsFunctionLike && next.Body.Length > 0)
            {
                current = next.Body.Trim();
                continue;
            }

            // 再当符号找位置（本文件 → include 闭包）
            var before = found.Count;
            foreach (var block in parsed.Passes)
            {
                CollectFromBlock(found, uri, text, lineStarts, block, current, "macro", "宏展开", _blockIndexCache);
            }

            foreach (var block in parsed.SharedSnippets)
            {
                CollectFromBlock(found, uri, text, lineStarts, block, current, "macro", "宏展开", _blockIndexCache);
            }

            var macroProbed = 0;
            foreach (var file in files)
            {
                // 与主循环同一套新鲜度约束：宏别名路径最容易撞上「头文件刚改过」，
                // 拿旧行号去跳正是这条链路上最难排查的错。
                if (TryGetFreshSnapshot(file, ref macroProbed, out var symbols))
                {
                    CollectFromSymbols(found, file, symbols, current, "macro", Path.GetFileName(file));
                }
            }

            if (found.Count > before)
            {
                return;
            }

            // 终点无源码位置（HLSL 内建 / 类型名）：停在宏定义处，不再展开。
            return;
        }
    }

    /// <summary>
    /// 按真实拼接顺序求值宏可见性：本文档的共享块与 Pass，再跟上 include 闭包（含 "#undef"）。
    /// </summary>
    /// <remarks>
    /// 只遍历 include 文件会漏掉写在本文件里的宏 —— "#define A B" 大量写在 "HLSLINCLUDE" 里，
    /// 那样别名永远解不开，F12 只停在宏定义处、拿不到实现位置。
    /// 顺序照 Unity 的拼接语义：共享块先于它拼进去的那个 Pass，include 闭包按真实 include
    /// 文本顺序（见 IncludeTextOrder）跟在后面；后来者覆盖先前的同名定义，与预处理器一致。
    /// </remarks>
    private Dictionary<string, HlslMacro> MacroVisibility(List<ShaderPassSnippet> localBlocks, List<string> files)
    {
        var visible = new Dictionary<string, HlslMacro>(StringComparer.Ordinal);

        // localBlocks 的次序是「光标所在 Pass 在前、共享块按出现顺序在后」（光标不在块内时只有共享块）。
        // 拼接语义要求：共享块之间保持文本顺序（后者覆盖前者），Pass 的内容拼在共享块之后、最后施加。
        // 不能整段倒序 —— 那会把两个共享块之间的先后也一起反过来，先出现的那份反而覆盖后出现的。
        var firstShared = localBlocks.Count > 0 && !localBlocks[0].IsSharedBlock ? 1 : 0;
        for (var i = firstShared; i < localBlocks.Count; i++)
        {
            ApplyMacroOps(visible, _blockIndexCache.GetOrBuild(localBlocks[i].RawHlslBlock).MacroOps);
        }

        if (firstShared == 1)
        {
            ApplyMacroOps(visible, _blockIndexCache.GetOrBuild(localBlocks[0].RawHlslBlock).MacroOps);
        }

        var probed = 0;
        foreach (var file in files)
        {
            // 同一条新鲜度约束：别名现在还在不在，不能按旧文本判定。
            if (TryGetFreshSnapshot(file, ref probed, out var symbols))
            {
                ApplyMacroOps(visible, symbols.MacroOps);
            }
        }

        return visible;
    }

    /// <summary>把一串宏事件依次施加到可见表上。</summary>
    private static void ApplyMacroOps(Dictionary<string, HlslMacro> visible, IReadOnlyList<HlslMacroOp> ops)
    {
        foreach (var op in ops)
        {
            if (op.IsUndef)
            {
                visible.Remove(op.Macro.Name);
            }
            else
            {
                visible[op.Macro.Name] = op.Macro;
            }
        }
    }

    private static bool IsPlainIdentifier(string value)
    {
        if (value.Length == 0 || !SymbolAtCursor.IsIdentifierChar(value[0]) || char.IsAsciiDigit(value[0]))
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!SymbolAtCursor.IsIdentifierChar(c))
            {
                return false;
            }
        }

        return true;
    }

    // ───────────────────────── 收尾 ─────────────────────────

    /// <summary>
    /// 去重、排序、截断。
    /// </summary>
    /// <remarks>
    /// "本地候选按「离光标最近的声明」优先"：真实工程里 "input" / "normalWS" 这类
    /// 形参名在多个函数里重名（实测同一语料 341 个同名声明点），「取最早行」会跳到别的函数里去。
    /// 真正的解法是词法作用域（需位置作用域索引，属架构级后续项）；在它落地之前，
    /// 「最近声明优先」能以 O(1) 额外成本覆盖绝大多数场景 —— 光标停在某处，最近的同名声明
    /// 大概率就是它所属的作用域（尤其光标本来就落在某条声明上时，距离为 0 的就是它自己）。
    /// include 候选没有「距离」概念，靠插入序号保持「越近的 include 越优先」的闭包次序 ——
    /// 这个次序绝不能被文件路径字母序顶掉，否则同名重载一多，第一个候选就跳到别的头文件。
    /// </remarks>
    private List<DefinitionCandidate> Finish(List<DefinitionCandidate> found, int cursorOffset)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<DefinitionCandidate>(found.Count);

        foreach (var candidate in found)
        {
            var key = candidate.Uri + "|" + candidate.Line + "|" + candidate.Character + "|" + candidate.Kind;
            if (seen.Add(key))
            {
                unique.Add(candidate);
            }
        }

        // 同档同距时必须靠插入序号决出先后，不能再拿 URI 比大小：List.Sort 不稳定，
        // 而跨文件候选的距离恒为 0（include 没有「距离」概念），比较函数一返回 0，
        // 「越近的 include 越优先」的次序就会被排成文件路径字母序 —— 重载一多，
        // 第一个候选直接跳到别的头文件去。序号取自去重后的位置，也就是候选被收集的先后：
        // 跨文件候选恰好是 include 闭包顺序（近 → 远），本地候选是声明在文本里的先后。
        var order = new int[unique.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (left, right) =>
        {
            var byRank = RankWeight(unique[left].Rank).CompareTo(RankWeight(unique[right].Rank));
            if (byRank != 0)
            {
                return byRank;
            }

            // 同一 rank 档内：本地候选按离光标的距离（最近优先），跨文件候选保持 include 顺序
            var byDistance = Distance(unique[left], cursorOffset).CompareTo(Distance(unique[right], cursorOffset));
            if (byDistance != 0)
            {
                return byDistance;
            }

            // left / right 是插入序号（int），不是候选本身；序号升序即「谁先被收集」
            return left.CompareTo(right);
        });

        var count = Math.Min(order.Length, _options.MaxCandidates);
        var sorted = new List<DefinitionCandidate>(count);
        for (var i = 0; i < count; i++)
        {
            sorted.Add(unique[order[i]]);
        }

        return sorted;
    }

    private static int Distance(DefinitionCandidate candidate, int cursorOffset)
        => candidate.IsLocal ? Math.Abs(candidate.Offset - cursorOffset) : 0;

    // 档位：写入点 → 本地声明 → 宏展开 → include。
    // 写入点排在声明之前是有意的：结构体字段声明只说明类型，值从哪来要看顶点那条赋值。
    private static int RankWeight(string rank) => rank.StartsWith("write", StringComparison.Ordinal) ? 0
        : rank.StartsWith("local", StringComparison.Ordinal) ? 1
        : rank.StartsWith("macro", StringComparison.Ordinal) ? 2
        : 3;

    // ───────────────────────── 位置换算 ─────────────────────────

    private static (int Line, int Character) ToPosition(int[] lineStarts, int offset)
    {
        var line = LineOffsets.LineOf(lineStarts, offset);
        return (line, offset - lineStarts[line]);
    }

    private static int LspKindOf(HlslDeclKind kind) => kind switch
    {
        HlslDeclKind.Struct => SymbolKinds.Struct,
        HlslDeclKind.Function => SymbolKinds.Function,
        HlslDeclKind.Field => SymbolKinds.Field,
        HlslDeclKind.Macro => SymbolKinds.Constant,
        _ => SymbolKinds.Variable,
    };

    private static string DetailOf(HlslDecl decl) => decl.Kind switch
    {
        HlslDeclKind.Function => decl.Detail + " (" + decl.Arity + " 参数)",
        HlslDeclKind.Macro => decl.Detail.Length == 0 ? "#define" : "#define " + decl.Detail,
        HlslDeclKind.Struct => "struct",
        _ => decl.Detail,
    };
}
