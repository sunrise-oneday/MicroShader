using System.Buffers;
using System.Text.Json;

namespace MicroShader.CoordinationEngine.Lsp;

/// <summary>
/// JSON-RPC 2.0 报文读取器：单遍 <see cref="Utf8JsonReader"/> 扫过顶层字段，
/// 把 "method" / "id" / "params" 各自"记成原始字节区间"，不做任何反射与动态序列化
/// （Native AOT 下唯一安全的解析姿势；v2.0 修正清单第 2 条）。
/// </summary>
/// <remarks>
/// 解析全程使用 <see cref="ReadOnlySequence{T}"/> 重载，因此帧跨多个管道段时也无需拼接拷贝。
/// </remarks>
public static class LspMessageReader
{
    /// <summary>尝试解析一条报文。返回 "false" 表示字节流不是合法 JSON-RPC。</summary>
    public static bool TryRead(in ReadOnlySequence<byte> payload, out LspEnvelope envelope)
    {
        envelope = default;

        if (payload.Length == 0)
        {
            envelope = new LspEnvelope { Kind = LspMessageKind.Invalid, Malformed = "空报文" };
            return false;
        }

        var method = default(ReadOnlySequence<byte>);
        var id = default(ReadOnlySequence<byte>);
        var parameters = default(ReadOnlySequence<byte>);
        var hasMethod = false;
        var hasId = false;
        var hasParams = false;
        var hasResultOrError = false;

        try
        {
            var reader = new Utf8JsonReader(payload, isFinalBlock: true, state: default);

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                envelope = new LspEnvelope { Kind = LspMessageKind.Invalid, Malformed = "顶层不是 JSON 对象" };
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    envelope = new LspEnvelope { Kind = LspMessageKind.Invalid, Malformed = "期望属性名" };
                    return false;
                }

                var name = reader.GetString();

                if (!reader.Read())
                {
                    envelope = new LspEnvelope { Kind = LspMessageKind.Invalid, Malformed = "属性缺少值" };
                    return false;
                }

                switch (name)
                {
                    case "method":
                        method = Capture(ref reader, payload);
                        hasMethod = true;
                        break;
                    case "id":
                        id = Capture(ref reader, payload);
                        hasId = true;
                        break;
                    case "params":
                        parameters = Capture(ref reader, payload);
                        hasParams = true;
                        break;
                    case "result" or "error":
                        _ = Capture(ref reader, payload);
                        hasResultOrError = true;
                        break;
                    default:
                        // jsonrpc / 未知扩展字段：跳过（宽容处理，不因版本扩展而拒收）。
                        _ = Capture(ref reader, payload);
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            envelope = new LspEnvelope { Kind = LspMessageKind.Invalid, Malformed = "JSON 语法错误: " + ex.Message };
            return false;
        }

        var kind = (hasMethod, hasId, hasResultOrError) switch
        {
            (true, true, _) => LspMessageKind.Request,
            (true, false, _) => LspMessageKind.Notification,
            (false, true, true) => LspMessageKind.Response,
            _ => LspMessageKind.Invalid,
        };

        if (kind == LspMessageKind.Invalid)
        {
            envelope = new LspEnvelope { Kind = LspMessageKind.Invalid, Malformed = "既非请求/通知也非响应" };
            return false;
        }

        envelope = new LspEnvelope
        {
            Kind = kind,
            Method = method,
            Id = id,
            Params = parameters,
            HasId = hasId,
            HasParams = hasParams,
        };
        return true;
    }

    /// <summary>
    /// 取当前 token 的原始字节区间。复合值先 <see cref="Utf8JsonReader.Skip"/> 到值末尾，
    /// 再用 "TokenStartIndex" / "BytesConsumed" 切成区间 —— 两个偏移对
    /// <see cref="ReadOnlySequence{T}"/> 重载都是"绝对"位置，因此跨段也准确。
    /// </summary>
    private static ReadOnlySequence<byte> Capture(ref Utf8JsonReader reader, in ReadOnlySequence<byte> payload)
    {
        var start = reader.TokenStartIndex;

        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();
        }

        var end = reader.BytesConsumed;
        return payload.Slice(start, end - start);
    }
}
