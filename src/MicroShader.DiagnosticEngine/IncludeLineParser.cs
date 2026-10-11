namespace MicroShader.DiagnosticEngine;

/// <summary>一行 "#include" / "#include_with_pragmas" 的解析结果。</summary>
internal readonly struct IncludeLine
{
    /// <summary>路径原文（不含引号/尖括号）。</summary>
    public string Path { get; init; }

    /// <summary>路径首个字符在该行中的 0-based 索引。</summary>
    public int PathStartIndex { get; init; }

    /// <summary>路径文字之前的字符数（含前导空白与开引号/尖括号）。</summary>
    public int PrefixLength { get; init; }

    /// <summary>是否为 "#include_with_pragmas"（DXC 不识别，必须降级）。</summary>
    public bool IsWithPragmas { get; init; }

    /// <summary>是否尖括号形式。</summary>
    public bool IsAngleBracket { get; init; }
}

/// <summary>
/// 逐行解析 "#include" 指令。
/// </summary>
/// <remarks>
/// 为什么装配器要自己再解析一遍，而不是只用模块 1 记录好的 "IncludeDirective"：
/// 共享块（"HLSLINCLUDE"）的内容也要拼进每个程序块，而它的行属于"另一个"切片对象；
/// 逐行解析让「代码块 → 行 → 是否 include」这条链在装配器内部闭环，不依赖跨模块的簿记一致性。
/// 只有「行首（允许前导空白）就是 #include」才算命中，因此 "// #include "x"" 这类注释行天然不会被误判。
/// 注意模块 1 的 "IncludeDirective.Column" 实为"路径首字符"的 1-based 列号
/// （= <see cref="PathStartIndex"/> + 1），而不是 Domain 注释里写的「路径起始的引号」的列号。
/// 装配器使用自解析结果，并在自检里以「两条路径给出同一结论」的形式钉住这个差异。
/// </remarks>
internal static class IncludeLineParser
{
    /// <summary>尝试把一行解析成 include 指令。</summary>
    public static bool TryParse(ReadOnlySpan<char> line, out IncludeLine parsed)
    {
        parsed = default;

        var index = 0;
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
        {
            index++;
        }

        if (index >= line.Length || line[index] != '#')
        {
            return false;
        }

        index++;
        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
        {
            index++;
        }

        var nameStart = index;
        while (index < line.Length && (char.IsLetter(line[index]) || line[index] == '_'))
        {
            index++;
        }

        var name = line[nameStart..index];
        bool withPragmas;
        if (name.SequenceEqual("include"))
        {
            withPragmas = false;
        }
        else if (name.SequenceEqual("include_with_pragmas"))
        {
            withPragmas = true;
        }
        else
        {
            return false;
        }

        while (index < line.Length && (line[index] == ' ' || line[index] == '\t'))
        {
            index++;
        }

        if (index >= line.Length)
        {
            return false;
        }

        char close;
        bool angle;
        if (line[index] == '"')
        {
            close = '"';
            angle = false;
        }
        else if (line[index] == '<')
        {
            close = '>';
            angle = true;
        }
        else
        {
            return false;
        }

        var pathStart = index + 1;
        var relative = line[pathStart..].IndexOf(close);
        var pathLength = relative < 0 ? line.Length - pathStart : relative;
        if (pathLength == 0)
        {
            return false;
        }

        parsed = new IncludeLine
        {
            Path = line.Slice(pathStart, pathLength).ToString(),
            PathStartIndex = pathStart,
            PrefixLength = pathStart,
            IsWithPragmas = withPragmas,
            IsAngleBracket = angle,
        };

        return true;
    }
}
