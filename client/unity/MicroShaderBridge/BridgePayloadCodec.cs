// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 契约 2 编码器（广播载荷）
//
// 输入是三个字符串集合，输出是**一帧**：单行 JSON + 换行符（LDJSON）。
// 纯函数，不碰 Unity API、不做 IO —— 因此它可以在任意线程被调用。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using System.Text;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 契约 2（<c>onShaderContextChanged</c>）的编码。
    /// </summary>
    /// <remarks>
    /// 载荷形状（与《工具整体设计文档》契约 2 逐字对应）：
    /// <code>
    /// {"event":"onShaderContextChanged","timestamp":…,
    ///  "data":{"activeKeywords":[…],
    ///          "globalDefines":{"defined":[…],"undefined":[…]}}}
    /// </code>
    /// 时间戳取 Unix 秒（UTC）：读端只拿它做展示与「谁更新」的粗判，
    /// **不参与内容等价判定** —— 那会让每次心跳都推进注册表版本、把诊断缓存整片打掉。
    /// </remarks>
    internal static class BridgePayloadCodec
    {
        /// <summary>每次编码预留的容量，避免典型载荷触发扩容。</summary>
        private const int InitialCapacity = 384;

        /// <summary>把一次上下文快照编码成载荷文本（不含换行）。</summary>
        public static string Encode(
            IReadOnlyList<string> activeKeywords,
            IReadOnlyList<string> definedMacros,
            IReadOnlyList<string> undefinedMacros,
            long timestampSeconds)
        {
            var builder = new StringBuilder(InitialCapacity);

            builder.Append("{\"event\":");
            BridgeJson.AppendString(builder, BridgeContracts.ContextChangedEvent);

            builder.Append(",\"timestamp\":");
            BridgeJson.AppendNumber(builder, timestampSeconds);

            builder.Append(",\"data\":{\"activeKeywords\":");
            BridgeJson.AppendStringArray(builder, activeKeywords);

            builder.Append(",\"globalDefines\":{\"defined\":");
            BridgeJson.AppendStringArray(builder, definedMacros);

            builder.Append(",\"undefined\":");
            BridgeJson.AppendStringArray(builder, undefinedMacros);

            builder.Append("}}}");
            return builder.ToString();
        }

        /// <summary>
        /// 把载荷文本封装成线格式：UTF-8 字节 + 结尾换行。
        /// </summary>
        /// <remarks>
        /// 协议是 LDJSON，一帧一行。换行必须由**发送方**补，读端按 '\n' 分帧；
        /// 缺了它读端会一直等下一行（表现为「连上了但收不到」）。
        /// </remarks>
        public static byte[] ToWireFrame(string payload)
        {
            if (string.IsNullOrEmpty(payload))
            {
                return Array.Empty<byte>();
            }

            return Encoding.UTF8.GetBytes(payload + "\n");
        }

        /// <summary>当前 Unix 秒（UTC）。</summary>
        public static long NowUnixSeconds()
            => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
