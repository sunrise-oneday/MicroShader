using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;
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
/// 而不是拿旧行号去跳。
/// 
/// "已知边界"：不实现「符号级作用域」——同一块内同名的局部变量与全局变量都算候选择。
/// 也不做条件编译求值（"#if" 分支里的声明照收），与索引器的口径一致。
/// </remarks>
internal sealed class DefinitionTracer
{
    private readonly INavigationHost _host;
    private readonly IncludeSymbolCache _cache;
    private readonly NavigationOptions _options;

    public DefinitionTracer(INavigationHost host, IncludeSymbolCache cache, NavigationOptions options)
    {
        _host = host;
        _cache = cache;
        _options = options;
    }

    public List<DefinitionCandidate> Trace(string uri, string text, int line, int character)
    {
        var found = new List<DefinitionCandidate>();

        var lineStarts = LineOffsets.Build(text);
        if (!SymbolAtCursor.TryFind(text, lineStarts, line, character, out var name, out _))
        {
            return found;
        }

        var parsed = new ShaderLabParseResult();
        new ShaderLabStateMachine().Parse(uri, text, parsed);

        var cursorOffset = lineStarts[Math.Min(Math.Max(line, 0), lineStarts.Length - 1)] + Math.Max(character, 0);

        var localBlocks = LocalScope(parsed, cursorOffset);
        foreach (var block in localBlocks)
        {
            CollectFromBlock(found, uri, text, lineStarts, block, name, "local", "本文件");
        }

        // 本地作用域一旦命中就返回：既符合「文件内优先」的语义，也是 F12 能守住 3ms 的原因
        // （详设 §五）。继续扫 include 链只会把它自己的声明再报一遍，还平白读盘。
        if (found.Count > 0)
        {
            return Finish(found, cursorOffset);
        }

        var files = IncludeClosure(uri, text, parsed);
        var probed = 0;

        foreach (var file in files)
        {
            if (_cache.TryGetFileByPath(file, out var symbols))
            {
                // 陈旧检测：写入戳变了就现场补扫（不拿旧行号跳）
                var stamp = _host.GetStamp(file);
                if (stamp != symbols.Stamp && probed < _options.MaxCrossFileProbes)
                {
                    probed++;
                    symbols = Probe(file, symbols);
                }
            }
            else if (probed < _options.MaxCrossFileProbes && _host.TryRead(file, out var content))
            {
                probed++;
                symbols = IncludeSymbolCache.FileSymbols.Build(content, _host.GetStamp(file));
                _cache.PutFile(file, symbols);
            }
            else
            {
                continue;
            }

            CollectFromSymbols(found, file, symbols, name, "include", Path.GetFileName(file));
        }

        ExpandMacroAliases(found, uri, text, lineStarts, parsed, files, name);

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

    private static void CollectFromBlock(
        List<DefinitionCandidate> found,
        string uri,
        string text,
        int[] lineStarts,
        ShaderPassSnippet block,
        string name,
        string rank,
        string container)
    {
        var index = HlslDocumentIndex.Build(block.RawHlslBlock.Span);
        if (!index.Declarations.TryGetValue(name, out var declarations))
        {
            return;
        }

        foreach (var decl in declarations)
        {
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
            if (_host.TryResolve(target, out var physical)
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

    private IncludeSymbolCache.FileSymbols Probe(string file, IncludeSymbolCache.FileSymbols stale)
    {
        if (!_host.TryRead(file, out var content))
        {
            return stale;   // 读不到就沿用旧表：行号可能偏，但比「跳不动」好
        }

        var rebuilt = IncludeSymbolCache.FileSymbols.Build(content, _host.GetStamp(file));
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
        if (!symbols.Declarations.TryGetValue(name, out var declarations) || symbols.LineStarts.Length == 0)
        {
            return;
        }

        // URI 走宿主：目标若正被编辑器打开（脏文件），必须原样回传客户端的原始 URI ——
        // VS Code 里未存盘的新文件是 untitled: 方案，自己拼 file:/// 会让它打开一个不存在的文件
        // （详设 v2.0 第 10 条）。
        var uri = _host.TryGetOpenDocument(file, out var openUri, out _) ? openUri : _host.ToFileUri(file);

        foreach (var decl in declarations)
        {
            if (decl.Kind == HlslDeclKind.Field)
            {
                continue;   // 字段由所属结构体给位置，不在这里重复
            }

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
        List<string> files,
        string name)
    {
        var macros = MacroVisibility(files);
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
                CollectFromBlock(found, uri, text, lineStarts, block, current, "macro", "宏展开");
            }

            foreach (var block in parsed.SharedSnippets)
            {
                CollectFromBlock(found, uri, text, lineStarts, block, current, "macro", "宏展开");
            }

            foreach (var file in files)
            {
                if (_cache.TryGetFileByPath(file, out var symbols))
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

    /// <summary>按 include 顺序求值宏可见性（含 "#undef"）。</summary>
    private Dictionary<string, HlslMacro> MacroVisibility(List<string> files)
    {
        var visible = new Dictionary<string, HlslMacro>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            if (!_cache.TryGetFileByPath(file, out var symbols))
            {
                continue;
            }

            foreach (var op in symbols.MacroOps)
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

        return visible;
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
    /// include 候选没有「距离」概念，保持「越近的 include 越优先」的原有次序。
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

        unique.Sort((left, right) =>
        {
            var byRank = RankWeight(left.Rank).CompareTo(RankWeight(right.Rank));
            if (byRank != 0)
            {
                return byRank;
            }

            // 同一 rank 档内：本地候选按离光标的距离（最近优先），跨文件候选保持 include 顺序
            var byDistance = Distance(left, cursorOffset).CompareTo(Distance(right, cursorOffset));
            if (byDistance != 0)
            {
                return byDistance;
            }

            var byUri = string.CompareOrdinal(left.Uri, right.Uri);
            return byUri != 0 ? byUri : left.Line.CompareTo(right.Line);
        });

        return unique.Count <= _options.MaxCandidates
            ? unique
            : unique.GetRange(0, _options.MaxCandidates);
    }

    private static int Distance(DefinitionCandidate candidate, int cursorOffset)
        => candidate.IsLocal ? Math.Abs(candidate.Offset - cursorOffset) : 0;

    private static int RankWeight(string rank) => rank.StartsWith("local", StringComparison.Ordinal) ? 0
        : rank.StartsWith("macro", StringComparison.Ordinal) ? 1
        : 2;

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
