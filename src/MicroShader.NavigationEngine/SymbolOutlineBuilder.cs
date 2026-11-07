using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;
using MicroShader.ShaderLab;

namespace MicroShader.NavigationEngine;

/// <summary>大纲树的一个节点（内部模型；序列化在 <see cref="NavigationService"/>）。</summary>
internal sealed class OutlineNode
{
    public string Name { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public int Kind { get; set; }

    /// <summary>声明所在行的行号（0-based）。</summary>
    public int Line { get; set; }

    /// <summary>声明名在该行内的起始列 / 结束列（UTF-16 code unit）。</summary>
    public int SelStart { get; set; }

    public int SelEnd { get; set; }

    /// <summary>
    /// 节点覆盖的行范围（"0-based LSP 行号"）。
    /// </summary>
    /// <remarks>
    /// 可写是因为父节点的 range 必须在"全部子节点建完之后"再扩张：
    /// 结构体的字段与它在同一行时，只覆盖名字的 range 装不下字段的列
    /// （详设 v2.0 第 8 条：父 range 必须包含所有子 range，否则 VS Code 大纲会错层）。
    /// </remarks>
    public int StartLine { get; set; }

    public int EndLine { get; set; }

    /// <summary>range 起始列；容器节点为 0（它们从行首开始）。</summary>
    public int StartChar { get; set; }

    /// <summary>range 结束列；容器节点取所在行的行尾。</summary>
    public int EndChar { get; set; }

    public List<OutlineNode> Children { get; } = [];

    /// <summary>归还对象池前清空状态（Name/Children 等），使下次 Rent 得到一个干净节点。</summary>
    public void Reset()
    {
        Name = string.Empty;
        Detail = string.Empty;
        Kind = 0;
        Line = 0;
        SelStart = 0;
        SelEnd = 0;
        StartLine = 0;
        EndLine = 0;
        StartChar = 0;
        EndChar = 0;
        Children.Clear();
    }
}

/// <summary>
/// 文档大纲与面包屑树构建器（"textDocument/documentSymbol"）。
/// </summary>
/// <remarks>
/// 
/// "层级"：Shader → SubShader → Pass → 程序块 → 块内符号（函数 / 结构体 → 字段 / 宏）。
/// "HLSLINCLUDE" 这类文件级共享块直接挂在 Shader 下。
/// 
/// "range 的两条硬约束"（详设 v2.0 第 8 条）："range" 必须包含
/// "selectionRange"，且父节点 range 必须包含全部子节点 range —— 否则 VS Code 的
/// 大纲/面包屑会错层。这里：符号节点 range = 整行、selectionRange = 名字精确列；
/// 块与容器节点的 range 取"首尾子节点行的并集"（刻意如此，见下）。
/// 
/// "已知取舍"：模块 1 的切片结果只给出"程序块"的行范围，没有
/// "SubShader {" / "Pass {" 自身的行列。为了不伪造位置，容器节点的 range
/// 由子节点并集推出，且"没有任何程序块的 SubShader / Pass 不产出节点"
/// （宁缺勿错：给错行号会让面包屑跳到无关位置）。
/// </remarks>
internal static class SymbolOutlineBuilder
{
    // 节点对象池：串行 LSP 会话上复用 OutlineNode 实例。大纲完成后整体归还，
    // 下次请求几乎零分配。大文件（~400 符号节点）每次省 ~58 KB。
    //
    // [ThreadStatic]：导航请求在会话读循环上串行执行，但测试会在线程池上并行跑多个
    // NavigationService。每线程一份池既零锁，又保证两个线程绝不会借到同一个节点实例。
    [ThreadStatic]
    private static Stack<OutlineNode>? _pool;

    private static OutlineNode Rent()
    {
        var pool = _pool;
        if (pool is not null && pool.TryPop(out var node))
        {
            node.Reset();
            return node;
        }

        return new OutlineNode();
    }

    /// <summary>把整棵大纲树的全部节点归还对象池（后序递归）。</summary>
    public static void ReleaseTree(OutlineNode? root)
    {
        if (root is null)
        {
            return;
        }

        var pool = _pool ??= new Stack<OutlineNode>(512);
        Release(root, pool);
    }

    private static void Release(OutlineNode node, Stack<OutlineNode> pool)
    {
        foreach (var child in node.Children)
        {
            Release(child, pool);
        }

        node.Reset();
        pool.Push(node);
    }

    public static OutlineNode Build(string text, ShaderLabParseResult parsed, int[] lineStarts, int[] allowedKinds, HlslBlockIndexCache blockIndexCache)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(parsed);

        // 行首表由调用方传入（NavigationService 缓存复用）：符号节点按行定位是热路径，
        // 每个节点都重建一次的话代价是 O(文件大小 × 符号数)。
        var lastLine = lineStarts.Length - 1;

        var root = Rent();
        root.Name = ShaderName(text) ?? "Shader";
        root.Detail = "shaderlab";
        root.Kind = SymbolKinds.Pick(SymbolKinds.Shader, allowedKinds);
        root.Line = 0;
        root.SelStart = 0;
        root.SelEnd = 0;
        root.StartLine = 0;
        root.EndLine = lastLine;
        root.StartChar = 0;
        root.EndChar = LineLength(text, lineStarts, lastLine);

        // ── 文件级共享块（HLSLINCLUDE / CGINCLUDE）：直接挂在 Shader 下 ──
        foreach (var shared in parsed.SharedSnippets)
        {
            root.Children.Add(BlockNode(shared, text, lineStarts, allowedKinds, blockIndexCache));
        }

        // ── SubShader → Pass → 程序块 → 符号 ──
        for (var sub = 0; sub < parsed.SubShaderCount; sub++)
        {
            var blocks = BlocksOfSubShader(parsed, sub);
            if (blocks.Count == 0)
            {
                continue;
            }

            var subNode = Rent();
            subNode.Name = "SubShader " + sub;
            subNode.Detail = "subshader";
            subNode.Kind = SymbolKinds.Pick(SymbolKinds.SubShader, allowedKinds);
            subNode.Line = LspLine(blocks[0].StartLineNumber);
            subNode.SelStart = 0;
            subNode.SelEnd = 0;
            subNode.StartLine = MinLine(blocks);
            subNode.EndLine = MaxLine(blocks);
            subNode.StartChar = 0;
            subNode.EndChar = LineLength(text, lineStarts, MaxLine(blocks));

            foreach (var group in GroupByPass(blocks))
            {
                subNode.Children.Add(PassNode(group.Key, group.Value, text, lineStarts, allowedKinds, blockIndexCache));
            }

            root.Children.Add(subNode);
        }

        // ── 游离块（不在任何 Pass 内）与 SubShaderIndex 为 -1 的块 ──
        foreach (var orphan in parsed.Passes)
        {
            if (orphan.SubShaderIndex >= 0 && orphan.SubShaderIndex < parsed.SubShaderCount)
            {
                continue;
            }

            root.Children.Add(BlockNode(orphan, text, lineStarts, allowedKinds, blockIndexCache));
        }

        // range 的最终形态必须等所有子节点就位后再定（详见 ExpandToChildren 的说明）
        ExpandToChildren(root, text, lineStarts);
        return root;
    }

    /// <summary>
    /// 把每个节点的 range 扩张到「自身的 selectionRange ∪ 全部子节点 range」。
    /// </summary>
    /// <remarks>
    /// 详设 v2.0 第 8 条的两条硬约束（"range" ⊇ "selectionRange"、
    /// 父 "range" ⊇ 子 "range"）在这里统一兑现。
    /// 必须在全部子节点建好之后做，否则「结构体与它的字段在同一行」这种情况必然违反第二条。
    /// </remarks>
    private static void ExpandToChildren(OutlineNode node, string text, int[] lineStarts)
    {
        foreach (var child in node.Children)
        {
            ExpandToChildren(child, text, lineStarts);
        }

        var startLine = node.StartLine;
        var startChar = node.StartChar;
        var endLine = node.EndLine;
        var endChar = node.EndChar;

        // 自身 selectionRange 先并入
        (startLine, startChar, endLine, endChar) = Union(startLine, startChar, endLine, endChar, node.Line, node.SelStart, node.Line, node.SelEnd);

        foreach (var child in node.Children)
        {
            (startLine, startChar, endLine, endChar) = Union(
                startLine, startChar, endLine, endChar,
                child.StartLine, child.StartChar, child.EndLine, child.EndChar);
        }

        node.StartLine = startLine;
        node.StartChar = startChar;
        node.EndLine = endLine;
        node.EndChar = Math.Max(endChar, LineLength(text, lineStarts, endLine));
    }

    private static (int StartLine, int StartChar, int EndLine, int EndChar) Union(
        int aStartLine, int aStartChar, int aEndLine, int aEndChar,
        int bStartLine, int bStartChar, int bEndLine, int bEndChar)
    {
        var startLine = aStartLine < bStartLine || (aStartLine == bStartLine && aStartChar <= bStartChar)
            ? (aStartLine, aStartChar)
            : (bStartLine, bStartChar);

        var endLine = aEndLine > bEndLine || (aEndLine == bEndLine && aEndChar >= bEndChar)
            ? (aEndLine, aEndChar)
            : (bEndLine, bEndChar);

        return (startLine.Item1, startLine.Item2, endLine.Item1, endLine.Item2);
    }

    private static List<ShaderPassSnippet> BlocksOfSubShader(ShaderLabParseResult parsed, int subShaderIndex)
    {
        var blocks = new List<ShaderPassSnippet>();
        foreach (var pass in parsed.Passes)
        {
            if (pass.SubShaderIndex == subShaderIndex)
            {
                blocks.Add(pass);
            }
        }

        return blocks;
    }

    private static List<KeyValuePair<int, List<ShaderPassSnippet>>> GroupByPass(List<ShaderPassSnippet> blocks)
    {
        var groups = new List<KeyValuePair<int, List<ShaderPassSnippet>>>();
        var index = new Dictionary<int, List<ShaderPassSnippet>>();

        foreach (var block in blocks)
        {
            if (!index.TryGetValue(block.PassIndex, out var list))
            {
                list = [];
                index[block.PassIndex] = list;
                groups.Add(new KeyValuePair<int, List<ShaderPassSnippet>>(block.PassIndex, list));
            }

            list.Add(block);
        }

        foreach (var pair in groups)
        {
            pair.Value.Sort(static (left, right) => left.BlockOrdinalInPass.CompareTo(right.BlockOrdinalInPass));
        }

        return groups;
    }

    private static OutlineNode PassNode(
        int passIndex,
        List<ShaderPassSnippet> blocks,
        string text,
        int[] lineStarts,
        int[] allowedKinds,
        HlslBlockIndexCache blockIndexCache)
    {
        var first = blocks[0];
        var name = string.IsNullOrEmpty(first.PassName) ? "Pass " + passIndex : first.PassName;

        // Pass 节点必须用自己的行（Pass 关键字那一行）而不是第一个程序块的行。
        // 用程序块的行会让 Pass 的 range 与子节点（程序块）完全重合 —— 粘滞滚动窗口
        // 在重合时只保留最内层，于是 Pass 永远不显示、只显示 HLSLPROGRAM。
        // 拿不到时（孤儿块）退回程序块的行。
        // 两条分支的行号口径不同，各转一次即可，多转一次就错一行：
        // PassStartLineNumber 与模块 1 的其它行号同口径，是 1-based，必须过 LspLine 换成节点行；
        // MinLine 返回的已经是 0-based（它内部做过 LspLine），直接用，再套一次会多减一行 ——
        // 于是 SubShader 里不在 Pass 内的程序块（孤儿块，PassStartLineNumber 为 -1）的
        // Pass 节点起始行会落到上一行去。
        var passLine = first.PassStartLineNumber >= 0
            ? LspLine(first.PassStartLineNumber)
            : MinLine(blocks);

        var node = Rent();
        node.Name = name;
        node.Detail = "#pragma vertex " + (first.VertexEntry ?? "-") + " · fragment " + (first.FragmentEntry ?? "-");
        node.Kind = SymbolKinds.Pick(SymbolKinds.Pass, allowedKinds);
        node.Line = passLine;
        node.SelStart = 0;
        node.SelEnd = 0;
        node.StartLine = passLine;
        // MaxLine 与 MinLine 同口径，返回的已经是 0-based，不能再套 LspLine。
        node.EndLine = MaxLine(blocks);
        node.StartChar = 0;
        node.EndChar = LineLength(text, lineStarts, MaxLine(blocks));

        foreach (var block in blocks)
        {
            node.Children.Add(BlockNode(block, text, lineStarts, allowedKinds, blockIndexCache));
        }

        return node;
    }

    private static OutlineNode BlockNode(
        ShaderPassSnippet block,
        string text,
        int[] lineStarts,
        int[] allowedKinds,
        HlslBlockIndexCache blockIndexCache)
    {
        var node = Rent();
        node.Name = block.Kind.ToString();
        node.Detail = block.IsSharedBlock ? "文件级共享块" : "程序块";
        node.Kind = SymbolKinds.Pick(SymbolKinds.Program, allowedKinds);
        node.Line = LspLine(block.StartLineNumber);
        node.SelStart = 0;
        node.SelEnd = 0;
        node.StartLine = LspLine(block.StartLineNumber);
        node.EndLine = LspLine(block.EndLineNumber);
        node.StartChar = 0;
        node.EndChar = LineLength(text, lineStarts, LspLine(block.EndLineNumber));

        // 块内符号：用同一套容错索引器，偏移加块起始偏移即得文档内偏移。
        // 块正文可能为空（未闭合块），空 span 返回空索引，不必特判。
        // 索引走共享缓存（HlslBlockIndexCache）：同一块在补全 / 大纲 / F12 之间只构建一次。
        var index = blockIndexCache.GetOrBuild(block.RawHlslBlock);
        node.Children.AddRange(SymbolNodes(text, lineStarts, block.ContentStartOffset, index, allowedKinds));
        return node;
    }

    private static List<OutlineNode> SymbolNodes(
        string text,
        int[] lineStarts,
        int blockOffset,
        HlslDocumentIndex index,
        int[] allowedKinds)
    {
        // 只建一个 List<OutlineNode>：不再有 (offset, node) 元组中间表和第二次复制。
        // 排序键直接取节点已有的位置字段（同一块内偏移与 (行号, 行内列) 单调一致），
        // 这样不用给每个节点加临时字段。父节点的 Children 直接 AddRange 这个列表。
        var nodes = new List<OutlineNode>();

        foreach (var pair in index.Structs)
        {
            var decl = index.Declarations.TryGetValue(pair.Key, out var list) ? FindStruct(list) : null;
            var offset = decl?.Offset ?? pair.Value.NameOffset;
            if (offset <= 0)
            {
                continue;
            }

            // 结构体节点先带空 Children 建出来（Children 是只读属性，子节点只能后加），
            // 位置在构造时一次算定，不再有占位节点与 Rebase。
            var node = SymbolNode(
                pair.Key,
                "struct · " + pair.Value.Fields.Count + " 字段",
                SymbolKinds.Pick(SymbolKinds.StructPref, allowedKinds),
                text,
                lineStarts,
                blockOffset + offset,
                pair.Key.Length);
            // 字段单独定位（在结构体正文里再扫一次太贵，这里用索引里已有的字段偏移）；
            // 字段节点直接按最终位置构造后加到结构体节点上。
            foreach (var field in pair.Value.Fields)
            {
                if (field.Offset <= 0)
                {
                    continue;
                }

                node.Children.Add(SymbolNode(
                    field.Name,
                    field.Type,
                    SymbolKinds.Pick(SymbolKinds.FieldPref, allowedKinds),
                    text,
                    lineStarts,
                    blockOffset + field.Offset,
                    field.Name.Length));
            }

            nodes.Add(node);
        }

        foreach (var pair in index.Declarations)
        {
            foreach (var decl in pair.Value)
            {
                if (decl.Kind is HlslDeclKind.Struct or HlslDeclKind.Field or HlslDeclKind.Macro or HlslDeclKind.Variable)
                {
                    continue;   // 结构体已在上面处理；字段属于结构体；变量不进大纲（噪声）；宏单独处理
                }

                if (decl.Kind != HlslDeclKind.Function)
                {
                    continue;
                }

                var node = SymbolNode(
                    decl.Name,
                    "(" + decl.Arity + " 参数) → " + decl.Detail,
                    SymbolKinds.Pick(SymbolKinds.FunctionPref, allowedKinds),
                    text,
                    lineStarts,
                    blockOffset + decl.Offset,
                    decl.Name.Length);
                nodes.Add(node);
            }
        }

        foreach (var op in index.MacroOps)
        {
            if (op.IsUndef)
            {
                continue;
            }

            var node = SymbolNode(
                op.Macro.Name,
                op.Macro.IsFunctionLike
                    ? "#define （" + op.Macro.Arity + " 参数）"
                    : "#define " + Truncate(op.Macro.Body, 60),
                SymbolKinds.Pick(SymbolKinds.MacroPref, allowedKinds),
                text,
                lineStarts,
                blockOffset + op.Macro.Offset,
                op.Macro.Name.Length);
            nodes.Add(node);
        }

        // 同一块内，偏移的先后与 (行号, 行内列) 的先后严格一致（两者是同一 text /
        // lineStarts / blockOffset 的单调换算），所以直接按位置字段排序，省掉节点上的
        // 临时偏移字段，避免给每个节点增大对象。
        nodes.Sort(static (left, right) =>
        {
            var byLine = left.Line.CompareTo(right.Line);
            return byLine != 0 ? byLine : left.SelStart.CompareTo(right.SelStart);
        });

        return nodes;
    }

    private static HlslDecl? FindStruct(List<HlslDecl> list)
    {
        foreach (var decl in list)
        {
            if (decl.Kind == HlslDeclKind.Struct)
            {
                return decl;
            }
        }

        return null;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    /// <summary>把绝对偏移换算成「行号 + 名字起止列」，纯值结果、不分配对象。</summary>
    private static (int Line, int StartChar, int EndChar) Position(string text, int[] lineStarts, int offset, int length)
    {
        var line = LineOffsets.LineOf(lineStarts, Math.Min(Math.Max(offset, 0), text.Length));
        var character = offset - lineStarts[line];

        return (line, character, character + length);
    }

    /// <summary>
    /// 按 "Position" 的结果直接构造符号节点：一个节点只分配一次。
    /// </summary>
    /// <remarks>
    /// 这里曾经是「先建占位节点、再算位置、最后 Rebase 搬字段」的三段式 ——
    /// 每出现一个符号就分配 3 个 "OutlineNode"，300 个 Pass 的文档光大纲就上万次分配。
    /// 现在位置先算成值元组，名字 / 详情 / 种类 / 位置一个对象构造器填完。
    /// "符号节点的 range 恒为名字所在的行、selectionRange 恒为名字的精确列"，
    /// 所以 Line = StartLine = EndLine、StartChar = SelStart、EndChar = SelEnd。
    /// </remarks>
    private static OutlineNode SymbolNode(
        string name,
        string detail,
        int kind,
        string text,
        int[] lineStarts,
        int offset,
        int nameLength)
    {
        var (line, startChar, endChar) = Position(text, lineStarts, offset, nameLength);

        var node = Rent();
        node.Name = name;
        node.Detail = detail;
        node.Kind = kind;
        node.Line = line;   // 符号节点的 range 恒为名字所在整行
        node.SelStart = startChar;
        node.SelEnd = endChar;
        node.StartLine = line;
        node.EndLine = line;
        node.StartChar = startChar;
        node.EndChar = endChar;
        return node;
    }

    /// <summary>某一行的字符数（不含行尾 CR/LF），用于容器节点的 range 结束列。</summary>
    private static int LineLength(string text, int[] lineStarts, int line)
        => LineOffsets.EndOfLine(text, lineStarts, line) - lineStarts[Math.Min(Math.Max(line, 0), lineStarts.Length - 1)];

    /// <summary>
    /// 模块 1 的块行号（"StartLineNumber" / "EndLineNumber"）是 "1-based"
    /// （与 "Corpus.LineAt"、诊断的 "Line" 同一口径；LSP 出口处会话再 "-1"）。
    /// 大纲走的是偏移换算（0-based），所以这里必须显式换算一次。
    /// </summary>
    private static int LspLine(int blockLineNumber) => Math.Max(0, blockLineNumber - 1);

    private static int MinLine(List<ShaderPassSnippet> blocks)
    {
        var min = int.MaxValue;
        foreach (var block in blocks)
        {
            if (block.StartLineNumber < min)
            {
                min = block.StartLineNumber;
            }
        }

        return min == int.MaxValue ? 0 : LspLine(min);
    }

    private static int MaxLine(List<ShaderPassSnippet> blocks)
    {
        var max = 0;
        foreach (var block in blocks)
        {
            if (block.EndLineNumber > max)
            {
                max = block.EndLineNumber;
            }
        }

        return LspLine(max);
    }

    /// <summary>取 "Shader "名字"" 里的名字；找不到返回 "null"。</summary>
    internal static string? ShaderName(string text)
    {
        var limit = Math.Min(text.Length, 4096);
        var head = text.AsSpan(0, limit);

        var at = head.IndexOf("Shader".AsSpan(), StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var quote = head[at..].IndexOf('"');
        if (quote < 0)
        {
            return null;
        }

        var from = at + quote + 1;
        var to = head[from..].IndexOf('"');
        return to <= 0 ? null : head.Slice(from, to).ToString();
    }
}
