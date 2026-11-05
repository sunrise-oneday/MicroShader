using MicroShader.DiagnosticEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 平台宏跟随「构建目标 / 图形 API」的回归用例（模块 3 第三层的版本×平台表）。
/// </summary>
/// <remarks>
/// 缺陷背景：表名与宏都写死成 Windows/D3D11，工程切到 Android/Vulkan 之后诊断仍按 D3D11 的
/// 宏集合编译，于是 #if SHADER_API_D3D11 的分支被当成生效，红线误报或漏报。
/// 现在平台事实由调用方从注册表的动态上下文取，取不到才回退到推定值。
/// </remarks>
internal static class PlatformMacroTests
{
    private const string Suite = "Platform.Api";

    private const string UnityVersion = "2022.3.62f3c1";

    public static void Register()
    {
        TestSuite.Add(Suite, "Vulkan_注入Vulkan宏且不再出现D3D11", VulkanReplacesD3D11);
        TestSuite.Add(Suite, "D3D11_与旧行为逐项一致", D3D11MatchesLegacy);
        TestSuite.Add(Suite, "未知图形API_回退推定并留痕", UnknownApiFallsBackWithTrace);
    }

    /// <summary>工程切到 Android/Vulkan：只能出现 Vulkan 的宏，D3D11 必须彻底消失。</summary>
    private static void VulkanReplacesD3D11()
    {
        using var project = new TempProject("apivulkan");
        project.Write("ProjectSettings/ProjectSettings.asset", "  m_ActiveColorSpace: 1\n");

        var set = PlatformMacroResolver.Resolve(
            project.Layout,
            null,
            UnityVersion,
            PlatformMacroResolver.DefaultPlatform,
            new ShaderPlatformFacts("Android", "Vulkan", PlatformFactsSource.Bridge));

        // 本机 Editor.log 若提供了 Platform defines 行，解算会直接采用它、根本不进版本×平台表 ——
        // 那种机器上这条用例测的对象不存在，软跳过而不是判失败。
        if (set.SourceKind == PlatformMacroSourceKind.EditorLog)
        {
            throw new SkipException("本机 Editor.log 提供了 Platform defines，本用例测的是版本×平台表，跳过");
        }

        var names = set.Macros.Select(m => m.Name).ToArray();
        Check.Contains(names, "SHADER_API_VULKAN", "图形 API=Vulkan 时必须注入 SHADER_API_VULKAN");
        Check.DoesNotContain(
            names,
            "SHADER_API_D3D11",
            "图形 API=Vulkan 时绝不能注入 SHADER_API_D3D11（否则 #if 分支走错，红线误报或漏报）");
        Check.True(set.HasPostulated, "查表结果是推定项，必须标出来");
        Check.True(
            set.Macros.Any(m => m.Name == "SHADER_API_VULKAN" && m.Source.Contains("Vulkan", StringComparison.Ordinal)),
            "推定项的来源必须写明是按哪个图形 API 查出来的");

        TestCaseHelpers.Report("Vulkan: " + set.Describe());
    }

    /// <summary>工程在 Windows/D3D11：结果必须与不看平台事实的旧行为逐项相同。</summary>
    private static void D3D11MatchesLegacy()
    {
        using var project = new TempProject("apid3d11");
        project.Write("ProjectSettings/ProjectSettings.asset", "  m_ActiveColorSpace: 1\n");

        var legacy = PlatformMacroResolver.Resolve(project.Layout, null, UnityVersion);
        var fromBridge = PlatformMacroResolver.Resolve(
            project.Layout,
            null,
            UnityVersion,
            PlatformMacroResolver.DefaultPlatform,
            new ShaderPlatformFacts("StandaloneWindows64", "Direct3D11", PlatformFactsSource.Bridge));

        Check.Contains(legacy.Macros.Select(m => m.Name), "SHADER_API_D3D11", "旧行为本身要给 D3D11 平台宏");

        static IEnumerable<string> Rows(PlatformMacroSet set)
            => set.Macros.Select(m => m.Name + "=" + m.Value + ":" + m.Provenance + ":" + m.Source);

        Check.SequenceEqual(
            Rows(legacy),
            Rows(fromBridge),
            "图形 API=Direct3D11 时必须与旧行为逐项一致（名字、值、证据等级、来源）");

        TestCaseHelpers.Report("D3D11 与旧行为一致: " + fromBridge.Describe());
    }

    /// <summary>认不出的图形 API：回退到 Windows/D3D11 推定值，并留下可追溯的痕迹。</summary>
    private static void UnknownApiFallsBackWithTrace()
    {
        using var project = new TempProject("apiunknown");
        project.Write("ProjectSettings/ProjectSettings.asset", "  m_ActiveColorSpace: 1\n");

        var set = PlatformMacroResolver.Resolve(
            project.Layout,
            null,
            UnityVersion,
            PlatformMacroResolver.DefaultPlatform,
            new ShaderPlatformFacts("WebGL", "WebGPU", PlatformFactsSource.Bridge));

        var names = set.Macros.Select(m => m.Name).ToArray();
        Check.Contains(names, "SHADER_API_D3D11", "认不出的图形 API 必须回退到 Windows/D3D11 推定值");
        Check.Contains(names, "UNITY_COMPILER_DXC", "回退时与旧行为一致，DXC 宏也在");
        Check.True(
            set.Notes.Any(n => n.Contains("WebGPU", StringComparison.Ordinal)),
            "回退必须留痕：备注里要写明是哪个图形 API 认不出来");

        TestCaseHelpers.Report("未知图形 API: " + set.Describe());
    }
}
