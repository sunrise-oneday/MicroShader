using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;
using MicroShader.ShaderLab;

namespace MicroShader.NavigationEngine;

/// <summary>大纲树的一个节点（内部模型；序列化在 <see cref="NavigationService"/>）。</summary>
internal sealed class OutlineNode
{
    public required string Name { get; init; }

    public string Detail { get; init; } = string.Empty;

    public int Kind { get; set; }

    /// <summary>声明所在行的行号（0-based）。</summary>
    public int Line { get; init; }

    /// <summary>声明名在该行内的起始列 / 结束列（UTF-16 code unit）。</summary>
    public int SelStart { get; init; }

    public int SelEnd { get; init; }

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
    public static OutlineNode Build(string text, ShaderLabParseResult parsed, int[] allowedKinds, HlslBlockIndexCache blockIndexCache)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(parsed);

        // 行首表只建一次并全程下传：符号节点按行定位是热路径，
        // 每个节点都重建一次的话代价是 O(文件大小 × 符号数)。
        var lineStarts = LineOffsets.Build(text);
        var lastLine = lineStarts.Length - 1;

        var root = new OutlineNode
        {
            Name = ShaderName(text) ?? "Shader",
            Detail = "shaderlab",
            Kind = SymbolKinds.Pick(SymbolKinds.Shader, allowedKinds),
            Line = 0,
            SelStart = 0,
            SelEnd = 0,
            StartLine = 0,
            EndLine = lastLine,
            StartChar = 0,
            EndChar = LineLength(text, lineStarts, lastLine),
        };

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

            var subNode = new OutlineNode
            {
                Name = "SubShader " + sub,
                Detail = "subshader",
                Kind = SymbolKinds.Pick(SymbolKinds.SubShader, allowedKinds),
                Line = LspLine(blocks[0].StartLineNumber),
                SelStart = 0,
                SelEnd = 0,
                StartLine = MinLine(blocks),
                EndLine = MaxLine(blocks),
                StartChar = 0,
                EndChar = LineLength(text, lineStarts, MaxLine(blocks)),
            };

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
        // 拿不到时（孤儿块）退回原来的行为。
        // 两个字段都是 1-based（与 BlockNode 一样经 LspLine 转成 0-based 节点行），
        // 这里曾经直接用 MinLine(blocks) 而漏了转换 —— 于是 Pass 的 range 既晚一行、
        // 又和子节点（程序块）完全重合，粘滞滚动窗口取最内层就看不见 Pass。
        var passLine = LspLine(first.PassStartLineNumber >= 0 ? first.PassStartLineNumber : MinLine(blocks));

        var node = new OutlineNode
        {
            Name = name,
            Detail = "#pragma vertex " + (first.VertexEntry ?? "-") + " · fragment " + (first.FragmentEntry ?? "-"),
            Kind = SymbolKinds.Pick(SymbolKinds.Pass, allowedKinds),
            Line = passLine,
            SelStart = 0,
            SelEnd = 0,
            StartLine = passLine,
            EndLine = LspLine(MaxLine(blocks)),
            StartChar = 0,
            EndChar = LineLength(text, lineStarts, MaxLine(blocks)),
        };

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
        var node = new OutlineNode
        {
            Name = block.Kind.ToString(),
            Detail = block.IsSharedBlock ? "文件级共享块" : "程序块",
            Kind = SymbolKinds.Pick(SymbolKinds.Program, allowedKinds),
            Line = LspLine(block.StartLineNumber),
            SelStart = 0,
            SelEnd = 0,
            StartLine = LspLine(block.StartLineNumber),
            EndLine = LspLine(block.EndLineNumber),
            StartChar = 0,
            EndChar = LineLength(text, lineStarts, LspLine(block.EndLineNumber)),
        };

        // 块内符号：用同一套容错索引器，偏移加块起始偏移即得文档内偏移。
        // 块正文可能为空（未闭合块），空 span 返回空索引，不必特判。
        // 索引走共享缓存（HlslBlockIndexCache）：同一块在补全 / 大纲 / F12 之间只构建一次。
        var index = blockIndexCache.GetOrBuild(block.RawHlslBlock.Span);
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
        var found = new List<(int Offset, OutlineNode Node)>();

        foreach (var pair in index.Structs)
        {
            var decl = index.Declarations.TryGetValue(pair.Key, out var list) ? FindStruct(list) : null;
            var offset = decl?.Offset ?? pair.Value.NameOffset;
            if (offset <= 0)
            {
                continue;
            }

            var node = new OutlineNode
            {
                Name = pair.Key,
                Detail = "struct · " + pair.Value.Fields.Count + " 字段",
                Kind = SymbolKinds.Pick(SymbolKinds.StructPref, allowedKinds),
                Line = 0,
                SelStart = 0,
                SelEnd = 0,
                StartLine = 0,
                EndLine = 0,
            };

            foreach (var field in pair.Value.Fields)
            {
                if (field.Offset <= 0)
                {
                    continue;
                }

                node.Children.Add(new OutlineNode
                {
                    Name = field.Name,
                    Detail = field.Type,
                    Kind = SymbolKinds.Pick(SymbolKinds.FieldPref, allowedKinds),
                    Line = 0,
                    SelStart = 0,
                    SelEnd = 0,
                    StartLine = 0,
                    EndLine = 0,
                    // 字段位置稍后统一换算：这里先借用 Name 位置，见下方 Position 调用
                });
            }

            var positioned = Position(text, lineStarts, blockOffset + offset, pair.Key.Length);
            node = Rebase(node, positioned);

            // 字段单独定位（在结构体正文里再扫一次太贵，这里用索引里已有的字段偏移）
            for (var k = 0; k < pair.Value.Fields.Count && k < node.Children.Count; k++)
            {
                var field = pair.Value.Fields[k];
                if (field.Offset <= 0)
                {
                    continue;
                }

                node.Children[k] = Rebase(node.Children[k], Position(text, lineStarts, blockOffset + field.Offset, field.Name.Length));
            }

            found.Add((offset, node));
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

                var node = new OutlineNode
                {
                    Name = decl.Name,
                    Detail = "(" + decl.Arity + " 参数) → " + decl.Detail,
                    Kind = SymbolKinds.Pick(SymbolKinds.FunctionPref, allowedKinds),
                    Line = 0,
                    SelStart = 0,
                    SelEnd = 0,
                    StartLine = 0,
                    EndLine = 0,
                };

                found.Add((decl.Offset, Rebase(node, Position(text, lineStarts, blockOffset + decl.Offset, decl.Name.Length))));
            }
        }

        foreach (var op in index.MacroOps)
        {
            if (op.IsUndef)
            {
                continue;
            }

            var kind = SymbolKinds.Pick(SymbolKinds.MacroPref, allowedKinds);
            var node = new OutlineNode
            {
                Name = op.Macro.Name,
                Detail = op.Macro.IsFunctionLike
                    ? "#define （" + op.Macro.Arity + " 参数）"
                    : "#define " + Truncate(op.Macro.Body, 60),
                Kind = kind,
                Line = 0,
                SelStart = 0,
                SelEnd = 0,
                StartLine = 0,
                EndLine = 0,
            };

            found.Add((op.Macro.Offset, Rebase(node, Position(text, lineStarts, blockOffset + op.Macro.Offset, op.Macro.Name.Length))));
        }

        found.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

        var nodes = new List<OutlineNode>(found.Count);
        foreach (var item in found)
        {
            nodes.Add(item.Node);
        }

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

    /// <summary>把绝对偏移换算成「整行 range + 名字精确 selectionRange」。</summary>
    private static OutlineNode Position(string text, int[] lineStarts, int offset, int length)
    {
        var line = LineOffsets.LineOf(lineStarts, Math.Min(Math.Max(offset, 0), text.Length));
        var character = offset - lineStarts[line];

        return new OutlineNode
        {
            Name = string.Empty,
            Line = line,
            SelStart = character,
            SelEnd = character + length,
            StartLine = line,
            EndLine = line,
            StartChar = character,
            EndChar = character + length,
        };
    }

    /// <summary>把位置从一个临时节点搬到目标节点上（保持其余字段不变）。</summary>
    private static OutlineNode Rebase(OutlineNode node, OutlineNode position)
    {
        var rebased = new OutlineNode
        {
            Name = node.Name,
            Detail = node.Detail,
            Kind = node.Kind,
            Line = position.Line,
            SelStart = position.SelStart,
            SelEnd = position.SelEnd,
            StartLine = position.StartLine,
            EndLine = position.EndLine,
            StartChar = position.StartChar,
            EndChar = position.EndChar,
        };

        rebased.Children.AddRange(node.Children);
        return rebased;
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
