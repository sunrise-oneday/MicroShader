using MicroShader.ContextEngine;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 第二层来源：从 "ProjectSettings/*.asset" 推导平台宏（v2.0 清单第 15 条）。
/// </summary>
/// <remarks>
/// "只推导字段语义已经在本机确证的东西。"目前只有色彩空间一项：
/// <list type="bullet">
/// <item>字段："ProjectSettings/ProjectSettings.asset" 的 "m_ActiveColorSpace"
/// （本机 NewWorld 实测在第 50 行，值 "1"）。</item>
/// <item>语义："0 = Gamma" / "1 = Linear"。Gamma 时 Unity 定义 "UNITY_COLORSPACE_GAMMA"；
/// Linear 时"不定义" —— 而 URP 里 "#if UNITY_COLORSPACE_GAMMA" 的写法共 5 处
/// （GlobalIllumination.hlsl:60、SSAO.hlsl:166、Bloom.shader:35 等），所以「不定义」本身是必须被如实表达的结论，
/// 不能默不做声。</item>
/// </list>
/// "刻意不做的推导"：光照贴图编码。文档把「光照贴图编码 → UNITY_LIGHTMAP_* 」列为第二层来源之一，
/// 但本机实测 "ProjectSettings.asset" 里只有 "m_BuildTargetGroupLightmapEncodingQuality"，
/// 且只列了 Android / iPhone / tvOS 三个目标（Standalone 走默认值），字段值到宏名的映射"未经验证"。
/// 凭文档说法补一个未验证的宏，正是「把推定当已证事实」，因此这里明确不做，并在备注里留成待办。
/// </remarks>
public static class ProjectSettingsMacroDeriver
{
    /// <summary>推导并把结果写入 <paramref name="set"/>；返回是否至少推导出一项。</summary>
    public static bool Derive(UnityProjectLayout layout, PlatformMacroSet set)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(set);

        var derived = DeriveColorSpace(layout, set);

        set.AddNote("光照贴图编码未推导：本机 ProjectSettings 只含移动端目标，字段→宏名映射未验证（待办）");
        return derived;
    }

    private static bool DeriveColorSpace(UnityProjectLayout layout, PlatformMacroSet set)
    {
        var path = VfsPath.Combine(layout.ProjectRoot, "ProjectSettings/ProjectSettings.asset");
        if (!TryFindIntSetting(path, "m_ActiveColorSpace", out var value, out var lineNumber) &&
            !TryFindIntSetting(path, "m_ColorSpace", out value, out lineNumber))
        {
            set.AddNote("ProjectSettings.asset 未找到 m_ActiveColorSpace/m_ColorSpace，色彩空间宏不注入");
            return false;
        }

        var source = "ProjectSettings.asset:" + lineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                     " m_ActiveColorSpace: " + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (value == 0)
        {
            set.Add("UNITY_COLORSPACE_GAMMA", "1", MacroProvenance.Derived, source + "（Gamma）");
            return true;
        }

        set.AddNote(source + "（Linear）⇒ 不定义 UNITY_COLORSPACE_GAMMA");
        return true;
    }

    private static bool TryFindIntSetting(string normalizedPath, string key, out int value, out int lineNumber)
    {
        value = 0;
        lineNumber = 0;

        var native = VfsPath.ToNative(normalizedPath);
        if (!File.Exists(native))
        {
            return false;
        }

        try
        {
            var current = 0;
            using var stream = new FileStream(native, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

            while (reader.ReadLine() is { } line)
            {
                current++;
                var trimmed = line.TrimStart();
                if (!trimmed.StartsWith(key, StringComparison.Ordinal))
                {
                    continue;
                }

                var colon = trimmed.IndexOf(':');
                if (colon < 0)
                {
                    continue;
                }

                var text = trimmed[(colon + 1)..].Trim();
                if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
                {
                    lineNumber = current;
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }
}
