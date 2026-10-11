using MicroShader.ContextEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 2（VFS 上下文引擎）的沙盒用例：在临时目录里合成一个 Unity 工程，
/// 覆盖 5 类包来源、4 条 include 规则、降级路径与无锁注册表。
/// </summary>
internal static class VfsSandboxTests
{
    private const string UnityVersion = "2022.3.62f3c1";

    public static void Register()
    {
        TestSuite.Add("Vfs.Path", "归一化消除 . 与 .. 并统一正斜杠", PathNormalization);
        TestSuite.Add("Vfs.Path", "IsUnder 不把前缀相同的兄弟目录误判为子目录", PathIsUnder);

        TestSuite.Add("Vfs.Packages", "PackageCache 按最后一个 @ 切分（registry 与 git 短 hash）", PackageCacheNameDerivation);
        TestSuite.Add("Vfs.Packages", "Packages 实体目录覆盖 PackageCache 同名包", EmbeddedWinsOverPackageCache);
        TestSuite.Add("Vfs.Packages", "PackageCache 覆盖 BuiltInPackages 同名包", PackageCacheWinsOverBuiltIn);
        TestSuite.Add("Vfs.Packages", "lock 的 source:embedded + version:file: 被识别为本地包", EmbeddedSourceWithFileVersion);
        TestSuite.Add("Vfs.Packages", "manifest 破损时降级扫盘并报 VFS0001", MalformedManifestDegrades);
        TestSuite.Add("Vfs.Packages", "CGIncludes 目录在构建期枚举成文件名集合", CGIncludesEnumeratedAtBuildTime);

        TestSuite.Add("Vfs.Include", "规则1 Packages/ 前缀命中包根", IncludeRulePackagesPrefix);
        TestSuite.Add("Vfs.Include", "规则1 未安装的包判为 UnknownPackage", IncludeRuleUnknownPackage);
        TestSuite.Add("Vfs.Include", "规则2 Assets/ 前缀相对工程根", IncludeRuleAssetsPrefix);
        TestSuite.Add("Vfs.Include", "规则3 裸名相对包含方文件目录", IncludeRuleRelative);
        TestSuite.Add("Vfs.Include", "规则4 裸名兜底到编辑器 CGIncludes", IncludeRuleCGIncludesFallback);
        TestSuite.Add("Vfs.Include", "彻底找不到时报依赖缺失而不是静默丢弃", IncludeRuleMissingFile);
        TestSuite.Add("Vfs.Include", "编译热路径不做磁盘 I/O（未落盘也返回候选路径）", IncludeHotPathIsPure);

        TestSuite.Add("Vfs.Registry", "快照置换单调递增且读取零锁", RegistryPublishesSnapshots);
        TestSuite.Add("Vfs.Registry", "动态宏上下文可独立置换", RegistryPublishesDynamicContext);
        TestSuite.Add("Vfs.Registry", "TryResolveVirtualPath 契约入口返回物理路径", RegistryContractEntryPoint);

        TestSuite.Add("Vfs.Watcher", "manifest.json 变化触发重建回调", WatcherDetectsManifestChange);
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
