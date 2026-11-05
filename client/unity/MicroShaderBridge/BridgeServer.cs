// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · TCP 广播服务端
//
// 拓扑：Unity 当服务端，外部语言服务端主动外连（ADR-016 的默认路线）。
// 绑 127.0.0.1 的**动态端口**（bind port 0），实际端口由调用方写进发现文件。
//
// 本类**不引用任何主线程限定的 Unity API**：它跑在三条后台线程上，
// 唯一的日志出口是线程安全的 BridgeLog。这一点是刻意的 —— 见 BridgeLog 的说明，
// 历史上正是「后台线程读 EditorPrefs」把监视线程整条打死的。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 极简 TCP 广播服务端。
    /// </summary>
    /// <remarks>
    /// 线程预算：1 监听 + 1 发送 + N 读取（N ≤ <see cref="BridgeContracts.MaxClients"/>）。
    /// 全部网络 I/O 都在后台线程；主线程只做一次 <see cref="Broadcast"/> 入队。
    /// 设计要点（每条都有实测代价）：
    /// <list type="bullet">
    /// <item><b>监视线程不许死</b>：非关闭原因异常只记日志 + 继续。线程死了端口仍由内核
    ///       完成握手，表现是「连得上但一帧都收不到」，极难归因。</item>
    /// <item><b>只入队、不直写</b>：主线程绝不碰套接字，避免一个慢客户端把编辑器卡住。</item>
    /// <item><b>满队列丢最旧</b>：对端只需要最新状态，历史帧没有价值。</item>
    /// <item><b>新客户端先给快照</b>：接入即把最后一帧推过去，读端不必等到下次变化才有数据。</item>
    /// <item><b>Stop 必须能立即回收端口</b>：只依赖域卸载是不够的（句柄不释放），
    ///       必须显式 <c>Stop()</c> 并等待线程退出。</item>
    /// </list>
    /// </remarks>
    internal sealed class BridgeServer : IDisposable
    {
        private readonly object _clientsGate = new object();
        private readonly object _outboxGate = new object();
        private readonly List<BridgeClientConnection> _clients = new List<BridgeClientConnection>(BridgeContracts.MaxClients);
        private readonly Queue<byte[]> _outbox = new Queue<byte[]>(BridgeContracts.OutboxCapacity);
        private readonly Action _onRefreshRequested;

        private TcpListener _listener;
        private Thread _acceptThread;
        private Thread _sendThread;
        private volatile bool _stopping;
        private volatile byte[] _lastFrame;
        private int _droppedFrames;
        private int _clientsServed;

        /// <summary>
        /// 在线客户端数的**无锁镜像**，仅供主线程做「有没有人看」的快速判定。
        /// </summary>
        /// <remarks>
        /// 不能直接让主线程读 <see cref="ClientCount"/>：那里要取 <c>_clientsGate</c>，
        /// 而调用点在 <c>EditorApplication.update</c>（实测约 295 次/秒）。
        /// ⚠ <b>语义提醒</b>：镜像只在锁内更新，因此对「对端已断开但读线程尚未收到 EOF」的连接
        /// 它会**暂时偏高**（本机实测：外部脚本断开后仍会显示「在线 1」直到下一次广播触发兜底回收）。
        /// 这对「要不要降频采样」这个判断是安全的（偏高只会多采样一点），
        /// 但**不能拿它当「连接质量」的判据** —— 那要看 <c>_clientsServed</c> 是否在增长，
        /// 以及发送循环的兜底回收是否把它清下去。持续偏高不降才是槽位泄漏。
        /// </remarks>
        private int _clientCountMirror;

        /// <param name="onRefreshRequested">
        /// 收到客户端 <c>refresh</c> 指令时的回调。**在后台线程上被调用** ——
        /// 调用方负责把它送回主线程（见 <see cref="BridgeMainThread"/>）。
        /// </param>
        public BridgeServer(Action onRefreshRequested)
        {
            _onRefreshRequested = onRefreshRequested ?? (() => { });
        }

        /// <summary>实际监听端口；未监听时为 0。</summary>
        public int Port { get; private set; }

        /// <summary>是否正在监听。</summary>
        public bool IsListening => _listener != null;

        /// <summary>当前在线客户端数（取锁，供菜单/日志等低频路径使用）。</summary>
        public int ClientCount
        {
            get
            {
                lock (_clientsGate)
                {
                    return _clients.Count;
                }
            }
        }

        /// <summary>当前是否有人在线（无锁快路径，供 <c>EditorApplication.update</c> 使用）。</summary>
        public bool HasClients => Volatile.Read(ref _clientCountMirror) > 0;

        /// <summary>本次会话累计接入过的客户端数。</summary>
        public int ClientsServed => _clientsServed;

        /// <summary>因积压被丢弃的帧数（稳态应当恒为 0）。</summary>
        public int DroppedFrames => _droppedFrames;

        /// <summary>
        /// 启动监听。
        /// </summary>
        /// <param name="preferredPort">期望端口；0 = 动态分配。被占用时**回落动态分配**。</param>
        /// <param name="error">失败原因（人读）。</param>
        public bool Start(int preferredPort, out string error)
        {
            error = string.Empty;

            try
            {
                _listener = Bind(preferredPort);
            }
            catch (SocketException first)
            {
                if (preferredPort == 0)
                {
                    error = "动态端口绑定失败：" + first.Message;
                    _listener = null;
                    return false;
                }

                // 固定端口被占：回落动态分配（ADR-016 v2.0：固定端口必须自带退避与降级）。
                try
                {
                    _listener = Bind(0);
                }
                catch (SocketException second)
                {
                    error = "固定端口 " + preferredPort + " 与动态端口均绑定失败：" + second.Message;
                    _listener = null;
                    return false;
                }
            }
            catch (Exception unexpected)
            {
                error = unexpected.Message;
                _listener = null;
                return false;
            }

            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "MicroShader.Bridge.Accept",
            };
            _acceptThread.Start();

            _sendThread = new Thread(SendLoop)
            {
                IsBackground = true,
                Name = "MicroShader.Bridge.Send",
            };
            _sendThread.Start();

            return true;
        }

        /// <summary>
        /// 入队一帧（主线程调用，绝不在此做网络 I/O）。
        /// </summary>
        /// <remarks>
        /// 没有客户端时直接返回：新客户端接上会立刻收到 <c>_lastFrame</c> 快照，
        /// 因此排队纯属浪费。
        /// </remarks>
        public void Broadcast(byte[] frame)
        {
            if (frame == null || frame.Length == 0)
            {
                return;
            }

            _lastFrame = frame;

            lock (_clientsGate)
            {
                if (_clients.Count == 0)
                {
                    return;
                }
            }

            lock (_outboxGate)
            {
                if (_outbox.Count >= BridgeContracts.OutboxCapacity)
                {
                    _outbox.Dequeue();
                    _droppedFrames++;
                }

                _outbox.Enqueue(frame);
                Monitor.Pulse(_outboxGate);
            }
        }

        /// <summary>
        /// 停止监听并**同步等待**线程退出。
        /// </summary>
        /// <remarks>
        /// <c>Thread.Abort</c> 能中断阻塞中的 <c>Accept</c>（实测 2 ms），但**不释放套接字句柄**：
        /// 端口仍被占用，紧接着重绑会报 <c>AddressAlreadyInUse</c>。
        /// 因此「显式 Stop + Join」是唯一被实测证明能立即回收端口的手段（实测耗时 20–24 ms）。
        /// </remarks>
        public void Stop(TimeSpan joinTimeout)
        {
            if (_stopping)
            {
                return;
            }

            _stopping = true;

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // 关闭竞争，忽略。
            }

            lock (_clientsGate)
            {
                foreach (var connection in _clients)
                {
                    connection.Close();
                }

                _clients.Clear();
                Volatile.Write(ref _clientCountMirror, 0);
            }

            lock (_outboxGate)
            {
                Monitor.Pulse(_outboxGate);
            }

            JoinQuietly(_acceptThread, joinTimeout);
            JoinQuietly(_sendThread, joinTimeout);
        }

        /// <summary>释放资源（要求已 <see cref="Stop"/>；重复调用安全）。</summary>
        public void Dispose()
        {
            _stopping = true;

            try
            {
                _listener?.Stop();
            }
            catch (Exception)
            {
                // 忽略。
            }

            lock (_clientsGate)
            {
                foreach (var connection in _clients)
                {
                    connection.Close();
                }

                _clients.Clear();
                Volatile.Write(ref _clientCountMirror, 0);
            }

            lock (_outboxGate)
            {
                _outbox.Clear();
                Monitor.Pulse(_outboxGate);
            }

            _listener = null;
        }

        private static TcpListener Bind(int port)
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Server.NoDelay = true;
            listener.Start(backlog: 8);
            return listener;
        }

        private static void JoinQuietly(Thread thread, TimeSpan timeout)
        {
            if (thread == null)
            {
                return;
            }

            try
            {
                if (!thread.Join(timeout))
                {
                    // 后台线程未在时限内退出：不阻塞域重载，只留痕（端口已由 Stop 交还）。
                    BridgeLog.Warn("桥接线程 " + thread.Name + " 未在时限内退出");
                }
            }
            catch (Exception)
            {
                // 忽略。
            }
        }

        private void AcceptLoop()
        {
            var listener = _listener;
            if (listener == null)
            {
                return;
            }

            while (!_stopping)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception exception)
                {
                    if (_stopping)
                    {
                        return;   // Stop()/Dispose() 的正常退出口。
                    }

                    // 非关闭原因导致的失败绝不能让监听线程静默死亡。
                    BridgeLog.Error("桥接监视线程异常（继续监听）：" + exception);
                    Thread.Sleep(250);
                    continue;
                }

                try
                {
                    Register(client);
                }
                catch (Exception exception)
                {
                    // 单个客户端的接入失败（线程配额、套接字选项…）不得把监视线程带走。
                    BridgeLog.Error("桥接客户端接入失败（继续监听）：" + exception);
                    SafeClose(client);
                }
            }
        }

        private void Register(TcpClient client)
        {
            var connection = new BridgeClientConnection(client, _onRefreshRequested, OnConnectionClosed);

            lock (_clientsGate)
            {
                if (_clients.Count >= BridgeContracts.MaxClients)
                {
                    // 明确的「先到者胜」：拒绝新客户端，而不是踢掉可能在工作的读端。
                    connection.Close();
                    BridgeLog.Warn("桥接已达客户端上限（" + BridgeContracts.MaxClients + "），拒绝新连接：" + connection.Remote);
                    return;
                }

                _clients.Add(connection);
                _clientsServed++;
                Volatile.Write(ref _clientCountMirror, _clients.Count);
            }

            new Thread(connection.Pump)
            {
                IsBackground = true,
                Name = "MicroShader.Bridge.Client",
            }.Start();

            // 接入即给快照：读端不必等到下次状态变化才有数据。
            var snapshot = _lastFrame;
            if (snapshot != null)
            {
                lock (_outboxGate)
                {
                    _outbox.Enqueue(snapshot);
                    Monitor.Pulse(_outboxGate);
                }
            }

            BridgeLog.Info("桥接客户端接入（当前 " + ClientCount + " 个）：" + connection.Remote);
        }

        private void OnConnectionClosed(BridgeClientConnection connection)
        {
            var removed = false;

            lock (_clientsGate)
            {
                removed = _clients.Remove(connection);
                Volatile.Write(ref _clientCountMirror, _clients.Count);
            }

            connection.Close();

            if (removed)
            {
                BridgeLog.InfoVerbose("桥接客户端断开：" + connection.Remote);
            }
        }

        private void SendLoop()
        {
            while (!_stopping)
            {
                byte[] frame;
                lock (_outboxGate)
                {
                    if (_outbox.Count == 0)
                    {
                        Monitor.Wait(_outboxGate, 250);
                        if (_outbox.Count == 0)
                        {
                            continue;
                        }
                    }

                    frame = _outbox.Dequeue();
                }

                foreach (var connection in SnapshotUsableConnections())
                {
                    if (!connection.TrySend(frame))
                    {
                        OnConnectionClosed(connection);
                    }
                }
            }
        }

        /// <summary>
        /// 取当前可用连接的快照，并顺手回收「已被对端断开但读线程还没收到 EOF」的槽位。
        /// </summary>
        /// <remarks>
        /// 兜底扫描是必要的：槽位泄漏会永久占用上限配额，表现为「新客户端再也接不进来」，
        /// 而且从客户端一侧看是「连接成功但没数据」—— 与监视线程死亡的症状一模一样。
        /// </remarks>
        private BridgeClientConnection[] SnapshotUsableConnections()
        {
            lock (_clientsGate)
            {
                for (var i = _clients.Count - 1; i >= 0; i--)
                {
                    if (_clients[i].IsUsable)
                    {
                        continue;
                    }

                    var stale = _clients[i];
                    _clients.RemoveAt(i);
                    stale.Close();
                }

                Volatile.Write(ref _clientCountMirror, _clients.Count);
                return _clients.ToArray();
            }
        }

        private static void SafeClose(TcpClient client)
        {
            try
            {
                client.Close();
            }
            catch (Exception)
            {
                // 忽略。
            }
        }
    }
}
