// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 日志出口
//
// 唯一持有「日志级别」的地方。之所以单独成类而不是直接用 Debug.Log：
//   1. 后台线程（监听 / 发送 / 客户端读取）也要能安全地打日志 —— 见下方 Verbose 的说明；
//   2. 统一前缀，用户可以在 Console 里一眼过滤出桥接的全部输出。
// ─────────────────────────────────────────────────────────────────────────────

using UnityEngine;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 桥接的日志出口。
    /// </summary>
    /// <remarks>
    /// <b>为什么后台线程可以安全调用本类</b>：<c>UnityEngine.Debug.Log</c> 是线程安全的，
    /// 与 <c>EditorPrefs</c> / <c>PlayerSettings</c> 这类**主线程限定** API 不同。
    /// 因此详细日志开关被**缓存**在这里的一个普通布尔字段上，而不是每次去问 EditorPrefs：
    /// 后者在后台线程会抛 <c>UnityException: GetBool can only be called from the main thread</c>，
    /// 而该异常曾在监听线程上逃出 <c>AcceptLoop</c> 把监听线程打死 ——
    /// 症状是「TCP 连得上（内核完成握手）但一帧都收不到」，极难归因。
    /// </remarks>
    internal static class BridgeLog
    {
        private const string Prefix = "[MicroShader] ";

        /// <summary>
        /// 详细日志开关。**允许任意线程读写**（缓存值，不是 EditorPrefs 直通）。
        /// </summary>
        /// <remarks>
        /// 由 <see cref="BridgeSettings"/> 在切换时写入、并在静态构造函数里预热。
        /// 后台线程只读、主线程只写。
        /// </remarks>
        public static volatile bool Verbose;

        public static void Info(string message) => Debug.Log(Prefix + message);

        public static void Warn(string message) => Debug.LogWarning(Prefix + message);

        public static void Error(string message) => Debug.LogError(Prefix + message);

        /// <summary>只在详细模式下输出。用于「只在状态跃迁时记日志」之外的额外线索。</summary>
        public static void InfoVerbose(string message)
        {
            if (Verbose)
            {
                Debug.Log(Prefix + message);
            }
        }
    }
}
