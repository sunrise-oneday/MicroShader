using System.Text;
using System.Text.Json;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.IntelliSenseEngine;
using MicroShader.NavigationEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 10 · 导航能力的"协议层"回环自检（走真实 "LspServerSession"）。
/// </summary>
/// <remarks>
/// <see cref="NavigationTests"/> 覆盖引擎算法；本文件覆盖「接线是否正确」：
/// 能力是否成套宣告、方法是否分发、客户端能力快照是否真的改变了返回形状、
/// 未注入时是否回 "MethodNotFound"（而不是静默丢弃让客户端挂死）。
/// </remarks>
internal static class NavigationRoundTripTests
{
    private const string Suite = "NavigationLsp";

    private const string DocumentUri = "file:///D:/proj/Assets/Shaders/Wire.shader";

    private const string Document = """
        Shader "T/Wire"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #include "Core.hlsl"
                    struct Varyings { float4 positionCS; half3 color; };
                    Varyings vert(Varyings input) { return input; }
                    half4 frag(Varyings i) : SV_Target { return i.color; }
                    ENDHLSL
                }
            }
        }
        """;

    /// <summary>折叠回环专用语料。行号（0-based）在断言里逐条写死，行一动断言就要跟着动。</summary>
    private const string FoldingUri = "file:///D:/proj/Assets/Shaders/Fold.shader";

    private const string FoldingDocument = """
        Shader "T/Fold"
        {
            SubShader
            {
                Pass
                {
                    CGPROGRAM
                    #ifdef X
                    float f()
                    {
                        if (f > 0)
                        {
                        }
                        else
                        {
                        }
                        for (;;)
                        {
                        }
                        return 1;
                    }
                    #endif
                    CBUFFER_START(UnityPerMaterial)
                        float4 _Base;
                    CBUFFER_END
                    ENDCG
                }
            }
        }
        """;

    public static void Register()
    {
        TestSuite.Add(Suite, "CapabilitiesDeclaredWhenInjected", CapabilitiesDeclaredWhenInjected);
        TestSuite.Add(Suite, "CapabilitiesAbsentWhenNotInjected", CapabilitiesAbsentWhenNotInjected);
        TestSuite.Add(Suite, "DefinitionOverWire", DefinitionOverWire);
        TestSuite.Add(Suite, "ShapeFollowsInitializedCapabilities", ShapeFollowsInitializedCapabilities);
        TestSuite.Add(Suite, "MethodNotFoundWhenDisabled", MethodNotFoundWhenDisabled);
        TestSuite.Add(Suite, "DocumentLinkResolve", DocumentLinkResolve);
        TestSuite.Add(Suite, "FoldingRangeStructure", FoldingRangeStructure);
    }

    // ───────────────────────── 能力宣告 ─────────────────────────

    private static void CapabilitiesDeclaredWhenInjected()
    {
        var client = Open(out _);

        var capabilities = JsonDocument.Parse(InitializeResponse!).RootElement
            .GetProperty("result").GetProperty("capabilities");

        Check.True(capabilities.TryGetProperty("definitionProvider", out _), "注入后必须宣告 definitionProvider");
        Check.True(capabilities.TryGetProperty("documentSymbolProvider", out _), "注入后必须宣告 documentSymbolProvider");
        Check.True(capabilities.TryGetProperty("foldingRangeProvider", out _),
            "注入后必须宣告 foldingRangeProvider（粘滞滚动窗口的折叠模型数据源）");

        var link = capabilities.GetProperty("documentLinkProvider");
        Check.True(link.GetProperty("resolveProvider").GetBoolean(),
            "documentLinkProvider 必须带 resolveProvider=true（首屏不给 target，见详设 v2.0 第 4 条）");
        Check.True(link.GetProperty("tooltipSupport").GetBoolean(), "documentLinkProvider 应带 tooltipSupport");

        client.Dispose();
    }

    private static void CapabilitiesAbsentWhenNotInjected()
    {
        using var client = new LspTestClient(new LspServerSession(new FakeAnalyzer().AnalyzeAsync));
        var response = Request(client, 1, "initialize", """{"processId":1,"rootUri":null,"capabilities":{}}""");
        var capabilities = JsonDocument.Parse(response).RootElement.GetProperty("result").GetProperty("capabilities");

        Check.True(!capabilities.TryGetProperty("definitionProvider", out _),
            "未注入导航能力时绝不能宣告（宣告了却不会响应 = 客户端永久挂起）");
        Check.True(!capabilities.TryGetProperty("documentLinkProvider", out _), "未注入时不得宣告 documentLinkProvider");
        Check.True(!capabilities.TryGetProperty("documentSymbolProvider", out _), "未注入时不得宣告 documentSymbolProvider");
        Check.True(!capabilities.TryGetProperty("foldingRangeProvider", out _), "未注入时不得宣告 foldingRangeProvider");
    }

    // ───────────────────────── 协议回环 ─────────────────────────

    private static void DefinitionOverWire()
    {
        var client = Open(out _);

        var response = Request(client, 5, "textDocument/definition",
            """{"textDocument":{"uri":""" + Json(DocumentUri) + """},"position":""" + PositionOf("vert") + "}");

        var result = JsonDocument.Parse(response).RootElement.GetProperty("result");
        Check.Equal(JsonValueKind.Array, result.ValueKind, "F12 必须回数组（Location[] / LocationLink[]）");
        Check.Equal(1, result.GetArrayLength(), "vert 的唯一声明应给出一个候选");
        Check.Equal(DocumentUri, result[0].GetProperty("uri").GetString(), "应指回本文档");

        client.Dispose();
    }

    private static void ShapeFollowsInitializedCapabilities()
    {
        // 客户端声明 linkSupport=true ⇒ 服务端必须回 LocationLink（targetUri），而不是 Location
        var client = Open(out _, linkSupport: true);

        var response = Request(client, 6, "textDocument/definition",
            """{"textDocument":{"uri":""" + Json(DocumentUri) + """},"position":""" + PositionOf("vert") + "}");

        var item = JsonDocument.Parse(response).RootElement.GetProperty("result")[0];
        Check.True(item.TryGetProperty("targetUri", out _),
            "客户端声明 linkSupport 后必须回 LocationLink（详设 v2.0 第 1 条）");

        client.Dispose();
    }

    private static void MethodNotFoundWhenDisabled()
    {
        using var client = new LspTestClient(new LspServerSession(new FakeAnalyzer().AnalyzeAsync));
        _ = Request(client, 1, "initialize", """{"processId":1,"rootUri":null,"capabilities":{}}""");

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"textDocument/definition\",\"params\":{}}")
            .AsTask().GetAwaiter().GetResult();

        var error = JsonDocument.Parse(ReceiveFrame(client, 9)).RootElement.GetProperty("error");
        Check.Equal(-32601, error.GetProperty("code").GetInt32(),
            "未启用导航时 definition 必须回 MethodNotFound(-32601)，不能静默丢弃");
    }

    // ───────────────────────── 折叠范围（粘滞滚动窗口的数据源） ─────────────────────────

    /// <summary>
    /// "textDocument/foldingRange" 回环：结构块全在、控制流全排除。
    /// </summary>
    /// <remarks>
    /// 粘滞滚动窗口的 foldingProviderModel 对折叠范围不做过滤，因此这里排除的控制流
    /// （if / else / for）就是它永远不会显示的东西 —— 断言的正是这个「不显示」。
    /// </remarks>
    private static void FoldingRangeStructure()
    {
        var client = Open(out _);

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{"
                + "\"uri\":" + Json(FoldingUri) + ",\"languageId\":\"shaderlab\",\"version\":1,\"text\":"
                + Json(FoldingDocument) + "}}}")
            .AsTask().GetAwaiter().GetResult();

        var response = Request(client, 9, "textDocument/foldingRange",
            "{\"textDocument\":{\"uri\":" + Json(FoldingUri) + "}}");

        var result = JsonDocument.Parse(response).RootElement.GetProperty("result");
        Check.Equal(JsonValueKind.Array, result.ValueKind, "foldingRange 必须回数组");
        Check.Equal(7, result.GetArrayLength(), "折叠范围必须恰好 7 条（结构块全在，控制流全排除）");

        var raw = result.GetRawText();
        Check.True(raw.Contains("{\"startLine\":0,\"endLine\":28}", StringComparison.Ordinal), "Shader 块");
        Check.True(raw.Contains("{\"startLine\":2,\"endLine\":27}", StringComparison.Ordinal), "SubShader 块");
        Check.True(raw.Contains("{\"startLine\":4,\"endLine\":26}", StringComparison.Ordinal), "Pass 块");
        Check.True(raw.Contains("{\"startLine\":6,\"endLine\":25}", StringComparison.Ordinal), "CGPROGRAM 程序块");
        Check.True(raw.Contains("{\"startLine\":7,\"endLine\":21}", StringComparison.Ordinal), "#ifdef 条件块");
        Check.True(raw.Contains("{\"startLine\":8,\"endLine\":20}", StringComparison.Ordinal), "函数体");
        Check.True(raw.Contains("{\"startLine\":22,\"endLine\":24}", StringComparison.Ordinal), "CBUFFER 块");
        Check.True(!raw.Contains("\"startLine\":10", StringComparison.Ordinal)
            && !raw.Contains("\"startLine\":13", StringComparison.Ordinal)
            && !raw.Contains("\"startLine\":16", StringComparison.Ordinal),
            "控制流（if / else / for）的起始行不得出现在折叠范围里");

        client.Dispose();
    }

    private static void DocumentLinkResolve()
    {
        var client = Open(out _);

        // ① 先发 documentLink 拿首屏结果（词法扫描 #include 行）
        var linkResp = Request(client, 7, "textDocument/documentLink",
            """{"textDocument":{"uri":""" + Json(DocumentUri) + "}}");

        var links = JsonDocument.Parse(linkResp).RootElement.GetProperty("result");
        Check.True(links.ValueKind == JsonValueKind.Array, "documentLink 必须回数组");
        Check.True(links.GetArrayLength() > 0, "至少有一条 include 链接");

        // ② 把首屏返回的 link 原样回传给 resolve
        var firstLink = links[0];
        var resolveParams = firstLink.GetRawText();
        var resolveResp = Request(client, 8, "documentLink/resolve", resolveParams);

        var root = JsonDocument.Parse(resolveResp).RootElement;
        Check.True(root.TryGetProperty("result", out var result), "resolve 必须返回 result（不是 error）");
        Check.True(result.ValueKind == JsonValueKind.Object || result.ValueKind == JsonValueKind.Null,
            "resolve 返回 DocumentLink 或 null（文件不存在时）");

        client.Dispose();
    }

    // ───────────────────────── 装置 ─────────────────────────

    /// <summary>最近一次 <see cref="Open"/> 时 initialize 的响应（断言能力宣告用）。</summary>
    private static string? InitializeResponse;

    /// <summary>打开一个会话并完成 initialize / initialized / didOpen。</summary>
    private static LspTestClient Open(out LspServerSession session, bool linkSupport = false, string? initializeResponse = null)
    {
        _ = initializeResponse;

        var host = new DiskNavigationHost();
        var navigation = new NavigationService(host, new IncludeSymbolCache());
        session = new LspServerSession(
            new FakeAnalyzer().AnalyzeAsync,
            null,
            null,
            new NavigationProvider
            {
                Definition = navigation.ProvideDefinition,
                DocumentLinks = navigation.ProvideDocumentLinks,
                ResolveDocumentLink = navigation.ResolveDocumentLink,
                DocumentSymbols = navigation.ProvideDocumentSymbols,
                FoldingRanges = navigation.ProvideFoldingRanges,
                CapabilitiesReceived = capabilities => navigation.ApplyCapabilities(
                    capabilities.LinkSupport,
                    capabilities.HierarchicalDocumentSymbol,
                    capabilities.SymbolKindValueSet),
            });

        host.OpenDocuments = session;

        var client = new LspTestClient(session);

        var capabilities = linkSupport ? """{"textDocument":{"definition":{"linkSupport":true}}}""" : "{}";
        InitializeResponse = Request(client, 1, "initialize",
            """{"processId":1,"rootUri":null,"capabilities":""" + capabilities + "}");

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\",\"params\":{}}")
            .AsTask().GetAwaiter().GetResult();

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":" + Json(DocumentUri)
            + ",\"languageId\":\"shaderlab\",\"version\":1,\"text\":" + Json(Document) + "}}}")
            .AsTask().GetAwaiter().GetResult();

        return client;
    }

    private static string Request(LspTestClient client, int id, string method, string parameters)
    {
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":" + Json(method) + ",\"params\":" + parameters + "}")
            .AsTask().GetAwaiter().GetResult();

        return ReceiveFrame(client, id);
    }

    /// <summary>
    /// 收到指定 id 的那一帧为止（didOpen 会异步推诊断，帧序不保证）。
    /// </summary>
    private static string ReceiveFrame(LspTestClient client, int id)
    {
        var deadline = Environment.TickCount64 + 5000;

        while (Environment.TickCount64 < deadline)
        {
            var frame = client.TryReceiveAsync(TimeSpan.FromSeconds(2)).AsTask().GetAwaiter().GetResult();
            if (frame is null)
            {
                break;
            }

            using var document = JsonDocument.Parse(frame);
            if (document.RootElement.TryGetProperty("id", out var found)
                && found.ValueKind == JsonValueKind.Number
                && found.GetInt32() == id)
            {
                return frame;
            }
        }

        throw new AssertionException("等不到响应 id=" + id);
    }

    /// <summary>算出某标识符末尾的 LSP 位置（0-based 行列），避免硬编码行号。</summary>
    private static string PositionOf(string symbol)
    {
        var at = Document.IndexOf(symbol, StringComparison.Ordinal);
        Check.True(at >= 0, "语料缺少符号：" + symbol);

        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < at; i++)
        {
            if (Document[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return "{\"line\":" + line + ",\"character\":" + (at + symbol.Length - lineStart) + "}";
    }

    private static string Json(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        builder.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default: builder.Append(c); break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>磁盘版最小宿主（组合根那个是 Server 内部的，自检不能引用 exe 工程）。</summary>
    private sealed class DiskNavigationHost : INavigationHost
    {
        public IOpenDocumentLookup? OpenDocuments { get; set; }

        public bool TryResolve(string virtualPath, out string physicalPath)
        {
            physicalPath = string.Empty;
            return false;
        }

        public bool TryResolveRelative(string currentPhysicalPath, string target, out string physicalPath)
        {
            physicalPath = string.Empty;

            var slash = currentPhysicalPath.LastIndexOfAny(['/', '\\']);
            if (slash <= 0)
            {
                return false;
            }

            var candidate = currentPhysicalPath[..(slash + 1)] + target;
            if (!File.Exists(candidate))
            {
                return false;
            }

            physicalPath = candidate;
            return true;
        }

        public bool TryRead(string physicalPath, out string text)
        {
            try
            {
                text = File.ReadAllText(physicalPath);
                return true;
            }
            catch (IOException)
            {
                text = string.Empty;
                return false;
            }
        }

        public long GetStamp(string physicalPath)
            => File.Exists(physicalPath) ? File.GetLastWriteTimeUtc(physicalPath).Ticks : 0;

        public bool Exists(string physicalPath) => File.Exists(physicalPath);

        public bool TryGetPhysicalPath(string uri, out string physicalPath)
        {
            var text = uri;
            if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                text = text["file:///".Length..];
            }

            physicalPath = Uri.UnescapeDataString(text).Replace('\\', '/');
            return physicalPath.Length > 0;
        }

        public bool TryGetOpenDocument(string physicalPath, out string uri, out string text)
            => OpenDocuments?.TryGetOpenDocument(physicalPath, out uri, out text) ?? NotFound(out uri, out text);

        private static bool NotFound(out string uri, out string text)
        {
            uri = string.Empty;
            text = string.Empty;
            return false;
        }

        public string ToFileUri(string physicalPath) => new Uri(physicalPath).AbsoluteUri;
    }
}
