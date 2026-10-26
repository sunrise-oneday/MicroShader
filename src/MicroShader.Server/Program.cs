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
//   3. 服务模式：首件事就是 StdioAirGapGuard.Install()，随后把进程级 STDOUT 封到 NUL
//      （协议层改用已预捕获的那份流），之后 stdout 只属于协议层
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

    // ★ 封住原生 STDOUT：上面的 Install 已经把原始输出流预捕获进 CapturedStdout，
    //   传输层此后只写那一份流，进程级句柄再没人用 —— 此刻才轮到把它指到 NUL。
    //   dxcompiler 之类原生库一旦 printf，字节就会混进 LSP 管道，客户端解析直接崩。
    //   顺序不可调换：必须先捕获再封口，否则连我们自己的协议通道都会被一起封掉。
    _ = StdioAirGapGuard.SealNativeStdout();

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

    // ★ 崩溃取证接线：以上两个 handler 只写 fatal.log，环形缓冲里的现场还得有人落盘。
    //   HookProcessExit 幂等，重复调用返回 false；正常收尾会调 MarkCleanExit，
    //   于是「正常关闭」不会再写 dump，只有没走到收尾的退出才转储。
    CrashFlightRecorder.CriticalFallback = static message =>
    {
        // 转储写不进磁盘时唯一还走得通的出口：固定 fatal.log（不依赖追踪开关）加一条环形日志。
        // 这两步都不会抛，降级通道本身绝不能再制造第二个故障。
        Log.Error(LogCategories.Runtime, message);
        DiagTrace.FatalText("DumpFallback", message);
    };
    _ = CrashFlightRecorder.HookProcessExit();

    var stopwatch = Stopwatch.StartNew();
    Log.Info(LogCategories.Runtime, $"启动中：{ServerOptions.ServerVersion}（AOT={IsNativeAot()}）");

    // ── 工程根与 VFS 索引 ──
    var projectLocated = UnityProjectLocator.TryFind(options.ProjectRoot, out var projectRoot, out var projectDetail);
    Log.Info(LogCategories.Runtime, $"工程根：{(projectRoot.Length == 0 ? "<无>" : projectRoot)}（{projectDetail}）");
    DiagTrace.Mark("工程根=" + (projectRoot.Length == 0 ? "<无>" : projectRoot) + "（" + projectDetail + "）");

    // 显式指定却定位失败：不能静默退化成空 VFS 索引，否则用户看到的只是
    // 「所有包都不在拓扑里」这种离原因十万八千里的现象。这里只留痕，不拒绝启动。
    string? projectRootProblem = null;
    if (UnityProjectLocator.IsExplicitRootRejected(options.ProjectRoot, projectLocated))
    {
        projectRootProblem = "你指定的路径无效：--project "
            + options.ProjectRoot
            + " 不是 Unity 工程根（" + projectDetail + "）。"
            + "已退化为空 VFS 索引，所有包都会被判成「不在拓扑中」；"
            + "请把它改成含 ProjectSettings/ProjectVersion.txt 的目录，或去掉 --project。";
        Log.Error(LogCategories.Vfs, projectRootProblem);
        DiagTrace.Mark(projectRootProblem);
    }

    var index = UnityProjectLocator.BuildIndexOrDefault(projectRoot);
    Log.Info(LogCategories.Vfs, $"VFS 索引：{index.PackageCount} 个包，generation={index.Generation}，降级={index.IsDegraded}");
    DiagTrace.Mark("VFS 索引：" + index.PackageCount + " 个包，降级=" + index.IsDegraded);

    // 按文档定位工程：一个窗口里可能开着多个 Unity 工程的文件（典型是把装着若干工程的
    // 父目录当工作区）。用文件自己的位置定位拓扑，才能既不误报「包不在当前工程的包拓扑中」，
    // 也不把相邻工程的包当成自己的。
    var locator = new VfsIndexLocator(index, projectRoot, message => Log.Warn(LogCategories.Vfs, message));
    DiagTrace.Mark("按文档定位工程：已启用（主工程根=" + (locator.PrimaryRoot.Length == 0 ? "<无>" : locator.PrimaryRoot) + "）");

    var registry = new ShaderContextRegistry(index, locator);

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

    // 标签表也按文档所属工程解析：合法标签值来自工程自己装的渲染管线包与工程 C#，
    // 用相邻工程的表去校验只会误报或漏报。
    var tagSchemas = new TagSchemaCache(locator, schema);

    // ── 模块 4：DXC 原生网关 ──
    if (!DxcLocator.TryResolve(options.DxcDirectory, out var dxcDirectory, out var dxcDetail))
    {
        // 没有 DXC 就没有诊断能力。必须留下可读原因与一份转储，而不是静默降级成「永不报错的服务」。
        Log.Error(LogCategories.Compiler, "无法定位 dxcompiler.dll。" + dxcDetail);
        Log.DumpDiagnosticSnapshot("dxc-not-found");

        // ★ 顺序不可颠倒：Shutdown 会关掉屏障持有的 stderr 文件句柄，
        //   之后写 Console.Error 只会写进已释放的句柄 —— 用户最后看到的就是一句空话。
        //   先把「缺哪个文件、去哪装」写完并冲出去，再释放屏障。
        //   人工验证：把 --dxc 指向一个空目录启动服务，stderr 重定向文件
        //   （StdioAirGapGuard.StderrRedirectPath，默认 %LOCALAPPDATA%/MicroShader/dumps/native/
        //   microshader_stderr.log）里必须出现这条完整的错误文本。
        Console.Error.WriteLine("无法定位 dxcompiler.dll。" + dxcDetail);
        Console.Error.Flush();
        StdioAirGapGuard.Shutdown();

        // 现场已经在上面显式转储过，退出时不必再补一份。
        CrashFlightRecorder.MarkCleanExit();
        return 3;
    }

    Log.Info(LogCategories.Compiler, $"DXC 目录：{dxcDirectory}（{dxcDetail}）");

    using var analyzer = new ShaderDocumentAnalyzer(
        registry,
        dxcDirectory,
        new DiagnosticEngineOptions
        {
            TagSchema = schema,
            TagSchemaFor = tagSchemas.For,
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
    var includeFiles = new VfsIncludeFileSource(locator);
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
        new IncludeSymbolProvider
        {
            Cache = includeCache,
            Files = includeFiles,
            Options = includeSymbolOptions,
            PathFromUri = VfsIncludeFileSource.UriToPath,
        },
        blockIndexCache,
        tagSchema: schema,
        tagSchemaFor: tagSchemas.For);

    // 导航（F12 定义 / include 超链接 / 大纲）。与补全共用同一份 include 链符号缓存与预热器：
    // 头文件只扫一遍，既供补全查类型，也供 F12 查声明位置与行表，请求路径上完全不读盘。
    var navigationHost = new VfsNavigationHost(includeFiles);
    var navigation = new NavigationService(navigationHost, includeCache, blockIndexCache: blockIndexCache);
    Log.Info(LogCategories.IntelliSense, "include 链符号：预热 + 兜底已启用");
    Log.Info(LogCategories.IntelliSense, "IntelliSense 已启用：completion(trigger='.') + hover");

    // 生效旋钮留痕：排障时「到底是默认值还是用户设过」必须一眼可辨（trace 与客户端输出通道都能看到）。
    var tuningLine = "性能旋钮：debounce=" + (options.AnalysisDebounceMs?.ToString() ?? "默认(300)")
        + "ms，maxCompileThreads=" + (options.MaxCompileThreads?.ToString() ?? "默认(4)")
        + "，syncIncludeFallback=" + (options.SyncIncludeFallbackFiles?.ToString() ?? "默认(2)")
        + "，unityBridge=" + (options.UnityBridge ? "开" : "关");
    Log.Info(LogCategories.Runtime, tuningLine);
    DiagTrace.Mark(tuningLine);

    // 文档生命周期回调统一入口：opened / saved / changed 三处共用，
    // 避免三份一模一样的闭包 lambda（方法组零分配）。
    void EnqueueWarm(string uri, string text)
        => includeWarmer.Enqueue(uri, VfsIncludeFileSource.UriToPath(uri), text);

    // 回调里用的分析器引用：它在会话收尾时会被 Dispose，而定时器回调可能正跑在半路上，
    // 所以取一份局部引用传进去 —— 回收函数自己判断与兜底，不依赖调用时机。
    var compilerPool = analyzer;

    // 会话参数单独留一个变量：空闲回收的定时器要读同一个回调，开关只在一个地方判定。
    var sessionOptions = new LspServerOptions
    {
        ServerName = "MicroShader",
        ServerVersion = ServerOptions.ServerVersion,
        Debounce = options.AnalysisDebounceMs is int debounceMs
            ? TimeSpan.FromMilliseconds(debounceMs)
            : null,

        // 空闲回收的动作：引擎只回答「现在闲不闲」，什么时候动手由下面那个定时器决定。
        // 关掉开关就是不给回调（--no-startup-gc / MICROSHADER_STARTUP_GC=0）。
        AfterIdle = options.StartupGc ? () => ReclaimIdleMemory(compilerPool) : null,
    };

    var session = new LspServerSession(
        analyzer.AnalyzeAsync,
        sessionOptions,
        new IntelliSenseProvider
        {
            Completion = intelliSense.ProvideCompletions,
            Hover = intelliSense.ProvideHover,

            // 预热必须非阻塞：Enqueue 只入队一次并唤醒后台消费者，立刻返回。
            // 三处文档生命周期回调（opened / saved / changed）走同一方法组，
            // 避免重复的闭包分配与三份一模一样的 lambda 代码。
            DocumentOpened = EnqueueWarm,
            DocumentSaved = EnqueueWarm,

            // didChange：编辑中间态新写的 #include 不必等保存就进入预热（warmer 内按
            // 「include 集合未变则跳过」去抖，逐键成本为微秒级的行扫描）。
            DocumentChanged = EnqueueWarm,
            DocumentClosed = includeWarmer.Invalidate,

            // 磁盘上 shader 相关文件变化：符号快照同样会陈旧，重扫已预热文档（写入戳兜底）。
            WatchedFilesChanged = includeWarmer.RescanAll,
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

    // ── 空闲内存回收的节拍器 ──
    // 上一版是「首份诊断发出后 6 秒回收一次」：单文件场景有效（私有内存 21MB→14MB），
    // 但批量打开时那一刀正好落在第 4-6 个文件上，冷启动的瞬时分配还没走完，只省下 7-9MB。
    // 现在改成真空闲才回收：连续 8 秒没有任何派发与发布，且调度器里没有在飞与排队的一轮。
    //
    // 限次、限频、限时不是保守而是必需品：完整那一档要 stop-the-world，没有上限就成了周期性
    // 停顿 —— 用户每隔几秒敲字卡一下，比多占十几兆内存难受得多。
    // 门槛数值与判定顺序都收在 IdleReclaimPolicy 里：那是一段纯函数，自检可以直接覆盖。
    // 这里只负责「按节拍读状态、够格就动手」。
    var timeProvider = sessionOptions.TimeProvider;
    var reclaimEpoch = timeProvider.GetTimestamp();
    var idleReclaims = 0;

    long ElapsedSinceWindowOpenedMs()
        => (long)timeProvider.GetElapsedTime(reclaimEpoch).TotalMilliseconds;

    // 初值往前推一个间隔：第一次回收只看那 8 秒静默与空闲，不该被「两次之间至少 30 秒」
    // 从启动时刻算起（机器刚启动就拉起服务时，那会白等半分钟）。
    var lastReclaimMs = -(long)IdleReclaimPolicy.MinimumGap.TotalMilliseconds;
    var idleReclaimBusy = 0;

    void TryIdleReclaim()
    {
        // 节拍线程上抛出去就是未处理异常（进程直接死），所以整段兜住。
        try
        {
            var nowMs = ElapsedSinceWindowOpenedMs();

            // 五道门槛（限次 / 限时 / 限频 / 判闲 / 判静默）全在纯函数里，这里只消费结论。
            var due = IdleReclaimPolicy.ShouldReclaim(
                sinceStart: TimeSpan.FromMilliseconds(nowMs),
                sinceLastActivity: session.SinceLastActivity,
                sinceLastReclaim: TimeSpan.FromMilliseconds(nowMs - Volatile.Read(ref lastReclaimMs)),
                isIdle: session.IsIdle,
                reclaimsSoFar: Volatile.Read(ref idleReclaims));

            if (!due) return;

            // 防重入：2 秒的节拍本来撞不上，但进程被冻住再唤醒时可能叠一次。
            if (Interlocked.CompareExchange(ref idleReclaimBusy, 1, 0) != 0) return;

            try
            {
                Volatile.Write(ref lastReclaimMs, nowMs);
                Interlocked.Increment(ref idleReclaims);
                sessionOptions.AfterIdle?.Invoke();
            }
            finally
            {
                Volatile.Write(ref idleReclaimBusy, 0);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategories.Runtime, "空闲回收失败：" + ex.Message);
        }
    }

    // 没有回调（开关关掉）就不必起节拍器：关掉开关连一次唤醒都不该多花。
    // 用 PeriodicTimer 配一个 async 循环，而不是定时器回调：判定已经是同步的纯函数，
    // 这里只需要「每 2 秒醒一次」，退出时靠取消令牌收尾，不必在回调里再兜未处理异常。
    using PeriodicTimer? idleReclaimTimer = sessionOptions.AfterIdle is null
        ? null
        : new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider);
    using var idleReclaimCts = new CancellationTokenSource();

    // 循环 Task 必须留句柄：退出时要等它真的停下来。只 Cancel 不等的话，它可能正卡在
    // TryIdleReclaim 的 GC 里（几百毫秒），而那时 CTS 与 timer 已经被 using 释放 ——
    // 下一拍读 Token 就是 ObjectDisposedException，且它还在拿已拆的会话做判定与回收。
    Task? idleLoop = null;
    if (idleReclaimTimer is { } ticker)
    {
        idleLoop = Task.Run(
            async () =>
            {
                try
                {
                    while (await ticker.WaitForNextTickAsync(idleReclaimCts.Token).ConfigureAwait(false))
                    {
                        TryIdleReclaim();
                    }
                }
                catch (OperationCanceledException)
                {
                    // 停机：节拍器正常离场，不打日志。
                }
                catch (ObjectDisposedException)
                {
                    // 收尾竞态：令牌源已释放，等价于「不再需要节拍」。
                }
            },
            CancellationToken.None);
    }

    // ── 模块 2：工程拓扑热更新 ──
    // 装包、升级包、删包、改 manifest 或 packages-lock、切 Unity 版本之后，服务端不能一直拿着
    // 启动那一刻的包拓扑跑到退出（症状：新装的包不在拓扑里、头文件解析到旧版本目录，只能重启编辑器）。
    // 这里把 VfsWatcher 接上：白名单文件变化 → 整份重建索引 → 原子置换注册表 → 让会话全量重算。
    // 监视范围就是 VfsWatcher 那四个文件，新增或删除 .hlsl 头文件不触发重建 —— include 由 DXC
    // 直接读盘、不经过 VFS，去 watch 整个 Assets 或 Library 只会把 CPU 烧在包解压与导入风暴上。
    // 没有工程根就没有可重建的拓扑，watch 一个不存在的目录只是白占句柄。
    using var vfsRefresher = projectRoot.Length == 0
        ? null
        : new VfsTopologyRefresher(
            projectRoot,
            registry,
            reason =>
            {
                // 拓扑换了就必须作废 include 链符号缓存：包升级、删包、切 Unity 版本之后，
                // 旧快照里的声明偏移与 include 边可能指向已经不存在的目录 —— 留着它，
                // F12 会跳到不存在的行，补全可能拿到旧版本的结构体。
                // 什么时候清：就在重建成功这一刻（连同预热的去抖表，否则重建整批会被跳过）。
                // 清完第一次请求会慢多少：直连 include 由 F12 既有的同步兜底现场补扫
                // （最多几个文件，数十毫秒量级）；更深的 include 与补全文档层等后台预热重扫，
                // 通常几百毫秒内补齐 —— 这段窗口只可能暂时少给候选，不会给错候选。
                // 只有可能改变 include 解析的因由才清符号缓存。心跳与缓冲区溢出重建出来的拓扑
                // 常常和原来一模一样（watcher 指纹里含 Unity 的锁文件，开关一次工程就会触发重建），
                // 无条件清等于把已打开文档的预热结果整批丢掉 —— 窗口内补全缺深层成员、F12 跳不进 include，
                // 而且正好落在用户开始干活的那一刻。
                if (reason is VfsChangeReason.ManifestChanged
                    or VfsChangeReason.LockChanged
                    or VfsChangeReason.ProjectVersionChanged)
                {
                    includeWarmer.ReclaimForTopologyChange();
                }
                else
                {
                    DiagTrace.Mark("拓扑重建但未清符号缓存（原因 " + reason + "）：包根可能没变");
                }

                session.RequestFullReanalysis("工程拓扑变化（" + reason + "）");
            },
            onWarning: message => Log.Warn(LogCategories.Vfs, message));

    vfsRefresher?.Start();

    // 与桥接结论一样攒到协议通道就绪后再报：此刻 Log.Info 还发不出去（出站通道在 RunAsync 里才建）。
    var vfsNote = vfsRefresher is null
        ? "工程拓扑热更新：未启用（没有工程根）"
        : "工程拓扑热更新已启用：监视 manifest / packages-lock / ProjectVersion，心跳兜底 1.5 秒";

    // 模块 11B：桥接状态报告**必须等协议通道真正就绪**。
    // 本机实测教训：出站通道在 RunAsync 里才被创建，在它之前调 Log.Info 会“成功”写入
    // 环形缓冲但被出站层静默丢掉（LspForwardSink → TrySendLogMessage 返回 false）。
    // 于是“桥接明明连上了，Output 面板却什么都没有”——最难排的一类“什么都没发生”。
    // 就绪点取 ServerState.ActiveServing：客户端已回过 initialized，此时发通知也不违反
    // “initialize 应答前不发帧”的协议约束。
    DiagTrace.Mark("Unity 桥接启动结论=" + bridgeNote);

    // 启动结论合成一条：工程根无效的告警排在最前面 —— 它决定了后面所有「包不在拓扑里」的
    // 现象该怎么解释，用户必须在第一眼就看到它，而不是埋在日志流中间。
    ReportWhenServing(session, timeProvider, () =>
    {
        var bridgeLine = unityBridge is null
            ? "Unity 桥接：未启用（" + bridgeNote + "）"
            : "Unity 桥接（启动快照）：" + unityBridge.Status.Describe() + "；" + bridgeNote;
        var head = projectRootProblem is null ? bridgeLine : projectRootProblem + "\n" + bridgeLine;
        return head + "\n" + vfsNote;
    });

    using var cts = new CancellationTokenSource();

    // Ctrl+C 的停机：RunAsync 收到令牌后会放弃阻塞中的读并返回，但返回之后还有释放引擎、
    // 排空出站这些收尾，任何一环卡住进程就挂在那里不走。所以再压一道有上限的兜底。
    Task<int>? runTask = null;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;   // 不硬退：先走正常停机，出站队列才排得空
        cts.Cancel();
        BeginCtrlCShutdown(session, () => runTask);
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
                    // 这里不走正常停机流程：就算令牌现在能叫醒大多数阻塞读，也保不住
                    // 「同步读套壳」那一种，而客户端已死、stdout 管道已无意义，无需排空，
                    // 直接退出最省事，也最快。
                    Log.Warn(LogCategories.Transport, "父进程 PID " + parentPid + " 已消失，按防孤儿策略退出");

                    // 退出码必须是 1，不能是 0：0 在本服务里等于「收到过 shutdown、正常收尾完毕」
                    // （见 RunAsync 末尾与 --help 的退出码表）。父进程是被强杀还是自己崩溃，
                    // 客户端与 CI 都要能区分出来，写成 0 会被当成一次干净退出。
                    Environment.Exit(1);
                }
            }
        }, CancellationToken.None);
    }

    // stdout 必须是屏障预捕获的那一份；Console.Out 已被换成 TextWriter.Null。
    var stdout = StdioAirGapGuard.CapturedStdout ?? Console.OpenStandardOutput();

    int exitCode;
    try
    {
        runTask = session.RunAsync(Console.OpenStandardInput(), stdout, cts.Token);
        exitCode = runTask.GetAwaiter().GetResult();
    }
    finally
    {
        // 节拍器的退出：取消并等它真正停下，之后才允许 using 释放 timer / CTS。
        // 放在 finally 里是因为 runTask 抛异常时同样必须停 —— 否则它是一个永远挂着的后台任务。
        try
        {
            idleReclaimCts.Cancel();
            idleLoop?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // 预期路径。
        }
        catch (ObjectDisposedException)
        {
            // 收尾竞态，同上。
        }
    }

    Log.Sink = null;
    Log.Info(
        LogCategories.Runtime,
        $"会话结束：退出码 {exitCode}，发布 {session.PublishedCount} 次诊断，合并 {session.ChangesCoalesced} 次变更，"
        + $"丢弃 {session.AnalysesDiscarded} 次陈旧分析，限流吞掉 {logSink.SuppressedCount} 条日志，"
        + $"总用时 {stopwatch.ElapsedMilliseconds} ms");

    StdioAirGapGuard.Shutdown();

    // 走到这里说明会话循环、出站排空与引擎释放全部完成：标记正常收尾，
    // ProcessExit 钩子不再补一份 dump（正常关编辑器不该留下崩溃现场）。
    // 故意放在最后一行之前 —— 收尾途中崩掉时这一步还没执行，取证链路照常转储。
    CrashFlightRecorder.MarkCleanExit();
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
static void ReportWhenServing(LspServerSession session, TimeProvider timeProvider, Func<string> message)
{
    _ = Task.Run(async () =>
    {
        // 时钟从会话那一条链上取：这里与空闲回收共用同一个来源，注入假时钟后整段都可控。
        var deadline = timeProvider.GetUtcNow() + TimeSpan.FromSeconds(30);
        while (timeProvider.GetUtcNow() < deadline && session.State != ServerState.ActiveServing)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), timeProvider).ConfigureAwait(false);
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
/// Ctrl+C 的兜底关机：先给正常路径一段自愈时间，超时就排空出站队列并强制结束进程。
/// </summary>
/// <remarks>
/// 为什么必须有这一道：Windows 上标准输入的 ReadAsync 有时是同步读套壳，
/// 被阻塞的读既叫不醒、也不会返回，RunAsync 就永远走不到收尾 —— 进程挂在 Ctrl+C 之后不走。
/// 会话层已经能在令牌响时放弃阻塞中的读，但那依赖「读至少返回了一个未完成的任务」；
/// 真卡在同步调用里时进程内没有任何手段能把它拿回来，只能在这里强制离场。
/// 先排空再退出，是为了把停机前已经承诺给客户端的帧（日志、诊断）尽量发完；
/// 排空本身带时限，对端不读也不会让退出再次变成无限等。
/// </remarks>
static void BeginCtrlCShutdown(LspServerSession session, Func<Task<int>?> running)
{
    _ = Task.Run(async () =>
    {
        var task = running();
        if (task is not null)
        {
            var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            if (finished == task)
            {
                return;   // 正常路径已在时限内返回，收尾与退出码交给主线程
            }
        }

        Log.Warn(LogCategories.Transport, "Ctrl+C：服务循环未在 5 秒内返回，排空出站后强制退出");
        try
        {
            await session.DrainOutboundAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 强制退出前的排空失败不值得再抛：反正下一步就是离场。
            Log.Warn(LogCategories.Transport, "强制退出前排空出站失败: " + ex.Message);
        }

        // 这里保持 0：Ctrl+C 是用户主动停机，不是异常终止（防孤儿那条路径才是 1）。
        Environment.Exit(0);
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

/// <summary>
/// 会话空闲时的一次内存回收：先收缩 DXC 实例池，再按堆大小挑一档 GC。
/// </summary>
/// <remarks>
/// 分两档的理由是停顿时间随堆线性涨：
/// 一、堆不超过 64MB 时做完整的 aggressive + LOH 压紧。
///     实测朴素 GC 不会 decommit 空段（committed 稳在 ~23MB），只有 Aggressive 加
///     LargeObjectHeapCompactionMode.CompactOnce 加 blocking 加 compacting 才会压紧并归还
///     （AOT 实测私有内存从 32.4MB 降到 10.5MB），代价是一次 ~10ms 停顿，此时完全值得。
///     末尾再补一次不阻塞的 GC：finalizer 里释放的对象要等队列排空后才死，第二次才收得回来。
/// 二、堆超过 64MB 时不这么做：大堆上 blocking 加 compacting 要 50-150ms，那时用户多半正在
///     编辑，这一下就是肉眼可见的卡顿。只做一次不阻塞、不压紧的回收，把死对象收掉，
///     空段先留给 OS 慢慢拿。回收效果打折是刻意的取舍 —— 内存在这里换的是手感。
/// 注意轻量那一档是非阻塞的，after 采样时回收可能还没跑完，日志里的数字会偏大，属正常。
/// 本方法在后台定时器线程上跑，分析器此刻要么空闲、要么已经释放，两种情况都不会受影响。
/// </remarks>
static void ReclaimIdleMemory(ShaderDocumentAnalyzer? analyzer)
{
    // 一池 DXC 实例是几十兆原生内存，托管堆回收看不见它们，所以先单独收一遍。
    // 分析器可能已被释放（会话收尾与定时器回调会擦肩而过）：方法自己还有一层兜底，
    // 这里再取局部引用判断一次，最坏结果是这一轮少省一点内存。
    if (analyzer is not null)
    {
        try
        {
            analyzer.TrimIdleCompilers(1);
        }
        catch (Exception ex)
        {
            Log.Warn(LogCategories.Compiler, "DXC 实例池收缩失败：" + ex.Message);
        }
    }

    const long SmallHeapLimitBytes = 64L * 1024 * 1024;

    try
    {
        var info = GC.GetGCMemoryInfo();
        var heap = info.HeapSizeBytes;
        var small = heap <= SmallHeapLimitBytes;

        if (small)
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
        }
        else
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);
        }

        var after = GC.GetGCMemoryInfo().TotalCommittedBytes;
        Log.Info(LogCategories.Runtime,
            $"空闲内存回收（{(small ? "完整" : "轻量")}，堆 {heap / 1024 / 1024}MB）："
            + $"committed {info.TotalCommittedBytes / 1024 / 1024}MB → {after / 1024 / 1024}MB");
    }
    catch (Exception ex)
    {
        Log.Warn(LogCategories.Runtime, "空闲内存回收失败：" + ex.Message);
    }
}

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
                             DXC 并行编译线程上限。默认 4（<=0 视为默认）。
                             每多一路并发约多占 6 MB 私有内存（实测）；调小可降低内存/CPU 峰值，
                             代价是多 Pass 文件分析变慢（单 Pass 的 shader 本就串行，不受影响）。
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
          --startup-gc <0|1>  是否启用空闲内存回收（默认 1）。
                             只有连续 8 秒没有任何分析活动、且调度器完全空闲时才动手：
                             收缩 DXC 实例池，再做一次压紧 GC 把空段归还 OS。
                             整进程最多 3 次、两次之间至少隔 30 秒、只在前 5 分钟内生效，
                             这样才不会变成周期性停顿。堆超过 64MB 时只做不阻塞的轻量回收。
                             环境变量：MICROSHADER_STARTUP_GC
          --no-startup-gc       等价于 --startup-gc 0。
          --version          打印版本并真实加载一次 DXC，然后退出（CI 冒烟门禁用）
          --help             打印本帮助

        退出码：
          0  正常（收到过 shutdown）
          1  异常终止（客户端未发 shutdown 就断开，或父进程消失后的防孤儿退出）
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
    public const string ServerVersion = "0.1.20";

    public string? ProjectRoot { get; private init; }

    public string? DxcDirectory { get; private init; }

    /// <summary>打字防抖（毫秒）。null = 用引擎默认（300ms）。</summary>
    public int? AnalysisDebounceMs { get; private init; }

    /// <summary>DXC 并行编译线程上限。null = 用引擎默认（4）。</summary>
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

    /// <summary>
    /// 是否启用空闲内存回收（默认开）。
    /// </summary>
    /// <remarks>
    /// 会话连续 8 秒没有分析活动、且调度器完全空闲时，在后台收缩 DXC 实例池并做一次
    /// 压紧 GC，把冷启动顶起来、之后一直挂着的空段归还 OS。
    /// 整进程最多回收 3 次、两次之间至少隔 30 秒、只在前 5 分钟内生效。
    /// 环境变量：MICROSHADER_STARTUP_GC=0 可关闭。
    /// </remarks>
    public bool StartupGc { get; private init; } = true;

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
        var startupGc = bool.TryParse(Environment.GetEnvironmentVariable("MICROSHADER_STARTUP_GC"), out var sg) ? sg : true;
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
                case "--startup-gc":
                    startupGc = ParseSwitch(NextValue(args, ref i, arg), arg);
                    break;
                case "--no-startup-gc":
                    startupGc = false;
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
            StartupGc = startupGc,
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
