using MicroShader.SelfTest;

var options = CommandLineOptions.Parse(args);

if (options.ShowHelp)
{
    CommandLineOptions.PrintUsage();
    return 0;
}

FixtureTests.Register();
MacroMatrixTests.Register();
RealWorldTests.Configure(options.UrpRoot);
RealWorldTests.Register();
ShaderTagTests.Configure(options.UrpRoot);
ShaderTagTests.Register();
ServerSmokeTests.Register();
IntelliSenseTests.Register();
IncludeSymbolTests.Register();
BuiltinUniformTests.Register();
NavigationTests.Register();
NavigationRoundTripTests.Register();
AttributionTests.Register();
VfsSandboxTests.Register();
BridgeTests.Register();
VfsRealWorldTests.Configure(Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT"));
VfsRealWorldTests.Register();
DiagnosticEngineTests.Register();
AssemblerRealWorldTests.Configure(Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT"));
AssemblerRealWorldTests.Register();
DxcGatewayTests.Register();
CompilerRealWorldTests.Configure(Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT"));
CompilerRealWorldTests.Register();
ObservabilityTests.Register();
CoordinationEngineTests.Register();
LspCorpusTests.Configure(Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT"));
LspCorpusTests.Register();

if (options.AnalyzePath is not null)
{
    return AnalyzeCommand.Run(options.AnalyzePath, options.ProjectRoot, options.ProbeLine, options.SaveMode, options.Emit, options.Verbose);
}

if (options.ListOnly)
{
    foreach (var testCase in TestSuite.All)
    {
        Console.WriteLine(testCase.FullName);
    }

    return 0;
}

Console.WriteLine("MicroShader · 自检（模块 1 ShaderLabStateMachine / 模块 2 ContextEngine / 模块 3+4 DiagnosticEngine / 模块 7 ObservabilityEngine / 模块 8 CoordinationEngine）");
Console.WriteLine($"  用例语料: {Corpus.FixtureDirectory}");

return TestSuite.Run(options.Filter, options.Verbose);

internal sealed class CommandLineOptions
{
    public string? Filter { get; private init; }

    public string? UrpRoot { get; private init; }

    public string? AnalyzePath { get; private init; }

    public string? ProjectRoot { get; private init; }

    public int ProbeLine { get; private init; }

    public bool SaveMode { get; private init; }

    public bool Emit { get; private init; }

    public bool Verbose { get; private init; }

    public bool ListOnly { get; private init; }

    public bool ShowHelp { get; private init; }

    public static CommandLineOptions Parse(string[] args)
    {
        string? filter = null;
        string? urpRoot = null;
        var verbose = false;
        var listOnly = false;
        var showHelp = false;
        string? analyzePath = null;
        string? projectRoot = null;
        var probeLine = 0;
        var saveMode = true;
        var emit = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--filter" when i + 1 < args.Length:
                    filter = args[++i];
                    break;
                case "--urp" when i + 1 < args.Length:
                    urpRoot = args[++i];
                    break;
                case "--analyze" when i + 1 < args.Length:
                    analyzePath = args[++i];
                    break;
                case "--project" when i + 1 < args.Length:
                    projectRoot = args[++i];
                    break;
                case "--line" when i + 1 < args.Length:
                    probeLine = int.TryParse(args[++i], out var parsedLine) ? parsedLine : 0;
                    break;
                case "--type":
                    saveMode = false;
                    break;
                case "--emit":
                    emit = true;
                    break;
                case "--verbose" or "-v":
                    verbose = true;
                    break;
                case "--list":
                    listOnly = true;
                    break;
                case "--selftest":
                    break;
                case "--help" or "-h" or "-?":
                    showHelp = true;
                    break;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}（用 --help 查看用法）");
                    showHelp = true;
                    break;
            }
        }

        return new CommandLineOptions
        {
            Filter = filter,
            UrpRoot = urpRoot,
            Verbose = verbose,
            ListOnly = listOnly,
            ShowHelp = showHelp,
            AnalyzePath = analyzePath,
            ProjectRoot = projectRoot,
            ProbeLine = probeLine,
            SaveMode = saveMode,
            Emit = emit,
        };
    }

    public static void PrintUsage()
    {
        Console.WriteLine("用法: MicroShader.SelfTest [--selftest] [--filter <子串>] [--urp <URP包目录>] [--list] [--verbose]");
        Console.WriteLine();
        Console.WriteLine("  --selftest        运行全部自检用例（默认动作，ADR-021 要求的独立 CLI 形态）");
        Console.WriteLine("  --filter <子串>   只运行名字包含该子串的用例");
        Console.WriteLine("  --urp <目录>      指定真实 URP 包目录（默认自动探测本机 Unity 工程）");
        Console.WriteLine("  --list            只列出用例名");
        Console.WriteLine("  --analyze <文件>  手动分析单个 .shader（切片 → 装配 → DXC → 归属映射），打印最终诊断");
        Console.WriteLine("  --project <目录>  配合 --analyze 指定 Unity 工程根（默认从文件位置向上找 ProjectSettings）");
        Console.WriteLine("  --line <n>        配合 --analyze 指定探针行（默认 0 = 全量）");
        Console.WriteLine("  --type            配合 --analyze 走打字快路径（默认保存路径全量扫描）");
        Console.WriteLine("  --emit            配合 --analyze 额外打印装配后的渲染文本");
        Console.WriteLine("  --verbose         逐条打印通过用例");
        Console.WriteLine();
        Console.WriteLine("退出码: 0 = 全部通过；1 = 存在失败用例。");
    }
}
