// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 主线程边界
//
// 后台线程 → Unity API 的**唯一合法通道**。
// 桥接的传输层（监听/发送/接收）全在后台线程，它们需要改 Unity 侧状态时，
// 一律 Post 到这里，由 EditorApplication.update 在主线程排水。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 主线程排水泵：<c>EditorApplication.update</c> + <c>lock(Queue&lt;Action&gt;)</c>。
    /// </summary>
    /// <remarks>
    /// 写法照抄 JetBrains Rider 的 Unity 插件范式，并按详设 v2.0 补三处语义：
    /// <list type="number">
    /// <item><b>入队短路</b>：若调用方已在主线程则**直接执行**，不让 1:1 请求平白多等一帧；</item>
    /// <item><b>主线程身份懒捕获</b>：首次排水时再取，避免构造期就去碰 Unity API；</item>
    /// <item><b>自研断言</b>：不引入任何外部断言库，用 <c>Debug.LogError</c> 在开发期抓住
    ///       「后台线程碰 Unity API」的违规 —— 这类违规的失败模式是静默的（见 BridgeLog 的说明）。</item>
    /// </list>
    /// 刻意**不使用** <c>ConcurrentQueue</c>：全仓统一一种写法，避免两种范式混用后
    /// 「到底哪个顺序」说不清。
    /// </remarks>
    internal static class BridgeMainThread
    {
        private static readonly Queue<Action> s_queue = new Queue<Action>();
        private static Thread s_mainThread;
        private static int s_pendingCount;

        /// <summary>是否已经捕获到主线程身份（排障用）。</summary>
        public static bool IsBound => s_mainThread != null;

        /// <summary>
        /// 队列里是否有待处理任务（**无锁快路径**）。
        /// </summary>
        /// <remarks>
        /// 给 <c>EditorApplication.update</c> 用：该回调实测约 295 次/秒，
        /// 而绝大多数 tick 队列是空的 —— 空的时候连 <c>lock</c> 都不应该取。
        /// 计数器用 <see cref="Interlocked"/> 维护，因此读取本身是无锁、无分配、纳秒级的。
        /// 竞态容忍：<see cref="Post"/> 先入队再自增，最坏情况是本帧看不到、下一帧看到 ——
        /// 对一条「人读的状态通道」而言，一帧的延迟无关紧要。
        /// </remarks>
        public static bool HasPendingWork => Volatile.Read(ref s_pendingCount) != 0;

        /// <summary>钉住主线程身份。由 <c>[InitializeOnLoad]</c> 静态构造函数（主线程）调用一次。</summary>
        public static void BindMainThread() => s_mainThread ??= Thread.CurrentThread;

        /// <summary>投递到主线程执行。</summary>
        public static void Post(Action action)
        {
            if (action == null)
            {
                return;
            }

            if (s_mainThread != null && Thread.CurrentThread == s_mainThread)
            {
                Run(action);
                return;
            }

            lock (s_queue)
            {
                s_queue.Enqueue(action);
                Interlocked.Increment(ref s_pendingCount);
            }
        }

        /// <summary>在 <c>EditorApplication.update</c> 里逐个排水；单个回调抛异常不影响后续。</summary>
        public static void Drain()
        {
            s_mainThread ??= Thread.CurrentThread;

            while (true)
            {
                Action pending;
                lock (s_queue)
                {
                    if (s_queue.Count == 0)
                    {
                        return;
                    }

                    pending = s_queue.Dequeue();
                    Interlocked.Decrement(ref s_pendingCount);
                }

                Run(pending);
            }
        }

        /// <summary>开发期抓住「后台线程碰 Unity API」的违规。</summary>
        public static void AssertMainThread(string what)
        {
#if UNITY_ASSERTIONS
            if (s_mainThread != null && Thread.CurrentThread != s_mainThread)
            {
                Debug.LogError(BridgeLogMarker + what + " 被非主线程调用：当前线程 "
                    + Thread.CurrentThread.ManagedThreadId + "，主线程 " + s_mainThread.ManagedThreadId);
            }
#else
            _ = what;
#endif
        }

        private const string BridgeLogMarker = "[MicroShader] 主线程契约违规：";

        private static void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                // 单个任务失败绝不能带走排水泵（否则之后所有跨线程请求都静默失效）。
                Debug.LogError("[MicroShader] 主线程任务异常：" + exception);
            }
        }
    }
}
