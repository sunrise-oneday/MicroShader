namespace MicroShader.CoordinationEngine.Documents;

/// <summary>
/// 行级差分：实现 ADR-011「探针行 = 服务端 diff 出的首个改动行」。
/// </summary>
/// <remarks>
/// "为什么不能依赖光标"：v2.0 修正清单第 12 条已核实，LSP 3.17 规范全文检索
/// "didFocus"/"activeTextEditor"/"focused" 命中数为 "0" —— 协议里根本
/// 没有光标/焦点通知。模块 8 正文第 6 节「基于光标变动事件的跨标签页调度」建立在一个
/// 不存在的前提上；而 ADR-011 早已把探针行的来源定为「服务端自己 diff」。
/// 本类就是那个来源，也让整个调度链路可以在无头环境下被完整测试。
/// </remarks>
public static class LineDiff
{
    /// <summary>
    /// 返回新文本中首个与旧文本不同的"1-based 行号"；两文本完全相同时返回 "0"。
    /// </summary>
    /// <remarks>
    /// 语义被刻意收紧为「首个差异字符在新文本中所处的行」——位置落在某一行的行尾（即那个
    /// "\n" 本身）时算作该行。第一版额外做了「差异点前一个字符是换行就 +1」的修正，
    /// 结果是「在第 10 行行首插入内容」被报成第 11 行（自检 "Doc.LineDiff" 抓到）。
    /// 那条修正对「纯追加」看似更直观，但对「行首插入」是错的，而后者在编辑场景中更常见。
    /// </remarks>
    public static int FirstChangedLine(ReadOnlySpan<char> oldText, ReadOnlySpan<char> newText)
    {
        // 单趟：找首个差异字符的同时累计公共前缀里的换行数。
        // 原实现先扫一遍求 common、再从头扫一遍数 '\n'，把同一段文本读了两遍（didChange 每次调用）。
        var max = Math.Min(oldText.Length, newText.Length);
        var common = 0;
        var line = 1;
        while (common < max && oldText[common] == newText[common])
        {
            if (newText[common] == '\n') line++;
            common++;
        }

        var identical = common == oldText.Length && common == newText.Length;
        if (identical) return 0;

        // common 就是新文本中第一个「新内容」的字符下标；line 已在同一趟里数出其所在行。
        return line;
    }

    /// <summary>
    /// 判断新旧文本之间的差异是否"仅限于空白与注释"——如果是，诊断结果不会改变，
    /// 调用方（LspServerSession.HandleDidChange）可以跳过分析。
    /// </summary>
    /// <remarks>
    /// "为什么需要它"：每次 didChange 都会 Dispatch 一次分析（ShaderLab Parse + DXC 诊断），
    /// 即使用户只是敲了个空格、缩进调整、改注释里的一两个字。这道判定用单趟扫描把这类编辑
    /// 过滤掉，省掉一次完整的 Parse + 编译器往返——在长文件上尤其可观。
    ///
    /// "判定口径"：逐字符对比"剥离 trivia（空白 + 行注释 + 块注释）后的内容流"。
    /// 两个文本的 trivia 可以不同（多了一行注释、少了几个空格），只要非 trivia 的字符
    /// 完全相同就返回 true。字符串字面量"不"被视为 trivia —— 字符串内容的改变会影响诊断，
    /// 因此扫描器维护一个"当前是否位于字符串内"的状态：字符串内部不再剥离空白与注释，
    /// 一律逐字符比较（含反斜杠转义），直到匹配的结束引号才回到 trivia 剥离。
    ///
    /// "为什么不做完整 tokenizer"：这里的目标是"快速排除"，不是精确解析。
    /// 剥离 trivia 只需识别 //...、/*...*/、空白三种形态，成本远低于完整分词；
    /// 字符串状态只需一个布尔量，不引入分词器。
    ///
    /// "为什么字符串状态不可省"：没有它时，形如 #pragma message "see http://x" 的行内字符串
    /// 会把 // 之后的整段吞成行注释；字符串里含 /* 时更会把其后**整篇**当作块注释。
    /// 两种情况下，字符串内部乃至其后真实的代码改动都会被判成"仅 trivia"而跳过分析。
    /// 自检 Doc.TriviaOnlyChanged 覆盖了这两种形态与转义引号。
    /// </remarks>
    public static bool OnlyTriviaChanged(ReadOnlySpan<char> oldText, ReadOnlySpan<char> newText)
    {
        var oi = 0;
        var ni = 0;
        var inString = false;

        while (true)
        {
            // 字符串内部不做 trivia 剥离：那里的空白与 // 都是字符串内容的一部分。
            if (!inString)
            {
                SkipTrivia(oldText, ref oi);
                SkipTrivia(newText, ref ni);
            }

            if (oi >= oldText.Length || ni >= newText.Length)
                break;

            var oc = oldText[oi];
            if (oc != newText[ni])
                return false;

            // 两侧在这一位上是同一个字符，故字符串状态可以共用一份。
            if (oc == '"')
            {
                inString = !inString;
                oi++;
                ni++;
                continue;
            }

            // 字符串内的转义序列整体作为内容比较，避免 \" 被误当成结束引号。
            if (inString && oc == '\\' && oi + 1 < oldText.Length && ni + 1 < newText.Length)
            {
                if (oldText[oi + 1] != newText[ni + 1])
                    return false;

                oi += 2;
                ni += 2;
                continue;
            }

            oi++;
            ni++;
        }

        // 收尾：未处在字符串内时跳过尾部 trivia，再确认两侧都恰好到末尾。
        if (!inString)
        {
            SkipTrivia(oldText, ref oi);
            SkipTrivia(newText, ref ni);
        }

        return oi == oldText.Length && ni == newText.Length;
    }

    /// <summary>跳过空白与注释，游标停在第一个非 trivia 字符上（与 TriggerContext 同口径）。</summary>
    private static void SkipTrivia(ReadOnlySpan<char> text, ref int i)
    {
        while (i < text.Length)
        {
            var c = text[i];

            // 空白
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // 行注释 //...
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                    i++;
                continue;
            }

            // 块注释 /*...*/
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                i = Math.Min(text.Length, i + 2);
                continue;
            }

            break;
        }
    }
}
