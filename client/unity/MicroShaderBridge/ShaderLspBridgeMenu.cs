// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 菜单（展示层）
//
// 与 ShaderLspBridge 分开的理由：那边是「生命周期与编排」，这边是「给人看的」。
// 两者的变更理由完全不同 —— 改一次菜单文案不应该碰到生命周期代码。
// 本文件只读状态、调公开入口，不含任何协议或传输逻辑。
// ─────────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
using System.IO;
using UnityEditor;

namespace MicroShader.UnityBridge
{
    /// <summary>桥接的编辑器菜单（<c>Tools/MicroShader/Unity Bridge</c>）。</summary>
    /// <remarks>
    /// 只做展示：状态文本来自 <see cref="ShaderLspBridge.DescribeStatus"/>，
    /// 开关落到 <see cref="BridgeSettings"/>，动作调 <see cref="ShaderLspBridge"/> 的公开入口。
    /// 本类不持有任何状态，也不含协议或传输逻辑。
    /// </remarks>
    internal static class ShaderLspBridgeMenu
    {
        private const string Root = "Tools/MicroShader/Unity Bridge/";

        [MenuItem(Root + "状态", priority = 1)]
        private static void Status() => BridgeLog.Info(ShaderLspBridge.DescribeStatus());

        [MenuItem(Root + "启用/禁用", priority = 2)]
        private static void ToggleEnabled()
        {
            BridgeSettings.Enabled = !BridgeSettings.Enabled;
            BridgeLog.Info("桥接已" + (BridgeSettings.Enabled ? "启用" : "禁用"));

            if (BridgeSettings.Enabled)
            {
                ShaderLspBridge.Restart();
            }
            else
            {
                // 关闭时顺手删掉发现文件：否则读端会一直试图连一个已经没人监听的端口。
                ShaderLspBridge.Stop(deleteEndpointFile: true);
            }
        }

        [MenuItem(Root + "重启监听", priority = 3)]
        private static void Restart() => ShaderLspBridge.Restart();

        [MenuItem(Root + "立即广播", priority = 4)]
        private static void ForceBroadcast() => ShaderLspBridge.ForceBroadcast();

        [MenuItem(Root + "打开发现文件", priority = 5)]
        private static void RevealEndpointFile()
        {
            var path = ShaderLspBridge.EndpointFilePath;
            if (File.Exists(path))
            {
                EditorUtility.RevealInFinder(path);
            }
            else
            {
                BridgeLog.Warn("发现文件尚不存在：" + path);
            }
        }

        [MenuItem(Root + "详细日志开关", priority = 6)]
        private static void ToggleVerbose()
        {
            BridgeSettings.Verbose = !BridgeSettings.Verbose;
            BridgeLog.Info("详细日志已" + (BridgeSettings.Verbose ? "开启" : "关闭"));
        }
    }
}
#endif
