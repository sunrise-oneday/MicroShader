using MicroShader.IntelliSenseEngine;

namespace MicroShader.NavigationEngine;

/// <summary>结构体成员的写入点回溯。</summary>
/// <remarks>
/// 片元里读到的每一个 varying 都是顶点写进来的副本：类型定义在结构体里，真正决定这个值的是
/// 顶点函数里那条「输出变量.字段 = …」。只给结构体字段声明，作者还得自己在几百行里翻那一条。
///
/// 匹配规则刻意收紧成两条，宁可少给也不给错行：
/// 一、接收者的类型必须能从同一块里解析出来（变量表里有它，且指向一个带该字段的结构体）——
/// 结构体定义在 include 里、或接收者类型未知时直接放弃；
/// 二、写入点的接收者类型必须与它一致（同名不同结构体的字段不串行）。
///
/// 注释与字符串整段跳过：注释掉的旧赋值不该被当成写入点。
/// </remarks>
internal static class StructMemberWrites
{
    /// <summary>一个写入点：字段名 token 在块正文里的相对偏移与长度。</summary>
    public readonly record struct Site(int Offset, int Length);

    /// <summary>
    /// 取光标处成员访问的接收者标识符：text(offset) 是一个名字，它左边紧挨着点号。
    /// </summary>
    /// <remarks>
    /// 只认纯标识符接收者。链式访问（a.b.c 里光标停在 c）时返回 b，再由类型表决定要不要继续，
    /// 猜不到类型就返回 false，交给上层保持原行为。
    /// </remarks>
    public static bool TryFindReceiver(string text, int offset, out string receiver)
    {
        receiver = string.Empty;

        var p = offset - 1;
        while (p >= 0 && char.IsWhiteSpace(text[p]))
        {
            p--;
        }

        if (p < 0 || text[p] != '.')
        {
            return false;
        }

        p--;
        while (p >= 0 && char.IsWhiteSpace(text[p]))
        {
            p--;
        }

        var end = p + 1;
        while (p >= 0 && SymbolAtCursor.IsIdentifierChar(text[p]))
        {
            p--;
        }

        var start = p + 1;
        if (start >= end || char.IsAsciiDigit(text[start]))
        {
            return false;
        }

        receiver = text[start..end];
        return true;
    }

    /// <summary>
    /// 扫出块正文里对「类型为 typeName 的变量的 fieldName 字段」的写入点。
    /// </summary>
    /// <param name="hlsl">块正文（偏移相对块起点）。</param>
    /// <param name="variablesToType">同一块的变量表，用来判断每个接收者的类型。</param>
    /// <param name="typeName">接收者应有的类型名。</param>
    /// <param name="fieldName">被写的字段名。</param>
    /// <param name="into">结果追加到这里（调用方负责清空与复用）。</param>
    public static void ScanWrites(
        ReadOnlySpan<char> hlsl,
        IReadOnlyDictionary<string, string> variablesToType,
        string typeName,
        string fieldName,
        List<Site> into)
    {
        var i = 0;

        while (i < hlsl.Length)
        {
            var c = hlsl[i];

            if (c == '/' && i + 1 < hlsl.Length)
            {
                if (hlsl[i + 1] == '/')
                {
                    i += 2;
                    while (i < hlsl.Length && hlsl[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (hlsl[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < hlsl.Length && !(hlsl[i] == '*' && hlsl[i + 1] == '/'))
                    {
                        i++;
                    }

                    i = Math.Min(hlsl.Length, i + 2);
                    continue;
                }
            }

            if (c == '"')
            {
                i = SkipString(hlsl, i);
                continue;
            }

            if (!SymbolAtCursor.IsIdentifierChar(c) || char.IsAsciiDigit(c))
            {
                i++;
                continue;
            }

            var receiverStart = i;
            while (i < hlsl.Length && SymbolAtCursor.IsIdentifierChar(hlsl[i]))
            {
                i++;
            }

            var receiver = hlsl[receiverStart..i];

            var p = i;
            SkipTrivia(hlsl, ref p);
            if (p >= hlsl.Length || hlsl[p] != '.')
            {
                continue;
            }

            p++;
            SkipTrivia(hlsl, ref p);

            if (p >= hlsl.Length || (!char.IsAsciiLetter(hlsl[p]) && hlsl[p] != '_'))
            {
                continue;
            }

            var fieldStart = p;
            while (p < hlsl.Length && SymbolAtCursor.IsIdentifierChar(hlsl[p]))
            {
                p++;
            }

            var field = hlsl[fieldStart..p];
            i = p;

            // 字段名先比（span 直比，零分配），命中了才为接收者取一个 string 去查表。
            if (!field.SequenceEqual(fieldName.AsSpan())
                || !variablesToType.TryGetValue(receiver.ToString(), out var receiverType)
                || !receiverType.Equals(typeName, StringComparison.Ordinal))
            {
                continue;
            }

            var q = p;
            SkipMemberChain(hlsl, ref q);
            if (!IsWriteOperator(hlsl, q))
            {
                continue;
            }

            into.Add(new Site(fieldStart, p - fieldStart));
        }
    }

    /// <summary>跳过成员访问之后的分量、数组下标与调用链，停在赋值运算符前。</summary>
    private static void SkipMemberChain(ReadOnlySpan<char> hlsl, ref int i)
    {
        while (true)
        {
            var p = i;
            SkipTrivia(hlsl, ref p);

            if (p < hlsl.Length && hlsl[p] == '.')
            {
                p++;
                SkipTrivia(hlsl, ref p);
                if (p >= hlsl.Length || (!char.IsAsciiLetter(hlsl[p]) && hlsl[p] != '_'))
                {
                    i = p;
                    return;
                }

                while (p < hlsl.Length && SymbolAtCursor.IsIdentifierChar(hlsl[p]))
                {
                    p++;
                }

                i = p;
                continue;
            }

            if (p < hlsl.Length && hlsl[p] == '[')
            {
                var depth = 0;
                while (p < hlsl.Length)
                {
                    if (hlsl[p] == '[')
                    {
                        depth++;
                    }
                    else if (hlsl[p] == ']')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            p++;
                            break;
                        }
                    }

                    p++;
                }

                i = p;
                continue;
            }

            i = p;
            return;
        }
    }

    /// <summary>当前是不是一个写（= 或复合赋值），且不是比较运算。</summary>
    private static bool IsWriteOperator(ReadOnlySpan<char> hlsl, int i)
    {
        if (i >= hlsl.Length)
        {
            return false;
        }

        var c = hlsl[i];

        if (c == '=')
        {
            return i + 1 >= hlsl.Length || hlsl[i + 1] != '=';
        }

        if (c is '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^')
        {
            var next = i + 1;
            return next < hlsl.Length && hlsl[next] == '='
                && (next + 1 >= hlsl.Length || hlsl[next + 1] != '=');
        }

        return false;
    }

    /// <summary>跳过空白与注释。</summary>
    private static void SkipTrivia(ReadOnlySpan<char> hlsl, ref int i)
    {
        while (i < hlsl.Length)
        {
            var c = hlsl[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < hlsl.Length)
            {
                if (hlsl[i + 1] == '/')
                {
                    i += 2;
                    while (i < hlsl.Length && hlsl[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (hlsl[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < hlsl.Length && !(hlsl[i] == '*' && hlsl[i + 1] == '/'))
                    {
                        i++;
                    }

                    i = Math.Min(hlsl.Length, i + 2);
                    continue;
                }
            }

            return;
        }
    }

    /// <summary>跳过一个字符串字面量（含转义）。</summary>
    private static int SkipString(ReadOnlySpan<char> hlsl, int i)
    {
        i++;
        while (i < hlsl.Length && hlsl[i] != '"' && hlsl[i] != '\n')
        {
            if (hlsl[i] == '\\')
            {
                i++;
            }

            i++;
        }

        return i < hlsl.Length && hlsl[i] == '"' ? i + 1 : i;
    }
}
