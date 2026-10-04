// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · JSON 编码基元
//
// 只有两件事：字符串转义、数组拼接。被契约 1（发现文件）与契约 2（广播载荷）
// 两个编码器共用 —— 转义规则只有一处实现，就不可能两个协议各错一半。
// ─────────────────────────────────────────────────────────────────────────────

using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 手工 JSON 编码基元（Unity 侧没有 System.Text.Json，也不允许引第三方依赖）。
    /// </summary>
    internal static class BridgeJson
    {
        /// <summary>按 RFC 8259 写一个 JSON 字符串（含引号）。控制字符转成 <c>\uXXXX</c>。</summary>
        public static void AppendString(StringBuilder builder, string value)
        {
            builder.Append('"');

            if (value != null)
            {
                for (var i = 0; i < value.Length; i++)
                {
                    var ch = value[i];
                    switch (ch)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (ch < 0x20)
                            {
                                builder.Append("\\u");
                                builder.Append(((int)ch).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                builder.Append(ch);
                            }

                            break;
                    }
                }
            }

            builder.Append('"');
        }

        /// <summary>写一个字符串数组。空集合写成 <c>[]</c>（不是 <c>null</c> —— 读端按数组解析）。</summary>
        public static void AppendStringArray(StringBuilder builder, IReadOnlyList<string> values)
        {
            builder.Append('[');

            if (values != null)
            {
                for (var i = 0; i < values.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    AppendString(builder, values[i]);
                }
            }

            builder.Append(']');
        }

        /// <summary>写一个字面量整数（用于 pid / port / 时间戳）。</summary>
        public static void AppendNumber(StringBuilder builder, long value)
            => builder.Append(value.ToString(CultureInfo.InvariantCulture));
    }
}
