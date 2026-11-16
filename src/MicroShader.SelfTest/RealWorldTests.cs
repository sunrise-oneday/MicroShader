using System.Diagnostics;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 真实工程语料验收：直接拿本机 Unity 工程里的官方 URP 14.0.12 包当靶子。
/// 固定用例只能证明「按设计跑」，真实语料才能证明「设计覆盖了现实」。
/// </summary>
internal static class RealWorldTests
{
    private const string Suite = "ShaderLab.RealWorld";

    private static string? s_configuredRoot;
    private static ScanSummary? s_summary;

    public static void Configure(string? urpRoot) => s_configuredRoot = urpRoot;

    public static void Register()
    {
        TestSuite.Add(Suite, "真实URP_全量切片无熔断", FullSliceWithoutFuse);
        TestSuite.Add(Suite, "真实URP_文件级HLSLINCLUDE覆盖所有同类块", FileLevelIncludeCoverage);
        TestSuite.Add(Suite, "真实URP_切片阶段零诊断", NoDiagnosticsOnHealthyCorpus);
        TestSuite.Add(Suite, "真实URP_耗时与分配基线", PerformanceBaseline);
    }

    private static ScanSummary Summary()
    {
        if (s_summary is not null)
        {
            return s_summary;
        }

        var root = DiscoverRoot() ?? throw new SkipException("未找到本机 URP 包（可用 --urp <dir> 或 MICROSHADER_URP_ROOT 指定）");
        var files = Directory.GetFiles(root, "*.shader", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var summary = new ScanSummary { Root = root, FileCount = files.Length };

        // 先把磁盘 I/O 全部做掉：分配基线只应度量「切片器自身」，不应把文件读取算进去，
        // 否则数字会被 ReadAllText 的字符串分配彻底淹没，失去回归价值。
        var texts = new string[files.Length];
        var sourceBytes = 0L;
        for (var i = 0; i < files.Length; i++)
        {
            texts[i] = File.ReadAllText(files[i]);
            sourceBytes += texts[i].Length;
        }

        summary.SourceCharacters = sourceBytes;

        var machine = new ShaderLabStateMachine();
        var result = new ShaderLabParseResult();

        // 预热一次，让 ArrayPool / 结果对象池进入稳态后再计数。
        machine.Parse(files[0], texts[0], result);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();

        for (var fileIndex = 0; fileIndex < files.Length; fileIndex++)
        {
            var file = files[fileIndex];
            var text = texts[fileIndex];
            machine.Parse(file, text, result);

            summary.BlockCount += result.Passes.Count;
            summary.SharedBlockCount += result.SharedBlocks.Count;
            summary.SubShaderCount += result.SubShaderCount;

            foreach (var diagnostic in result.Diagnostics)
            {
                var code = diagnostic.Code ?? "<null>";
                summary.Diagnostics[code] = summary.Diagnostics.GetValueOrDefault(code) + 1;
                if (code == ShaderLabDiagnosticCodes.UnterminatedBlock)
                {
                    summary.UnterminatedBlocks++;
                }
                else if (code == ShaderLabDiagnosticCodes.ForceClosedBlock)
                {
                    summary.ForceClosedBlocks++;
                }
                else if (code == ShaderLabDiagnosticCodes.MismatchedEndMarker)
                {
                    summary.MismatchedEndMarkers++;
                }
            }

            var hlslShared = result.SharedBlocks.Count(b => b.Kind == ShaderBlockKind.Hlsl);
            var hlslBlocks = result.Passes.Count(p => p.Kind == ShaderBlockKind.Hlsl);
            if (hlslShared > 0)
            {
                summary.FilesWithHlslInclude++;
                if (hlslBlocks == 0)
                {
                    summary.HlslIncludeWithoutProgramBlock++;
                }
            }

            if (hlslShared > 1)
            {
                summary.FilesWithMultipleHlslInclude++;
            }

            summary.Snapshots.Add(new FileSnapshot(file, hlslShared, hlslBlocks, result));

            if (hlslShared > 0 && hlslBlocks > 0 && result.SubShaderCount > 1)
            {
                summary.FilesWithCrossSubShaderInclude++;
            }
        }

        stopwatch.Stop();
        summary.AllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        summary.ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds;

        s_summary = summary;
        return summary;
    }

    private sealed record FileSnapshot(string Path, int HlslSharedBlocks, int HlslProgramBlocks, ShaderLabParseResult Result);

    private sealed class ScanSummary
    {
        public string Root { get; set; } = string.Empty;
        public int FileCount { get; set; }
        public int BlockCount { get; set; }
        public int SharedBlockCount { get; set; }
        public int SubShaderCount { get; set; }
        public int UnterminatedBlocks { get; set; }
        public int ForceClosedBlocks { get; set; }
        public int MismatchedEndMarkers { get; set; }
        public int FilesWithHlslInclude { get; set; }
        public int FilesWithMultipleHlslInclude { get; set; }
        public int HlslIncludeWithoutProgramBlock { get; set; }
        public int FilesWithCrossSubShaderInclude { get; set; }
        public long AllocatedBytes { get; set; }
        public long SourceCharacters { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public Dictionary<string, int> Diagnostics { get; } = new(StringComparer.Ordinal);
        public List<FileSnapshot> Snapshots { get; } = new();
    }

    private static string? DiscoverRoot()
    {
        if (!string.IsNullOrWhiteSpace(s_configuredRoot))
        {
            return Directory.Exists(s_configuredRoot) ? s_configuredRoot : null;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("MICROSHADER_URP_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && Directory.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        var candidates = new List<string>();

        foreach (var driveProgramFiles in new[] { @"D:\Program Files\U3D", @"C:\Program Files\U3D" })
        {
            if (!Directory.Exists(driveProgramFiles))
            {
                continue;
            }

            try
            {
                foreach (var install in Directory.GetDirectories(driveProgramFiles))
                {
                    var packageCache = Path.Combine(install, "Library", "PackageCache");
                    if (Directory.Exists(packageCache))
                    {
                        candidates.AddRange(Directory.GetDirectories(packageCache, "com.unity.render-pipelines.universal@*"));
                    }
                }
            }
            catch (IOException)
            {
                // 忽略不可读目录，继续探测其它候选。
            }
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var packagesRoot in new[]
                 {
                     Path.Combine(localAppData, "Unity", "cache", "packages", "packages.unity.com"),
                     Path.Combine(localAppData, "Unity", "cache", "packages"),
                 })
        {
            if (!Directory.Exists(packagesRoot))
            {
                continue;
            }

            try
            {
                candidates.AddRange(Directory.GetDirectories(packagesRoot, "com.unity.render-pipelines.universal@*"));
            }
            catch (IOException)
            {
                // 同上。
            }
        }

        return candidates
            .Where(Directory.Exists)
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static void FullSliceWithoutFuse()
    {
        var summary = Summary();

        Check.True(summary.FileCount > 0, "语料库不得为空");
        Check.Equal(0, summary.UnterminatedBlocks, "真实 URP 官方包中不得出现未闭合代码块（否则说明切片器误判了现实写法）");
        Check.Equal(0, summary.ForceClosedBlocks, "真实 URP 官方包中不得出现被强制闭合的块");
        Check.Equal(0, summary.MismatchedEndMarkers, "真实 URP 官方包中不得出现方言不匹配的结束标记");

        Console.WriteLine();
        Console.WriteLine($"        语料根目录      : {summary.Root}");
        Console.WriteLine($"        .shader 文件数  : {summary.FileCount}");
        Console.WriteLine($"        代码块总数      : {summary.BlockCount}");
        Console.WriteLine($"        共享块总数      : {summary.SharedBlockCount}");
        Console.WriteLine($"        SubShader 总数  : {summary.SubShaderCount}");
        Console.WriteLine($"        含 HLSLINCLUDE 的文件: {summary.FilesWithHlslInclude}（其中跨多 SubShader: {summary.FilesWithCrossSubShaderInclude}）");
    }

    private static void FileLevelIncludeCoverage()
    {
        var summary = Summary();
        var checkedFiles = 0;

        foreach (var snapshot in summary.Snapshots)
        {
            if (snapshot.HlslSharedBlocks == 0 || snapshot.HlslProgramBlocks == 0)
            {
                continue;
            }

            var result = snapshot.Result;
            var hlslSharedIndices = new HashSet<int>(
                result.SharedBlocks
                    .Select((block, index) => (block, index))
                    .Where(pair => pair.block.Kind == ShaderBlockKind.Hlsl)
                    .Select(pair => pair.index));

            foreach (var pass in result.Passes.Where(p => p.Kind == ShaderBlockKind.Hlsl))
            {
                var attached = new HashSet<int>(pass.ApplicableSharedBlockIndices);
                Check.True(
                    hlslSharedIndices.SetEquals(attached),
                    $"{Path.GetFileName(snapshot.Path)} 的 HLSLPROGRAM 块（第 {pass.StartLineNumber} 行）必须挂上本文件全部 {hlslSharedIndices.Count} 个 HLSLINCLUDE，实际挂了 {attached.Count} 个");
            }

            checkedFiles++;
        }

        Check.True(checkedFiles > 0, "语料库里应当至少存在一个使用 HLSLINCLUDE 的官方 shader（URP Particles/Sprite 系列）");
        Console.WriteLine($"        已校验的 HLSLINCLUDE 文件数: {checkedFiles}");
    }

    private static void NoDiagnosticsOnHealthyCorpus()
    {
        var summary = Summary();

        if (summary.Diagnostics.Count == 0)
        {
            return;
        }

        var detail = string.Join(", ", summary.Diagnostics.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));
        throw new AssertionException(
            "官方 URP 包是合法着色器语料，切片阶段不得产出任何诊断。" + Environment.NewLine +
            "  实际诊断: " + detail + Environment.NewLine +
            "  （若某条确属官方写法，应把该规则降级或加白名单，而不是放宽断言）");
    }

    private static void PerformanceBaseline()
    {
        var summary = Summary();
        var perFile = summary.FileCount == 0 ? 0 : summary.ElapsedMilliseconds / summary.FileCount;
        var bytesPerFile = summary.FileCount == 0 ? 0 : summary.AllocatedBytes / summary.FileCount;
        var bytesPerKilochar = summary.SourceCharacters == 0 ? 0 : summary.AllocatedBytes * 1024.0 / summary.SourceCharacters;

        Console.WriteLine($"        源文本规模      : {summary.SourceCharacters / 1024.0:F1} K 字符");
        Console.WriteLine($"        切片总耗时      : {summary.ElapsedMilliseconds:F1} ms（{perFile:F3} ms/文件）");
        Console.WriteLine($"        切片器分配(不含IO): {summary.AllocatedBytes / 1024.0:F1} KB（{bytesPerFile / 1024.0:F1} KB/文件，{bytesPerKilochar:F1} B/K 字符）");

        Check.True(summary.ElapsedMilliseconds > 0, "耗时基线应大于 0");
        Check.True(perFile < 5.0, $"单文件切片耗时基线异常（{perFile:F3} ms/文件）——模块 1 是打字热路径的上游，不允许退化");
    }
}
