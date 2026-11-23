using System.Diagnostics;
using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 手动验证入口：把一个真实 .shader 文件喂进完整管线（切片 → 装配 → 原生编译 → 归属映射），
/// 把最终诊断按「用户会在编辑器里看到的形态」打印出来。
/// 这是模块 4 的肉眼验收工具，不参与自检计分。
/// </summary>
internal static class AnalyzeCommand
{
    public static int Run(string filePath, string? projectRoot, int probeLine, bool isSave, bool emit, bool verbose)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            Console.Error.WriteLine($"文件不存在: {fullPath}");
            return 2;
        }

        projectRoot ??= DiscoverProject(fullPath);
        if (projectRoot is null)
        {
            Console.Error.WriteLine("未能定位 Unity 工程根（用 --project <目录> 指定）");
            return 2;
        }

        Console.WriteLine($"源文件   : {fullPath}");
        Console.WriteLine($"Unity 工程: {projectRoot}");

        var sw = Stopwatch.StartNew();
        var index = VfsIndexBuilder.Build(projectRoot!);
        var indexMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"VFS 索引 : {index.PackageCount} 个包（{indexMs:F1} ms）");

        var registry = new ShaderContextRegistry(index);

        // 模块 11B：把 Unity 桥接接上，让 --analyze 反映**编辑器当下的**宏状态。
        // 未绑定（Unity 未运行 / 桥接未安装）时只提示一行，行为与引入前完全一致。
        var unityBridge = TryAttachUnityBridge(projectRoot!, registry);
        try
        {
            if (!ShaderDiagnosticEngine.TryCreate(registry, DxcGatewayTests.DxcDirectory(), out var engine, out var error) || engine is null)
            {
                Console.Error.WriteLine("dxcompiler.dll 不可用: " + error);
                return 2;
            }

            using (engine)
            {
                Console.WriteLine($"DXC      : {engine.DxcVersion}");

                var text = File.ReadAllText(fullPath);
                var parse = new ShaderLabStateMachine().Parse(fullPath, text);
                var document = new SourceShaderDocument
                {
                    FileUri = parse.TargetUri,
                    FilePath = fullPath,
                    FullText = text,
                    Version = 1,
                };

                Console.WriteLine($"切片     : {parse.Passes.Count} 个 Pass / {parse.SharedSnippets.Count} 个共享块 / "
                    + $"{parse.Passes.Sum(p => p.ActivePragmaDefines.Count)} 条变体声明 / 切片诊断 {parse.Diagnostics.Count} 条");

                if (parse.Diagnostics.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine("── 切片阶段诊断（模块 1，尚未进入编译器）───────────────");
                    Print(parse.Diagnostics);
                }

                var report = engine.Analyze(document, probeLine, isSave);
                var wall = sw.Elapsed.TotalMilliseconds;

                Console.WriteLine();
                Console.WriteLine($"装配     : Pass 单元 {report.UnitCount} → 阶段派发 {report.DispatchCount}"
                    + $"（编译 {report.CompiledUnitCount} / 抑制 {report.SuppressedUnitCount} / 中止 {report.AbortedUnitCount}）");
                Console.WriteLine($"编译     : 原始诊断 {report.RawDiagnosticCount} 条，丢弃 pragma-message {report.DroppedPragmaMessages} 条，"
                    + $"列降级 {report.DegradedColumns} 条，内部错误 {report.InternalErrors} 条");
                Console.WriteLine($"耗时     : 编译器 {report.CompilerMicroseconds / 1000.0:F1} ms，分析总计 {report.ElapsedMicroseconds / 1000.0:F1} ms，"
                    + $"进程墙钟 {wall:F1} ms");

                Console.WriteLine();
                Console.WriteLine(report.Items.Count == 0
                    ? "── 诊断：无 ────────────────────────────────────────────"
                    : $"── 诊断 {report.Items.Count} 条 ──────────────────────────────────────");

                Print(report.Items);

                if (emit)
                {
                    Emit(registry, document, parse, verbose);
                }
            }
        }
        finally
        {
            unityBridge?.Dispose();
        }

        return 0;
    }

    /// <summary>
    /// 接上 Unity 桥接并**有界等待**首帧（最多 2 秒）。
    /// </summary>
    /// <remarks>
    /// 这里刻意同步等待：<c>--analyze</c> 是人工验收工具，用户期望「这一行输出就是编辑器现在的状态」；
    /// 而服务模式的宿主（<c>Program.cs</c>）是**不等待**的 —— 诊断绝不能为桥接让路。
    /// </remarks>
    private static UnityBridgeClient? TryAttachUnityBridge(string projectRoot, ShaderContextRegistry registry)
    {
        // 与 ServerOptions 同一个开关：便于做「桥接开/关」的逐字节对照验收。
        var toggle = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_BRIDGE");
        if (toggle is not null && (toggle.Trim() is "0" or "false" or "off" or "no"))
        {
            Console.WriteLine("Unity 桥接: 已按 MICROSHADER_UNITY_BRIDGE 显式关闭");
            return null;
        }

        if (!UnityProjectLayout.TryLocate(projectRoot, out var layout, out var locateError) || layout is null)
        {
            Console.WriteLine($"Unity 桥接: 未启用（{locateError}）");
            return null;
        }

        var client = new UnityBridgeClient(layout, registry);
        client.Start();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && !registry.Current.Dynamic.IsLive)
        {
            var state = client.Status.State;
            if (state is UnityBridgeLinkState.Unbound or UnityBridgeLinkState.Stale)
            {
                // 明确未绑定/不可信：不必把 2 秒预算耗完。
                break;
            }

            Thread.Sleep(50);
        }

        Console.WriteLine("Unity 桥接: " + client.Status.Describe());
        if (registry.Current.Dynamic.IsLive)
        {
            var dynamicContext = registry.Current.Dynamic;
            Console.WriteLine("           : 活动关键字 " + dynamicContext.ActiveKeywords.Length + " 项 / 动态宏 "
                + dynamicContext.DefinedMacros.Length + " 项（已定义）+ " + dynamicContext.UndefinedMacros.Length + " 项（显式未定义）");
        }

        return client;
    }

    private static void Print(IReadOnlyList<ShaderDiagnosticItem> items)
    {
        foreach (var item in items)
        {
            var code = string.IsNullOrEmpty(item.Code) ? string.Empty : " " + item.Code;
            Console.WriteLine($"  {item.Line,4}:{item.Column,-4} [{item.Severity}]{code} {item.Message}");

            if (item.RelatedFilePath is not null)
            {
                Console.WriteLine($"              ↳ 关联: {item.RelatedFilePath}:{item.RelatedLine}");
            }
        }
    }

    /// <summary>把装配后的渲染文本逐单元打印出来（排查坐标映射问题时的第一手材料）。</summary>
    private static void Emit(ShaderContextRegistry registry, SourceShaderDocument document, ShaderLabParseResult parse, bool verbose)
    {
        var assembler = new VirtualTextAssembler(registry);
        var report = assembler.AssembleDocument(document, parse, ShaderStage.None);
        Console.WriteLine();
        Console.WriteLine($"── 渲染文本（{report.Units.Count} 个单元）────────────────────────");

        var index = 0;
        foreach (var unit in report.Units)
        {
            Console.WriteLine($"  ── 单元 {index++}：阶段 {unit.Stage} / "
                + $"{unit.Text?.LineMap.LineCount ?? 0} 行 / 重写 {unit.Text?.LineMap.RewriteCount ?? 0} 行 / {unit.Failure}");
            if (verbose && unit.Text is not null)
            {
                foreach (var line in unit.Text.GetText().Split('\n'))
                {
                    Console.WriteLine("    | " + line);
                }
            }
        }

        report.DisposeAll();
    }

    /// <summary>从 .shader 路径向上找 ProjectSettings/ProjectVersion.txt；否则回落到环境变量与默认工程。</summary>
    private static string? DiscoverProject(string shaderPath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(shaderPath)!);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ProjectSettings", "ProjectVersion.txt")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        var fromEnv = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT");
        if (!string.IsNullOrEmpty(fromEnv) && Directory.Exists(fromEnv))
        {
            return fromEnv;
        }

        return Directory.Exists(@"D:\Program Files\U3D\NewWorld") ? @"D:\Program Files\U3D\NewWorld" : null;
    }
}
