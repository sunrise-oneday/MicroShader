namespace MicroShader.ContextEngine;

/// <summary>
/// VFS 路径归一化：全仓路径比较的唯一口径。
/// </summary>
/// <remarks>
/// 约定：归一化后的路径 "一律使用正斜杠"、不含 "." / ".." 段、无重复分隔符、无尾部分隔符。
/// 理由不只是风格 —— "#line" 的 filename 携带反斜杠会引入转义歧义（见模块 3），
/// 全链路统一正斜杠可以从源头消灭这类问题。
/// </remarks>
public static class VfsPath
{
    /// <summary>归一化一个绝对或相对路径；输入为空时返回空串。</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var length = path.Length;
        var buffer = length <= 512 ? stackalloc char[length] : new char[length];
        var written = 0;
        var isUnc = false;
        var prefixLength = 0;

        // 盘符或 UNC 前缀先原样保留。
        if (length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]))
        {
            isUnc = true;
            buffer[written++] = '/';
            buffer[written++] = '/';
            prefixLength = 2;
        }
        else if (length >= 2 && path[1] == ':')
        {
            buffer[written++] = path[0];
            buffer[written++] = ':';
            prefixLength = 2;
            if (length >= 3 && IsSeparator(path[2]))
            {
                buffer[written++] = '/';
                prefixLength = 3;
            }
        }

        var absolute = isUnc || prefixLength > 0;
        var prefixWritten = written;

        // 栈里存的是每个可弹出段的"末尾偏移"；.. 上溯时回到前一段的末尾（或根前缀末尾）。
        Span<int> segmentEnds = stackalloc int[64];
        var segmentCount = 0;

        var i = prefixLength;
        while (i < length)
        {
            while (i < length && IsSeparator(path[i]))
            {
                i++;
            }

            var start = i;
            while (i < length && !IsSeparator(path[i]))
            {
                i++;
            }

            var segmentLength = i - start;
            if (segmentLength == 0)
            {
                continue;
            }

            var segment = path.AsSpan(start, segmentLength);
            if (segmentLength == 1 && segment[0] == '.')
            {
                continue;
            }

            if (segmentLength == 2 && segment[0] == '.' && segment[1] == '.')
            {
                if (segmentCount > 0)
                {
                    segmentCount--;
                    written = segmentCount > 0 ? segmentEnds[segmentCount - 1] : prefixWritten;
                }
                else if (!absolute)
                {
                    // 相对路径已经上溯到顶，多余的 .. 原样保留，且不再进入可弹出栈
                    // （否则 "../../a" 会被第二个 .. 错误地弹掉第一个）。
                    if (written > 0 && buffer[written - 1] != '/')
                    {
                        buffer[written++] = '/';
                    }

                    buffer[written++] = '.';
                    buffer[written++] = '.';
                }

                continue;
            }

            if (written > 0 && buffer[written - 1] != '/')
            {
                buffer[written++] = '/';
            }

            segment.CopyTo(buffer[written..]);
            written += segmentLength;
            if (segmentCount < segmentEnds.Length)
            {
                segmentEnds[segmentCount++] = written;
            }
        }

        if (written == 0)
        {
            return absolute ? "/" : string.Empty;
        }

        var result = new string(buffer[..written]);

        // "C:" 单独出现时补一个根斜杠语义，避免与相对路径混淆。
        if (!isUnc && result.Length == 2 && result[1] == ':')
        {
            result += "/";
        }

        return result;
    }

    /// <summary>拼接两段路径并归一化。</summary>
    public static string Combine(string left, string right)
    {
        if (string.IsNullOrEmpty(left))
        {
            return Normalize(right);
        }

        if (string.IsNullOrEmpty(right))
        {
            return Normalize(left);
        }

        return Normalize(left + "/" + right);
    }

    /// <summary>取目录部分（归一化后）。</summary>
    public static string GetDirectoryName(string normalizedPath)
    {
        if (string.IsNullOrEmpty(normalizedPath))
        {
            return string.Empty;
        }

        var index = normalizedPath.LastIndexOf('/');
        if (index < 0)
        {
            return string.Empty;
        }

        // 根目录（"C:/" 或 "//"）取到分隔符本身。
        if (index == normalizedPath.Length - 1)
        {
            return normalizedPath;
        }

        return index == 2 && normalizedPath[1] == ':' ? normalizedPath[..3] : normalizedPath[..index];
    }

    /// <summary>取文件名部分（含扩展名）。</summary>
    public static ReadOnlySpan<char> GetFileName(ReadOnlySpan<char> normalizedPath)
    {
        var index = normalizedPath.LastIndexOf('/');
        return index < 0 ? normalizedPath : normalizedPath[(index + 1)..];
    }

    /// <summary>判断 <paramref name="path"/> 是否位于 <paramref name="directory"/> 之下（均须已归一化）。</summary>
    public static bool IsUnder(string path, string directory)
    {
        if (directory.Length == 0 || path.Length <= directory.Length)
        {
            return false;
        }

        if (!path.AsSpan(0, directory.Length).Equals(directory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path[directory.Length] == '/';
    }


    /// <summary>取目录部分（span 版本，命中时零分配）。</summary>
    public static ReadOnlySpan<char> GetDirectoryName(ReadOnlySpan<char> normalizedPath)
    {
        if (normalizedPath.IsEmpty)
        {
            return ReadOnlySpan<char>.Empty;
        }

        var index = normalizedPath.LastIndexOf('/');
        if (index < 0)
        {
            return ReadOnlySpan<char>.Empty;
        }

        if (index == normalizedPath.Length - 1)
        {
            return normalizedPath;
        }

        return index == 2 && normalizedPath[1] == ':' ? normalizedPath[..3] : normalizedPath[..index];
    }

    /// <summary>
    /// 拼接两段路径（span 版本）。两侧都已归一化时只产生"一次"字符串分配（结果本身）。
    /// </summary>
    public static string Combine(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        if (left.IsEmpty)
        {
            return Normalize(right.ToString());
        }

        if (right.IsEmpty)
        {
            return Normalize(left.ToString());
        }

        if (IsNormalizedFast(left) && IsNormalizedFast(right))
        {
            // string.Concat(span, span, span) 只分配结果字符串本身；
            // 早期用 new char[] + new string(buffer) 的写法会多分配一个临时缓冲区（实测多 264 B/次）。
            return string.Concat(left, "/", right);
        }

        return Normalize(left.ToString() + "/" + right.ToString());
    }

    /// <summary>
    /// 快速判定「这段路径本身已经是归一化形态」：无反斜杠、无空段、无 "." / ".." 段。
    /// 用途是让热路径跳过整段重建，只做一次拼接。
    /// </summary>
    internal static bool IsNormalizedFast(ReadOnlySpan<char> path)
    {
        if (path.IndexOf('\\') >= 0)
        {
            return false;
        }

        if (path.IsEmpty)
        {
            return true;
        }

        var index = 0;
        while (index < path.Length)
        {
            var slash = path[index..].IndexOf('/');
            var segment = slash < 0 ? path[index..] : path.Slice(index, slash);

            if (segment.IsEmpty)
            {
                return false;
            }

            if (segment[0] == '.' && (segment.Length == 1 || (segment.Length == 2 && segment[1] == '.')))
            {
                return false;
            }

            if (slash < 0)
            {
                break;
            }

            index += slash + 1;
            if (index >= path.Length)
            {
                // 尾部分隔符
                return false;
            }
        }

        return true;
    }

    /// <summary>转回 Windows 原生形式（真正要访问磁盘时使用）。</summary>
    public static string ToNative(string normalizedPath) => normalizedPath.Replace('/', '\\');

    private static bool IsSeparator(char c) => c is '/' or '\\';
}
