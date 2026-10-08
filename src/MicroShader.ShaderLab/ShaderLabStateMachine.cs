using MicroShader.Domain;

namespace MicroShader.ShaderLab;

/// <summary>
/// 模块 1：行扫描切片器与变体推导（规格书 §五 模块 1）。
/// </summary>
/// <remarks>
/// 
/// 纯 <see cref="ReadOnlySpan{T}"/> 单向有限状态机，"不引入任何 AST 库"。一次线性扫描同时完成：
/// 结构切片（Pass / HLSLPROGRAM / CGPROGRAM / HLSLINCLUDE / CGINCLUDE）、
/// 入口提取（"#pragma vertex/fragment/kernel"）、变体声明提取、"#include" 记录。
/// 
/// 
/// 线程安全：本类型"有状态且非线程安全"。每个文档分析线程必须持有自己的实例
/// （与 ADR-019 的 PerThread 池同构）。切片结果对象可用 <see cref="ShaderLabParseResult"/> 复用。
/// 
/// </remarks>
public sealed class ShaderLabStateMachine
{
    private const string MarkerHlslProgram = "HLSLPROGRAM";
    private const string MarkerEndHlsl = "ENDHLSL";
    private const string MarkerCgProgram = "CGPROGRAM";
    private const string MarkerEndCg = "ENDCG";
    private const string MarkerHlslInclude = "HLSLINCLUDE";
    private const string MarkerCgInclude = "CGINCLUDE";

    // 去重集合：随实例复用（本类型已声明为「有状态且非线程安全」，每分析线程独占一个实例）。
    // 用于把 CloseBlock 里 ActivePragmaDefines 的线性 Contains 从 O(V^2) 降到 O(V)。
    private readonly HashSet<string> _activeKeywordSeen = new(StringComparer.Ordinal);

    // 共享块索引按方言分桶（只有 HLSL/CG 两种），AssignSharedBlocks 据此把
    // 原来的 O(Passes x SharedBlocks) 逐元素比较降为 O(Passes + SharedBlocks) + 一次块拷贝。
    private readonly List<int> _sharedHlslIndices = new();
    private readonly List<int> _sharedCgIndices = new();

    /// <summary>切片一篇着色器文本，返回全新结果对象。</summary>
    public ShaderLabParseResult Parse(string filePath, string text)
    {
        var result = new ShaderLabParseResult();
        Parse(filePath, text.AsMemory(), result);
        return result;
    }

    /// <summary>切片一篇着色器文本，写入调用方提供的可复用结果对象（零分配热路径形态）。</summary>
    public ShaderLabParseResult Parse(string filePath, string text, ShaderLabParseResult into)
    {
        Parse(filePath, text.AsMemory(), into);
        return into;
    }

    /// <summary>切片一篇着色器文本（<see cref="ReadOnlyMemory{T}"/> 形态，便于零拷贝切片）。</summary>
    public void Parse(string filePath, ReadOnlyMemory<char> text, ShaderLabParseResult into)
    {
        ArgumentNullException.ThrowIfNull(into);

        into.Reset();
        into.FilePath = filePath;
        into.TargetUri = FileUri.FromPath(filePath);

        var span = text.Span;
        var length = span.Length;

        var inBlockComment = false;
        var blockCommentStartLine = 0;

        var lineNumber = 1;
        var pos = 0;

        var braceDepth = 0;
        var subShaderIndex = -1;
        var passCounter = 0;
        var currentPassIndex = -1;
        var currentPassName = string.Empty;
        var passScopeActive = false;
        var passScopeDepth = 0;
        var blockOrdinalInPass = 0;
        var pendingPass = false;

        // "Pass" 关键字所在行（与 StartLineNumber 同口径）。pendingPass 只记了"正在等 {"，
        // 没记行号；而文档大纲需要 Pass 自己的行来给出正确的 range。
        var currentPassStartLine = -1;

        // ── Tags 块采集状态（模块 9 标签语义校验的输入）──
        var tagsActive = false;
        var tagsSawBrace = false;
        var tagsBaseDepth = 0;
        var tagsOrdinal = 0;
        var tagsComment = false;
        var tagsScope = ShaderTagScope.Shader;

        var openActive = false;
        var openKind = ShaderBlockKind.None;
        var openShared = false;
        var openStartLine = 0;
        var openContentStart = 0;
        var openBlockOrdinal = 0;
        var openPassIndex = -1;
        var openPassName = string.Empty;
        var openPassStartLine = -1;
        var openSubShaderIndex = -1;

        while (pos <= length)
        {
            ReadOnlySpan<char> line;
            int nextPos;

            if (pos >= length)
            {
                line = default;
                nextPos = length + 1;
            }
            else
            {
                var lineEnd = span[pos..].IndexOf('\n');
                if (lineEnd < 0)
                {
                    line = span[pos..];
                    nextPos = length + 1;
                }
                else
                {
                    line = span.Slice(pos, lineEnd);
                    nextPos = pos + lineEnd + 1;
                }
            }

            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var info = default(LineInfo);
            var wasInBlockComment = inBlockComment;
            ShaderLineScanner.Scan(line, ref inBlockComment, ref info);
            if (!wasInBlockComment && inBlockComment)
            {
                blockCommentStartLine = lineNumber;
            }

            if (info.HasUnterminatedString)
            {
                into.AddDiagnostic(new ShaderDiagnosticItem
                {
                    Severity = DiagnosticSeverity.Warning,
                    Code = ShaderLabDiagnosticCodes.UnterminatedString,
                    Line = lineNumber,
                    Column = info.FirstStringStart + 1,
                    Message = "未闭合的字符串字面量：本行以 \" 开始但未找到结束引号。",
                    TargetUri = into.TargetUri,
                });
            }

            var passDeclaredThisLine = false;

            if (openActive)
            {
                // ── 代码块内部：只认结束标记与「另一个开启标记」 ──
                if (info.FirstKind == LineTokenKind.Identifier)
                {
                    var token = info.FirstToken;

                    if (IsEndMarkerFor(token, openKind))
                    {
                        CloseBlock(endLine: lineNumber, contentEnd: pos, unterminated: false, forceClosed: false);
                    }
                    else if (TryMatchStartMarker(token, out var newKind, out var newShared))
                    {
                        into.AddDiagnostic(new ShaderDiagnosticItem
                        {
                            Severity = DiagnosticSeverity.Error,
                            Code = ShaderLabDiagnosticCodes.ForceClosedBlock,
                            Line = lineNumber,
                            Column = 1,
                            Message = $"检测到新的代码块开启标记 {token.ToString()}，但上一个由 {MarkerName(openKind, openShared)} 开启的代码块尚未闭合（缺少 {EndMarkerName(openKind)}）。",
                            TargetUri = into.TargetUri,
                        });

                        CloseBlock(endLine: lineNumber - 1, contentEnd: pos, unterminated: true, forceClosed: true);
                        OpenBlock(newKind, newShared, lineNumber, nextPos);
                    }
                    else if (IsAnyEndMarker(token))
                    {
                        into.AddDiagnostic(new ShaderDiagnosticItem
                        {
                            Severity = DiagnosticSeverity.Error,
                            Code = ShaderLabDiagnosticCodes.MismatchedEndMarker,
                            Line = lineNumber,
                            Column = 1,
                            Message = $"代码块结束标记不匹配：{MarkerName(openKind, openShared)} 应当由 {EndMarkerName(openKind)} 闭合，实际遇到 {token.ToString()}。",
                            TargetUri = into.TargetUri,
                        });

                        CloseBlock(endLine: lineNumber, contentEnd: pos, unterminated: true, forceClosed: true);
                    }
                }
            }
            else
            {
                // ── Tags 块采集（仅 ShaderLab 顶层；代码块里的 Tags 是 HLSL 标识符，不是元数据）──
                // 层级取「本行之前」的状态：Tags 自身不会声明 Pass/SubShader。
                var tagsOpenedHere = false;

                if (!tagsActive
                    && info.FirstKind == LineTokenKind.Identifier
                    && info.FirstToken.SequenceEqual("Tags"))
                {
                    tagsActive = true;
                    tagsSawBrace = info.HasBraceOpen;
                    tagsBaseDepth = braceDepth;
                    tagsComment = wasInBlockComment;
                    tagsOrdinal = 0;
                    tagsScope = passScopeActive
                        ? ShaderTagScope.Pass
                        : subShaderIndex < 0 ? ShaderTagScope.Shader : ShaderTagScope.SubShader;
                    tagsOpenedHere = true;
                }

                if (tagsActive)
                {
                    if (!tagsSawBrace)
                    {
                        if (info.HasBraceOpen)
                        {
                            tagsSawBrace = true;
                        }
                        else if (!tagsOpenedHere && IsTagsRegionBoundary(in info))
                        {
                            // 畸形：Tags 之后迟迟没有出现 '{'，却来了下一条结构语句 —— 放弃采集，
                            // 否则后面的普通字符串会被误当成标签（实测抓到过这个假阳性）。
                            tagsActive = false;
                        }
                    }

                    // 只有真正进入花括号组之后才采集：合法形态下 '{' 之前的行只可能是空行或注释。
                    if (tagsActive && tagsSawBrace)
                    {
                        ShaderTagScanner.ScanLine(line, lineNumber, tagsScope, subShaderIndex, ref tagsOrdinal, ref tagsComment, into);
                    }
                }

                // ── ShaderLab 顶层 ──
                if (info.FirstKind == LineTokenKind.Identifier)
                {
                    var token = info.FirstToken;

                    if (TryMatchStartMarker(token, out var kind, out var shared))
                    {
                        OpenBlock(kind, shared, lineNumber, nextPos);
                    }
                    else if (token.SequenceEqual("Pass"))
                    {
                        passDeclaredThisLine = true;
                    }
                    else if (token.SequenceEqual("SubShader"))
                    {
                        subShaderIndex++;
                        into.SubShaderCount = subShaderIndex + 1;
                    }
                    else if (token.SequenceEqual("Name") && passScopeActive && currentPassName.Length == 0 && info.HasString)
                    {
                        currentPassName = info.FirstStringContent.ToString();
                    }
                }

                if (!openActive)
                {
                    if (passDeclaredThisLine)
                    {
                        pendingPass = true;
                        currentPassStartLine = lineNumber;
                    }

                    if (pendingPass && info.HasBraceOpen)
                    {
                        passScopeDepth = braceDepth + info.BraceDeltaBeforeFirstOpen + 1;
                        currentPassIndex = passCounter++;
                        currentPassName = string.Empty;
                        blockOrdinalInPass = 0;
                        passScopeActive = true;
                        pendingPass = false;
                    }
                    else if (pendingPass && !passDeclaredThisLine && info.HasCodeToken)
                    {
                        // 畸形：Pass 之后没有出现 '{'，放弃该作用域，避免后续无关花括号被误认为 Pass 体。
                        pendingPass = false;
                    }

                    var newDepth = braceDepth + info.BraceDelta;
                    if (passScopeActive && newDepth < passScopeDepth)
                    {
                        passScopeActive = false;
                        currentPassIndex = -1;
                        currentPassName = string.Empty;
                    }

                    braceDepth = newDepth < 0 ? 0 : newDepth;

                    // Tags 的花括号组在本行闭合 → 退出标签采集区域。
                    if (tagsActive && tagsSawBrace && braceDepth <= tagsBaseDepth)
                    {
                        tagsActive = false;
                    }
                }
            }

            lineNumber++;
            pos = nextPos;
            if (nextPos > length)
            {
                break;
            }
        }

        var lastLineNumber = lineNumber - 1;

        if (openActive)
        {
            into.HasUnterminatedBlock = true;
            into.AddDiagnostic(new ShaderDiagnosticItem
            {
                Severity = DiagnosticSeverity.Error,
                Code = ShaderLabDiagnosticCodes.UnterminatedBlock,
                Line = openStartLine,
                Column = 1,
                Message = "语法错误: 检测到未闭合的代码块，缺少对应的 ENDHLSL 或 ENDCG。",
                TargetUri = into.TargetUri,
            });

            CloseBlock(endLine: lastLineNumber, contentEnd: length, unterminated: true, forceClosed: false);
        }

        if (inBlockComment)
        {
            into.HasUnterminatedBlockComment = true;
            into.AddDiagnostic(new ShaderDiagnosticItem
            {
                Severity = DiagnosticSeverity.Error,
                Code = ShaderLabDiagnosticCodes.UnterminatedBlockComment,
                Line = blockCommentStartLine,
                Column = 1,
                Message = "未闭合的块注释 /*：此后所有代码都被当作注释吞掉，请补上 */。",
                TargetUri = into.TargetUri,
            });
        }

        AssignSharedBlocks(into);
        return;

        void OpenBlock(ShaderBlockKind kind, bool shared, int markerLine, int contentStartOffset)
        {
            openActive = true;
            openKind = kind;
            openShared = shared;
            openStartLine = markerLine;
            openContentStart = contentStartOffset;
            openPassIndex = passScopeActive ? currentPassIndex : -1;
            openPassName = passScopeActive ? currentPassName : string.Empty;
            openPassStartLine = passScopeActive ? currentPassStartLine : -1;
            openSubShaderIndex = subShaderIndex;
            openBlockOrdinal = shared ? 0 : blockOrdinalInPass++;
        }

        void CloseBlock(int endLine, int contentEnd, bool unterminated, bool forceClosed)
        {
            var start = openContentStart;
            var end = contentEnd;
            if (end < start)
            {
                end = start;
            }

            if (end > length)
            {
                end = length;
            }

            var block = new ShaderProgramBlock(
                openKind,
                openShared,
                openStartLine,
                endLine,
                start,
                end,
                unterminated,
                forceClosed);

            if (openShared)
            {
                into.SharedBlocks.Add(block);
            }

            var snippet = into.RentSnippet();
            snippet.Kind = openKind;
            snippet.IsSharedBlock = openShared;
            snippet.PassIndex = openPassIndex;
            snippet.IsOrphan = openPassIndex < 0;
            snippet.PassName = openPassName;
            snippet.PassStartLineNumber = openPassStartLine;
            snippet.StartLineNumber = openStartLine;
            snippet.EndLineNumber = endLine;
            snippet.SubShaderIndex = openSubShaderIndex;
            snippet.BlockOrdinalInPass = openBlockOrdinal;
            snippet.ContentStartOffset = start;
            snippet.ContentEndOffset = end;
            snippet.IsUnterminated = unterminated;
            snippet.IsForceClosed = forceClosed;
            snippet.RawHlslBlock = text.Slice(start, end - start);
            snippet.IsLegacyCg = openKind == ShaderBlockKind.Cg;

            ShaderPragmaScanner.ScanBlock(text.Span.Slice(start, end - start), openStartLine + 1, snippet);

            // 去重改用 HashSet.Add 的返回值（O(1)）；ActivePragmaDefines 仍保序输出，契约不变。
            // 原实现是 List.Contains(..., Ordinal) 的线性扫描，V 个变体时退化成 O(V^2)。
            _activeKeywordSeen.Clear();
            foreach (var variant in snippet.Variants)
            {
                if (variant.SelectedKeyword.Length == 0)
                {
                    continue;
                }

                if (_activeKeywordSeen.Add(variant.SelectedKeyword))
                {
                    snippet.ActivePragmaDefines.Add(variant.SelectedKeyword);
                }
            }

            if (openShared)
            {
                // HLSLINCLUDE/CGINCLUDE 的内容会被拼进每一个同类程序块（v2.0 清单第 3 条），
                // 因此它的 pragma/include 必须与 Pass 块一样被扫描并保留 —— 否则共享块里声明的
                // 关键字会从宏矩阵里静默消失。它不进 Passes：共享块不作为独立编译单元。
                into.SharedSnippets.Add(snippet);
            }
            else
            {
                ApplyLegacyCgPolicy(snippet);
                into.Passes.Add(snippet);
            }

            openActive = false;
            openKind = ShaderBlockKind.None;
            openShared = false;
        }
    }

    /// <summary>
    /// 遗留 CG 方言的抑制策略：块内未引用现代渲染管线核心头文件时，主动抑制原生 DXC 派发。
    /// 老式内置管线（"UnityCG.cginc" + SM3.0 语义）喂给 SM6.0 只会产出大面积误报，
    /// 这是规格书 §五 模块 1 的明确要求。
    /// </summary>
    private static void ApplyLegacyCgPolicy(ShaderPassSnippet snippet)
    {
        if (!snippet.IsLegacyCg)
        {
            return;
        }

        foreach (var include in snippet.Includes)
        {
            if (IsModernPipelineHeader(include.Path))
            {
                return;
            }
        }

        snippet.SuppressNativeDispatch = true;
        snippet.SuppressReason = "遗留 CGPROGRAM 块未引用现代渲染管线核心头文件（Core.hlsl / com.unity.render-pipelines.* / ShaderLibrary/），按设计抑制 DXC 派发以避免大面积误报。";
    }

    private static bool IsModernPipelineHeader(string path)
    {
        if (path.Length == 0)
        {
            return false;
        }

        return path.Contains("Core.hlsl", StringComparison.OrdinalIgnoreCase)
            || path.Contains("com.unity.render-pipelines", StringComparison.OrdinalIgnoreCase)
            || path.Contains("ShaderLibrary/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 把文件级共享块挂到每一个同类编译单元上。
    /// v2.0 语义："HLSLINCLUDE" 作用于本文件内"所有" "HLSLPROGRAM" 块（官方原文
    /// "anywhere in this source file"），"CGINCLUDE" 同理作用于所有 "CGPROGRAM"，两者互不交叉。
    /// 因为共享块可能出现在文件任意位置（甚至在被它影响的 Pass 之后），必须等整篇扫描完成后再回填。
    /// </summary>
    private void AssignSharedBlocks(ShaderLabParseResult result)
    {
        if (result.SharedBlocks.Count == 0)
        {
            return;
        }

        // 先按方言把共享块索引分桶一次（下标升序，保持与原来一致的顺序），
        // 再对每个 Pass 直接块拷贝对应桶 —— 避免了 P x S 次枚举比较。
        _sharedHlslIndices.Clear();
        _sharedCgIndices.Clear();
        for (var i = 0; i < result.SharedBlocks.Count; i++)
        {
            switch (result.SharedBlocks[i].Kind)
            {
                case ShaderBlockKind.Hlsl:
                    _sharedHlslIndices.Add(i);
                    break;
                case ShaderBlockKind.Cg:
                    _sharedCgIndices.Add(i);
                    break;
            }
        }

        foreach (var snippet in result.Passes)
        {
            var bucket = snippet.Kind switch
            {
                ShaderBlockKind.Hlsl => _sharedHlslIndices,
                ShaderBlockKind.Cg => _sharedCgIndices,
                _ => null,
            };

            if (bucket is { Count: > 0 })
            {
                snippet.ApplicableSharedBlockIndices.AddRange(bucket);
            }
        }
    }

    /// <summary>
    /// 判断一行是否意味着「畸形 Tags 区域到此为止」：Tags 之后迟迟没有出现 "{"，
    /// 却来了下一条 ShaderLab 结构语句。此时必须放弃采集，否则会把后面的标签算进这一块。
    /// </summary>
    private static bool IsTagsRegionBoundary(in LineInfo info)
    {
        if (info.FirstKind != LineTokenKind.Identifier)
        {
            return false;
        }

        var token = info.FirstToken;
        return token.SequenceEqual("Pass")
            || token.SequenceEqual("SubShader")
            || token.SequenceEqual("Tags")
            || token.SequenceEqual("Name")
            || token.SequenceEqual("LOD")
            || token.SequenceEqual("Properties")
            || token.SequenceEqual("HLSLPROGRAM")
            || token.SequenceEqual("CGPROGRAM")
            || token.SequenceEqual("HLSLINCLUDE")
            || token.SequenceEqual("CGINCLUDE")
            || token.SequenceEqual("FallBack")
            || token.SequenceEqual("CustomEditor");
    }

    private static bool TryMatchStartMarker(ReadOnlySpan<char> token, out ShaderBlockKind kind, out bool shared)
    {
        if (token.SequenceEqual(MarkerHlslProgram))
        {
            kind = ShaderBlockKind.Hlsl;
            shared = false;
            return true;
        }

        if (token.SequenceEqual(MarkerHlslInclude))
        {
            kind = ShaderBlockKind.Hlsl;
            shared = true;
            return true;
        }

        if (token.SequenceEqual(MarkerCgProgram))
        {
            kind = ShaderBlockKind.Cg;
            shared = false;
            return true;
        }

        if (token.SequenceEqual(MarkerCgInclude))
        {
            kind = ShaderBlockKind.Cg;
            shared = true;
            return true;
        }

        kind = ShaderBlockKind.None;
        shared = false;
        return false;
    }

    private static bool IsEndMarkerFor(ReadOnlySpan<char> token, ShaderBlockKind kind) => kind switch
    {
        ShaderBlockKind.Hlsl => token.SequenceEqual(MarkerEndHlsl),
        ShaderBlockKind.Cg => token.SequenceEqual(MarkerEndCg),
        _ => false,
    };

    private static bool IsAnyEndMarker(ReadOnlySpan<char> token) =>
        token.SequenceEqual(MarkerEndHlsl) || token.SequenceEqual(MarkerEndCg);

    private static string EndMarkerName(ShaderBlockKind kind) => kind switch
    {
        ShaderBlockKind.Hlsl => MarkerEndHlsl,
        ShaderBlockKind.Cg => MarkerEndCg,
        _ => "ENDHLSL/ENDCG",
    };

    private static string MarkerName(ShaderBlockKind kind, bool shared) => kind switch
    {
        ShaderBlockKind.Hlsl => shared ? MarkerHlslInclude : MarkerHlslProgram,
        ShaderBlockKind.Cg => shared ? MarkerCgInclude : MarkerCgProgram,
        _ => "?",
    };
}
