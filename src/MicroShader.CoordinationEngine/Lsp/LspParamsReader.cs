using System.Buffers;
using MicroShader.CoordinationEngine.Scheduling;
using System.Text.Json;

namespace MicroShader.CoordinationEngine.Lsp;

/// <summary>"textDocument/didOpen" 参数。</summary>
public readonly struct DidOpenParams
{
    public required string Uri { get; init; }

    public required string Text { get; init; }

    public required int Version { get; init; }

    public string? LanguageId { get; init; }
}

/// <summary>"textDocument/didChange" 参数（Full 同步下的扁平形态）。</summary>
public readonly struct DidChangeParams
{
    public required string Uri { get; init; }

    public required string Text { get; init; }

    public required int Version { get; init; }
}

/// <summary>"textDocument/didSave" 参数。</summary>
public readonly struct DidSaveParams
{
    public required string Uri { get; init; }

    public int Reason { get; init; }

    /// <summary>客户端是否带了 "reason"。</summary>
    public bool HasReason { get; init; }
}

/// <summary>
/// 文档同步通知的参数读取器（<see cref="Utf8JsonReader"/> 手工游走，零反射、AOT 安全）。
/// </summary>
public static class LspParamsReader
{
    /// <summary>读 "textDocument/didOpen"。</summary>
    public static bool TryReadDidOpen(in ReadOnlySequence<byte> parameters, out DidOpenParams result)
    {
        result = default;
        if (parameters.Length == 0) return false;

        try
        {
            var reader = new Utf8JsonReader(parameters, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            string? uri = null;
            string? text = null;
            string? languageId = null;
            var version = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                var name = reader.GetString();
                if (!reader.Read()) return false;

                if (name == "textDocument" && reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadTextDocument(ref reader, ref uri, ref languageId, ref text, ref version, withText: true);
                }
                else
                {
                    reader.Skip();
                }
            }

            if (uri is null || text is null) return false;

            result = new DidOpenParams
            {
                Uri = uri,
                Text = text,
                Version = version,
                LanguageId = languageId,
            };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>读 "textDocument/didChange"。Full 同步下取 "contentChanges" 最后一个元素的 "text"。</summary>
    public static bool TryReadDidChange(in ReadOnlySequence<byte> parameters, out DidChangeParams result)
    {
        result = default;
        if (parameters.Length == 0) return false;

        try
        {
            var reader = new Utf8JsonReader(parameters, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            string? uri = null;
            string? text = null;
            var version = 0;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                var name = reader.GetString();
                if (!reader.Read()) return false;

                switch (name)
                {
                    case "textDocument" when reader.TokenType == JsonTokenType.StartObject:
                        ReadTextDocumentBasics(ref reader, ref uri, ref version);
                        break;
                    case "contentChanges" when reader.TokenType == JsonTokenType.StartArray:
                        ReadContentChanges(ref reader, ref text);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (uri is null || text is null) return false;

            result = new DidChangeParams { Uri = uri, Text = text, Version = version };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>读 "textDocument/didSave"。</summary>
    public static bool TryReadDidSave(in ReadOnlySequence<byte> parameters, out DidSaveParams result)
    {
        result = default;
        if (parameters.Length == 0) return false;

        try
        {
            var reader = new Utf8JsonReader(parameters, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            string? uri = null;
            var reason = 0;
            var hasReason = false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                var name = reader.GetString();
                if (!reader.Read()) return false;

                switch (name)
                {
                    case "textDocument" when reader.TokenType == JsonTokenType.StartObject:
                        ReadUri(ref reader, ref uri);
                        break;
                    case "reason" when reader.TokenType == JsonTokenType.Number:
                        reason = reader.TryGetInt32(out var r) ? r : 0;
                        hasReason = true;
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            if (uri is null) return false;

            result = new DidSaveParams { Uri = uri, Reason = reason, HasReason = hasReason };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读 initialize 里的导航相关客户端能力。
    /// </summary>
    /// <remarks>
    /// 路径：params.capabilities.textDocument.definition.linkSupport、
    /// ……documentSymbol.hierarchicalDocumentSymbolSupport、……documentSymbol.symbolKind.valueSet。
    /// 用 JsonDocument 而不是单遍 reader：initialize 每会话只来一次，而这段 JSON 嵌套深、字段可选，
    /// 手写单遍状态机极易出错。这与零反射约束不冲突——被禁的是反射式序列化，不是 System.Text.Json 本身。
    /// 缺字段一律当 false 或空集：宁可回保守形状（Location 数组、扁平大纲），也不假设客户端支持增强形状。
    /// </remarks>
    public static bool TryReadNavigationCapabilities(
        in ReadOnlySequence<byte> parameters,
        out ClientNavigationCapabilities capabilities)
    {
        capabilities = ClientNavigationCapabilities.Default;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(parameters);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var linkSupport = false;
            var hierarchical = false;
            var kinds = Array.Empty<int>();

            if (document.RootElement.TryGetProperty("capabilities", out var caps)
                && caps.ValueKind == JsonValueKind.Object
                && caps.TryGetProperty("textDocument", out var textDocument)
                && textDocument.ValueKind == JsonValueKind.Object)
            {
                if (textDocument.TryGetProperty("definition", out var definition)
                    && definition.ValueKind == JsonValueKind.Object
                    && definition.TryGetProperty("linkSupport", out var link)
                    && link.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    linkSupport = link.GetBoolean();
                }

                if (textDocument.TryGetProperty("documentSymbol", out var symbols)
                    && symbols.ValueKind == JsonValueKind.Object)
                {
                    if (symbols.TryGetProperty("hierarchicalDocumentSymbolSupport", out var hier)
                        && hier.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        hierarchical = hier.GetBoolean();
                    }

                    if (symbols.TryGetProperty("symbolKind", out var symbolKind)
                        && symbolKind.ValueKind == JsonValueKind.Object
                        && symbolKind.TryGetProperty("valueSet", out var valueSet)
                        && valueSet.ValueKind == JsonValueKind.Array)
                    {
                        var list = new List<int>();
                        foreach (var item in valueSet.EnumerateArray())
                        {
                            if (item.ValueKind == JsonValueKind.Number)
                            {
                                if (item.TryGetInt32(out var sv))
                                    list.Add(sv);
                            }
                        }

                        kinds = [.. list];
                    }
                }
            }

            capabilities = new ClientNavigationCapabilities(linkSupport, hierarchical, kinds);
            return true;
        }
    }

    /// <summary>读 "textDocument/didClose" 的 URI。</summary>
    public static bool TryReadDocumentUri(in ReadOnlySequence<byte> parameters, out string uri)
    {
        uri = string.Empty;
        if (parameters.Length == 0) return false;

        try
        {
            var reader = new Utf8JsonReader(parameters, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            string? found = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                var name = reader.GetString();
                if (!reader.Read()) return false;

                if (name == "textDocument" && reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadUri(ref reader, ref found);
                }
                else
                {
                    reader.Skip();
                }
            }

            if (found is null) return false;
            uri = found;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// 读 "textDocument/completion" / "textDocument/hover" 的
    /// "textDocument.uri" 与 "position"（0-based，UTF-16 码元）。
    /// </summary>
    /// <remarks>
    /// 三项缺一不可：<paramref name="uri"/> 为空、或缺 line/character 时返回 "false"。
    /// "不做任何默认值填充" —— 把"缺字段"当成 (0,0) 会让服务端对文件首行做补全，
    /// 属于静默错误，比直接报 InvalidParams 危险得多。
    /// </remarks>
    public static bool TryReadPositionRequest(
        in ReadOnlySequence<byte> parameters,
        out string uri,
        out int line,
        out int character)
    {
        uri = string.Empty;
        line = -1;
        character = -1;

        if (parameters.Length == 0)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(parameters, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            string? foundUri = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var name = reader.GetString();
                if (!reader.Read())
                {
                    return false;
                }

                if (name == "textDocument" && reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadTextDocumentUri(ref reader, ref foundUri);
                }
                else if (name == "position" && reader.TokenType == JsonTokenType.StartObject)
                {
                    ReadPosition(ref reader, ref line, ref character);
                }
                else
                {
                    reader.Skip();
                }
            }

            if (foundUri is null || line < 0 || character < 0)
            {
                return false;
            }

            uri = foundUri;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ReadTextDocumentUri(ref Utf8JsonReader reader, ref string? found)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return;
            }

            var name = reader.GetString();
            if (!reader.Read())
            {
                return;
            }

            if (name == "uri" && reader.TokenType == JsonTokenType.String)
            {
                found = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }
    }

    private static void ReadPosition(ref Utf8JsonReader reader, ref int line, ref int character)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                return;
            }

            var name = reader.GetString();
            if (!reader.Read())
            {
                return;
            }

            if (reader.TokenType != JsonTokenType.Number)
            {
                reader.Skip();
                continue;
            }

            if (name == "line")
            {
                line = reader.TryGetInt32(out var ln) ? ln : 0;
            }
            else if (name == "character")
            {
                character = reader.TryGetInt32(out var ch) ? ch : 0;
            }
        }
    }

    /// <summary>读 "$/cancelRequest" 的 "params.id" 原始区间。</summary>
    public static bool TryReadCancelId(in ReadOnlySequence<byte> parameters, out ReadOnlySequence<byte> id)
    {
        id = default;
        if (parameters.Length == 0) return false;

        try
        {
            var reader = new Utf8JsonReader(parameters, isFinalBlock: true, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return false;

                var name = reader.GetString();
                if (!reader.Read()) return false;

                if (name == "id")
                {
                    var start = reader.TokenStartIndex;
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) reader.Skip();
                    id = parameters.Slice(start, reader.BytesConsumed - start);
                    return true;
                }

                reader.Skip();
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ReadUri(ref Utf8JsonReader reader, ref string? uri)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return;
            if (reader.TokenType != JsonTokenType.PropertyName) return;

            var name = reader.GetString();
            if (!reader.Read()) return;

            if (name == "uri" && reader.TokenType == JsonTokenType.String) uri = reader.GetString();
            else reader.Skip();
        }
    }

    private static void ReadTextDocument(
        ref Utf8JsonReader reader,
        ref string? uri,
        ref string? languageId,
        ref string? text,
        ref int version,
        bool withText)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return;
            if (reader.TokenType != JsonTokenType.PropertyName) return;

            var name = reader.GetString();
            if (!reader.Read()) return;

            switch (name)
            {
                case "uri" when reader.TokenType == JsonTokenType.String:
                    uri = reader.GetString();
                    break;
                case "languageId" when reader.TokenType == JsonTokenType.String:
                    if (withText) languageId = reader.GetString();
                    break;
                case "version" when reader.TokenType == JsonTokenType.Number:
                    version = reader.TryGetInt32(out var v2) ? v2 : 0;
                    break;
                case "text" when reader.TokenType == JsonTokenType.String:
                    if (withText) text = reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    /// <summary>只取 "textDocument.uri" 与 "version"（didChange 用，不读 text）。</summary>
    private static void ReadTextDocumentBasics(ref Utf8JsonReader reader, ref string? uri, ref int version)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return;
            if (reader.TokenType != JsonTokenType.PropertyName) return;

            var name = reader.GetString();
            if (!reader.Read()) return;

            switch (name)
            {
                case "uri" when reader.TokenType == JsonTokenType.String:
                    uri = reader.GetString();
                    break;
                case "version" when reader.TokenType == JsonTokenType.Number:
                    version = reader.TryGetInt32(out var v3) ? v3 : 0;
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }
    }

    private static void ReadContentChanges(ref Utf8JsonReader reader, ref string? text)
    {
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return;
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName) return;

                var name = reader.GetString();
                if (!reader.Read()) return;

                if (name == "text" && reader.TokenType == JsonTokenType.String) text = reader.GetString();
                else reader.Skip();
            }
        }
    }
}
