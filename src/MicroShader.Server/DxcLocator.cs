using MicroShader.DiagnosticEngine;

namespace MicroShader.Server;

/// <summary>
/// 定位 "dxcompiler.dll"（+ 可选成对的 "dxil.dll"）。
/// </summary>
/// <remarks>
/// "顺序即交付语义"：交付包里两个 DLL 与 exe 同目录，所以「exe 所在目录」必须排在
/// 开发机 SDK 之前 —— 否则装了 Windows SDK 的开发者会静默用到另一份 DXC，两个环境的诊断结果不可比。
/// "绝不从系统目录动态搜版本"：详设 v2.0 第 2 条要求锁定二进制源。这里只枚举
/// Windows Kits 的"明确子路径"，且不跨版本混搭（只取同一目录下的 dxcompiler + dxil）。
/// </remarks>
internal static class DxcLocator
{
    private const string OverrideEnv = "MICROSHADER_DXC_DIR";

    /// <summary>解析顺序：CLI → 环境变量 → exe 同目录（交付形态）→ Windows SDK（开发形态）→ 仓库探针目录。</summary>
    public static bool TryResolve(string? fromCli, out string directory, out string detail)
    {
        var tried = new List<string>();

        if (!string.IsNullOrWhiteSpace(fromCli))
        {
            if (IsValid(fromCli)) { directory = fromCli; detail = "CLI --dxc"; return true; }
            tried.Add("--dxc " + fromCli);
        }

        var overridden = Environment.GetEnvironmentVariable(OverrideEnv);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            if (IsValid(overridden)) { directory = overridden; detail = OverrideEnv; return true; }
            tried.Add(OverrideEnv + "=" + overridden);
        }

        // 交付形态：exe 与 DLL 同目录。
        var beside = AppContext.BaseDirectory;
        if (IsValid(beside)) { directory = beside; detail = "exe 同目录"; return true; }
        tried.Add(beside);

        foreach (var sdk in EnumerateWindowsSdkDirs())
        {
            if (IsValid(sdk)) { directory = sdk; detail = "Windows SDK"; return true; }
            tried.Add(sdk);
        }

        // 开发形态：从 exe 向上找仓库根，再进探针目录。
        foreach (var probe in EnumerateRepoProbeDirs())
        {
            if (IsValid(probe)) { directory = probe; detail = "仓库探针目录"; return true; }
            tried.Add(probe);
        }

        directory = string.Empty;
        detail = "已尝试：" + string.Join(" | ", tried);
        return false;
    }

    private static bool IsValid(string directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && File.Exists(Path.Combine(directory, "dxcompiler.dll"));

    private static IEnumerable<string> EnumerateWindowsSdkDirs()
    {
        var kits = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrWhiteSpace(kits))
        {
            yield break;
        }

        var bin = Path.Combine(kits, "Windows Kits", "10", "bin");
        if (!Directory.Exists(bin))
        {
            yield break;
        }

        // 版本目录名形如 10.0.26100.0；降序取最新优先，但每个目录内的两个 DLL 必须成对（同一次 SDK 安装）。
        IEnumerable<string> versions;
        try
        {
            versions = Directory.EnumerateDirectories(bin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var version in versions.OrderByDescending(static d => d, StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(version, "x64");
        }
    }

    private static IEnumerable<string> EnumerateRepoProbeDirs()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MicroShader.sln")))
            {
                yield return Path.Combine(dir.FullName, "probes", "dxc-aot", "native");
                yield break;
            }

            dir = dir.Parent;
        }
    }
}
