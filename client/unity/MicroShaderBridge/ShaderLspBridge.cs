// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 组合根（生命周期与编排）
//
// 安装：把本文件夹整个丢进 Unity 工程的任意 "Editor" 文件夹下，例如
//       <Project>/Assets/Editor/MicroShaderBridge/
//       （放在任意 Editor 文件夹下即可；各文件自带 #if UNITY_EDITOR 保护，
//         误放到 Assets/ 根也不会污染播放器构建）
//
// 零资产依赖、零第三方依赖（不用 UniTask、不用 HttpListener、不用 WebSocket）。
//
// 本类只做三件事：**接线**（订阅编辑器事件）、**编排**（采样 → 写文件 → 广播）、
// **暴露状态**（供菜单与排障读取）。具体能力都委派给同目录下的专职类：
//
//   BridgeSettings        EditorPrefs / SessionState 的唯一入口
//   BridgeEnvironment     编辑器环境事实采集
//   BridgeContextSnoop    10Hz 采样 + 变化判定 + 产出载荷
//   BridgePayloadCodec    契约 2 载荷编码（纯函数）
//   BridgeEndpointDocument 契约 1 发现文件编码 + reloading 标记（纯函数）
//   BridgeEndpointFile    发现文件的原子写 / 标记 / 删除
//   BridgeServer          TCP 监听与广播（不引用主线程限定 API）
//   BridgeClientConnection 单客户端连接
//   BridgeMainThread      后台线程 → 主线程的唯一通道
//   BridgeLog / BridgeJson / BridgeContracts  横切与协议常量
// ─────────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// Unity 侧桥接探针。<c>[InitializeOnLoad]</c> 静态类，**不是** MonoBehaviour。
    /// </summary>
    /// <remarks>
    /// 生命周期（全部经真机验证）：
    /// <list type="bullet">
    /// <item><b>启动</b>：静态构造函数（主线程）绑定线程身份、预热缓存、订阅事件、按开关启动监听；</item>
    /// <item><b>域重载前</b>：<c>Stop()</c>+<c>Join</c> 主动释放端口（唯一能立即回收端口的手段），
    ///       发现文件保留并标 <c>reloading</c>；</item>
    /// <item><b>重载后</b>：静态构造函数再次运行，优先复用上次端口重绑，并重写发现文件续命；</item>
    /// <item><b>退出</b>：<c>quitting</c> 里删发现文件 —— 这是**唯一**删除它的时机。</item>
    /// </list>
    /// 刻意不做的事：<c>assemblyCompilationStarted</c> 里**不动任何文件**。
    /// 它早于 <c>beforeAssemblyReload</c>，且这次编译可能根本不触发重载
    /// （<c>assemblyCompilationNotRequired</c>）；提前删文件会让读端误判 Unity 已退出。
    /// </remarks>
    [InitializeOnLoad]
    internal static class ShaderLspBridge
    {
        private static BridgeServer s_server;
        private static BridgeContextSnoop s_snoop;
        private static string s_startError;
        private static long s_broadcastCount;

        /// <summary>历史累计客户端数（不含当前会话；当前会话的计数由服务端自己持有）。</summary>
        private static int s_clientsServedBaseline;

        static ShaderLspBridge()
        {
            // 静态构造函数由 Unity 在**主线程**上跑：先钉住线程身份，
            // 之后 BridgeMainThread.Post 的「已在主线程则短路」才有判据。
            // 这一步必须在批处理早退**之前**（否则无头环境下主线程身份永远捕获不到）。
            BridgeMainThread.BindMainThread();

            if (Application.isBatchMode)
            {
                // 批处理（CI / 无头构建）下没有交互式编辑，不占端口。
                return;
            }

            // 整体护栏：`[InitializeOnLoad]` 的静态构造函数抛异常时，Unity 会打一条
            // **指向 ProcessInitializeOnLoadAttributes 的难读堆栈** —— 那段堆栈会长得像
            // 「Locus/Unity 自身坏了」，把排障引到完全错误的方向。因此这里把整段兜住：
            // 桥接是**可选旁路**，它初始化失败绝不能影响诊断主链路，也不能拖累同级
            // 的其它 [InitializeOnLoad] 类。
            try
            {
                Initialize();
            }
            catch (Exception exception)
            {
                BridgeLog.Error("桥接初始化失败（已降级为不启用；诊断与其它编辑器功能不受影响）：" + exception);
            }
        }

        /// <summary>真正的初始化步骤（已在 <see cref="ShaderLspBridge"/> 的静态构造函数里被兜住）。</summary>
        private static void Initialize()
        {
            s_broadcastCount = BridgeSettings.BroadcastCount;
            s_clientsServedBaseline = BridgeSettings.ClientsServed;

            // 预热后台线程可读的缓存（唯一一次 EditorPrefs 读取）。
            BridgeSettings.PrimeCache();

            // 订阅必须在「是否启用」判定**之前**：否则「先禁用、后用菜单启用」会永远收不到 tick。
            EditorApplication.update += OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnQuitting;
            CompilationPipeline.assemblyCompilationStarted += OnAssemblyCompilationStarted;

            if (BridgeSettings.Enabled)
            {
                Start();
            }
        }

        // ── 对外状态（菜单与排障读取）────────────────────────────────────────

        /// <summary>桥接是否正在监听。</summary>
        public static bool IsRunning => s_server != null && s_server.IsListening;

        /// <summary>当前监听端口；未监听时为 0。</summary>
        public static int Port => s_server != null ? s_server.Port : 0;

        /// <summary>当前在线客户端数。</summary>
        public static int ClientCount => s_server != null ? s_server.ClientCount : 0;

        /// <summary>累计接入过的客户端数（含跨域重载的历史）。</summary>
        public static int ClientsServed => s_clientsServedBaseline + (s_server != null ? s_server.ClientsServed : 0);

        /// <summary>累计广播次数（含跨域重载的历史）。</summary>
        public static long BroadcastCount => s_broadcastCount;

        /// <summary>最近一次生成的上报载荷；从未生成过时为 null。</summary>
        public static string CurrentPayload => s_snoop != null ? s_snoop.Payload : null;

        /// <summary>启动失败原因；正常时为 null。</summary>
        public static string StartError => s_startError;

        /// <summary>发现文件的绝对路径。</summary>
        public static string EndpointFilePath
            => Path.Combine(ProjectRoot, "Library", BridgeContracts.DiscoveryDirectoryName, BridgeContracts.DiscoveryFileName);

        /// <summary>Unity 工程根（<c>Application.dataPath</c> 的父目录）。</summary>
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        /// <summary>
        /// 一份人读的全量状态报告。
        /// </summary>
        /// <remarks>
        /// **刻意做成公开方法**：菜单（<see cref="ShaderLspBridgeMenu"/>）与 Agent 排障共用同一实现。
        /// 若只让菜单能看，Agent 就只能靠反射逐个字段拼装，而反射拼装会在**字段改名后静默失败**
        /// （不是报错，是拿到 null）—— 那正是最难归因的一类故障。
        /// Agent 侧只需一行：<c>MicroShader.UnityBridge.ShaderLspBridge.DescribeStatus()</c>。
        /// </remarks>
        public static string DescribeStatus()
        {
            var builder = new StringBuilder();
            builder.AppendLine("Unity 桥接状态");
            builder.AppendLine("  启用：" + BridgeSettings.Enabled + "（菜单 Toggle 切换；EditorPrefs，不进 git，每位开发者独立）");
            builder.AppendLine("  运行：" + IsRunning + "，端口：" + (Port == 0 ? "<未监听>" : Port.ToString()));
            builder.AppendLine("  客户端：在线 " + ClientCount + "，累计接入 " + ClientsServed);
            builder.AppendLine("  广播次数：" + BroadcastCount + "（跨域重载累计）");
            builder.AppendLine("  详细日志：" + BridgeSettings.Verbose + "（关闭时只记状态跃迁）");
            builder.AppendLine("  主线程身份已捕获：" + BridgeMainThread.IsBound);
            builder.AppendLine("  批处理模式：" + Application.isBatchMode + "（批处理下桥接刻意不启动）");
            builder.AppendLine("  发现文件：" + EndpointFilePath);

            var startError = StartError;
            if (!string.IsNullOrEmpty(startError))
            {
                builder.AppendLine("  启动错误：" + startError);
            }

            var payload = CurrentPayload;
            builder.Append("  载荷：").Append(payload ?? "<尚未生成>");
            return builder.ToString();
        }

        // ── 启停 ─────────────────────────────────────────────────────────────

        /// <summary>启动（或重启）监听并发布发现文件。重复调用等价于重启。</summary>
        public static void Start()
        {
            BridgeMainThread.AssertMainThread(nameof(Start));

            Stop(deleteEndpointFile: false);

            var server = new BridgeServer(() => BridgeMainThread.Post(ForceBroadcast));

            if (!server.Start(ResolvePreferredPort(), out var error))
            {
                s_startError = error;
                BridgeLog.Error("桥接监听启动失败：" + error);
                server.Dispose();
                return;
            }

            s_server = server;
            s_startError = null;
            s_snoop = new BridgeContextSnoop();

            BridgeSettings.LastPort = server.Port;

            // 首帧：立刻写一份发现文件 + 算一次载荷，新客户端接上就有快照。
            s_snoop.Sample(EditorApplication.timeSinceStartup, hasConsumers: true, out _);
            PublishSnapshot(writeEndpoint: true, broadcastPayload: true, countBroadcast: false);

            BridgeLog.Info("桥接已监听 " + BridgeContracts.LoopbackAddress + ":" + server.Port
                + "（transport=" + BridgeContracts.Transport + "，动态端口），发现文件：" + EndpointFilePath);
        }

        /// <summary>停止监听。<paramref name="deleteEndpointFile"/> 只在编辑器退出时为 true。</summary>
        public static void Stop(bool deleteEndpointFile)
        {
            BridgeMainThread.AssertMainThread(nameof(Stop));

            var server = s_server;
            s_server = null;
            s_snoop = null;

            if (server != null)
            {
                // Stop + Join 是唯一被实测证明能立即回收端口的手段（详设 v2.0 第 4 条）。
                server.Stop(TimeSpan.FromSeconds(2));
                server.Dispose();
            }

            if (deleteEndpointFile)
            {
                BridgeEndpointFile.Delete(EndpointFilePath);
            }
        }

        /// <summary>重启（菜单与启用开关使用）。</summary>
        public static void Restart()
        {
            BridgeMainThread.AssertMainThread(nameof(Restart));

            if (!BridgeSettings.Enabled || Application.isBatchMode)
            {
                BridgeLog.Info("桥接当前为禁用或无头模式，未启动。");
                return;
            }

            Start();
        }

        /// <summary>
        /// 手动触发一次「重算 + 重播」。
        /// </summary>
        /// <remarks>
        /// 忽略节流：手动请求必须立即生效（节流是给 10Hz tick 用的）。
        /// 也用于响应客户端的 <c>refresh</c> 指令（经 <see cref="BridgeMainThread"/> 回到主线程）。
        /// </remarks>
        public static void ForceBroadcast()
        {
            BridgeMainThread.AssertMainThread(nameof(ForceBroadcast));

            if (s_snoop == null)
            {
                return;
            }

            s_snoop.Sample(EditorApplication.timeSinceStartup, hasConsumers: true, out _);
            PublishSnapshot(writeEndpoint: true, broadcastPayload: true, countBroadcast: true);
        }

        // ── 编辑器事件 ───────────────────────────────────────────────────────

        private static void OnUpdate()
        {
            // 无锁快路径：队列为空时不取锁（本回调实测约 295 次/秒）。
            if (BridgeMainThread.HasPendingWork)
            {
                BridgeMainThread.Drain();
            }

            var snoop = s_snoop;
            var server = s_server;
            if (snoop == null || server == null)
            {
                return;
            }

            // 节流（详设 v2.0 第 10 条：空闲下 update 实测约 295 次/秒）：
            // 有人在线 → 10Hz；无人在线 → 0.5Hz（此时结果只能用来刷发现文件）。
            var hasConsumers = server.HasClients;
            if (!snoop.ShouldSample(EditorApplication.timeSinceStartup, hasConsumers))
            {
                return;
            }

            var changed = snoop.Sample(EditorApplication.timeSinceStartup, hasConsumers, out var environmentChanged);

            if (changed)
            {
                PublishSnapshot(writeEndpoint: true, broadcastPayload: true, countBroadcast: true);
            }
            else if (environmentChanged)
            {
                // 载荷没变、只有环境快照变了（例如切了构建目标）：只刷发现文件，不发帧。
                PublishSnapshot(writeEndpoint: true, broadcastPayload: false, countBroadcast: false);
            }
        }

        private static void OnAssemblyCompilationStarted(string assemblyPath)
        {
            // 编译边界**只留痕**：不在这里删/改发现文件（见类注释）。
            BridgeLog.InfoVerbose("程序集编译开始：" + Path.GetFileName(assemblyPath)
                + "（桥接暂不动作，等 beforeAssemblyReload 定夺）");
        }

        private static void OnBeforeAssemblyReload()
        {
            // 域重载即将发生：主动 Stop() 立即释放端口，发现文件保留并标记 reloading，
            // 重载后由静态构造函数重写续命。跨重载统计经 SessionState 带过去。
            var server = s_server;
            if (server == null)
            {
                return;
            }

            // 统计量必须在把 s_server 置空**之前**取：ClientsServed 依赖当前会话的服务端计数。
            var clientsServedTotal = s_clientsServedBaseline + server.ClientsServed;

            server.Stop(TimeSpan.FromSeconds(2));
            server.Dispose();
            s_server = null;
            s_snoop = null;

            BridgeSettings.BroadcastCount = s_broadcastCount;
            BridgeSettings.ClientsServed = clientsServedTotal;

            BridgeEndpointFile.MarkReloading(EndpointFilePath);
        }

        private static void OnQuitting()
        {
            // 唯一删除发现文件的位置（ADR-016 v2.0：域重载路径不删）。
            Stop(deleteEndpointFile: true);

            EditorApplication.update -= OnUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.quitting -= OnQuitting;
            CompilationPipeline.assemblyCompilationStarted -= OnAssemblyCompilationStarted;
        }

        // ── 发布 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 把当前采样结果发布出去。<b>必须在主线程调用</b>。
        /// </summary>
        /// <param name="writeEndpoint">是否重写发现文件。</param>
        /// <param name="broadcastPayload">是否广播载荷帧。</param>
        /// <param name="countBroadcast">是否计入广播统计（首帧与环境变更不算）。</param>
        private static void PublishSnapshot(bool writeEndpoint, bool broadcastPayload, bool countBroadcast)
        {
            BridgeMainThread.AssertMainThread(nameof(PublishSnapshot));

            var server = s_server;
            var snoop = s_snoop;
            if (server == null || snoop == null)
            {
                return;
            }

            if (writeEndpoint)
            {
                var facts = BridgeEndpointFacts.Capture(
                    server.Port,
                    reloading: false,
                    ProjectRoot,
                    snoop.Environment);

                BridgeEndpointFile.Write(EndpointFilePath, BridgeEndpointDocument.Encode(facts));
            }

            if (!broadcastPayload)
            {
                return;
            }

            var payload = snoop.Payload;
            if (string.IsNullOrEmpty(payload))
            {
                return;
            }

            server.Broadcast(BridgePayloadCodec.ToWireFrame(payload));

            if (countBroadcast)
            {
                s_broadcastCount++;
                BridgeSettings.BroadcastCount = s_broadcastCount;
            }
        }

        /// <summary>
        /// 解析本次要绑定的端口。
        /// </summary>
        /// <remarks>
        /// 固定端口优先；否则优先**复用上次端口**（域重载后 Stop 已把端口交还，
        /// 复用可让读端平滑重连），绑不上时由 <see cref="BridgeServer.Start"/> 自动回落动态分配。
        /// </remarks>
        private static int ResolvePreferredPort()
        {
            var configured = BridgeSettings.PreferredPort;
            return configured > 0 ? configured : BridgeSettings.LastPort;
        }
    }
}
#endif
