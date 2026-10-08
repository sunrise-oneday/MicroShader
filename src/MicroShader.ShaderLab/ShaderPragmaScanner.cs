using MicroShader.Domain;

namespace MicroShader.ShaderLab;

/// <summary>代码态词元（pragma 参数解析用）。只记录标识符与字符串，忽略运算符噪声。</summary>
internal readonly struct CodeToken
{
    public CodeToken(int start, int length, bool isString)
    {
        Start = start;
        Length = length;
        IsString = isString;
    }

    public int Start { get; }
    public int Length { get; }
    public bool IsString { get; }
}

/// <summary>
/// 块内容扫描器：在切出的代码块内部提取 "#pragma vertex/fragment/kernel"、变体声明与 "#include"。
/// 与 <see cref="ShaderLineScanner"/> 共用同一套「注释/字符串感知」的行扫描，不做任何 AST 化。
/// </summary>
internal static class ShaderPragmaScanner
{
    private const int MaxTokens = 64;

    // 线程级复用去重集合：把单条 #pragma 内关键字去重从 List.Contains 的 O(K^2) 降到 O(K)。
    // 只在本线程的扫描过程中短暂占用，每次使用前 Clear，无堆增长、无锁。
    [ThreadStatic]
    private static HashSet<string>? t_seenKeywords;

    private static HashSet<string> SeenKeywords => t_seenKeywords ??= new HashSet<string>(StringComparer.Ordinal);

    /// <summary>扫描一个代码块的内容区。</summary>
    /// <param name="content">块内容切片（不含开启/结束标记行）。</param>
    /// <param name="firstContentLineNumber">块内容第一行的 1-based 物理绝对行号。</param>
    /// <param name="snippet">输出目标。</param>
    public static void ScanBlock(ReadOnlySpan<char> content, int firstContentLineNumber, ShaderPassSnippet snippet)
    {
        var inBlockComment = false;
        var lineNumber = firstContentLineNumber;
        var pos = 0;

        while (pos <= content.Length)
        {
            ReadOnlySpan<char> line;
            int nextPos;

            if (pos >= content.Length)
            {
                line = default;
                nextPos = content.Length + 1;
            }
            else
            {
                var lineEnd = content[pos..].IndexOf('\n');
                if (lineEnd < 0)
                {
                    line = content[pos..];
                    nextPos = content.Length + 1;
                }
                else
                {
                    line = content.Slice(pos, lineEnd);
                    nextPos = pos + lineEnd + 1;
                }
            }

            if (line.Length > 0 && line[^1] == '\r')
            {
                line = line[..^1];
            }

            var info = default(LineInfo);
            ShaderLineScanner.Scan(line, ref inBlockComment, ref info);

            if (info.FirstKind == LineTokenKind.HashDirective)
            {
                if (info.FirstToken.SequenceEqual("pragma"))
                {
                    ParsePragma(line, info.FirstTokenEnd, lineNumber, snippet);
                }
                else if (info.FirstToken.SequenceEqual("include") || info.FirstToken.SequenceEqual("include_with_pragmas"))
                {
                    var isWithPragmas = info.FirstToken.SequenceEqual("include_with_pragmas");
                    ParseInclude(line, info.FirstTokenEnd, lineNumber, isWithPragmas, snippet);
                }
            }

            lineNumber++;
            pos = nextPos;
            if (nextPos > content.Length)
            {
                break;
            }
        }
    }

    private static void ParsePragma(ReadOnlySpan<char> line, int start, int lineNumber, ShaderPassSnippet snippet)
    {
        Span<CodeToken> tokens = stackalloc CodeToken[MaxTokens];
        var count = CollectTokens(line, start, tokens);

        if (count == 0)
        {
            return;
        }

        var directive = line.Slice(tokens[0].Start, tokens[0].Length).ToString();

        switch (directive)
        {
            case "vertex":
                if (snippet.VertexEntry is null && count > 1)
                {
                    snippet.VertexEntry = line.Slice(tokens[1].Start, tokens[1].Length).ToString();
                }

                return;

            case "fragment":
                if (snippet.FragmentEntry is null && count > 1)
                {
                    snippet.FragmentEntry = line.Slice(tokens[1].Start, tokens[1].Length).ToString();
                }

                return;

            case "kernel":
                if (snippet.KernelEntry is null && count > 1)
                {
                    snippet.KernelEntry = line.Slice(tokens[1].Start, tokens[1].Length).ToString();
                }

                return;
        }

        if (!TryClassifyVariantDirective(directive, out var kind, out var isLocal, out var stage))
        {
            return;
        }

        if (kind == ShaderVariantKind.BuiltIn)
        {
            snippet.Variants.Add(new ShaderVariantDeclaration
            {
                Kind = ShaderVariantKind.BuiltIn,
                DirectiveName = directive,
                Stage = stage,
                IsLocal = isLocal,
                LineNumber = lineNumber,
            });
            return;
        }

        var keywords = new List<string>(Math.Max(0, count - 1));
        var seen = SeenKeywords;
        seen.Clear();
        string? selected = null;

        for (var i = 1; i < count; i++)
        {
            if (tokens[i].IsString)
            {
                continue;
            }

            var keyword = line.Slice(tokens[i].Start, tokens[i].Length).ToString();

            if (IsOffPlaceholder(keyword))
            {
                continue;
            }

            // 选变体策略（ADR-006）= 本 set 中「第一个真实关键字」。
            // 文档对「首项」有两种读法，这里按 ADR-006 的判定理由裁定：
            //   判定理由原文 ——「避免因无宏激活导致 #if defined(...) 内部逻辑被预处理器直接裁掉，
            //   杜绝『代码写错但编译器给虚假通过』的缺陷」。
            // 下划线占位符是该 set 的「关闭变体」；若选中它，由 #if defined(_MAIN_LIGHT_SHADOWS) 保护的
            // 大片 URP 代码（阴影、级联、屏幕空间阴影…）会被整段裁掉 —— 正是该 ADR 要杜绝的情形。
            // 因此跳过占位符、取第一个真实关键字；占位符本身绝不作为宏注入。
            selected ??= keyword;

            if (seen.Add(keyword))
            {
                keywords.Add(keyword);
            }
        }

        snippet.Variants.Add(new ShaderVariantDeclaration
        {
            Kind = kind,
            DirectiveName = directive,
            Stage = stage,
            IsLocal = isLocal,
            Keywords = keywords,
            SelectedKeyword = selected ?? string.Empty,
            LineNumber = lineNumber,
        });
    }

    private static void ParseInclude(ReadOnlySpan<char> line, int start, int lineNumber, bool isWithPragmas, ShaderPassSnippet snippet)
    {
        Span<CodeToken> tokens = stackalloc CodeToken[MaxTokens];
        var count = CollectTokens(line, start, tokens);

        if (count == 0 || !tokens[0].IsString)
        {
            return;
        }

        snippet.Includes.Add(new IncludeDirective
        {
            Path = line.Slice(tokens[0].Start, tokens[0].Length).ToString(),
            LineNumber = lineNumber,
            Column = tokens[0].Start + 1, // 路径首字符的 1-based 列号
            IsWithPragmas = isWithPragmas,
        });
    }

    /// <summary>"_" / "__" 是「该 set 的关闭变体」占位符，不是关键字。</summary>
    private static bool IsOffPlaceholder(string keyword) =>
        keyword.Length is 1 or 2 && keyword.AsSpan().IndexOfAnyExcept('_') < 0;

    /// <summary>
    /// 把 "multi_compile*" / "shader_feature*" 指令名拆成 (种类, 是否 local, 阶段修饰)。
    /// 后缀不属于 {local, fragment, vertex} 组合的一律判为 Unity 内建变体指令（"_fog"/"_instancing"/"_fwdbase"…）。
    /// </summary>
    private static bool TryClassifyVariantDirective(string directive, out ShaderVariantKind kind, out bool isLocal, out ShaderStage stage)
    {
        kind = ShaderVariantKind.MultiCompile;
        isLocal = false;
        stage = ShaderStage.None;

        ReadOnlySpan<char> rest;
        if (directive.StartsWith("multi_compile", StringComparison.Ordinal))
        {
            kind = ShaderVariantKind.MultiCompile;
            rest = directive.AsSpan("multi_compile".Length);
        }
        else if (directive.StartsWith("shader_feature", StringComparison.Ordinal))
        {
            kind = ShaderVariantKind.ShaderFeature;
            rest = directive.AsSpan("shader_feature".Length);
        }
        else
        {
            return false;
        }

        if (rest.IsEmpty)
        {
            return true;
        }

        if (rest[0] != '_')
        {
            kind = ShaderVariantKind.BuiltIn;
            return true;
        }

        var suffix = rest[1..];
        foreach (var range in suffix.Split('_'))
        {
            var segment = suffix[range];

            if (segment.SequenceEqual("local"))
            {
                isLocal = true;
                continue;
            }

            if (segment.SequenceEqual("fragment"))
            {
                stage = ShaderStage.Fragment;
                continue;
            }

            if (segment.SequenceEqual("vertex"))
            {
                stage = ShaderStage.Vertex;
                continue;
            }

            if (segment.SequenceEqual("compute"))
            {
                stage = ShaderStage.Compute;
                continue;
            }

            // 未识别的后缀（fog / instancing / fwdbase / prepassfinal / …）→ Unity 内建变体指令。
            kind = ShaderVariantKind.BuiltIn;
            isLocal = false;
            stage = ShaderStage.None;
            return true;
        }

        return true;
    }

    /// <summary>收集代码态标识符与字符串词元，跳过注释与运算符。</summary>
    private static int CollectTokens(ReadOnlySpan<char> line, int start, Span<CodeToken> buffer)
    {
        var n = line.Length;
        var i = start;
        var count = 0;

        while (i < n && count < buffer.Length)
        {
            var c = line[i];

            if (c is ' ' or '\t' or '\r')
            {
                i++;
                continue;
            }

            if (c == '/')
            {
                if (i + 1 < n && line[i + 1] == '/')
                {
                    break;
                }

                if (i + 1 < n && line[i + 1] == '*')
                {
                    var close = line[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                    if (close < 0)
                    {
                        break;
                    }

                    i += 2 + close + 2;
                    continue;
                }

                i++;
                continue;
            }

            if (c == '"')
            {
                var j = i + 1;
                var closed = false;
                while (j < n)
                {
                    if (line[j] == '\\')
                    {
                        j += 2;
                        continue;
                    }

                    if (line[j] == '"')
                    {
                        closed = true;
                        break;
                    }

                    j++;
                }

                var end = closed ? j : n;
                buffer[count++] = new CodeToken(i + 1, Math.Max(0, end - i - 1), true);
                i = closed ? j + 1 : n;
                continue;
            }

            if (ShaderLineScanner.IsIdentifierStart(c) || char.IsAsciiDigit(c))
            {
                var j = i;
                while (j < n && ShaderLineScanner.IsIdentifierChar(line[j]))
                {
                    j++;
                }

                buffer[count++] = new CodeToken(i, j - i, false);
                i = j;
                continue;
            }

            i++;
        }

        return count;
    }
}
