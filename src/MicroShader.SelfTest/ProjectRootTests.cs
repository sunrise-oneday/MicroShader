using MicroShader.ContextEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 「文档按自己所在的 Unity 工程解析」。
/// </summary>
/// <remarks>
/// 场景：一个语言服务器窗口里同时开着多个 Unity 工程的文件 —— 最常见的打开方式
/// 就是把「装着若干工程的父目录」当工作区。此时不能只有一个全局工程根：
/// 要么定位不到（空索引：所有 "Packages/…" 都报「不在当前工程的包拓扑中」），
/// 要么定到相邻工程上（拓扑与真相不符）。工程拓扑是「文件属于谁」的函数。
/// </remarks>
internal static class ProjectRootTests
{
    public const string Suite = "ProjectRoot";

    public static void Register()
    {
        TestSuite.Add(Suite, "从文件向上定位所属工程根", RootResolvedFromFile);
        TestSuite.Add(Suite, "主工程之外的文件用自己工程的拓扑", PerDocumentIndex);
        TestSuite.Add(Suite, "主工程根为空时仍能按文档定位", WorksWithNoPrimaryRoot);
        TestSuite.Add(Suite, "工程外的游离文件回退主索引", StrayFallsBackToPrimary);
        TestSuite.Add(Suite, "include 解析按文档所属工程", IncludeResolvedPerDocument);
    }

    /// <summary>从工程内的文件逐级向上找到工程根；途中目录与 PackageCache 都算工程内。</summary>
    private static void RootResolvedFromFile()
    {
        using var project = FakeProject.Create("com.example.marker");

        var locator = new VfsIndexLocator();
        var deep = project.PathOf("Assets/Shaders/Sub/Doc.shader");
        Check.Equal(project.Root, locator.RootFor(deep), "工程内的文件应定位到工程根");

        // 包缓存里的文件（Library/PackageCache/…）同样属于该工程：现实里相对 include
        // 会从包内文件继续向上找，不能在这里断链。
        var inCache = project.PathOf("Library/PackageCache/com.unity.render-pipelines.universal@14.0.12/ShaderLibrary/Core.hlsl");
        Check.Equal(project.Root, locator.RootFor(inCache), "包缓存里的文件也应定位到同一个工程根");
    }

    /// <summary>文件属于哪个工程，就用哪个工程的包拓扑 —— 与「主工程」是谁无关。</summary>
    private static void PerDocumentIndex()
    {
        using var projectA = FakeProject.Create("com.example.marker");
        using var projectB = FakeProject.Create(null);

        // 主工程是 B；A 是「另一个工程」。
        var primary = VfsIndexBuilder.Build(projectB.Root);
        var locator = new VfsIndexLocator(primary, projectB.Root);

        var fileA = projectA.PathOf("Assets/Shaders/Doc.shader");
        var fileB = projectB.PathOf("Assets/Shaders/Doc.shader");

        Check.True(ReferenceEquals(primary, locator.ForFile(fileB)), "主工程的文件直接用主索引");
        Check.Equal(projectA.Root, locator.RootFor(fileA), "另一个工程的文件应定位到它自己的根");

        var indexA = locator.ForFile(fileA);
        Check.True(indexA.TryGetPackage("com.example.marker", out _), "应按 A 的拓扑解析（只有 A 装了该包）");
        Check.True(!primary.TryGetPackage("com.example.marker", out _), "对照：主工程 B 没有该包");
        Check.True(ReferenceEquals(indexA, locator.ForFile(fileA)), "同一文档重复查询命中缓存");
    }

    /// <summary>启动时定位不到工程（父目录工作区且无 --project）也要能按文档兜住。</summary>
    private static void WorksWithNoPrimaryRoot()
    {
        using var project = FakeProject.Create("com.example.marker");

        var locator = new VfsIndexLocator(VfsIndex.Empty, null);
        Check.Equal(string.Empty, locator.PrimaryRoot, "前提：主工程根为空");

        var index = locator.ForFile(project.PathOf("Assets/Shaders/Doc.shader"));
        Check.True(index.PackageCount > 0, "主工程根为空时仍应按文档定位到工程，实际 " + index.PackageCount + " 个包");
    }

    /// <summary>工程之外的文件（游离 .shader）：定位不到就用主索引，与修复前一致。</summary>
    private static void StrayFallsBackToPrimary()
    {
        using var project = FakeProject.Create(null);
        var primary = VfsIndexBuilder.Build(project.Root);
        var locator = new VfsIndexLocator(primary, project.Root);

        var stray = Path.Combine(Path.GetTempPath(), "microshader-stray-" + Guid.NewGuid().ToString("N"), "Loose.shader");
        Check.True(locator.RootFor(stray) is null, "工程外的文件不应定位到工程根");
        Check.True(ReferenceEquals(primary, locator.ForFile(stray)), "游离文件回退主索引");
        Check.True(ReferenceEquals(primary, locator.ForFile(null)), "没有路径时回退主索引");
    }

    /// <summary>
    /// 回归门禁（用户实测报告的场景）：工作区是「装着多个 Unity 工程的父目录」时，
    /// 每个文档的 include 必须按它自己那个工程解析。
    /// </summary>
    /// <remarks>
    /// 用「只有 A 装的包」与 "Assets/… 前缀" 两条断言，都不依赖本机 Unity 安装里有什么 ——
    /// 编辑器自带的 BuiltInPackages 也会提供包（如 URP），拿它当判据会得到与机器相关的结论。
    /// </remarks>
    private static void IncludeResolvedPerDocument()
    {
        using var projectA = FakeProject.Create("com.example.marker");
        using var projectB = FakeProject.Create(null);

        projectA.WriteFile("Packages/com.example.marker/marker.hlsl", "float4 _Marker;");
        projectA.WriteFile("Assets/Shaders/Local.hlsl", "float4 _LocalOfA;");
        projectB.WriteFile("Assets/Shaders/Local.hlsl", "float4 _LocalOfB;");

        // 主工程 = B；文档分别来自 A 与 B。
        var primary = VfsIndexBuilder.Build(projectB.Root);
        var registry = new ShaderContextRegistry(primary, new VfsIndexLocator(primary, projectB.Root));

        var documentInA = projectA.PathOf("Assets/Shaders/Doc.shader");
        var documentInB = projectB.PathOf("Assets/Shaders/Doc.shader");

        // ① 只有 A 装的包：从 A 的文档解析得到，从 B 的文档解析不到。
        var markerFromA = registry.ResolveInclude("Packages/com.example.marker/marker.hlsl", documentInA);
        Check.True(markerFromA.Found, "A 的文档应能解析 A 装的包（修复前在父目录工作区下会报「不在包拓扑中」）");
        Check.Equal(
            projectA.PathOf("Packages/com.example.marker/marker.hlsl"),
            markerFromA.PhysicalPath,
            "物理路径应落在 A 的实体包目录里");

        var markerFromB = registry.ResolveInclude("Packages/com.example.marker/marker.hlsl", documentInB);
        Check.True(!markerFromB.Found, "B 的文档不该凭空解析出 A 装的包（不得被相邻工程洗白）");

        // ② "Assets/…" 前缀的物理落点 = 该文档所属工程的根，而不是启动时那一个。
        var assetsFromA = registry.ResolveInclude("Assets/Shaders/Local.hlsl", documentInA);
        Check.Equal(projectA.PathOf("Assets/Shaders/Local.hlsl"), assetsFromA.PhysicalPath, "A 的文档应落在 A 的 Assets 下");

        var assetsFromB = registry.ResolveInclude("Assets/Shaders/Local.hlsl", documentInB);
        Check.Equal(projectB.PathOf("Assets/Shaders/Local.hlsl"), assetsFromB.PhysicalPath, "B 的文档应落在 B 的 Assets 下");

        // ③ 同一文档重复解析走备忘：结果一致（防「备忘与索引错配」）。
        Check.Equal(markerFromA.PhysicalPath, registry.ResolveInclude("Packages/com.example.marker/marker.hlsl", documentInA).PhysicalPath,
            "重复解析结果必须一致");
    }

    /// <summary>
    /// 临时假工程：带 ProjectSettings 标记、PackageCache 里的 URP，以及可选的一个标记包。
    /// </summary>
    private sealed class FakeProject : IDisposable
    {
        private const string UrpRoot = "Library/PackageCache/com.unity.render-pipelines.universal@14.0.12";

        private readonly string _temp;

        private FakeProject(string temp, bool withUniversalPackage)
        {
            _temp = temp;
            Root = VfsPath.Normalize(temp);
            WithUniversalPackage = withUniversalPackage;
        }

        public string Root { get; }

        public bool WithUniversalPackage { get; }

        public string PathOf(string relative) => VfsPath.Combine(Root, relative);

        public static FakeProject Create(string? extraPackageName, bool withUniversalPackage = true)
        {
            var temp = Path.Combine(Path.GetTempPath(), "microshader-project-" + Guid.NewGuid().ToString("N"));
            var project = new FakeProject(temp, withUniversalPackage);

            Directory.CreateDirectory(Path.Combine(temp, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(temp, "Packages"));
            Directory.CreateDirectory(Path.Combine(temp, "Assets/Shaders"));
            Directory.CreateDirectory(Path.Combine(temp, "Library/PackageCache"));

            File.WriteAllText(
                Path.Combine(temp, "ProjectSettings/ProjectVersion.txt"),
                "m_EditorVersion: 2022.3.62f3c1\nm_EditorVersionWithRevision: 2022.3.62f3c1 (1623fc0bbb97)\r\n");

            var dependencies = new List<string>();
            if (withUniversalPackage)
            {
                dependencies.Add("\"com.unity.render-pipelines.universal\":\"14.0.12\"");
                project.WritePackage(UrpRoot, "com.unity.render-pipelines.universal");
                project.WriteFile(UrpRoot + "/ShaderLibrary/Core.hlsl", "float4 _CoreDummy;");
            }

            if (!string.IsNullOrEmpty(extraPackageName))
            {
                dependencies.Add("\"" + extraPackageName + "\":\"1.0.0\"");
                project.WritePackage("Packages/" + extraPackageName, extraPackageName);
            }

            project.WriteFile("Packages/manifest.json", "{\"dependencies\":{" + string.Join(',', dependencies) + "}}");
            return project;
        }

        /// <summary>写一个最小可识别的包（有 package.json 才被当作包，而非同名文件夹）。</summary>
        public void WritePackage(string relativeDirectory, string packageName)
            => WriteFile(relativeDirectory + "/package.json", "{\"name\":\"" + packageName + "\",\"version\":\"1.0.0\"}");

        public void WriteFile(string relativePath, string content)
        {
            var full = Path.Combine(_temp, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 临时目录删不掉不影响用例结论（系统会在重启后清理）。
            }
        }
    }
}
