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

    /// <summary>
    /// 宏实参：光标位于 "SAMPLER(" 或 "CBUFFER_START(" 的括号内（可带前缀），
    /// 如 "CBUFFER_START(" 或 "CBUFFER_START(Uni"。
    /// </summary>
    MacroArgument = 3,

    /// <summary>
    /// 方括号属性标注：光标位于左方括号之后、右方括号之前（可带标识符前缀），如 "[b"。
    /// </summary>
    Attribute = 4,
}

/// <summary>解析出的触发上下文。</summary>
public readonly record struct TriggerContext(TriggerKind Kind, string Prefix, string MemberBase)
{
    /// <summary>
    /// 成员访问的完整点号链（从头到紧邻点号的那一级），如 "v.posOS.xy" 在 "xy" 处为 ["v","posOS"]。
    /// 单级 "i." 时为 ["i"]。<see cref="MemberBase"/> 恒等于最后一项（向后兼容）。
    /// </summary>
    public string[] MemberChain { get; init; } = [];

    /// <summary>MacroArgument 时：括号前的宏名（"CBUFFER_START" / "SAMPLER"）；其余场景为空串。</summary>
    public string CallName { get; init; } = string.Empty;

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

        // 方括号属性标注：光标位于 "[" 之后、"]" 之前（同行、可带空格与标识符前缀）。
        // 数组下标与数组声明用的是同一对方括号，靠 "[" 左侧的非空白字符排除：
        // 左侧是标识符、右圆括号或另一个左方括号时，那是下标或数组声明，不是属性标注。
        // 允许左侧是 "]"：HLSL 允许连续写多个属性，如 [unroll(4)] [allow_uav_condition] for(...)。
        if (prefixStart > lineStart)
        {
            var bracket = prefixStart - 1;
            while (bracket >= lineStart && (text[bracket] == ' ' || text[bracket] == '\t'))
            {
                bracket--;
            }

            if (bracket >= lineStart && text[bracket] == '[' && IsAttributeBracket(text, lineStart, bracket))
            {
                return new TriggerContext(TriggerKind.Attribute, prefix, string.Empty);
            }
        }

        // 宏实参上下文："SAMPLER(" / "CBUFFER_START(" 的括号内（同行、可带前缀）。
        // 只认这两个宏名 —— 其余括号内一律保持原行为（函数调用实参位置不打扰），
        // 这是刻意收窄的触发面：括号内补全如果没有明确的候选来源，弹窗只会是噪音。
        if (prefixStart > lineStart)
        {
            var open = prefixStart - 1;
            while (open >= lineStart && (text[open] == ' ' || text[open] == '\t'))
            {
                open--;
            }

            if (open >= lineStart && text[open] == '(')
            {
                var nameEnd = open;
                while (nameEnd > lineStart && (text[nameEnd - 1] == ' ' || text[nameEnd - 1] == '\t'))
                {
                    nameEnd--;
                }

                var nameStart = nameEnd;
                while (nameStart > lineStart && IsIdentifierChar(text[nameStart - 1]))
                {
                    nameStart--;
                }

                var callName = text[nameStart..nameEnd];
                if (callName.SequenceEqual("CBUFFER_START") || callName.SequenceEqual("SAMPLER"))
                {
                    return new TriggerContext(TriggerKind.MacroArgument, prefix.ToString(), string.Empty)
                    {
                        CallName = callName.ToString(),
                    };
                }
            }
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

    /// 判断某个左方括号是不是属性标注的左括号。
    /// 左邻是行首空白、分号、花括号、等号、逗号或上一个属性的右方括号时算属性；
    /// 左邻是标识符、右圆括号、点号或另一个左方括号时是数组下标、数组声明或嵌套下标，不算属性。
    /// 遇到上一个属性的右方括号时，向前找配对的左方括号再对那一对做同样判定：
    /// 这样连续多个属性仍然成立，而二维数组 a[0][1] 会被正确排除。
    private static bool IsAttributeBracket(string text, int lineStart, int open)
    {
        var before = open - 1;
        while (before >= lineStart && (text[before] == ' ' || text[before] == '\t'))
        {
            before--;
        }

        if (before < lineStart)
        {
            return true;
        }

        var c = text[before];
        if (c == ']')
        {
            var depth = 0;
            for (var i = before; i >= lineStart; i--)
            {
                if (text[i] == ']')
                {
                    depth++;
                }
                else if (text[i] == '[')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return IsAttributeBracket(text, lineStart, i);
                    }
                }
            }

            return false;
        }

        return c is not ('[' or ')' or '.') && !IsIdentifierChar(c);
    }

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
