using System.Buffers;
using MicroShader.Domain;
using MicroShader.ObservabilityEngine;

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
    /// <summary>栈上首扫的词元槽位数。这是热路径的常态容量，不是语义上限。</summary>
    private const int StackTokenCapacity = 64;

    /// <summary>
    /// 单条 #pragma 的词元硬上限。正常 "multi_compile" 一行几十个关键字离它很远，
    /// 它只用来挡住病态超长行把内存吃穿：超过即截断，并记一条 DiagTrace，绝不静默丢弃。
    /// </summary>
    private const int HardTokenLimit = 1024;

    /// <summary>
    /// pragma 行内空白：空格、制表符、回车。回车只在未经切行处理的续行上出现，集合保持与原规则一致。
    /// </summary>
    private static readonly SearchValues<char> InlineWhitespace = SearchValues.Create(" \t\r");

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
        Span<CodeToken> stackTokens = stackalloc CodeToken[StackTokenCapacity];
        CodeToken[]? rented = null;

        try
        {
            var count = CollectTokens(line, start, stackTokens, out var stackFull);

            if (!stackFull)
            {
                ParsePragmaTokens(line, stackTokens[..count], lineNumber, snippet);
                return;
            }

            // 栈上 64 个槽位不够。一行几十个关键字的 multi_compile 是常态，
            // 此时必须重扫而不是沿用被写满的栈缓冲 —— 少一个关键字就会让整段变体被错判。
            // 重扫只在罕见长行发生，CollectTokens 写满即停，成本可接受。
            rented = ArrayPool<CodeToken>.Shared.Rent(HardTokenLimit);

            // 池化数组可能比请求的长（ArrayPool 按尺寸桶向上取整），这里再切一刀，
            // 保证「单条 pragma 最多 HardTokenLimit 个词元」是真实上界而不是池桶尺寸。
            var rentedTokens = rented.AsSpan(0, HardTokenLimit);
            count = CollectTokens(line, start, rentedTokens, out var stillFull);

            if (stillFull)
            {
                // 病态超长行：截断到硬上限，但必须留一条痕迹。
                // 静默丢弃词元造成的「变体少定义」最难查，宁可多写一行追踪。
                DiagTrace.Mark("pragma 词元超过硬上限，已截断 line=" + lineNumber + " 上限=" + HardTokenLimit);
            }

            ParsePragmaTokens(line, rentedTokens[..count], lineNumber, snippet);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<CodeToken>.Shared.Return(rented);
            }
        }
    }

    /// <summary>把一行 #pragma 的词元翻译成入口名或变体声明。</summary>
    /// <param name="line">整行切片（取词元原文用）。</param>
    /// <param name="tokens">已收集的词元，首个是指令名。</param>
    /// <param name="lineNumber">该行 1-based 物理绝对行号。</param>
    /// <param name="snippet">输出目标。</param>
    private static void ParsePragmaTokens(ReadOnlySpan<char> line, ReadOnlySpan<CodeToken> tokens, int lineNumber, ShaderPassSnippet snippet)
    {
        if (tokens.Length == 0)
        {
            return;
        }

        var directive = line.Slice(tokens[0].Start, tokens[0].Length).ToString();

        switch (directive)
        {
            case "vertex":
                if (snippet.VertexEntry is null && tokens.Length > 1)
                {
                    snippet.VertexEntry = line.Slice(tokens[1].Start, tokens[1].Length).ToString();
                }

                return;

            case "fragment":
                if (snippet.FragmentEntry is null && tokens.Length > 1)
                {
                    snippet.FragmentEntry = line.Slice(tokens[1].Start, tokens[1].Length).ToString();
                }

                return;

            case "kernel":
                if (tokens.Length > 1)
                {
                    var name = line.Slice(tokens[1].Start, tokens[1].Length).ToString();
                    snippet.KernelEntry ??= name;
                    snippet.KernelEntries.Add(name);
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

        var keywords = new List<string>(Math.Max(0, tokens.Length - 1));
        var seen = SeenKeywords;
        seen.Clear();
        string? selected = null;

        for (var i = 1; i < tokens.Length; i++)
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
        if (!TryReadIncludePath(line, start, out var pathStart, out var pathLength))
        {
            return;
        }

        snippet.Includes.Add(new IncludeDirective
        {
            Path = line.Slice(pathStart, pathLength).ToString(),
            LineNumber = lineNumber,
            Column = pathStart + 1, // 路径首字符的 1-based 列号
            IsWithPragmas = isWithPragmas,
        });
    }

    /// <summary>
    /// 取出 "#include" 之后的路径区间，同时接受双引号与尖括号两种写法。
    /// 只认这两种定界符：引号写法要求第一个词元是字符串，尖括号写法要求同一行内找到配对的右尖括号；
    /// 缺少闭合定界符时直接失败 —— 绝不退化成「把后面任意 token 当路径」。
    /// </summary>
    private static bool TryReadIncludePath(ReadOnlySpan<char> line, int start, out int pathStart, out int pathLength)
    {
        pathStart = 0;
        pathLength = 0;

        var blankStop = line[start..].IndexOfAnyExcept(InlineWhitespace);
        var i = blankStop < 0 ? line.Length : start + blankStop;

        if (i >= line.Length)
        {
            return false;
        }

        if (line[i] == '<')
        {
            // 尖括号写法（#include <UnityCG.cginc>）：路径取到首个 '>' 之前。
            // 注意 CollectTokens 会跳过 '<' '>' 这两个非标识符字符，故这里必须单独按定界符切片。
            var close = line[(i + 1)..].IndexOf('>');
            if (close <= 0)
            {
                return false;
            }

            pathStart = i + 1;
            pathLength = close;
            return true;
        }

        // #include 只关心第一个词元（路径），64 个槽位永远够用；写满与否与这里无关。
        Span<CodeToken> tokens = stackalloc CodeToken[StackTokenCapacity];
        var count = CollectTokens(line, start, tokens, out _);

        if (count == 0 || !tokens[0].IsString)
        {
            return false;
        }

        pathStart = tokens[0].Start;
        pathLength = tokens[0].Length;
        return true;
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
    /// <param name="line">整行切片。</param>
    /// <param name="start">起始列（跳过 "#pragma" 等前缀）。</param>
    /// <param name="buffer">输出缓冲。</param>
    /// <param name="bufferFull">缓冲被写满而提前停下（行内可能还有词元，调用方需扩容重扫）。</param>
    private static int CollectTokens(ReadOnlySpan<char> line, int start, Span<CodeToken> buffer, out bool bufferFull)
    {
        var n = line.Length;
        var i = start;
        var count = 0;

        while (i < n && count < buffer.Length)
        {
            var c = line[i];

            if (c is ' ' or '\t' or '\r')
            {
                // 整段掠过连续空白：返回第一个非空白下标，-1 表示直到行尾都是空白。
                var blankStop = line[i..].IndexOfAnyExcept(InlineWhitespace);
                i = blankStop < 0 ? n : i + blankStop;
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
                // 与 ShaderLineScanner 共用同一套字符集合：整段掠过标识符字符，-1 表示命中到行尾。
                var wordStop = line[i..].IndexOfAnyExcept(ShaderLineScanner.IdentifierChars);
                var j = wordStop < 0 ? n : i + wordStop;

                buffer[count++] = new CodeToken(i, j - i, false);
                i = j;
                continue;
            }

            i++;
        }

        // count 顶到缓冲长度且行内还有字符 = 可能是「写满即停」而不是「正好扫完」。
        // 这里只做保守判定：真正的判据交给调用方的扩容重扫（重扫不会再写满即停才算了结）。
        bufferFull = count >= buffer.Length && i < n;
        return count;
    }
}
