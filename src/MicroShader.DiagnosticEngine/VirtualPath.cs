using System.Text;
using MicroShader.ContextEngine;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// "#line" 使用的虚拟路径（ADR-023 / v2.0 清单第 11、17 条）。
/// </summary>
/// <remarks>
/// 用虚拟路径做 "#line" 文件名的理由是「稳定标识 + 与 VFS/跳转统一、抗 PackageCache 版本漂移」，
/// "不是"「与 Unity Console 口径一致」—— 实测 Unity 自己报的是物理路径
/// （"Library/PackageCache/&lt;包&gt;@&lt;版本或短 hash&gt;/…"），所以物理路径必须另外塞进
/// 诊断的 message / relatedInformation，不能让用户两条线索都没有。
/// 两条硬约束（第 17 条）：
/// <list type="number">
/// <item>"禁止反斜杠"："#line 42 "E:\Com\…"" 会被 DXC 报成 "E:ComShadersMy.shader"，
/// 分隔符被转义吃掉、路径静默改坏 —— 属数据损坏级缺陷。本类只产出正斜杠。</item>
/// <item>"禁止冒号"："vfs:7f3a" 会让 ":42:36" 的冒号既是 ID 分隔符又是 "file:line:col" 分隔符，
/// 产生解析歧义。因此兜底形态是 "vfs-&lt;8hex&gt;/&lt;文件名&gt;"。</item>
/// </list>
/// 形态优先级："Assets/&lt;相对路径&gt;" → "Packages/&lt;包名&gt;/&lt;相对路径&gt;" →
/// "CGIncludes/&lt;文件名&gt;" → "vfs-&lt;8hex&gt;/&lt;文件名&gt;"。前三种本身就是 Unity 生态里的逻辑路径，
/// 天然跨版本稳定且可读；只有既不在 Assets、也不在任何包根、也不在 CGIncludes 的文件才退化为哈希形态。
/// </remarks>
public static class VirtualPath
{
    /// <summary>为物理路径生成稳定的虚拟路径。</summary>
    /// <param name="physicalPath">物理绝对路径（分隔符任意）。</param>
    /// <param name="index">VFS 快照；可为 null（此时只能产出 "vfs-&lt;hash&gt;" 形态）。</param>
    public static string For(string physicalPath, VfsIndex? index)
    {
        ArgumentException.ThrowIfNullOrEmpty(physicalPath);

        var normalized = Normalize(physicalPath);

        if (index is { } vfs)
        {
            var projectRoot = vfs.Layout.ProjectRoot;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                var assetsRoot = VfsPath.Combine(projectRoot, "Assets");
                if (VfsPath.IsUnder(normalized, assetsRoot) && TryRelative(normalized, assetsRoot, out var underAssets))
                {
                    return "Assets/" + underAssets;
                }
            }

            // 原来在 Packages 上做 O(包数) 线性扫描，且每轮都 Normalize(root) 分配一个临时字符串。
            // 改为沿路径祖先链反查（VfsIndex.TryMatchPackageRoot）：O(路径深度)、命中零分配，
            // 并且天然取得「最长（最具体）包根」，不再依赖字典遍历顺序。
            if (vfs.TryMatchPackageRoot(normalized, out var packageName, out var rootLength))
            {
                return "Packages/" + packageName + "/" + normalized[(rootLength + 1)..];
            }

            if (vfs.Installation is { HasCGIncludes: true } installation)
            {
                var cgRoot = installation.CGIncludesRoot;
                if (!string.IsNullOrEmpty(cgRoot) &&
                    VfsPath.IsUnder(normalized, cgRoot) &&
                    TryRelative(normalized, cgRoot, out var underCg)
                    && underCg.IndexOf('/') < 0)
                {
                    return "CGIncludes/" + underCg;
                }
            }
        }

        return "vfs-" + ShortHash(normalized) + "/" + VfsPath.GetFileName(normalized).ToString();
    }

    /// <summary>把物理路径归一化为正斜杠、小写盘符的形式（仅用于比较/哈希，不用于 "#line" 展示）。</summary>
    public static string Normalize(string path)
    {
        var trimmed = path.Trim();
        var buffer = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            buffer.Append(c == '\\' ? '/' : c);
        }

        return buffer.ToString();
    }

    /// <summary>FNV-1a 32 位，小写不敏感（Windows 路径大小写不敏感）。</summary>
    private static string ShortHash(string normalized)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;

        var hash = offsetBasis;
        foreach (var c in normalized)
        {
            var lower = char.ToLowerInvariant(c);
            hash ^= (byte)lower;
            hash *= prime;
            hash ^= (byte)(lower >> 8);
            hash *= prime;
        }

        return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryRelative(string candidate, string root, out string relative)
    {
        var normalizedRoot = Normalize(root).TrimEnd('/');
        var normalizedCandidate = Normalize(candidate);
        if (normalizedCandidate.Length <= normalizedRoot.Length + 1)
        {
            relative = string.Empty;
            return false;
        }

        relative = normalizedCandidate[(normalizedRoot.Length + 1)..];
        return relative.Length != 0;
    }
}
