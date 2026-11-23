using System.Globalization;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 组装产物的不变量校验（ADR-023 的 I1 / I2 / I3 / I5）。
/// </summary>
/// <remarks>
/// 这些不变量是 "#line" 方案的"唯一安全网"：行号锚定错了，编译器不会报错，
/// 只会把诊断指到错误的行上 —— 而错误行号的诊断比没有诊断更坏。因此每次组装都要能自证。
/// <list type="bullet">
/// <item>"I2" 锚定段的第 k 行必须与源文件逐字节相等（重写行除外）。</item>
/// <item>"I3" 把渲染文本里自己产出的 "#line" 重新解析一遍，必须与段记录的行号/路径一致。</item>
/// <item>"I5" 抽样把 (行, 列) 在 UTF-16 与 UTF-8 之间往返，必须落在合法字符边界上。</item>
/// </list>
/// "I1 的落地形式"见 <see cref="VirtualTextAssembler"/> 的类注释：文档原文的「注入段只在文件头」
/// 与「HLSLINCLUDE 注入进每个程序块」互相冲突，本实现取「除头部外，注入段恰好 1 行且必须是 #line」。
/// </remarks>
public static class AssemblyInvariants
{
    /// <summary>校验一个组装产物；返回违反项描述（空列表 = 全部通过）。</summary>
    public static List<string> Verify(AssembledShaderText text, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(sourceText);

        var violations = new List<string>();
        var map = text.LineMap;
        var segments = map.Segments;

        if (segments.Count == 0)
        {
            violations.Add("I0: 组装产物没有任何段（渲染文本与映射表都不是由段列表生成的）。");
            return violations;
        }

        if (segments[0].Kind != SegmentKind.Injected)
        {
            violations.Add("I1: 文件头不是注入段。");
        }

        VerifyInjectionSegments(text, segments, violations);
        VerifyLineAnchors(text, segments, violations);
        VerifyAnchoredText(text, segments, sourceText, violations);
        VerifyColumnRoundTrip(text, segments, violations);

        return violations;
    }

    private static void VerifyInjectionSegments(AssembledShaderText text, IReadOnlyList<Segment> segments, List<string> violations)
    {
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (segment.Kind != SegmentKind.Injected)
            {
                continue;
            }

            if (i == 0)
            {
                var last = text.GetLine(segment.RenderedEndLine);
                if (!TryParseLineAnchor(last, out _, out _))
                {
                    violations.Add($"I1: 头部注入段（渲染行 {segment.RenderedStartLine}..{segment.RenderedEndLine}）的最后一行不是 #line 指令。");
                }

                continue;
            }

            // 文件中间的注入段：只允许是孤零零一条 #line。
            if (segment.RenderedLineCount != 1)
            {
                violations.Add($"I1: 非头部注入段（渲染行 {segment.RenderedStartLine}..{segment.RenderedEndLine}）有 {segment.RenderedLineCount} 行，只允许 1 行 #line。");
                continue;
            }

            var line = text.GetLine(segment.RenderedStartLine);
            if (!TryParseLineAnchor(line, out _, out _))
            {
                violations.Add($"I1: 渲染行 {segment.RenderedStartLine} 是注入行但不是 #line 指令：{line.ToString()}");
            }

            if (i + 1 >= segments.Count || segments[i + 1].Kind != SegmentKind.Anchored)
            {
                violations.Add($"I1: 渲染行 {segment.RenderedStartLine} 的 #line 后面没有紧跟锚定段。");
            }
        }
    }

    private static void VerifyLineAnchors(AssembledShaderText text, IReadOnlyList<Segment> segments, List<string> violations)
    {
        foreach (var segment in segments)
        {
            if (segment.Kind != SegmentKind.Anchored)
            {
                continue;
            }

            var anchorLine = segment.RenderedStartLine - 1;
            if (anchorLine < 1)
            {
                violations.Add("I3: 锚定段之前没有 #line 锚。");
                continue;
            }

            var anchor = text.GetLine(anchorLine);
            if (!TryParseLineAnchor(anchor, out var declared, out var path))
            {
                violations.Add($"I3: 渲染行 {anchorLine} 不是可解析的 #line 指令：{anchor.ToString()}");
                continue;
            }

            if (declared != segment.SourceStartLine)
            {
                violations.Add($"I3: #line 声明源行 {declared}，但段记录的起始源行是 {segment.SourceStartLine}。");
            }

            if (!string.Equals(path, text.VirtualPath, StringComparison.Ordinal))
            {
                violations.Add($"I3: #line 路径 '{path}' 与虚拟路径 '{text.VirtualPath}' 不一致。");
            }
        }
    }

    private static void VerifyAnchoredText(
        AssembledShaderText text,
        IReadOnlyList<Segment> segments,
        string sourceText,
        List<string> violations)
    {
        var source = sourceText.AsSpan();
        var sourceIndex = SourceLineIndex.Build(source);

        foreach (var segment in segments)
        {
            if (segment.Kind != SegmentKind.Anchored)
            {
                continue;
            }

            for (var k = 0; k < segment.RenderedLineCount; k++)
            {
                var renderedLine = segment.RenderedStartLine + k;
                if (text.LineMap.IsRewritten(renderedLine))
                {
                    continue;
                }

                var sourceLine = segment.SourceStartLine + k;
                if (sourceLine < 1 || sourceLine > sourceIndex.LineCount)
                {
                    violations.Add($"I2: 渲染行 {renderedLine} 指向不存在的源行 {sourceLine}。");
                    continue;
                }

                var expectedStart = sourceIndex.LineStart(sourceLine);
                var expectedEnd = sourceIndex.LineEnd(source, sourceLine, source.Length);
                var expected = source[expectedStart..expectedEnd];
                var actual = text.GetLine(renderedLine);

                // 允许渲染行被内容区间末端截断（只比较共同长度），但不允许内容不同。
                if (actual.Length > expected.Length || !expected[..actual.Length].SequenceEqual(actual))
                {
                    violations.Add($"I2: 渲染行 {renderedLine} 与源行 {sourceLine} 不一致：'{actual.ToString()}' vs '{expected.ToString()}'");
                }
            }
        }
    }

    private static void VerifyColumnRoundTrip(AssembledShaderText text, IReadOnlyList<Segment> segments, List<string> violations)
    {
        var mapped = 0;

        foreach (var segment in segments)
        {
            if (segment.Kind != SegmentKind.Anchored)
            {
                continue;
            }

            for (var k = 0; k < segment.RenderedLineCount; k++)
            {
                var renderedLine = segment.RenderedStartLine + k;
                var line = text.GetLine(renderedLine);

                for (var column = 1; column <= line.Length + 1; column++)
                {
                    // 指向低代理项的列不是字符边界，按定义不参与往返。
                    if (column <= line.Length && char.IsLowSurrogate(line[column - 1]))
                    {
                        continue;
                    }

                    var utf8 = Utf8Column.ToUtf8Column(line, column);
                    var back = Utf8Column.ToUtf16Column(line, utf8, out var exact);
                    if (!exact || back != column)
                    {
                        violations.Add($"I5: 渲染行 {renderedLine} 列 {column} 的 UTF-16/UTF-8 往返不一致（utf8={utf8} → {back}, exact={exact}）。");
                        break;
                    }
                }

                if (!text.LineMap.TryMapRenderedToSource(renderedLine, 1, out var sourceLine, out var sourceColumn, out _))
                {
                    violations.Add($"I5: 渲染行 {renderedLine} 无法映射回源文件。");
                    continue;
                }

                if (sourceLine != segment.SourceStartLine + k || sourceColumn < 1)
                {
                    violations.Add($"I5: 渲染行 {renderedLine} 映射到源 ({sourceLine},{sourceColumn})，期望行 {segment.SourceStartLine + k}。");
                }

                mapped++;
            }
        }

        if (mapped == 0)
        {
            violations.Add("I5: 没有任何锚定行参与列号往返校验。");
        }
    }

    /// <summary>解析 "#line N "path""。</summary>
    public static bool TryParseLineAnchor(ReadOnlySpan<char> line, out int declaredLine, out string path)
    {
        declaredLine = 0;
        path = string.Empty;

        var span = line.TrimStart();
        if (span.Length < 6 || span[0] != '#' || !span[1..].StartsWith("line", StringComparison.Ordinal))
        {
            return false;
        }

        span = span[5..].TrimStart();
        var digits = 0;
        while (digits < span.Length && char.IsAsciiDigit(span[digits]))
        {
            digits++;
        }

        if (digits == 0 || !int.TryParse(span[..digits], NumberStyles.Integer, CultureInfo.InvariantCulture, out declaredLine))
        {
            return false;
        }

        span = span[digits..].TrimStart();
        if (span.Length < 2 || span[0] != '"')
        {
            return false;
        }

        var close = span[1..].IndexOf('"');
        if (close < 0)
        {
            return false;
        }

        path = span.Slice(1, close).ToString();
        return true;
    }
}
