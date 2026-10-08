using System.Diagnostics;
using MicroShader.ContextEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 2 的真机语料用例：直接对磁盘上真实的 Unity 工程（D:\Program Files\U3D\NewWorld）做验收。
/// </summary>
/// <remarks>
/// 这些用例的价值在于「固定用例永远造不出的真实拓扑」：
/// <list type="bullet">
///   <item>BuiltInPackages 与 PackageCache 同时存在 "com.unity.render-pipelines.core"，必须验证谁赢。</item>
///   <item>URP 包内 700+ 条 "#include" 的四规则分布。</item>
///   <item>lock 里 "source:embedded" 但 "version:file:" 的本地包。</item>
/// </list>
/// 工程不存在时整体跳过（不算失败），保证在没有 Unity 的机器上也不会误报。
/// </remarks>
internal static class VfsRealWorldTests
{
    private static string? _projectRoot;

    public static void Configure(string? explicitRoot)
    {
        _projectRoot = explicitRoot;
    }

    public static void Register()
    {
        TestSuite.Add("VfsReal.Project", "从包内 hlsl 反推工程根并读出 Unity 版本", ProjectLayout);
        TestSuite.Add("VfsReal.Install", "三层定位命中编辑器安装目录", InstallLocated);
        TestSuite.Add("VfsReal.Packages", "真实包拓扑与来源优先级", PackageTopology);
        TestSuite.Add("VfsReal.Include", "URP 包内全量 include 的四规则命中", IncludeRulesAcrossUrp);
        TestSuite.Add("VfsReal.Cost", "冷启动建索引开销与查询分配字节", Cost);
    }

    private static void ProjectLayout()
    {
        var root = RequireProject();
        TestCaseHelpers.Report("工程根: " + root);

        var shaderPath = Path.Combine(root, "Library/PackageCache/com.unity.render-pipelines.universal@14.0.12/ShaderLibrary/Shadows.hlsl");
        if (!File.Exists(shaderPath))
        {
            throw new SkipException("URP 14.0.12 未安装到该工程的 PackageCache");
        }

        Check.True(
            UnityProjectLayout.TryLocate(shaderPath, out var layout, out var error),
            "应当能从 PackageCache 里的 hlsl 反推工程根：" + error);

        Check.Equal(
            VfsPath.Normalize(root),
            layout!.ProjectRoot,
            "反推出的工程根应与预期一致");

        Check.True(layout.UnityVersion is not null, "应读出 m_EditorVersion");
        TestCaseHelpers.Report("Unity 版本: " + layout.UnityVersion);
    }

    private static void InstallLocated()
    {
        var index = BuildRealIndex(out var elapsed);
        TestCaseHelpers.Report($"索引构建: {index.PackageCount} 个包, {elapsed.TotalMilliseconds:F1} ms");

        if (index.Installation is null)
        {
            throw new SkipException("本机未定位到 Unity 编辑器安装目录（Hub / EditorInstance / 注册表均未命中）");
        }

        TestCaseHelpers.Report($"编辑器安装: {index.Installation.InstallRoot} （来源 {index.Installation.LocatedBy}）");

        Check.True(index.Installation.HasCGIncludes, "应能找到 <Editor>/Data/CGIncludes");
        Check.True(index.Installation.HasBuiltInPackages, "应能找到 BuiltInPackages");

        var cgCount = index.CGIncludeFileNames.Count;
        var builtInCount = Directory.EnumerateDirectories(VfsPath.ToNative(index.Installation.BuiltInPackagesRoot)).Count();
        TestCaseHelpers.Report($"CGIncludes: {cgCount} 个文件 / BuiltInPackages: {builtInCount} 个包");

        Check.True(cgCount >= 50, $"CGIncludes 文件数应 >= 50，实际 {cgCount}");
        Check.True(builtInCount >= 55, $"BuiltInPackages 包数应 >= 55，实际 {builtInCount}");
        Check.True(index.IsKnownCGInclude("AutoLight.cginc"), "AutoLight.cginc 应出现在 CGIncludes 集合中");
        Check.True(!index.IsKnownCGInclude("TMPro_UGUI.cginc"), "TMPro_UGUI.cginc 不在 CGIncludes 目录，必须判假");

        Check.True(
            index.Installation.LocatedBy is UnityInstallLocatorKind.UnityHub or UnityInstallLocatorKind.EditorInstanceJson or UnityInstallLocatorKind.Registry,
            "定位来源必须明确");
    }

    private static void PackageTopology()
    {
        var index = BuildRealIndex(out _);

        // BuiltInPackages 与 PackageCache 同时含 com.unity.render-pipelines.core，
        // 工程实际使用的是 PackageCache 里那份，否则会读到编辑器自带的旧版本。
        Check.True(index.TryGetPackage("com.unity.render-pipelines.core", out var core), "core 包应存在");
        Check.Equal(PackageSourceKind.PackageCache, core!.Source, "PackageCache 必须赢过 BuiltInPackages 同名包");

        Check.True(index.TryGetPackage("com.unity.render-pipelines.universal", out var urp), "URP 包应存在");
        Check.Equal("14.0.12", urp!.LockVersion, "URP 版本应来自 lock 文件");
        Check.True(
            urp.PhysicalRoot.Contains("PackageCache", StringComparison.OrdinalIgnoreCase),
            "URP 应从 PackageCache 解析");

        // 本机实测：Packages/com.farlocus.locus 的 lock 记录是 source=embedded + version=file:com.farlocus.locus
        Check.True(index.TryGetPackage("com.farlocus.locus", out var local), "本地包应存在");
        Check.Equal(PackageSourceKind.Embedded, local!.Source, "Packages/ 实体目录下的包判为 Embedded");

        Check.True(index.PackageCount >= 60, $"包总数应 >= 60，实际 {index.PackageCount}");

        var byKind = index.Packages.Values
            .GroupBy(p => p.Source)
            .OrderBy(g => g.Key)
            .Select(g => $"{g.Key}={g.Count()}");
        TestCaseHelpers.Report("包来源分布: " + string.Join(", ", byKind));
    }

    private static void IncludeRulesAcrossUrp()
    {
        var index = BuildRealIndex(out _);
        Check.True(index.TryGetPackage("com.unity.render-pipelines.universal", out var urp), "URP 包应存在");

        var files = Directory
            .EnumerateFiles(VfsPath.ToNative(urp!.PhysicalRoot), "*.hlsl", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(VfsPath.ToNative(urp.PhysicalRoot), "*.shader", SearchOption.AllDirectories))
            .ToArray();

        var total = 0;
        var byRule = new Dictionary<IncludeRuleKind, int>();
        var unknownPackages = new List<string>();
        var misses = new List<string>();

        foreach (var file in files)
        {
            var normalizedFile = VfsPath.Normalize(file);
            foreach (var (includePath, _) in TestCaseHelpers.EnumerateIncludes(normalizedFile))
            {
                total++;

                var hot = IncludeResolver.Resolve(index, includePath, normalizedFile, verifyOnDisk: false);
                if (!hot.Found)
                {
                    if (hot.Miss == IncludeMissKind.UnknownPackage)
                    {
                        unknownPackages.Add(hot.MissingPackageName ?? includePath);
                    }

                    continue;
                }

                byRule[hot.Rule] = byRule.GetValueOrDefault(hot.Rule) + 1;

                var verified = IncludeResolver.Resolve(index, includePath, normalizedFile, verifyOnDisk: true);
                if (!verified.Found)
                {
                    misses.Add($"{VfsPath.GetFileName(normalizedFile)} -> {includePath}");
                }
            }
        }

        var distribution = string.Join(", ", byRule.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"));

        TestCaseHelpers.Report($"扫过 {files.Length} 个文件 / {total} 条 #include");
        TestCaseHelpers.Report("规则分布: " + distribution);
        TestCaseHelpers.Report($"盘上缺失: {misses.Count} 条；未知包: {unknownPackages.Distinct().Count()} 个");

        Check.True(total >= 500, $"URP 包内 include 数量应 >= 500，实际 {total}");
        Check.True(
            byRule.GetValueOrDefault(IncludeRuleKind.PackageVirtualPrefix) >= total * 0.8,
            "规则 1（Packages/ 前缀）应覆盖 80% 以上");
        Check.Equal(0, unknownPackages.Distinct().Count(), "URP 包内不应出现未知包："
            + string.Join(", ", unknownPackages.Distinct().Take(5)));
        Check.True(
            misses.Count <= total / 20,
            $"盘上缺失比例应低于 5%，实际 {misses.Count}/{total}："
                + string.Join(" | ", misses.Take(5)));
    }

    private static void Cost()
    {
        var index = BuildRealIndex(out var coldMs);
        TestCaseHelpers.Report($"冷启动建索引: {coldMs.TotalMilliseconds:F1} ms");

        Check.True(coldMs.TotalMilliseconds < 3000, $"冷启动建索引应 < 3000ms，实际 {coldMs.TotalMilliseconds:F1} ms");

        // 1) 纯索引查询必须零分配（alternate lookup 走 ReadOnlySpan<char>）。
        const int LookupIterations = 20_000;
        var name = "com.unity.render-pipelines.universal".AsSpan();
        var cgName = "AutoLight.cginc".AsSpan();

        // 预热
        for (var i = 0; i < 1000; i++)
        {
            index.TryGetPackage(name, out _);
            index.IsKnownCGInclude(cgName);
        }

        // 两个窗口：第一窗把一次性开销（分层编译提升、内联失败回退等）吸收掉，
        // 第二窗才是稳态。若存在「每次调用都分配」的缺陷，第二窗会以 20000 倍放大暴露出来。
        var warmBytes = Measure(index, name, cgName, LookupIterations, out _);
        var lookupBytes = Measure(index, name, cgName, LookupIterations, out var perLookupNs);

        TestCaseHelpers.Report(
            $"索引查询: {LookupIterations} 轮，首窗分配 {warmBytes} B，稳态窗分配 {lookupBytes} B，{perLookupNs:F0} ns/轮（含两次查询）");
        Check.Equal(0L, lookupBytes, "索引查询在稳态下必须零分配");

        // 2) 路径物化：每次恰好一个结果字符串（拼接不可避免），不得出现额外的中间分配。
        var including = VfsPath.Combine(index.Packages["com.unity.render-pipelines.universal"].PhysicalRoot, "ShaderLibrary/Shadows.hlsl");
        var include = "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl".AsSpan();

        for (var i = 0; i < 1000; i++)
        {
            IncludeResolver.Resolve(index, include, including, verifyOnDisk: false);
        }

        const int ResolveIterations = 20_000;
        var beforeResolve = GC.GetAllocatedBytesForCurrentThread();
        var resolveStartedAt = Stopwatch.GetTimestamp();
        for (var i = 0; i < ResolveIterations; i++)
        {
            IncludeResolver.Resolve(index, include, including, verifyOnDisk: false);
        }

        var resolveTicks = Stopwatch.GetTimestamp() - resolveStartedAt;
        var resolveBytes = GC.GetAllocatedBytesForCurrentThread() - beforeResolve;
        var perResolveBytes = (double)resolveBytes / ResolveIterations;

        TestCaseHelpers.Report(
            $"include 解析: {ResolveIterations} 轮，{perResolveBytes:F1} B/次，"
            + $"{resolveTicks * (1_000_000_000.0 / Stopwatch.Frequency) / ResolveIterations:F0} ns/次");

        Check.True(
            perResolveBytes <= 512,
            $"每次解析的分配应不超过一个结果字符串（<=512B），实际 {perResolveBytes:F1} B");
    }

    private static long Measure(VfsIndex index, ReadOnlySpan<char> name, ReadOnlySpan<char> cgName, int iterations, out double nanosecondsPerIteration)
    {
        // 用静态 Stopwatch.GetTimestamp()：Stopwatch.StartNew() 会在被测窗口内 new 出一个
        // Stopwatch 实例（本机实测恰好 40 B），把探针自己的开销算到被测代码头上。
        var before = GC.GetAllocatedBytesForCurrentThread();
        var startedAt = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            index.TryGetPackage(name, out _);
            index.IsKnownCGInclude(cgName);
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - startedAt;
        nanosecondsPerIteration = elapsedTicks * (1_000_000_000.0 / Stopwatch.Frequency) / iterations;
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static VfsIndex BuildRealIndex(out TimeSpan elapsed)
    {
        var root = RequireProject();
        var watch = Stopwatch.StartNew();
        var index = VfsIndexBuilder.Build(root);
        watch.Stop();
        elapsed = watch.Elapsed;
        return index;
    }

    private static string RequireProject()
    {
        var candidate = _projectRoot
            ?? Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT")
            ?? Path.Combine("D:", "Program Files", "U3D", "NewWorld");

        if (!File.Exists(Path.Combine(candidate, "ProjectSettings/ProjectVersion.txt")))
        {
            throw new SkipException($"未找到可用的 Unity 工程：{candidate}（可用 --urp 或 MICROSHADER_UNITY_PROJECT 指定）");
        }

        return candidate;
    }
}

/// <summary>真机用例里的公共小工具。</summary>
internal static class TestCaseHelpers
{
    private static readonly List<string> Buffer = [];

    /// <summary>输出一行报告（仅 --verbose 之外也始终打印，便于采集基线）。</summary>
    public static void Report(string message)
    {
        Buffer.Add(message);
        Console.WriteLine();
        Console.WriteLine("        · " + message);
    }

    /// <summary>逐行提取 "#include"（返回路径原文与 1-based 行号）。</summary>
    public static IEnumerable<(string Path, int Line)> EnumerateIncludes(string filePath)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath);
        }
        catch (IOException)
        {
            yield break;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#include", StringComparison.Ordinal))
            {
                continue;
            }

            var rest = line[8..].Trim();
            if (rest.Length < 3)
            {
                continue;
            }

            var open = rest[0];
            var close = open == '"' ? '"' : open == '<' ? '>' : '\0';
            if (close == '\0')
            {
                continue;
            }

            var end = rest.IndexOf(close, 1);
            if (end <= 1)
            {
                continue;
            }

            yield return (rest[1..end], i + 1);
        }
    }
}
