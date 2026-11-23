namespace MicroShader.IntelliSenseEngine;

/// 一次签名帮助请求的定位结果：被调用的名字，以及光标落在第几个实参上。
/// 名字可能是单级（EvaluateAlphaCore），也可能是成员调用（tex.Sample）。
public readonly record struct SignatureContext(string Callee, int ActiveParameter)
{
    /// 从光标位置向前找当前所在的调用括号。
    /// 只回答「谁被调用、现在在第几个参数」，不判断这个名字是不是真的函数 ——
    /// 那是查询侧的职责：查不到签名就不回包，客户端表现是「这里没有签名」，
    /// 而不是给出一个错的。
    /// 扫描上限 4KB：签名帮助由左括号与逗号触发，逐键调用，必须给一个硬边界；
    /// 超过上限就放弃，宁可少给也不把大文件整篇扫一遍。
    /// 深度归零时遇到分号或花括号就停：那是上一条语句的边界，说明光标不在任何调用里。
    public static bool TryFind(string text, int cursor, out SignatureContext found)
    {
        ArgumentNullException.ThrowIfNull(text);
        found = default;

        if (cursor <= 0 || cursor > text.Length)
        {
            return false;
        }

        var limit = Math.Max(0, cursor - MaxScanLength);
        var depth = 0;
        var open = -1;

        for (var i = cursor - 1; i >= limit; i--)
        {
            var c = text[i];

            if (c == ')')
            {
                depth++;
                continue;
            }

            if (c == '(')
            {
                if (depth == 0)
                {
                    open = i;
                    break;
                }

                depth--;
                continue;
            }

            if (depth == 0 && (c == ';' || c == '{' || c == '}'))
            {
                return false;
            }
        }

        if (open < 0)
        {
            return false;
        }

        // 括号左侧：跳过空白后取标识符，允许 a.b 这样的成员调用。
        var end = open - 1;
        while (end >= limit && char.IsWhiteSpace(text[end]))
        {
            end--;
        }

        var start = end + 1;
        while (start > limit && (IsIdentifierChar(text[start - 1]) || text[start - 1] == '.'))
        {
            start--;
        }

        if (start > end)
        {
            return false;
        }

        var callee = text[start..(end + 1)];
        if (callee[0] == '.' || callee[^1] == '.')
        {
            return false;
        }

        // 数光标之前的顶层逗号：第几个参数由它决定。
        var parameter = 0;
        var nesting = 0;
        for (var i = open + 1; i < cursor; i++)
        {
            var c = text[i];

            if (c is '(' or '[' or '{')
            {
                nesting++;
            }
            else if (c is ')' or ']' or '}')
            {
                nesting--;
            }
            else if (c == ',' && nesting == 0)
            {
                parameter++;
            }
        }

        found = new SignatureContext(callee.ToString(), parameter);
        return true;
    }

    /// 向前扫描的硬上限。超过就当作「不在调用里」，避免大文件逐键全扫。
    private const int MaxScanLength = 4096;

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
