// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 单客户端连接
//
// 一个已接入的读端（语言服务端）连接：收发、断线判定、指令解析。
//
// 为什么单独成类：连接的**全部状态与规则**（读超时不算断开、超长行丢弃、
// 未知指令静默忽略、关闭幂等）都只属于它自己。服务端只管「拒绝/接纳」与「分发」，
// 不需要知道任何一帧是怎么读进来的。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Net.Sockets;
using System.Text;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 一个已接入客户端的连接。
    /// </summary>
    /// <remarks>
    /// 线程模型：<see cref="Pump"/> 在**一条专用后台线程**上阻塞读取；
    /// <see cref="TrySend"/> 由服务端的发送线程调用；<see cref="Close"/> 可从任意线程调用。
    /// 关闭是幂等的（多次调用安全），因为「读线程发现 EOF」与「发送失败」都可能先触发它。
    /// </remarks>
    internal sealed class BridgeClientConnection
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly Action _onRefreshRequested;
        private readonly Action<BridgeClientConnection> _onClosed;

        private volatile bool _closed;

        /// <param name="client">已 accept 的套接字。</param>
        /// <param name="onRefreshRequested">收到 <c>refresh</c> 指令时的回调（由调用方决定怎么回到主线程）。</param>
        /// <param name="onClosed">连接终结时回调一次，供服务端回收槽位。</param>
        public BridgeClientConnection(
            TcpClient client,
            Action onRefreshRequested,
            Action<BridgeClientConnection> onClosed)
        {
            _client = client;
            _onRefreshRequested = onRefreshRequested;
            _onClosed = onClosed;
            _stream = client.GetStream();

            ApplySocketOptions();
        }

        /// <summary>对端地址（排障用）。</summary>
        public string Remote
        {
            get
            {
                try
                {
                    return _client.Client.RemoteEndPoint != null ? _client.Client.RemoteEndPoint.ToString() : "<unknown>";
                }
                catch (Exception)
                {
                    return "<unknown>";
                }
            }
        }

        /// <summary>是否已被标记关闭。</summary>
        public bool IsClosed => _closed;

        /// <summary>槽位是否仍可用（服务端的兜底回收扫描用）。</summary>
        /// <remarks>
        /// 多一层「对端是否已断开」的判定：若只依赖读线程回收，
        /// 一旦某个读线程卡住，槽位会永久占用 <see cref="BridgeContracts.MaxClients"/> 配额，
        /// 表现为「新客户端再也接不进来」。
        /// </remarks>
        public bool IsUsable
        {
            get
            {
                if (_closed)
                {
                    return false;
                }

                try
                {
                    return _client.Connected;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 阻塞读取直到对端关闭或本端被关闭。由服务端在专用后台线程上启动。
        /// </summary>
        /// <remarks>
        /// 读超时（<c>ReceiveTimeout</c>）在这里**不算断开** —— 它只是本轮没有数据，
        /// 对端在空闲时本来就不会说话。若把超时当断开，读端会因为周期性重连而反复丢状态。
        /// </remarks>
        public void Pump()
        {
            var chunk = new byte[256];
            var line = new byte[BridgeContracts.MaxClientCommandBytes];
            var length = 0;

            try
            {
                while (!_closed)
                {
                    int read;
                    try
                    {
                        read = _stream.Read(chunk, 0, chunk.Length);
                    }
                    catch (System.IO.IOException)
                    {
                        continue;
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    for (var i = 0; i < read; i++)
                    {
                        var value = chunk[i];
                        if (value == (byte)'\n')
                        {
                            if (length > 0)
                            {
                                HandleCommand(line, length);
                                length = 0;
                            }

                            continue;
                        }

                        if (value == (byte)'\r')
                        {
                            continue;
                        }

                        if (length >= line.Length)
                        {
                            // 超长行：整行丢弃，不让对端的残渣在内存里无界增长。
                            length = 0;
                            BridgeLog.InfoVerbose("桥接忽略超长客户端指令（上限 "
                                + BridgeContracts.MaxClientCommandBytes + " 字节）");
                            continue;
                        }

                        line[length++] = value;
                    }
                }
            }
            catch (Exception)
            {
                // 断开路径：套接字被本端 Close、或对端异常关闭。
            }
            finally
            {
                _onClosed?.Invoke(this);
            }
        }

        /// <summary>发送一帧。返回 false 表示该连接应被回收。</summary>
        public bool TrySend(byte[] frame)
        {
            if (_closed || frame == null || frame.Length == 0)
            {
                return !_closed;
            }

            try
            {
                _stream.Write(frame, 0, frame.Length);
                _stream.Flush();
                return true;
            }
            catch (Exception)
            {
                // 发送失败/超时：立刻断开，绝不让一个卡死的客户端拖住广播线程。
                _closed = true;
                return false;
            }
        }

        /// <summary>关闭连接（幂等）。</summary>
        /// <remarks>
        /// 必须容忍重复调用：读线程发现 EOF 与发送失败都可能先触发它，
        /// 而两条路径背后是同一个套接字。
        /// </remarks>
        public void Close()
        {
            _closed = true;

            try
            {
                _client.Close();
            }
            catch (Exception)
            {
                // 关闭竞争，忽略。
            }
        }

        /// <summary>
        /// 解析一行客户端指令。
        /// </summary>
        /// <remarks>
        /// 协议本身是 push-only，这是便于排障的可选增强。
        /// **未知指令必须静默忽略**（不是报错）：读端可能来自更新的版本，
        /// 前向兼容是这条通道的唯一生存方式。
        /// </remarks>
        private void HandleCommand(byte[] buffer, int length)
        {
            var text = Encoding.UTF8.GetString(buffer, 0, length).Trim();
            if (text.Length == 0)
            {
                return;
            }

            if (text.IndexOf(BridgeContracts.RefreshCommand, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                BridgeLog.InfoVerbose("收到 refresh 指令：" + Remote);
                _onRefreshRequested?.Invoke();
                return;
            }

            BridgeLog.InfoVerbose("忽略未知客户端指令：" + text);
        }

        private void ApplySocketOptions()
        {
            try
            {
                _client.NoDelay = true;
                _client.Client.SendTimeout = 1000;
                _client.Client.ReceiveTimeout = 500;
            }
            catch (Exception)
            {
                // 套接字选项设置失败不致命：只是少了超时保护。
            }
        }
    }
}
