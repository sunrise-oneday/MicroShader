using System.Diagnostics;
using System.Runtime.CompilerServices;
using MicroShader.ContextEngine;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.DiagnosticEngine;
using MicroShader.IntelliSenseEngine;
using MicroShader.NavigationEngine;
using MicroShader.ObservabilityEngine;
using MicroShader.Server;

// ============================================================================
// MicroShader · UnityShaderLsp —— LSP 服务端宿主（模块 12 的交付入口）
//
// 启动序列（顺序即约束，不可调换）：
//   1. 解析参数（"刻意不产生任何输出" —— 服务模式下 stdout 是协议通道）
//   2. --help / --version 诊断模式：需要 stdout 打印，因此"不安装" stdio 屏障
//   3. 服务模式：首件事就是 StdioAirGapGuard.Install()，之后 stdout 只属于协议层
//   4. 解析工程根 → 建 VFS 索引 → 采集标签校验表 → 建诊断引擎
//   5. 建会话 → 接日志出站 → 在 stdin/stdout 上跑服务循环 → 用退出码表达结果
// ============================================================================

return Dispatch(args);

static int Dispatch(string[] args)
{
    ServerOptions options;
    try
    {
        options = ServerOptions.Parse(args);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine("参数错误：" + ex.Message);
        Console.Error.WriteLine("用 --help 查看用法。");
        return 2;
    }

    if (options.ShowHelp)
    {
        PrintUsage();
        return 0;
    }

    if (options.ShowVersion)
    {
        return PrintVersion(options.DxcDirectory);
    }

    return RunServer(options);
}

// ── 服务模式 ────────────────────────────────────────────────────────────────

static int RunServer(ServerOptions options)
{
    // ★ 必须在任何输出/传输动作之前：捕获原始 stdout 并接管进程级 STDOUT/STDERR 句柄。
    StdioAirGapGuard.Install();

    // 临时诊断追踪（哨兵文件 %LOCALAPPDATA%\MicroShader\dumps\trace.on 存在即生效）。
    DiagTrace.Init(null);
    DiagTrace.Mark("RunServer 进入 cwd=" + Environment.CurrentDirectory
        + " aot=" + IsNativeAot()
        + " projectOption=" + (options.ProjectRoot ?? "<null>")
        + " debounceOption=" + (options.AnalysisDebounceMs?.ToString() ?? "<null>")
        + " threadsOption=" + (options.MaxCompileThreads?.ToString() ?? "<null>")
        + " syncFallbackOption=" + (options.SyncIncludeFallbackFiles?.ToString() ?? "<null>")
        + " unityBridgeOption=" + (options.UnityBridge ? "on" : "off")
        + " args=[" + string.Join(' ', Environment.GetCommandLineArgs()) + "]");

    // ★ 全局兜底（详设 v2.0「思路 E」）：Native AOT 下后台线程的未处理异常会让进程 FailFast，
    //   并把退出码变成 0xC0000409；这是唯一来得及留痕的位置，固定写 fatal.log，不依赖追踪开关。
    AppDomain.CurrentDomain.UnhandledException += static (_, e) =>
        DiagTrace.Fatal("UnhandledException", e.ExceptionObject as Exception);
    TaskScheduler.UnobservedTaskException += static (_, e) =>
    {
        DiagTrace.Fatal("UnobservedTaskException", e.Exception);
        e.SetObserved();
    };

    var stopwatch = Stopwatch.StartNew();
    Log.Info(LogCategories.Runtime, $"启动中：{ServerOptions.ServerVersion}（AOT={IsNativeAot()}）");

    // ── 工程根与 VFS 索引 ──
    _ = UnityProjectLocator.TryFind(options.ProjectRoot, out var projectRoot, out var projectDetail);
    Log.Info(LogCategories.Runtime, $"工程根：{(projectRoot.Length == 0 ? "<无>" : projectRoot)}（{projectDetail}）");
    DiagTrace.Mark("工程根=" + (projectRoot.Length == 0 ? "<无>" : projectRoot) + "（" + projectDetail + "）");

    var index = UnityProjectLocator.BuildIndexOrDefault(projectRoot);
    Log.Info(LogCategories.Vfs, $"VFS 索引：{index.PackageCount} 个包，generation={index.Generation}，降级={index.IsDegraded}");
    DiagTrace.Mark("VFS 索引：" + index.PackageCount + " 个包，降级=" + index.IsDegraded);

    var registry = new ShaderContextRegistry(index);

    // ── 模块 11B：Unity 桥接（拓扑：Unity-as-server，本端外连）──
    // 发现文件缺失、破损、判活失败一律退化为「未绑定」，**绝不影响**诊断主链路；
    // 上线后它只做一件事：把编辑器权威的全局宏/关键字状态推进注册表快照。
    //
    // 启动结论（含「为何没启用」）攒在 bridgeNote 里、**等日志汇装上后再报**：
    // 桥接的建立/跃迁都发生在毫秒级，早于 Log.Sink 赋值，直接记会静默丢掉 ——
    // 用户装好了桥接却在 Output 面板里看不到任何证据，是最难排的一类「什么都没发生」。
    UnityBridgeClient? unityBridge = null;
    string bridgeNote;
    if (!options.UnityBridge)
    {
        bridgeNote = "已按 --no-unity-bridge / MICROSHADER_UNITY_BRIDGE 关闭";
    }
    else if (projectRoot.Length == 0)
    {
        bridgeNote = "未定位到 Unity 工程根（没有 endpoint.json 可读），跳过";
    }
    else
    {
        unityBridge = StartUnityBridge(projectRoot, registry, out bridgeNote);
    }

    // ── 模块 9：标签校验表（冷启动一次性采集）──
    var schema = TagSchemaHarvester.Harvest(index, projectRoot);

    // ── 模块 4：DXC 原生网关 ──
    if (!DxcLocator.TryResolve(options.DxcDirectory, out var dxcDirectory, out var dxcDetail))
    {
        // 没有 DXC 就没有诊断能力。必须留下可读原因与一份转储，而不是静默降级成「永不报错的服务」。
        Log.Error(LogCategories.Compiler, "无法定位 dxcompiler.dll。" + dxcDetail);
        Log.DumpDiagnosticSnapshot("dxc-not-found");
        StdioAirGapGuard.Shutdown();
        Console.Error.WriteLine("无法定位 dxcompiler.dll。" + dxcDetail);
        return 3;
    }

    Log.Info(LogCategories.Compiler, $"DXC 目录：{dxcDirectory}（{dxcDetail}）");

    using var analyzer = new ShaderDocumentAnalyzer(
        registry,
        dxcDirectory,
        new DiagnosticEngineOptions
        {
            TagSchema = schema,
            AttachRelatedInformation = true,
            FilterPragmaMessages = true,
            MaxCompileThreads = options.MaxCompileThreads ?? 0,
        });

    Log.Info(LogCategories.Compiler, $"DXC 版本：{analyzer.DxcVersion}（用时 {stopwatch.ElapsedMilliseconds} ms）");

    // ── 模块 8：协商会话 ──
    // ── 模块 9：IntelliSense（补全 + 悬停）──
    // 能力集与实现在同一处注入：session 只在委托非 null 时才在 initialize 里宣告对应能力，
    // 从结构上排除「宣告了不响应」或「实现了没宣告」。
    // ── include 链符号：C 后台预热（didOpen 触发）+ B 同步兜底（缓存 miss 时最多扫 2 个直连 include）──
    // 预热器持有后台单消费者队列；Main 退出时由 using 释放（取消并等待，上限 2 秒）。
    var includeCache = new IncludeSymbolCache();
    var includeFiles = new VfsIncludeFileSource(index, projectRoot);
    using var includeWarmer = new IncludeSymbolWarmer(includeFiles, includeCache);

    // 只在宿主显式给了值时才替换 Options —— 否则保持 new IncludeSymbolOptions() 的类默认值，
    // 与引入旋钮前逐字节一致（SyncFallbackFiles 默认 2）。
    var includeSymbolOptions = options.SyncIncludeFallbackFiles is int syncFallback
        ? new IncludeSymbolOptions { SyncFallbackFiles = syncFallback }
        : new IncludeSymbolOptions();

    // 块索引缓存：补全（光标所在块）、大纲（全部块）、F12（相关块）共用一份 —— 一次构建、多处复用。
    var blockIndexCache = new HlslBlockIndexCache();

    var intelliSense = new IntelliSenseService(
        null,
        new IncludeSymbolProvider { Cache = includeCache, Files = includeFiles, Options = includeSymbolOptions },
        blockIndexCache);

    // 导航（F12 定义 / include 超链接 / 大纲）。与补全共用同一份 include 链符号缓存与预热器：
    // 头文件只扫一遍，既供补全查类型，也供 F12 查声明位置与行表，请求路径上完全不读盘。
    var navigationHost = new VfsNavigationHost(includeFiles);
    var navigation = new NavigationService(navigationHost, includeCache, blockIndexCache: blockIndexCache);
    Log.Info(LogCategories.IntelliSense, "include 链符号：预热 + 兜底已启用");
    Log.Info(LogCategories.IntelliSense, "IntelliSense 已启用：completion(trigger='.') + hover");

    // 生效旋钮留痕：排障时「到底是默认值还是用户设过」必须一眼可辨（trace 与客户端输出通道都能看到）。
    var tuningLine = "性能旋钮：debounce=" + (options.AnalysisDebounceMs?.ToString() ?? "默认(300)")
        + "ms，maxCompileThreads=" + (options.MaxCompileThreads?.ToString() ?? "默认(8)")
        + "，syncIncludeFallback=" + (options.SyncIncludeFallbackFiles?.ToString() ?? "默认(2)")
        + "，unityBridge=" + (options.UnityBridge ? "开" : "关");
    Log.Info(LogCategories.Runtime, tuningLine);
    DiagTrace.Mark(tuningLine);

    var session = new LspServerSession(
        analyzer.AnalyzeAsync,
        new LspServerOptions
        {
            ServerName = "MicroShader",
            ServerVersion = ServerOptions.ServerVersion,
            Debounce = options.AnalysisDebounceMs is int debounceMs
                ? TimeSpan.FromMilliseconds(debounceMs)
                : null,
        },
        new IntelliSenseProvider
        {
            Completion = intelliSense.ProvideCompletions,
            Hover = intelliSense.ProvideHover,

            // 预热必须非阻塞：Enqueue 只入队一次并唤醒后台消费者，立刻返回。
            DocumentOpened = (uri, text) => includeWarmer.Enqueue(uri, VfsIncludeFileSource.UriToPath(uri), text),
            DocumentSaved = (uri, text) => includeWarmer.Enqueue(uri, VfsIncludeFileSource.UriToPath(uri), text),
            DocumentClosed = includeWarmer.Invalidate,
        },
        new NavigationProvider
        {
            Definition = navigation.ProvideDefinition,
            DocumentLinks = navigation.ProvideDocumentLinks,
            ResolveDocumentLink = navigation.ResolveDocumentLink,
            DocumentSymbols = navigation.ProvideDocumentSymbols,
            FoldingRanges = navigation.ProvideFoldingRanges,

            // 客户端能力快照只有 initialize 才知道，而组合根构造服务时还没有：
            // 由会话收到 initialize 后转达，返回形状的决策仍留在引擎内部。
            CapabilitiesReceived = capabilities => navigation.ApplyCapabilities(
                capabilities.LinkSupport,
                capabilities.HierarchicalDocumentSymbol,
                capabilities.SymbolKindValueSet),
        });

    // 破环：导航宿主要查「哪个文件正被编辑器打开」（脏文件跳转），而会话又要靠它构造出的导航服务。
    // 顺序因此是：先建服务，再建会话，最后把会话回填给宿主。
    navigationHost.OpenDocuments = session;

    // ── 模块 7 → 客户端：日志旁路（README 模块 8 后续项第 1 条）──
    using var logSink = new ClientLogSink(session);
    Log.Sink = logSink;

    // 模块 11B：桥接状态报告**必须等协议通道真正就绪**。
    // 本机实测教训：出站通道在 RunAsync 里才被创建，在它之前调 Log.Info 会“成功”写入
    // 环形缓冲但被出站层静默丢掉（LspForwardSink → TrySendLogMessage 返回 false）。
    // 于是“桥接明明连上了，Output 面板却什么都没有”——最难排的一类“什么都没发生”。
    // 就绪点取 ServerState.ActiveServing：客户端已回过 initialized，此时发通知也不违反
    // “initialize 应答前不发帧”的协议约束。
    DiagTrace.Mark("Unity 桥接启动结论=" + bridgeNote);
    ReportWhenServing(session, () => unityBridge is null
        ? "Unity 桥接：未启用（" + bridgeNote + "）"
        : "Unity 桥接（启动快照）：" + unityBridge.Status.Describe() + "；" + bridgeNote);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;   // 不硬退：让 RunAsync 正常返回，保证出站队列能排空
        cts.Cancel();
    };

    // 防孤儿（详设 v2.0 第 1b 条）：VS Code 等客户端把父进程 PID 放进 MICROSHADER_PARENT_PID。
    // 父进程被强杀时子进程的退出 handler 不会执行（detached/强杀场景），轮询是唯一可靠手段。
    // 每 5 秒确认父进程存活，且进程名与启动时一致（防 PID 复用误判）；消失即取消会话令牌走正常退出。
    // 不设该环境变量时完全不轮询——直接命令行运行不受影响。
    var parentPidRaw = Environment.GetEnvironmentVariable("MICROSHADER_PARENT_PID");
    if (int.TryParse(parentPidRaw, out var parentPid))
    {
        string? parentName = null;
        try
        {
            parentName = Process.GetProcessById(parentPid).ProcessName;
        }
        catch (Exception)
        {
            // 父进程在拉起我们之前就已消失：不特殊处理，首轮轮询会立即退出。
        }

        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                bool alive;
                try
                {
                    alive = !Process.GetProcessById(parentPid).HasExited;
                }
                catch (Exception)
                {
                    alive = false;   // PID 不存在 = 父进程已消失
                }

                if (alive && parentName is not null)
                {
                    try
                    {
                        alive = string.Equals(Process.GetProcessById(parentPid).ProcessName, parentName, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        alive = false;
                    }
                }

                if (!alive)
                {
                    // 这里不能走 cts.Cancel()：stdin 重定向后是 SyncStream 包装，
                    // 其 ReadAsync 是同步读套壳，阻塞中令牌无效——取消永远叫不醒读循环，
                    // 只能直接退出。客户端已死，stdout 管道已无意义，无需排空。
                    Log.Warn(LogCategories.Transport, "父进程 PID " + parentPid + " 已消失，按防孤儿策略退出");
                    Environment.Exit(0);
                }
            }
        }, CancellationToken.None);
    }

    // stdout 必须是屏障预捕获的那一份；Console.Out 已被换成 TextWriter.Null。
    var stdout = StdioAirGapGuard.CapturedStdout ?? Console.OpenStandardOutput();

    var exitCode = session
        .RunAsync(Console.OpenStandardInput(), stdout, cts.Token)
        .GetAwaiter()
        .GetResult();

    Log.Sink = null;
    Log.Info(
        LogCategories.Runtime,
        $"会话结束：退出码 {exitCode}，发布 {session.PublishedCount} 次诊断，合并 {session.ChangesCoalesced} 次变更，"
        + $"丢弃 {session.AnalysesDiscarded} 次陈旧分析，限流吞掉 {logSink.SuppressedCount} 条日志，"
        + $"总用时 {stopwatch.ElapsedMilliseconds} ms");

    StdioAirGapGuard.Shutdown();
    return exitCode;
}

/// <summary>
/// 把一条“启动结论”推到客户端的 Output 面板；**等会话进入 ActiveServing 再发**。
/// </summary>
/// <remarks>
/// 消息用委托惰性求值：真正发送时的状态才是用户看到的状态（启动结论里常常带着
/// “当前是否已连上 Unity”这类会变的字段）。有界等待 30 秒，超时即放弃（客户端本来
/// 也可能永远不发 initialized）。
/// </remarks>
static void ReportWhenServing(LspServerSession session, Func<string> message)
{
    _ = Task.Run(async () =>
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && session.State != ServerState.ActiveServing)
        {
            try
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
        }

        if (session.State != ServerState.ActiveServing)
        {
            return;
        }

        try
        {
            session.TrySendLogMessage(3, message());   // 3 = MessageType.Info
        }
        catch (Exception)
        {
            // 报告失败绝不能带崩服务端。
        }
    });
}

/// <summary>
/// 启动模块 11B 的 Unity 桥接客户端（拓扑：Unity-as-server，本端外连）。
/// </summary>
/// <remarks>
/// 该链路是**纯旁路**：发现文件不存在（Unity 没开/桥接未安装）、pid 已死、
/// transport 不认识 —— 全部退化为「未绑定」，诊断链路一行不受影响；
/// 它在线的唯一作用是「把编辑器的权威宏状态推进注册表快照」，
/// 于是模块 3 的注入段从「推定」换成「实测」。
/// 只记状态**跃迁**（ADR-027：不得在 tick 路径上打日志，这条链路是低频事件）。
/// </remarks>
static UnityBridgeClient? StartUnityBridge(string projectRoot, ShaderContextRegistry registry, out string note)
{
    if (!UnityProjectLayout.TryLocate(projectRoot, out var layout, out var error) || layout is null)
    {
        note = "未能从工程根定位布局（" + error + "）";
        return null;
    }

    var client = new UnityBridgeClient(
        layout,
        registry,
        log: message => Log.Info(LogCategories.Vfs, message));

    client.Start();
    note = "发现文件 " + client.EndpointPath + "（未上线时静默等待，不阻塞诊断）";
    return client;
}

// ── 诊断模式 ────────────────────────────────────────────────────────────────

/// <summary>
/// 打印版本并"真的"验证一次 DXC 就地可用。
/// </summary>
/// <remarks>
/// 详设 v2.0 第 6 条明确："--version" 不能只证明「.NET 运行时能启动」，
/// 必须真调 "DxcCreateInstance" / "IDxcVersionInfo" 并打印版本 —— 否则 AOT 发布后的
/// 延迟绑定 P/Invoke 失败会被完全掩盖（DLL 缺失时进程仍能启动，直到第一次编译才崩）。
/// </remarks>
static int PrintVersion(string? dxcFromCli)
{
    Console.WriteLine("UnityShaderLsp " + ServerOptions.ServerVersion);
    Console.WriteLine($".NET {Environment.Version} · {(Environment.Is64BitProcess ? "x64" : "x86")} · {DescribeRuntime()}");

    if (!DxcLocator.TryResolve(dxcFromCli, out var directory, out var detail))
    {
        Console.WriteLine("DXC: 未找到 —— " + detail);
        return 3;
    }

    if (!NativeCompilerGateway.TryCreate(directory, out var gateway, out var error, maxThreads: 1) || gateway is null)
    {
        Console.WriteLine($"DXC: 加载失败（{directory}）—— {error}");
        return 4;
    }

    using (gateway)
    {
        // DxcVersion 内部走 DxcCreateInstance + QueryInterface(IDxcVersionInfo) + GetVersion，
        // 因此这一行同时就是「原生互操作垫片真的能用」的证据。
        Console.WriteLine($"DXC: {gateway.DxcVersion}  （{directory}，来源 {detail}）");
    }

    return 0;
}

/// <summary>
/// 描述当前进程的宿主形态。
/// </summary>
/// <remarks>
/// "不能用 "RuntimeFeature.IsDynamicCodeSupported" 单独判定 AOT"：只要工程里写了
/// "PublishAot=true"，SDK 就会把这个 feature switch 一并写进 runtimeconfig，
/// 于是连 "dotnet run"（真 JIT）也报 "false"。
/// "也不能用 "Assembly.Location""：它会被单文件分析器判为 IL3000，
/// 而本工程把 IL 警告一律当错误（详设 v2.0 第 1 条）。
/// 也不用 <see cref="Environment.ProcessPath"/>："dotnet run" 走的是 apphost，
/// 进程路径同样是我们的 ".exe"。
/// 采用的判据是「托管程序集还在不在」：NativeAOT 产物里根本没有 IL，
/// exe 旁边"不会"有 "UnityShaderLsp.dll"；apphost/JIT 形态下 exe 只是启动器，
/// 真正的程序集就在同目录。这是物理事实，不依赖任何可被构建属性改写的 feature switch。
/// </remarks>
static bool IsNativeAot()
{
    var managedAssembly = Path.Combine(AppContext.BaseDirectory, "UnityShaderLsp.dll");
    return !File.Exists(managedAssembly);
}

static string DescribeRuntime() =>
    IsNativeAot()
        ? "NativeAOT（原生 exe）"
        : $"CoreCLR/JIT（动态代码={RuntimeFeature.IsDynamicCodeSupported}）";

static void PrintUsage()
{
    Console.WriteLine("""
        UnityShaderLsp —— Unity URP ShaderLab / HLSL 诊断语言服务器（stdio 传输）

        用法：
          UnityShaderLsp [选项]

        选项：
          --project <目录>   指定 Unity 工程根（默认：MICROSHADER_UNITY_PROJECT，
                             否则从当前目录向上找 ProjectSettings/ProjectVersion.txt）
          --dxc <目录>       指定含 dxcompiler.dll 的目录
                             （默认顺序：环境变量 MICROSHADER_DXC_DIR → exe 同目录 → Windows SDK → 仓库探针目录）
          --debounce <毫秒>  打字防抖时长。默认 300，夹取到 [200, 5000]。
                             调大 = 合并打字突发、降低 CPU 峰值，代价是诊断更新更迟。
                             环境变量：MICROSHADER_ANALYSIS_DEBOUNCE_MS
          --max-compile-threads <n>
                             DXC 并行编译线程上限。默认 8（<=0 视为默认）。
                             调小可降低 CPU 占用峰值，代价是分析变慢。
                             环境变量：MICROSHADER_MAX_COMPILE_THREADS
          --sync-include-fallback <n>
                             补全缓存未命中时，同步扫描「直连 include」的个数。默认 2；
                             0 = 关闭同步兜底（补全路径完全不读盘，代价是新加的 #include
                             在预热完成前补不出符号）。
                             环境变量：MICROSHADER_SYNC_INCLUDE_FALLBACK_FILES
          --unity-bridge <0|1>  是否启用 Unity 编辑器桥接（模块 11B）。默认 1。
                             启用后读 <Project>/Library/MicroShader/endpoint.json，
                             外连 Unity 并接收全局宏/关键字广播，用于把注入段的平台宏
                             从「推定」换成「实测」。发现文件不存在时静默等待，
                             诊断链路完全不受影响。
                             环境变量：MICROSHADER_UNITY_BRIDGE
          --no-unity-bridge     等价于 --unity-bridge 0。
          --version          打印版本并真实加载一次 DXC，然后退出（CI 冒烟门禁用）
          --help             打印本帮助

        退出码：
          0  正常（收到过 shutdown）
          1  客户端未发 shutdown 就断开
          2  参数错误
          3  未找到 dxcompiler.dll
          4  dxcompiler.dll 加载失败

        传输：Content-Length 分帧的 LSP over stdio。stdout 只承载协议字节，
              所有人类可读输出走 window/logMessage 或 stderr 重定向文件。
        """);
}

/// <summary>已解析的命令行。</summary>
internal sealed class ServerOptions
{
    public const string ServerVersion = "0.1.0";

    public string? ProjectRoot { get; private init; }

    public string? DxcDirectory { get; private init; }

    /// <summary>打字防抖（毫秒）。null = 用引擎默认（300ms）。</summary>
    public int? AnalysisDebounceMs { get; private init; }

    /// <summary>DXC 并行编译线程上限。null = 用引擎默认（8）。</summary>
    public int? MaxCompileThreads { get; private init; }

    /// <summary>补全缓存未命中时同步扫描的直连 include 个数。null = 用引擎默认（2）；0 = 关闭同步兜底。</summary>
    public int? SyncIncludeFallbackFiles { get; private init; }

    /// <summary>
    /// 是否启用模块 11B 的 Unity 桥接（默认开）。
    /// </summary>
    /// <remarks>
    /// 关掉它只影响「注入段里能不能看到编辑器权威宏」；发现文件不存在时行为与关掉完全一致。
    /// 环境变量：<c>MICROSHADER_UNITY_BRIDGE=0</c> 可关闭。
    /// </remarks>
    public bool UnityBridge { get; private init; } = true;

    public bool ShowVersion { get; private init; }

    public bool ShowHelp { get; private init; }

    public static ServerOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        string? project = null;
        string? dxc = null;

        // 性能旋钮：命令行优先，其次环境变量；两者都没有就保持 null（= 用引擎默认，行为零漂移）。
        string? debounceRaw = Environment.GetEnvironmentVariable("MICROSHADER_ANALYSIS_DEBOUNCE_MS");
        string? threadsRaw = Environment.GetEnvironmentVariable("MICROSHADER_MAX_COMPILE_THREADS");
        string? syncRaw = Environment.GetEnvironmentVariable("MICROSHADER_SYNC_INCLUDE_FALLBACK_FILES");
        var bridgeRaw = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_BRIDGE");
        var bridge = true;
        var showVersion = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--project" or "--project-root":
                    project = NextValue(args, ref i, arg);
                    break;
                case "--dxc" or "--dxc-dir":
                    dxc = NextValue(args, ref i, arg);
                    break;
                case "--debounce" or "--analysis-debounce-ms":
                    debounceRaw = NextValue(args, ref i, arg);
                    break;
                case "--max-compile-threads":
                    threadsRaw = NextValue(args, ref i, arg);
                    break;
                case "--sync-include-fallback" or "--sync-include-fallback-files":
                    syncRaw = NextValue(args, ref i, arg);
                    break;
                case "--unity-bridge":
                    bridge = ParseSwitch(NextValue(args, ref i, arg), arg);
                    break;
                case "--no-unity-bridge":
                    bridge = false;
                    break;
                case "--version" or "-v":
                    showVersion = true;
                    break;
                case "--help" or "-h" or "-?":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException("未知参数 " + arg);
            }
        }

        return new ServerOptions
        {
            ProjectRoot = project,
            DxcDirectory = dxc,
            AnalysisDebounceMs = ParseOptionalInt(debounceRaw, "--debounce"),
            MaxCompileThreads = ParseOptionalInt(threadsRaw, "--max-compile-threads"),
            SyncIncludeFallbackFiles = ParseOptionalInt(syncRaw, "--sync-include-fallback"),
            UnityBridge = bridgeRaw is null ? bridge : ParseSwitch(bridgeRaw, "MICROSHADER_UNITY_BRIDGE"),
            ShowVersion = showVersion,
            ShowHelp = showHelp,
        };

        static int? ParseOptionalInt(string? raw, string name)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            if (!int.TryParse(raw.Trim(), out var value))
            {
                throw new ArgumentException(name + " 需要整数，收到：" + raw);
            }

            return value;
        }

        static bool ParseSwitch(string? raw, string name)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return true;
            }

            var text = raw.Trim();
            if (text is "0" or "false" or "off" or "no" || text.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (text is "1" or "true" or "on" or "yes" || text.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            throw new ArgumentException(name + " 需要布尔取值（0/1），收到：" + raw);
        }

        static string NextValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException(name + " 缺少取值");
            }

            return args[++index];
        }
    }
}
