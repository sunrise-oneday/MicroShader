// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 持久化状态
//
// EditorPrefs（跨会话）与 SessionState（跨域重载）的**唯一入口**。
// 集中在这里不是为了好看，而是一条硬约束：这两个 API 都是主线程限定的，
// 「哪个值可以被后台线程读」必须只有一个答案（见 BridgeLog.Verbose）。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEditor;
using UnityEngine;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 桥接的持久化状态：编辑器偏好（跨会话）+ 会话状态（跨域重载）。
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>EditorPrefs</b>：跨会话保存，但**不进 git** —— 团队里每个人的开关状态独立；</item>
    /// <item><b>SessionState</b>：域重载后仍存活（<c>static</c> 字段不会），用来把端口与统计量带过重载边界。</item>
    /// </list>
    /// ⚠ 本类除 <see cref="VerboseCached"/> 外**全部成员都必须在主线程调用**。
    /// </remarks>
    internal static class BridgeSettings
    {
        private const string PrefEnabled = "MicroShader.Bridge.Enabled";
        private const string PrefPort = "MicroShader.Bridge.Port";
        private const string PrefVerbose = "MicroShader.Bridge.Verbose";

        private const string SessionLastPort = "MicroShader.Bridge.LastPort";
        private const string SessionBroadcasts = "MicroShader.Bridge.Broadcasts";
        private const string SessionClientsServed = "MicroShader.Bridge.ClientsServed";

        /// <summary>是否启用桥接（默认启用）。主线程限定。</summary>
        public static bool Enabled
        {
            get => EditorPrefs.GetBool(PrefEnabled, true);
            set => EditorPrefs.SetBool(PrefEnabled, value);
        }

        /// <summary>固定端口；0 = 动态端口（默认）。主线程限定。</summary>
        public static int PreferredPort
        {
            get => EditorPrefs.GetInt(PrefPort, 0);
            set => EditorPrefs.SetInt(PrefPort, Mathf.Clamp(value, 0, 65535));
        }

        /// <summary>详细日志开关（EditorPrefs 直通）。主线程限定；写入时同步刷新后台可读缓存。</summary>
        public static bool Verbose
        {
            get => EditorPrefs.GetBool(PrefVerbose, false);
            set
            {
                EditorPrefs.SetBool(PrefVerbose, value);
                BridgeLog.Verbose = value;
            }
        }

        /// <summary>后台线程可读的详细日志开关（就是 <see cref="BridgeLog.Verbose"/>）。</summary>
        public static bool VerboseCached => BridgeLog.Verbose;

        /// <summary>把 EditorPrefs 读进缓存。**必须在主线程、且在桥接启动之前**调用一次。</summary>
        public static void PrimeCache() => BridgeLog.Verbose = EditorPrefs.GetBool(PrefVerbose, false);

        /// <summary>上次成功监听的端口（域重载后优先复用，好让读端平滑重连）。</summary>
        public static int LastPort
        {
            get => SessionState.GetInt(SessionLastPort, 0);
            set => SessionState.SetInt(SessionLastPort, value);
        }

        /// <summary>历史累计广播次数（绝对量，主线程每次真实广播即刷新）。</summary>
        public static long BroadcastCount
        {
            get => SessionState.GetInt(SessionBroadcasts, 0);
            set => SessionState.SetInt(SessionBroadcasts, ClampToInt(value));
        }

        /// <summary>历史累计接入过的客户端数（**不含当前会话**，当前会话的计数由服务端自己持有）。</summary>
        public static int ClientsServed
        {
            get => SessionState.GetInt(SessionClientsServed, 0);
            set => SessionState.SetInt(SessionClientsServed, value);
        }

        private static int ClampToInt(long value) => value > int.MaxValue ? int.MaxValue : (int)value;
    }
}
