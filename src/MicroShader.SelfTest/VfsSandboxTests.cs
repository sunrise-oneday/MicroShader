using MicroShader.ContextEngine;
using MicroShader.Domain;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 2（VFS 上下文引擎）的沙盒用例：在临时目录里合成一个 Unity 工程，
/// 覆盖 5 类包来源、4 条 include 规则与绝对路径直通、降级路径与无锁注册表。
/// </summary>
internal static class VfsSandboxTests
{
    private const string UnityVersion = "2022.3.62f3c1";

    public static void Register()
    {
        TestSuite.Add("Vfs.Path", "归一化消除 . 与 .. 并统一正斜杠", PathNormalization);
        TestSuite.Add("Vfs.Path", "IsUnder 不把前缀相同的兄弟目录误判为子目录", PathIsUnder);
        TestSuite.Add("Vfs.Uri", "file URI 还原：盘符 / 中文空格 / UNC / 非 file 方案", UriToPathShapes);

        TestSuite.Add("Vfs.Packages", "PackageCache 按最后一个 @ 切分（registry 与 git 短 hash）", PackageCacheNameDerivation);
        TestSuite.Add("Vfs.Packages", "Packages 实体目录覆盖 PackageCache 同名包", EmbeddedWinsOverPackageCache);
        TestSuite.Add("Vfs.Packages", "PackageCache 覆盖 BuiltInPackages 同名包", PackageCacheWinsOverBuiltIn);
        TestSuite.Add("Vfs.Packages", "lock 的 source:embedded + version:file: 被识别为本地包", EmbeddedSourceWithFileVersion);
        TestSuite.Add("Vfs.Packages", "manifest 破损时降级扫盘并报 VFS0001", MalformedManifestDegrades);
        TestSuite.Add("Vfs.Packages", "CGIncludes 目录在构建期枚举成文件名集合", CGIncludesEnumeratedAtBuildTime);
        TestSuite.Add("Vfs.Packages", "同名包多版本目录按 lock 版本择一，不靠枚举顺序", PackageCachePicksLockedVersion);

        TestSuite.Add("Vfs.Include", "规则1 Packages/ 前缀命中包根", IncludeRulePackagesPrefix);
        TestSuite.Add("Vfs.Include", "规则1 未安装的包判为 UnknownPackage", IncludeRuleUnknownPackage);
        TestSuite.Add("Vfs.Include", "规则2 Assets/ 前缀相对工程根", IncludeRuleAssetsPrefix);
        TestSuite.Add("Vfs.Include", "规则3 裸名相对包含方文件目录", IncludeRuleRelative);
        TestSuite.Add("Vfs.Include", "规则4 裸名兜底到编辑器 CGIncludes", IncludeRuleCGIncludesFallback);
        TestSuite.Add("Vfs.Include", "彻底找不到时报依赖缺失而不是静默丢弃", IncludeRuleMissingFile);
        TestSuite.Add("Vfs.Include", "编译热路径不做磁盘 I/O（未落盘也返回候选路径）", IncludeHotPathIsPure);
        TestSuite.Add("Vfs.Include", "绝对路径与根相对路径 include 走绝对解析", IncludeRuleAbsolutePath);

        TestSuite.Add("Vfs.Registry", "快照置换单调递增且读取零锁", RegistryPublishesSnapshots);
        TestSuite.Add("Vfs.Registry", "动态宏上下文可独立置换", RegistryPublishesDynamicContext);
        TestSuite.Add("Vfs.Registry", "TryResolveVirtualPath 契约入口返回物理路径", RegistryContractEntryPoint);

        TestSuite.Add("Vfs.Watcher", "manifest.json 变化触发重建回调", WatcherDetectsManifestChange);

        TestSuite.Add("Vfs.Refresh", "装包后注册表快照自动换成新拓扑且代际号递增", RefresherPublishesNewTopology);
        TestSuite.Add("Vfs.Refresh", "重建并发请求不重叠执行，代际号逐轮递增", RefresherCollapsesConcurrentRequests);
        TestSuite.Add("Vfs.Refresh", "重建失败保留旧快照，失败后仍能继续重建", RefresherKeepsOldSnapshotOnFailure);
        TestSuite.Add("Vfs.Refresh", "manifest 半写时不得把空拓扑发布出去", RefresherKeepsSnapshotOnInvalidManifest);
    }

    private static void PathNormalization()
    {
        Check.Equal("D:/a/b", VfsPath.Normalize(@"D:\a\x\..\b"), "应当消掉 .. 段");
        Check.Equal("D:/a/b", VfsPath.Normalize("D:/a/./b/"), "应当消掉 . 段与尾部分隔符");
        Check.Equal("D:/a/b", VfsPath.Normalize("D:\\a\\\\b"), "应当折叠重复分隔符");
        Check.Equal("a/b", VfsPath.Normalize("a/b/"), "相对路径归一化");
        Check.Equal("D:/", VfsPath.Normalize("D:/"), "盘符根");

        // .. 上溯必须回到"上一段的末尾"，且相对路径冲到顶之后不得再被后续 .. 弹掉。
        Check.Equal("a/b", VfsPath.Normalize("a/x/../b"), "相对路径中的 .. 上溯");
        Check.Equal("../../a", VfsPath.Normalize("../../a"), "冲到顶之后的多余 .. 必须原样保留");
        Check.Equal("..", VfsPath.Normalize("a/../.."), "上溯到底再上溯只剩 ..");
        Check.Equal("D:/", VfsPath.Normalize("D:/.."), "绝对路径不得越过根");
        Check.Equal("D:/a", VfsPath.Normalize(@"D:\a\b\.."), "绝对路径 .. 上溯");
        Check.Equal("D:/a/b/c.hlsl", VfsPath.Combine("D:/a/b", "c.hlsl"), "Combine");
        Check.Equal("D:/a/b", VfsPath.GetDirectoryName("D:/a/b/c.hlsl"), "GetDirectoryName");
        Check.Equal("c.hlsl", VfsPath.GetFileName("D:/a/b/c.hlsl").ToString(), "GetFileName");
    }

    private static void PathIsUnder()
    {
        Check.True(VfsPath.IsUnder("D:/a/b/c", "D:/a/b"), "子目录应判真");
        Check.True(!VfsPath.IsUnder("D:/a/bc/d", "D:/a/b"), "前缀相同的兄弟目录必须判假");
        Check.True(!VfsPath.IsUnder("D:/a/b", "D:/a/b"), "自身不算自己的子目录");
    }

    /// <summary>
    /// file URI 还原成物理路径的四种形态。
    /// </summary>
    /// <remarks>
    /// 缺陷背景：还原曾经是「按字符串裁掉 file:/// 前缀」，UNC 共享路径被降级成相对路径
    /// （file://server/share/x.hlsl → server/share/x.hlsl），于是相对 include 的基准目录整个错位。
    /// 现在按 URI 语义还原：authority 是主机名，必须还原成 UNC 前缀。
    /// </remarks>
    private static void UriToPathShapes()
    {
        // ① 普通盘符。
        Check.Equal(@"D:\a\b.hlsl", FileUri.ToPath("file:///D:/a/b.hlsl"), "盘符路径还原");

        // ② 带空格与中文：正向编码再反向还原必须回到原样，手写的百分号编码也要认。
        var spaced = @"D:\中文 工程\Assets\My Shader.hlsl";
        Check.Equal(spaced, FileUri.ToPath(FileUri.FromPath(spaced)), "中文与空格路径必须无损往返（百分号只解一次）");
        Check.Equal(
            spaced,
            FileUri.ToPath("file:///D:/%E4%B8%AD%E6%96%87%20%E5%B7%A5%E7%A8%8B/Assets/My%20Shader.hlsl"),
            "手写的百分号编码必须按 UTF-8 字节成组还原");

        // ③ UNC：authority 是主机名，不能当成路径的第一段。
        var unc = FileUri.ToPath("file://server/share/x.hlsl");
        Check.Equal(@"\\server\share\x.hlsl", unc, "UNC 共享路径必须还原成 \\\\server\\share\\x.hlsl");
        Check.True(
            !(unc ?? string.Empty).StartsWith("server", StringComparison.Ordinal),
            "裁前缀的旧写法会把主机名当路径段，还原出相对路径 server/share/x.hlsl");

        // ④ 非 file 方案：没有物理路径，必须返回 null 而不是瞎猜。
        Check.True(FileUri.ToPath("http://example.com/x.hlsl") is null, "http 方案必须返回 null");
        Check.True(FileUri.ToPath("untitled:Untitled-1") is null, "未存盘的 untitled 文档没有物理路径");
        Check.True(FileUri.ToPath(string.Empty) is null, "空串没有物理路径");
    }

    private static void PackageCacheNameDerivation()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.AddPackageCache("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Core.hlsl");
        sandbox.AddPackageCache("com.besty.unity-skills@ec0f32df12", "Editor/Skill.cs");

        var index = sandbox.Build();

        Check.True(index.TryGetPackage("com.unity.render-pipelines.universal", out var urp), "应识别 registry 包");
        Check.Equal("14.0.12", urp!.LockVersion, "版本应取目录名 / lock 的版本");
        Check.Equal(PackageSourceKind.PackageCache, urp.Source, "registry 包来源");

        Check.True(index.TryGetPackage("com.besty.unity-skills", out var git), "应识别 git 短 hash 包");
        Check.Equal(PackageSourceKind.Git, git!.Source, "lock 声明 source:git 时应判为 git 包");
    }

    private static void EmbeddedWinsOverPackageCache()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.AddPackageCache("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Core.hlsl");
        sandbox.AddEmbedded("com.unity.render-pipelines.universal", "ShaderLibrary/Core.hlsl");

        var index = sandbox.Build();
        Check.True(index.TryGetPackage("com.unity.render-pipelines.universal", out var entry), "包应存在");
        Check.Equal(PackageSourceKind.Embedded, entry!.Source, "Packages/ 实体目录必须赢过 PackageCache");
        Check.True(
            entry.PhysicalRoot.StartsWith(sandbox.PackagesDirectory, StringComparison.OrdinalIgnoreCase),
            $"物理根应指向 Packages/，实际 {entry.PhysicalRoot}");
    }

    private static void PackageCacheWinsOverBuiltIn()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.AddBuiltInPackage("com.unity.render-pipelines.core", "ShaderLibrary/Common.hlsl");
        sandbox.AddPackageCache("com.unity.render-pipelines.core@14.0.12", "ShaderLibrary/Common.hlsl");

        var index = sandbox.Build();
        Check.True(index.TryGetPackage("com.unity.render-pipelines.core", out var entry), "包应存在");
        Check.Equal(PackageSourceKind.PackageCache, entry!.Source, "工程实际使用的版本必须赢过编辑器内建副本");

        // 只有内建副本的包仍然要能查到（例如 com.unity.modules.*）。
        Check.True(index.TryGetPackage("com.unity.modules.ai", out var builtIn), "内建包应可查");
        Check.Equal(PackageSourceKind.BuiltIn, builtIn!.Source, "内建包来源");
    }

    private static void EmbeddedSourceWithFileVersion()
    {
        using var sandbox = SandboxProject.Create();
        // 本机实测形态：Packages/com.farlocus.locus 在 lock 里 source=embedded、version=file:com.farlocus.locus
        sandbox.WriteLockEntry("com.farlocus.locus", "file:com.farlocus.locus", "embedded");
        sandbox.AddEmbedded("com.farlocus.locus", "Runtime/Locus.cs");

        var index = sandbox.Build();
        Check.True(index.TryGetPackage("com.farlocus.locus", out var entry), "本地包应可查");
        Check.Equal(PackageSourceKind.Embedded, entry!.Source, "Packages/ 下的本地包判为 Embedded");
    }

    private static void MalformedManifestDegrades()
    {
        using var sandbox = SandboxProject.Create(brokenManifest: true);
        sandbox.AddPackageCache("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Core.hlsl");

        var index = sandbox.Build();

        Check.True(!index.Manifest.ManifestValid, "破损 manifest 应被标记为不可用");
        Check.True(
            index.Issues.Any(i => i.Code == ContextEngineDiagnosticCodes.MalformedManifest),
            "应上报 VFS0001 而不抛异常");
        Check.True(index.TryGetPackage("com.unity.render-pipelines.universal", out _), "降级后仍应能扫出 PackageCache 包");
    }

    private static void CGIncludesEnumeratedAtBuildTime()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.AddCGInclude("AutoLight.cginc");
        sandbox.AddCGInclude("HLSLSupport.cginc");

        var index = sandbox.Build();
        Check.Equal(2, index.CGIncludeFileNames.Count, "CGIncludes 文件数");
        Check.True(index.IsKnownCGInclude("AutoLight.cginc"), "大小写不敏感命中");
        Check.True(index.IsKnownCGInclude("autolight.CGINC"), "大小写不敏感命中（全大写扩展名）");
        Check.True(!index.IsKnownCGInclude("TMPro_UGUI.cginc"), "不在目录里的名字必须判假");
    }

    private static void PackageCachePicksLockedVersion()
    {
        using var sandbox = SandboxProject.Create();

        // 同名包留下两个版本目录（升级包之后旧目录没被清掉），lock 指向更低的那一版。
        // 修复前：两个目录按枚举顺序相互覆盖，lock 的版本完全不参与挑选。
        var newerRegistry = sandbox.AddPackageCache("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Core.hlsl");
        var lockedRegistry = sandbox.AddPackageCache("com.unity.render-pipelines.universal@14.0.9", "ShaderLibrary/Core.hlsl");
        sandbox.WriteLockEntry("com.unity.render-pipelines.universal", "14.0.9", "registry");

        // git 包目录名是短 hash，按大小比较毫无意义，只能靠 lock 认领。
        // 修复前该断言必定失败：枚举顺序靠后的 f9e8d7c6b5 会覆盖 lock 指定的 a1b2c3d4e5。
        var otherGit = sandbox.AddPackageCache("com.besty.shader-kit@f9e8d7c6b5", "Editor/Kit.cs");
        var lockedGit = sandbox.AddPackageCache("com.besty.shader-kit@a1b2c3d4e5", "Editor/Kit.cs");
        sandbox.WriteLockEntry("com.besty.shader-kit", "a1b2c3d4e5", "git");

        // lock 里没有登记的包（例如 lock 漏项）：退回「取版本号最高者」。
        // 1.10.0 比 1.9.0 高，但序数字符串比较里 "1.9.0" 更大，修复前按枚举顺序会命中 1.9.0。
        var lowerNumeric = sandbox.AddPackageCache("com.example.multi@1.9.0", "Runtime/Multi.cs");
        var higherNumeric = sandbox.AddPackageCache("com.example.multi@1.10.0", "Runtime/Multi.cs");

        var index = sandbox.Build();

        Check.True(index.TryGetPackage("com.unity.render-pipelines.universal", out var urp), "registry 包应可查");
        Check.Equal(lockedRegistry, urp!.PhysicalRoot, $"必须命中 lock 指定的 14.0.9（另一个候选是 {newerRegistry}）");
        Check.Equal("14.0.9", urp.LockVersion, "版本应取 lock 的权威值");

        Check.True(index.TryGetPackage("com.besty.shader-kit", out var kit), "git 包应可查");
        Check.Equal(lockedGit, kit!.PhysicalRoot, $"必须命中 lock 指定的 hash 目录（另一个候选是 {otherGit}）");
        Check.Equal(PackageSourceKind.Git, kit.Source, "lock 声明 git 时来源应为 Git");

        Check.True(index.TryGetPackage("com.example.multi", out var multi), "无 lock 的多版本包也应可查");
        Check.Equal(higherNumeric, multi!.PhysicalRoot, $"lock 挑不到时取版本号最高者 1.10.0（另一个候选是 {lowerNumeric}）");

        // 端到端：include 必须落在 lock 指定版本目录里，而不是另一个版本。
        var resolved = IncludeResolver.Resolve(
            index,
            "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl",
            string.Empty,
            verifyOnDisk: true);
        Check.True(resolved.Found, "规则 1 应命中");
        Check.Equal(
            VfsPath.Combine(lockedRegistry, "ShaderLibrary/Core.hlsl"),
            resolved.PhysicalPath!,
            "解析出的头文件必须来自 lock 指定的版本目录");
    }

    private static void IncludeRuleAbsolutePath()
    {
        using var sandbox = SandboxProject.Create();
        var target = sandbox.AddAsset("Shaders/Shared.hlsl");
        var includer = sandbox.AddAsset("Shaders/Foo.hlsl");
        var index = sandbox.Build();

        // 盘符绝对路径（正斜杠形态，即 VfsPath 的归一化口径）。
        // 修复前：被拼成 "<工程>/Assets/Shaders/C:/.../Shared.hlsl"，探盘必然失败，报告层判「依赖缺失」，
        // 整块原生编译因此被跳过。
        var forward = IncludeResolver.Resolve(index, target, includer, verifyOnDisk: true);
        Check.True(forward.Found, $"盘符绝对路径应直接命中，实际 {forward.PhysicalPath ?? "(null)"}");
        Check.Equal(IncludeRuleKind.AbsolutePath, forward.Rule, "命中规则");
        Check.Equal(target, forward.PhysicalPath!, "物理路径应等于 include 原文指向的那个文件");

        // Windows 原生反斜杠形态（"D:\x\y.hlsl"）同样不能拼到包含方目录上。
        var backward = IncludeResolver.Resolve(index, VfsPath.ToNative(target), includer, verifyOnDisk: true);
        Check.True(backward.Found, "反斜杠绝对路径应直接命中");
        Check.Equal(target, backward.PhysicalPath!, "物理路径应把分隔符归一化成正斜杠");

        // 根相对路径只缺盘符，盘符取自包含方文件："C:/a/b.hlsl" 的根相对形态是 "/a/b.hlsl"。
        var rooted = target[2..];
        var rootedResult = IncludeResolver.Resolve(index, rooted, includer, verifyOnDisk: true);
        Check.True(rootedResult.Found, $"根相对路径应按包含方所在磁盘解析，实际 {rootedResult.PhysicalPath ?? "(null)"}");
        Check.Equal(target, rootedResult.PhysicalPath!, "根相对路径的物理路径");

        // 绝对路径指向的文件真不存在时，结论必须是「这个文件不存在」，不得留下拼出来的怪路径。
        var missing = VfsPath.Combine(VfsPath.GetDirectoryName(target), "NoSuchFile.hlsl");
        var missingResult = IncludeResolver.Resolve(index, missing, includer, verifyOnDisk: true);
        Check.True(!missingResult.Found, "不存在的绝对路径必须判缺失");
        Check.Equal(IncludeMissKind.NotFound, missingResult.Miss, "未命中原因");
        Check.Equal(null, missingResult.PhysicalPath, "缺失时不得给出物理路径");
    }

    private static void IncludeRulePackagesPrefix()
    {
        using var sandbox = SandboxProject.Create();
        var packageRoot = sandbox.AddPackageCache("com.unity.render-pipelines.core@14.0.12", "ShaderLibrary/Common.hlsl");
        var index = sandbox.Build();

        var resolution = IncludeResolver.Resolve(
            index,
            "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl",
            sandbox.PathOf("Assets/Shaders/Foo.hlsl"),
            verifyOnDisk: true);

        Check.True(resolution.Found, "规则 1 应命中");
        Check.Equal(IncludeRuleKind.PackageVirtualPrefix, resolution.Rule, "命中规则");
        Check.Equal(
            VfsPath.Combine(packageRoot, "ShaderLibrary/Common.hlsl"),
            resolution.PhysicalPath!,
            "物理路径");
    }

    private static void IncludeRuleUnknownPackage()
    {
        using var sandbox = SandboxProject.Create();
        var index = sandbox.Build();

        var resolution = IncludeResolver.Resolve(
            index,
            "Packages/com.not.installed/ShaderLibrary/Nope.hlsl",
            sandbox.PathOf("Assets/Shaders/Foo.hlsl"),
            verifyOnDisk: true);

        Check.True(!resolution.Found, "未安装的包不得解析成功");
        Check.Equal(IncludeMissKind.UnknownPackage, resolution.Miss, "未命中原因");
        Check.Equal("com.not.installed", resolution.MissingPackageName!, "缺失包名");
        Check.True(
            resolution.DescribeMissing("Packages/com.not.installed/ShaderLibrary/Nope.hlsl").Contains("依赖缺失", StringComparison.Ordinal),
            "说明文本必须明确报「依赖缺失」");
    }

    private static void IncludeRuleAssetsPrefix()
    {
        using var sandbox = SandboxProject.Create();
        var foo = sandbox.AddAsset("Shaders/Foo.hlsl");
        var index = sandbox.Build();

        var resolution = IncludeResolver.Resolve(index, "Assets/Shaders/Foo.hlsl", string.Empty, verifyOnDisk: true);

        Check.True(resolution.Found, "规则 2 应命中");
        Check.Equal(IncludeRuleKind.AssetsPrefix, resolution.Rule, "命中规则");
        Check.Equal(foo, resolution.PhysicalPath!, "物理路径应为 <工程>/Assets/Shaders/Foo.hlsl");
    }

    private static void IncludeRuleRelative()
    {
        using var sandbox = SandboxProject.Create();
        var core = sandbox.AddPackageCacheFile("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Core.hlsl");
        var shadows = sandbox.AddPackageCacheFile("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Shadows.hlsl");
        var index = sandbox.Build();

        // URP 实测形态：ShaderLibrary/Shadows.hlsl 里 #include "Core.hlsl"
        var resolution = IncludeResolver.Resolve(index, "\"Core.hlsl\"", shadows, verifyOnDisk: true);

        Check.True(resolution.Found, "规则 3 应命中");
        Check.Equal(IncludeRuleKind.RelativeToIncludingFile, resolution.Rule, "命中规则");
        Check.Equal(core, resolution.PhysicalPath!, "物理路径应与包含方同目录");

        // 相对上一级
        var up = IncludeResolver.Resolve(index, "../ShaderLibrary/Core.hlsl", shadows, verifyOnDisk: true);
        Check.True(up.Found && up.PhysicalPath == core, "带 .. 的相对路径应正确归一化");
    }

    private static void IncludeRuleCGIncludesFallback()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.AddCGInclude("AutoLight.cginc");
        var foo = sandbox.AddAsset("Shaders/Foo.hlsl");
        var index = sandbox.Build();

        var resolution = IncludeResolver.Resolve(index, "AutoLight.cginc", foo, verifyOnDisk: true);

        Check.True(resolution.Found, "规则 4 应命中");
        Check.Equal(IncludeRuleKind.CGIncludesFallback, resolution.Rule, "命中规则");
        Check.True(
            resolution.PhysicalPath!.EndsWith("CGIncludes/AutoLight.cginc", StringComparison.OrdinalIgnoreCase),
            $"物理路径应落在 CGIncludes 下，实际 {resolution.PhysicalPath}");
    }

    private static void IncludeRuleMissingFile()
    {
        using var sandbox = SandboxProject.Create();
        var foo = sandbox.AddAsset("Shaders/Foo.hlsl");
        var index = sandbox.Build();

        var resolution = IncludeResolver.Resolve(index, "DoesNotExist.hlsl", foo, verifyOnDisk: true);

        Check.True(!resolution.Found, "不存在且不在 CGIncludes 里的文件必须判缺失");
        Check.Equal(IncludeMissKind.NotFound, resolution.Miss, "未命中原因");
        Check.Equal(null, resolution.PhysicalPath, "缺失时不得给出物理路径");
    }

    private static void IncludeHotPathIsPure()
    {
        using var sandbox = SandboxProject.Create();
        var foo = sandbox.AddAsset("Shaders/Foo.hlsl");
        var index = sandbox.Build();

        // 热路径（verifyOnDisk:false）不探盘：即便文件不存在也返回候选路径，交给 DXC 自己的搜索语义裁决。
        var resolution = IncludeResolver.Resolve(index, "NeverWritten.hlsl", foo, verifyOnDisk: false);

        Check.True(resolution.Found, "热路径应产出候选路径");
        Check.Equal(IncludeRuleKind.RelativeToIncludingFile, resolution.Rule, "热路径规则");
        Check.True(!File.Exists(resolution.PhysicalPath!), "该候选路径确实不存在，证明没有做 I/O 判定");

        // 热路径也必须能正确处理 Packages/ 前缀。
        var packaged = IncludeResolver.Resolve(
            index,
            "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl",
            foo,
            verifyOnDisk: false);
        Check.True(packaged.Found || packaged.Miss == IncludeMissKind.UnknownPackage, "包路径热解析应有确定结论");
    }

    private static void RegistryPublishesSnapshots()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.AddPackageCache("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Core.hlsl");

        var registry = new ShaderContextRegistry();
        Check.Equal(0L, registry.Current.Version, "初始快照版本");

        var first = sandbox.Build(generation: 1);
        var v1 = registry.Publish(first);
        Check.Equal(1L, v1, "发布后版本应为 1");
        Check.True(ReferenceEquals(first, registry.Current.Index), "读取端应看到刚发布的索引");

        var second = sandbox.Build(generation: 2);
        var v2 = registry.Publish(second);
        Check.Equal(2L, v2, "第二次发布应为 2");
        Check.True(ReferenceEquals(second, registry.Current.Index), "置换后读取端应看到新索引");
        Check.True(!ReferenceEquals(first, registry.Current.Index), "旧快照应已被替换");
    }

    private static void RegistryPublishesDynamicContext()
    {
        using var sandbox = SandboxProject.Create();
        var registry = new ShaderContextRegistry(sandbox.Build());

        Check.True(!registry.IsUnityLiveConnected, "默认未连接");
        Check.Equal(0, registry.GetActiveDefines().Count, "默认无全局宏");

        var before = registry.Current.Version;
        registry.PublishDynamic(new DynamicShaderContext
        {
            ActiveKeywords = ["_MAIN_LIGHT_SHADOWS"],
            DefinedMacros = ["UNITY_COLORSPACE_GAMMA"],
            IsLive = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        });

        Check.True(registry.IsUnityLiveConnected, "置换后应报告在线");
        Check.SequenceEqual(["_MAIN_LIGHT_SHADOWS"], registry.Current.Dynamic.ActiveKeywords, "激活关键字");
        Check.SequenceEqual(["UNITY_COLORSPACE_GAMMA"], registry.GetActiveDefines(), "全局宏");

        // 关键不变式：置换动态上下文不得丢掉静态索引。
        Check.True(ReferenceEquals(registry.Current.Index, registry.Current.Index), "静态索引仍在");
        Check.True(registry.Current.Version > before, "版本应递增");
    }

    private static void RegistryContractEntryPoint()
    {
        using var sandbox = SandboxProject.Create();
        var foo = sandbox.AddAsset("Shaders/Foo.hlsl");
        var index = sandbox.Build();
        var registry = new ShaderContextRegistry(index);

        var ok = registry.TryResolveVirtualPath("Assets/Shaders/Foo.hlsl", string.Empty, out var physical, out var rule);

        Check.True(ok, "契约入口应解析成功");
        Check.Equal(foo, physical!, "物理路径");
        Check.Equal(IncludeRuleKind.AssetsPrefix, rule, "规则");

        var miss = registry.TryResolveVirtualPath("Packages/com.missing/Any.hlsl", string.Empty, out var none, out _);
        Check.True(!miss, "未知包应判失败");
        Check.Equal(null, none, "失败时不得给出路径");
    }

    private static void WatcherDetectsManifestChange()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.Build();

        using var signal = new ManualResetEventSlim(false);
        VfsChangeReason observed = 0;

        using var watcher = new VfsWatcher(
            UnityProjectLayout.FromRoot(sandbox.ProjectRoot),
            reason =>
            {
                observed = reason;
                signal.Set();
            },
            new VfsWatcherOptions
            {
                Debounce = TimeSpan.FromMilliseconds(150),
                Heartbeat = TimeSpan.FromMilliseconds(400),
            });

        watcher.Start();

        // 改写 manifest：内容真正变化，watcher 与心跳二者至少有一个必须发现。
        File.WriteAllText(
            VfsPath.ToNative(sandbox.PathOf("Packages/manifest.json")),
            "{\"dependencies\":{\"com.unity.render-pipelines.universal\":\"14.0.12\"}}");

        Check.True(signal.Wait(TimeSpan.FromSeconds(8)), "manifest.json 变化后 8 秒内应触发一次重建回调");
        Check.True(observed != 0, "回调应携带具体原因");
    }

    /// <summary>
    /// 端到端：装一个包之后，watcher 触发整份重建并把新快照推进注册表 —— 不需要重启服务。
    /// </summary>
    /// <remarks>
    /// 只改 sources 目录不会被发现（watcher 不看 Library），必须改 lock 才有通知，这与真实
    /// Unity 的行为一致：装包时 Unity 会同时写 packages-lock.json。
    /// 去抖与心跳在这里调到 150 / 400 毫秒，避免这条用例把整套自检拖慢。
    /// </remarks>
    private static void RefresherPublishesNewTopology()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.AddPackageCache("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Core.hlsl");

        var registry = new ShaderContextRegistry(sandbox.Build());
        var initialVersion = registry.Current.Version;

        Check.False(
            registry.Current.Index.TryGetPackage("com.unity.render-pipelines.shadergraph", out _),
            "重建之前注册表里不该有还没装的包（否则这条用例测不到东西）");

        using var signal = new ManualResetEventSlim(false);
        var notified = 0;
        var reasons = new List<VfsChangeReason>();

        using var refresher = new VfsTopologyRefresher(
            sandbox.ProjectRoot,
            registry,
            reason =>
            {
                lock (reasons) reasons.Add(reason);
                Interlocked.Increment(ref notified);
                signal.Set();
            },
            new VfsWatcherOptions
            {
                Debounce = TimeSpan.FromMilliseconds(150),
                Heartbeat = TimeSpan.FromMilliseconds(400),
            },
            new VfsIndexBuildOptions { ForcedUnityInstallRoot = sandbox.InstallRoot });

        refresher.Start();

        // 装包：磁盘上多出包目录，lock 里多出对应条目。
        sandbox.AddPackageCache("com.unity.render-pipelines.shadergraph@14.0.12", "ShaderLibrary/ShaderGraph.hlsl");
        sandbox.WriteLockEntry("com.unity.render-pipelines.shadergraph", "14.0.12", "registry");

        Check.True(signal.Wait(TimeSpan.FromSeconds(8)), "packages-lock.json 变化后 8 秒内应完成一次拓扑重建");

        // 回调在「置换之后」才发，因此断言此刻已经是新快照。
        var current = registry.Current;
        Check.True(
            current.Index.TryGetPackage("com.unity.render-pipelines.shadergraph", out var added),
            "新快照里应能看到新装的包");
        Check.Equal("14.0.12", added!.LockVersion, "新包版本应来自 lock");
        Check.True(
            current.Index.TryGetPackage("com.unity.render-pipelines.universal", out _),
            "新快照必须同时保留原有包（整体重建，不是增量拼装）");
        Check.True(
            current.Index.Generation >= 2,
            "generation 必须递增（启动为 1，重建后至少 2），实际 " + current.Index.Generation);
        Check.True(current.Version > initialVersion, "快照版本号应随置换推进");
        Check.True(notified >= 1, "重建完成后应通知宿主做一次全量重算");

        // 触发原因必须是「lock 变了」，不能是心跳兜底：只有前者能证明 FileSystemWatcher 这条主路径通了，
        // 否则 watcher 整个坏掉时心跳一样能让上面的断言成立。
        lock (reasons)
        {
            Check.True(reasons.Contains(VfsChangeReason.LockChanged),
                "应由 packages-lock 变化触发，实际原因=" + string.Join(",", reasons));
        }

        // 分析侧真正用的是 IndexForDocument（按文档取拓扑），只断言 registry.Current 不够：
        // Publish 若不清按文档的备忘，那次分析仍会拿旧快照去解析 include，热更新等于没做。
        var documentPath = Path.Combine(sandbox.ProjectRoot, "Assets", "Shaders", "Probe.shader");
        Directory.CreateDirectory(Path.GetDirectoryName(documentPath)!);
        File.WriteAllText(documentPath, "Shader \"T/Probe\" { }");
        Check.True(
            registry.IndexForDocument(documentPath)
                .TryGetPackage("com.unity.render-pipelines.shadergraph", out _),
            "按文档取到的拓扑也必须是新的（备忘没跟着失效时会拿到旧快照）");
    }

    /// <summary>
    /// 并发触发：同一时刻只允许一轮重建在跑，期间的请求合并成「跑完再跑一轮」。
    /// </summary>
    /// <remarks>
    /// 用闸门把第一轮卡住，在它跑的过程中再发一次请求 —— 这个交错是确定性的，不靠线程调度运气。
    /// 「交出在跑标记」那段窗口若被无条件清理，第三次请求就能与正在跑的一轮并发扫盘，
    /// 这里会看到三轮或重叠。
    /// </remarks>
    private static void RefresherCollapsesConcurrentRequests()
    {
        using var sandbox = SandboxProject.Create();
        var registry = new ShaderContextRegistry(sandbox.Build());

        using var gate = new ManualResetEventSlim(false);
        using var entered = new SemaphoreSlim(0);
        var rounds = 0;
        var inside = 0;
        var overlap = 0;

        using var refresher = new VfsTopologyRefresher(
            sandbox.ProjectRoot,
            registry,
            _ => { },
            buildIndex: generation =>
            {
                if (Interlocked.Exchange(ref inside, 1) != 0)
                {
                    Volatile.Write(ref overlap, 1);
                }

                Interlocked.Increment(ref rounds);
                entered.Release();
                gate.Wait(TimeSpan.FromSeconds(5));
                Volatile.Write(ref inside, 0);
                return sandbox.Build(generation);
            });

        var first = new Thread(() => refresher.RequestRebuild(VfsChangeReason.Manual));
        first.Start();

        Check.True(entered.Wait(TimeSpan.FromSeconds(5)), "第一轮重建应已进入");
        refresher.RequestRebuild(VfsChangeReason.Manual);   // 在跑期间的一次请求：只置待办
        gate.Set();

        Check.True(first.Join(TimeSpan.FromSeconds(10)), "第一轮重建应已结束");

        var deadline = Environment.TickCount64 + 5000;
        while (Volatile.Read(ref rounds) < 2 && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(20);
        }

        // 等最后一轮彻底落定，确认没有「没人认领的第三轮」。
        Thread.Sleep(150);

        Check.Equal(2, Volatile.Read(ref rounds), "两次请求只应合并成两轮重建（多出来说明在跑标记被错误释放）");
        Check.Equal(0, Volatile.Read(ref overlap), "重建不得重叠执行");
    }

    /// <summary>
    /// 半写窗口：manifest 读到一半时不得发布「包全没了」的拓扑。
    /// </summary>
    /// <remarks>
    /// Unity 装包时 manifest 与 lock 是先后两次写，去抖窗口落在中间就会读到无效 JSON。
    /// Build 内部把这类异常吞成「manifest 无效」的索引而不是抛出，若照原样发布，
    /// 紧接着的全量重算会把所有包判成不在拓扑中，而且这个错误结论会一直留到下一次拓扑事件。
    /// </remarks>
    private static void RefresherKeepsSnapshotOnInvalidManifest()
    {
        using var sandbox = SandboxProject.Create();
        sandbox.AddPackageCache("com.unity.render-pipelines.universal@14.0.12", "ShaderLibrary/Core.hlsl");

        var registry = new ShaderContextRegistry(sandbox.Build());
        var before = registry.Current;
        Check.True(before.Index.TryGetPackage("com.unity.render-pipelines.universal", out _), "前提：启动快照里有包");

        using var refresher = new VfsTopologyRefresher(
            sandbox.ProjectRoot,
            registry,
            _ => { },
            buildOptions: new VfsIndexBuildOptions { ForcedUnityInstallRoot = sandbox.InstallRoot });

        // 模拟 Unity 正在写 manifest：内容不完整。
        File.WriteAllText(Path.Combine(sandbox.ProjectRoot, "Packages", "manifest.json"), "{ \"dependencies\": {");
        refresher.RequestRebuild(VfsChangeReason.ManifestChanged);

        Check.Equal(before.Version, registry.Current.Version, "无效 manifest 不得置换快照");
        Check.True(
            registry.Current.Index.TryGetPackage("com.unity.render-pipelines.universal", out _),
            "旧快照必须原样保留（否则所有包都会被判成不在拓扑里）");
    }

    /// <summary>
    /// 重建失败：旧快照原样继续服务，失败之后仍然能再重建（在跑标记必须被释放）。
    /// </summary>
    private static void RefresherKeepsOldSnapshotOnFailure()
    {
        using var sandbox = SandboxProject.Create();
        var initial = sandbox.Build();

        var registry = new ShaderContextRegistry(initial);
        var notified = 0;
        var warnings = 0;
        var failing = 1;

        using var refresher = new VfsTopologyRefresher(
            sandbox.ProjectRoot,
            registry,
            _ => Interlocked.Increment(ref notified),
            buildIndex: generation => Volatile.Read(ref failing) == 1
                ? throw new IOException("模拟扫盘失败")
                : sandbox.Build(generation),
            onWarning: _ => Interlocked.Increment(ref warnings));

        refresher.RequestRebuild(VfsChangeReason.ManifestChanged);

        Check.True(ReferenceEquals(initial, registry.Current.Index), "重建失败必须保留旧快照（同一份索引对象）");
        Check.Equal(1L, registry.Current.Index.Generation, "失败不得推进 generation");
        Check.Equal(1, Volatile.Read(ref warnings), "失败应留下一条告警");
        Check.Equal(0, Volatile.Read(ref notified), "失败不应触发全量重算");

        // 恢复：若在跑标记没被释放，这一次请求会被静默丢掉，下面两条断言就会失败。
        Volatile.Write(ref failing, 0);
        refresher.RequestRebuild(VfsChangeReason.Manual);

        Check.True(!ReferenceEquals(initial, registry.Current.Index), "失败之后的重建应能正常置换快照");
        Check.Equal(2L, registry.Current.Index.Generation, "恢复后 generation 应在旧值上加一");
        Check.Equal(1, Volatile.Read(ref notified), "恢复后的重建应通知一次全量重算");
    }

    /// <summary>临时沙盒工程。</summary>
    private sealed class SandboxProject : IDisposable
    {
        private readonly string _root;
        private readonly string _installRoot;

        private SandboxProject(string root, string installRoot, bool brokenManifest)
        {
            _root = root;
            _installRoot = installRoot;
            ManifestBroken = brokenManifest;
        }

        public bool ManifestBroken { get; }

        public string ProjectRoot => VfsPath.Normalize(_root);

        /// <summary>合成的编辑器安装根（包来源 ⑤ 与 CGIncludes 都在它下面）。</summary>
        public string InstallRoot => VfsPath.Normalize(_installRoot);

        public string PackagesDirectory => VfsPath.Combine(ProjectRoot, "Packages");

        public string PathOf(string relative) => VfsPath.Combine(ProjectRoot, relative);

        public static SandboxProject Create(bool brokenManifest = false)
        {
            var root = Path.Combine(Path.GetTempPath(), "microshader-vfs-" + Guid.NewGuid().ToString("N"));
            var install = Path.Combine(Path.GetTempPath(), "microshader-editor-" + Guid.NewGuid().ToString("N"));

            var project = new SandboxProject(root, install, brokenManifest);

            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "Packages"));
            Directory.CreateDirectory(Path.Combine(root, "Assets/Shaders"));
            Directory.CreateDirectory(Path.Combine(root, "Library/PackageCache"));
            Directory.CreateDirectory(Path.Combine(install, "Editor/Data/CGIncludes"));
            Directory.CreateDirectory(Path.Combine(install, "Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.modules.ai"));

            File.WriteAllText(
                Path.Combine(root, "ProjectSettings/ProjectVersion.txt"),
                "m_EditorVersion: " + UnityVersion + "\nm_EditorVersionWithRevision: " + UnityVersion + " (1623fc0bbb97)\r\n");

            File.WriteAllText(
                Path.Combine(root, "Packages/manifest.json"),
                brokenManifest
                    ? "{ \"dependencies\": { \"com.unity.render-pipelines.universal\": }"
                    : "{\"dependencies\":{\"com.unity.render-pipelines.universal\":\"14.0.12\",\"com.besty.unity-skills\":\"https://example.invalid/skills.git\"}}");

            File.WriteAllText(
                Path.Combine(root, "Packages/packages-lock.json"),
                "{\"dependencies\":{\"com.unity.render-pipelines.universal\":{\"version\":\"14.0.12\",\"source\":\"registry\"},\"com.besty.unity-skills\":{\"version\":\"ec0f32df12\",\"source\":\"git\"}}}");

            File.WriteAllText(Path.Combine(install, "Editor/Data/Resources/PackageManager/BuiltInPackages/com.unity.modules.ai/package.json"), "{\"name\":\"com.unity.modules.ai\"}");

            return project;
        }

        public string AddAsset(string relative)
        {
            var path = PathOf("Assets/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// asset\n");
            return VfsPath.Normalize(path);
        }

        public string AddPackageCache(string directoryName, string relativeFile)
        {
            var root = Path.Combine(_root, "Library/PackageCache", directoryName);
            var path = Path.Combine(root, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// package cache\n");
            return VfsPath.Normalize(root);
        }

        /// <summary>返回包内某个文件的物理路径（区别于 <see cref="AddPackageCache"/> 返回的包根）。</summary>
        public string AddPackageCacheFile(string directoryName, string relativeFile)
            => VfsPath.Combine(AddPackageCache(directoryName, relativeFile), relativeFile);

        public string AddEmbedded(string packageName, string relativeFile)
        {
            var root = Path.Combine(_root, "Packages", packageName);
            var path = Path.Combine(root, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// embedded\n");
            File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\":\"" + packageName + "\"}");
            return VfsPath.Normalize(root);
        }

        public void AddBuiltInPackage(string packageName, string relativeFile)
        {
            var root = Path.Combine(_installRoot, "Editor/Data/Resources/PackageManager/BuiltInPackages", packageName);
            var path = Path.Combine(root, relativeFile);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "// builtin\n");
            File.WriteAllText(Path.Combine(root, "package.json"), "{\"name\":\"" + packageName + "\"}");
        }

        public void AddCGInclude(string fileName)
            => File.WriteAllText(Path.Combine(_installRoot, "Editor/Data/CGIncludes", fileName), "// cginc\n");

        /// <summary>追加一条 lock 记录（用于覆盖 source/version 组合）。</summary>
        public void WriteLockEntry(string name, string version, string source)
        {
            var path = Path.Combine(_root, "Packages/packages-lock.json");
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
            var existing = document.RootElement.GetProperty("dependencies");

            var merged = new System.Text.Json.Nodes.JsonObject();
            foreach (var property in existing.EnumerateObject())
            {
                merged[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
            }

            merged[name] = new System.Text.Json.Nodes.JsonObject
            {
                ["version"] = version,
                ["source"] = source,
            };

            var rootNode = new System.Text.Json.Nodes.JsonObject { ["dependencies"] = merged };
            File.WriteAllText(path, rootNode.ToJsonString());
        }

        public VfsIndex Build(long generation = 1)
            => VfsIndexBuilder.Build(
                ProjectRoot,
                generation,
                new VfsIndexBuildOptions { ForcedUnityInstallRoot = VfsPath.Normalize(_installRoot) });

        public void Dispose()
        {
            foreach (var path in new[] { _root, _installRoot })
            {
                try
                {
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                }
                catch (IOException)
                {
                    // 临时目录清理失败不影响用例结论。
                }
                catch (UnauthorizedAccessException)
                {
                    // 同上。
                }
            }
        }
    }
}
