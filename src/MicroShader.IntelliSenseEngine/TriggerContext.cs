namespace MicroShader.IntelliSenseEngine;

/// <summary>光标处的触发形态。</summary>
public enum TriggerKind
{
    /// <summary>不构成补全触发场景（例如光标在空白处）。</summary>
    None = 0,

    /// <summary>成员访问：光标处于点号之后（可带前缀），如 "input." 或 "input.po"。</summary>
    MemberAccess = 1,

    /// <summary>标识符前缀补全，如 "Trans"。</summary>
    IdentifierPrefix = 2,
}

/// <summary>解析出的触发上下文。</summary>
public readonly record struct TriggerContext(TriggerKind Kind, string Prefix, string MemberBase)
{
    /// <summary>
    /// 成员访问的完整点号链（从头到紧邻点号的那一级），如 "v.posOS.xy" 在 "xy" 处为 ["v","posOS"]。
    /// 单级 "i." 时为 ["i"]。<see cref="MemberBase"/> 恒等于最后一项（向后兼容）。
    /// </summary>
    public string[] MemberChain { get; init; } = [];

    /// <summary>
    /// 从「文档全文 + LSP 0-based 位置」推导触发上下文。
    /// </summary>
    /// <remarks>
    /// 把 0-based 的 (line, character) 变成绝对偏移需要按换行符行进；这里线性扫描到目标行 ——
    /// 单次补全一次 O(文档长度) 的字符级扫描（无分配、无正则），实测远低于 5ms 配额。
    /// "只在代码态触发"：位于注释或字符串内的请求返回 <see cref="TriggerKind.None"/>，
    /// 否则写注释时也会弹出无关候选。
    /// </remarks>
    public static TriggerContext Detect(string text, int line, int character)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (line < 0 || character < 0)
        {
            return new TriggerContext(TriggerKind.None, string.Empty, string.Empty);
        }

        var lineStart = 0;
        var currentLine = 0;
        while (currentLine < line && lineStart < text.Length)
        {
            var nl = text.IndexOf('\n', lineStart);
            if (nl < 0)
            {
                break;
            }

            lineStart = nl + 1;
            currentLine++;
        }

        if (currentLine != line)
        {
            return new TriggerContext(TriggerKind.None, string.Empty, string.Empty);
        }

        var cursor = Math.Min(text.Length, lineStart + character);
        if (cursor > 0 && text[cursor - 1] == '\r')
        {
            cursor--;
        }

        if (InCommentOrString(text, cursor))
        {
            return new TriggerContext(TriggerKind.None, string.Empty, string.Empty);
        }

        var prefixStart = cursor;
        while (prefixStart > lineStart && IsIdentifierChar(text[prefixStart - 1]))
        {
            prefixStart--;
        }

        var prefix = text[prefixStart..cursor];

        // "input." 与 "input.po" 两种形态在这里统一判掉
        if (prefixStart > lineStart && text[prefixStart - 1] == '.')
        {
            var baseEnd = prefixStart - 1;
            var baseStart = baseEnd;
            while (baseStart > lineStart && IsIdentifierChar(text[baseStart - 1]))
            {
                baseStart--;
            }

            if (baseStart == baseEnd)
            {
                return new TriggerContext(TriggerKind.None, string.Empty, string.Empty);
            }

            // 嵌套链："a.b.c." → 继续向左收集 "b"、"a"（同行内、纯标识符段；遇调用/下标即停）
            var chain = new List<string>(4) { text[baseStart..baseEnd] };
            var head = baseStart;
            while (head - 1 > lineStart && text[head - 1] == '.' && IsIdentifierChar(text[head - 2]))
            {
                var segEnd = head - 1;
                var segStart = segEnd;
                while (segStart > lineStart && IsIdentifierChar(text[segStart - 1]))
                {
                    segStart--;
                }

                chain.Add(text[segStart..segEnd]);
                head = segStart;
            }

            chain.Reverse();
            return new TriggerContext(TriggerKind.MemberAccess, prefix.ToString(), text[baseStart..baseEnd])
            {
                MemberChain = [.. chain],
            };
        }

        return prefix.Length == 0
            ? new TriggerContext(TriggerKind.None, string.Empty, string.Empty)
            : new TriggerContext(TriggerKind.IdentifierPrefix, prefix.ToString(), string.Empty);
    }

    /// <summary>
    /// 判断光标是否落在行注释、块注释或字符串字面量"内部"。
    /// </summary>
    /// <remarks>
    /// "注意与「跳过注释」的区别"：跳过注释只是把扫描位置推进到注释之后，
    /// 但这里要回答的是「光标本身在不在里面」——
    /// 早期实现只回传「块注释是否仍未闭合」，于是行注释里的 "i." 会被误判成成员访问
    /// （自检 "IntelliSense.Trigger" 抓到）。现在对三种区间都做显式的"光标是否落在其中"判定。
    /// </remarks>
    private static bool InCommentOrString(string text, int cursor)
    {
        var i = 0;

        while (i < cursor)
        {
            var c = text[i];

            if (c == '/' && i + 1 < cursor && text[i + 1] == '/')
            {
                var newline = text.IndexOf('\n', i);
                if (newline < 0 || newline >= cursor)
                {
                    return true;   // 光标在本行注释内
                }

                i = newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < cursor && text[i + 1] == '*')
            {
                var close = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0 || close + 2 > cursor)
                {
                    return true;   // 光标在块注释内（含未闭合）
                }

                i = close + 2;
                continue;
            }

            if (c == '"')
            {
                var j = i + 1;
                while (j < cursor && text[j] != '"')
                {
                    j += text[j] == (char)92 ? 2 : 1;
                }

                if (j >= cursor)
                {
                    return true;   // 光标在字符串内
                }

                i = j + 1;
                continue;
            }

            i++;
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
