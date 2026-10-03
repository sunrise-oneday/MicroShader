namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 入队期脱敏器：把用户绝对路径替换成占位符，使内存黑匣子本身不含敏感信息
/// （v2.0 清单第 7 条：脱敏时机必须前移到「入队时」，dump 阶段只做兜底扫描）。
/// </summary>
/// <remarks>
/// 全部在 <see cref="ReadOnlySpan{T}"/> 上做逐字符比较与拷贝，"零分配、零正则"。
/// 反斜杠与正斜杠视为等价，因此同时覆盖 "C:\Users\x\p" 与 "C:/Users/x/p"。
/// </remarks>
public sealed class LogSanitizer
{
    /// <summary>不做任何替换的实例（热路径据此整体跳过 sanitize）。</summary>
    public static readonly LogSanitizer Empty = new([]);

    private readonly string[] _roots;
    private readonly string _placeholder;

    public LogSanitizer(IEnumerable<string> roots, string placeholder = "~/")
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentException.ThrowIfNullOrEmpty(placeholder);

        // 长的根先匹配：C:\Users\name 必须先于 C:\Users 命中，否则会切出半截路径。
        List<string> list = [];
        foreach (string root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root)) list.Add(root);
        }
        list.Sort(static (a, b) => b.Length.CompareTo(a.Length));
        _roots = [.. list];
        _placeholder = placeholder;
    }

    /// <summary>是否有根需要替换。热路径用它一次判断就整体跳过。</summary>
    public bool HasRoots => _roots.Length > 0;

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>
    /// 把 <paramref name="source"/> 的脱敏结果写入 <paramref name="destination"/>，返回写入长度。
    /// 目标不够长时安静截断（绝不抛异常 —— 日志路径不允许影响主流程）。
    /// </summary>
    public int Apply(ReadOnlySpan<char> source, Span<char> destination)
    {
        if (!HasRoots)
        {
            int n = Math.Min(source.Length, destination.Length);
            source[..n].CopyTo(destination);
            return n;
        }

        int written = 0;
        int i = 0;
        while (i < source.Length)
        {
            int matched = MatchRootLength(source, i);
            if (matched > 0)
            {
                written = Append(_placeholder, destination, written);
                i += matched;

                // 吃掉紧跟的分隔符：否则 C:\Users\me\Proj\Assets\A.shader
                // 会变成 ~/\Assets\A.shader（双分隔）而不是 ~/Assets\A.shader。
                if (i < source.Length && (source[i] == '\\' || source[i] == '/')) i++;
            }
            else
            {
                if (written >= destination.Length) break;
                destination[written++] = source[i];
                i++;
            }
        }
        return written;
    }

    /// <summary>分配一个脱敏后的新字符串（仅 dump / 自检路径使用）。</summary>
    public string ApplyToString(ReadOnlySpan<char> source)
    {
        if (!HasRoots) return source.ToString();
        Span<char> buffer = source.Length <= 512 ? stackalloc char[512] : new char[source.Length + 64];
        int n = Apply(source, buffer);
        return buffer[..n].ToString();
    }

    private static int Append(string text, Span<char> destination, int written)
    {
        int n = Math.Min(text.Length, destination.Length - written);
        text.AsSpan(0, n).CopyTo(destination[written..]);
        return written + n;
    }

    /// <summary>在 <paramref name="start"/> 处命中哪个根；返回根长度，未命中返回 0。</summary>
    private int MatchRootLength(ReadOnlySpan<char> source, int start)
    {
        foreach (string root in _roots)
        {
            if (source.Length - start < root.Length) continue;
            // 先比首字符，绝大多数位置在这里就被否掉。
            if (!SameChar(source[start], root[0])) continue;

            bool hit = true;
            for (int k = 1; k < root.Length; k++)
            {
                if (!SameChar(source[start + k], root[k])) { hit = false; break; }
            }
            if (hit) return root.Length;
        }
        return 0;
    }

    private static bool SameChar(char a, char b)
    {
        if (a == b) return true;
        if (a == '\\' && b == '/') return true;
        if (a == '/' && b == '\\') return true;
        return char.ToUpperInvariant(a) == char.ToUpperInvariant(b);
    }
}
