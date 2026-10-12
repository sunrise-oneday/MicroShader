using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;
using MicroShader.NavigationEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 10 · 跨文件定义跳转与符号导航自检。
/// </summary>
/// <remarks>
/// 每条用例都对着详设 v2.0 修正清单里的一条硬要求，不是为了覆盖率凑数：
/// 1→N 重载（第 2 条）、宏别名解包给两候选（第 6 条）、脏文件必须回原始 URI（第 10 条）、
/// 大纲 range 必须包含 selectionRange 且父含子（第 8 条）、形状随客户端能力分派（第 1 条）、
/// 坏路径 resolve 返回 null（第 4 条）。
/// </remarks>
internal static class NavigationTests
{
    private const string Suite = "Navigation";

    private const string PackagePhysical =
        "D:/proj/Library/PackageCache/com.test.shaders@1.0.0/ShaderLibrary/Common.hlsl";

    private const string LocalPhysical = "D:/proj/Assets/Shaders/Local.hlsl";

    private const string SelfPhysical = "D:/proj/Assets/Shaders/Self.hlsl";

    private const string DocumentPhysical = "D:/proj/Assets/Shaders/Test.shader";

    private const string DocumentUri = "file:///D:/proj/Assets/Shaders/Test.shader";

    private const string Header =
        "#define MS_ALIAS MS_Target\nfloat3 MS_Target(float3 p) { return p; }\nfloat3 MS_Target(float3 p, float w) { return p * w; }\n";

    private const string LocalHeader = "float4 MS_LocalOnly(float4 x) { return x; }\n";

    private const string SelfHeader =
        "#include \"Self.hlsl\"\nfloat3 MS_Cyclic(float3 p) { return p; }\n";

    private const string Document = """
        Shader "T/Nav"
        {
            HLSLINCLUDE
            #include "Packages/com.test.shaders/ShaderLibrary/Common.hlsl"
            struct VertexOutput { float4 positionCS : SV_POSITION; half3 color; };
            ENDHLSL

            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #include "Local.hlsl"
                    #define MS_LOCAL_CONST 1.0
                    #pragma vertex vert
                    #pragma fragment frag

                    VertexOutput vert(VertexOutput input) { return input; }
                    half4 frag(VertexOutput i) : SV_Target
                    {
                        float3 p = MS_Target(1);
                        float4 q = MS_LocalOnly(1);
                        float3 m = MS_ALIAS;
                        return i.color;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    public static void Register()
    {
        TestSuite.Add(Suite, "DocumentLinksLexical", DocumentLinksLexical);
        TestSuite.Add(Suite, "LinkResolveExisting", LinkResolveExisting);
        TestSuite.Add(Suite, "LinkResolveMissing", LinkResolveMissing);
        TestSuite.Add(Suite, "OutlineHierarchical", OutlineHierarchical);
        TestSuite.Add(Suite, "OutlineFlat", OutlineFlat);
        TestSuite.Add(Suite, "OutlineKindsInValueSet", OutlineKindsInValueSet);
        TestSuite.Add(Suite, "DefinitionLocalFunction", DefinitionLocalFunction);
        TestSuite.Add(Suite, "DefinitionSharedInclude", DefinitionSharedInclude);
        TestSuite.Add(Suite, "DefinitionCrossFile", DefinitionCrossFile);
        TestSuite.Add(Suite, "DefinitionOverloads", DefinitionOverloads);
        TestSuite.Add(Suite, "DefinitionMacroTwoCandidates", DefinitionMacroTwoCandidates);
        TestSuite.Add(Suite, "DefinitionDirtyFileUri", DefinitionDirtyFileUri);
        TestSuite.Add(Suite, "ShapeFollowsClientCapability", ShapeFollowsClientCapability);
        TestSuite.Add(Suite, "CyclicIncludeSafe", CyclicIncludeSafe);
    }

    // ───────────────────────── 1. #include 超链接（首屏纯词法）─────────────────────────

    private static void DocumentLinksLexical()
    {
        var host = new FakeHost();
        var service = new NavigationService(host, new IncludeSymbolCache());

        var json = Links(service, DocumentUri, host.Physical(DocumentUri), Document);
        Check.True(json is not null, "含 include 的文档必须给出 documentLink");

        var links = JsonDocument.Parse(json!).RootElement;
        Check.Equal(2, links.GetArrayLength(), "两条 include（虚拟 + 相对）都应给出链接");

        var first = links[0];
        Check.Equal("Packages/com.test.shaders/ShaderLibrary/Common.hlsl",
            first.GetProperty("data").GetProperty("target").GetString(), "链接应带上 include 目标字符串");

        // range 必须只覆盖引号内的路径文本（详设要求「提取引号包裹的范围」）
        var range = first.GetProperty("range");
        var start = range.GetProperty("start");
        var end = range.GetProperty("end");
        var lineText = LineOfText(Document, start.GetProperty("line").GetInt32());
        var from = start.GetProperty("character").GetInt32();
        var covered = lineText.Substring(from, end.GetProperty("character").GetInt32() - from);
        Check.Equal("Packages/com.test.shaders/ShaderLibrary/Common.hlsl", covered,
            "range 必须正好覆盖引号内的路径（不含引号）");

        Check.Equal(0, host.Reads, "首屏必须零 I/O（详设 v2.0 第 4 条）");
        TestCaseHelpers.Report("documentLink：" + links.GetArrayLength() + " 条，首屏读盘 " + host.Reads + " 次");
    }

    private static void LinkResolveExisting()
    {
        var host = new FakeHost();
        var service = new NavigationService(host, new IncludeSymbolCache());

        var json = Links(service, DocumentUri, host.Physical(DocumentUri), Document);
        var link = JsonDocument.Parse(json!).RootElement[1];

        var resolved = Resolve(service, link.GetRawText());
        Check.True(resolved is not null, "相对 include 指向真实存在的文件时必须能 resolve");

        var target = JsonDocument.Parse(resolved!).RootElement.GetProperty("target").GetString();
        Check.Equal(new Uri(LocalPhysical).AbsoluteUri, target, "resolve 必须给出 percent-encoded 的 file:/// URI");
        Check.True(target!.StartsWith("file:///", StringComparison.Ordinal), "必须是三斜杠的 file URI（详设第 9 条）");
    }

    private static void LinkResolveMissing()
    {
        var host = new FakeHost();
        var document = Document.Replace("Local.hlsl", "DoesNotExist.hlsl", StringComparison.Ordinal);
        var service = new NavigationService(host, new IncludeSymbolCache());

        var json = Links(service, DocumentUri, host.Physical(DocumentUri), document);
        Check.True(json is not null, "坏路径仍然要给出链接（首屏是纯词法，不做存活性判断）");

        var links = JsonDocument.Parse(json!).RootElement;
        Check.Equal(2, links.GetArrayLength(), "坏路径也占一条");

        var resolved = Resolve(service, links[1].GetRawText());
        Check.True(resolved is null, "目标不存在时 resolve 必须返回 null（链接不可点击，而不是点了弹 File not found）");
    }

    // ───────────────────────── 2. 大纲 ─────────────────────────

    private static void OutlineHierarchical()
    {
        var host = new FakeHost();
        var service = new NavigationService(host, new IncludeSymbolCache(),
            new NavigationOptions { HierarchicalDocumentSymbol = true });

        var json = Symbols(service, DocumentUri, host.Physical(DocumentUri), Document);
        Check.True(json is not null, "必须给出大纲");

        var root = JsonDocument.Parse(json!).RootElement;
        Check.Equal(1, root.GetArrayLength(), "层级形态只发一个根（Shader）");
        Check.Equal("T/Nav", root[0].GetProperty("name").GetString(), "根节点名取 Shader 引号里的名字");
        Check.Equal(1, root[0].GetProperty("kind").GetInt32(), "Shader 应映射为 File(1)");

        // 共享块（HLSLINCLUDE）挂在根下，且在 SubShader 之前
        var shared = root[0].GetProperty("children")[0];
        Check.Equal(2, shared.GetProperty("kind").GetInt32(), "HLSLINCLUDE 应映射为 Module(2)");
        Check.Contains(NamesOf(shared.GetProperty("children")), "VertexOutput",
            "HLSLINCLUDE 块下应列出它声明的结构体");

        var sub = root[0].GetProperty("children")[1];
        Check.Equal(3, sub.GetProperty("kind").GetInt32(), "SubShader 应映射为 Namespace(3)");

        var pass = sub.GetProperty("children")[0];
        Check.Equal(6, pass.GetProperty("kind").GetInt32(), "Pass 应映射为 Method(6)");

        var block = pass.GetProperty("children")[0];
        Check.Equal(2, block.GetProperty("kind").GetInt32(), "HLSLPROGRAM 应映射为 Module(2)");

        var names = NamesOf(block.GetProperty("children"));
        Check.Contains(names, "vert", "块内应列出 vert 函数");
        Check.Contains(names, "frag", "块内应列出 frag 函数");
        Check.Contains(names, "MS_LOCAL_CONST", "块内应列出块内的 #define");
        Check.DoesNotContain(names, "VertexOutput", "HLSLINCLUDE 的结构体不该出现在程序块下（层级不能串）");

        AssertContainment(root[0], "Shader");
        TestCaseHelpers.Report("大纲（层级）：程序块内 " + names.Count + " 个符号");
    }

    private static void OutlineFlat()
    {
        var host = new FakeHost();
        var service = new NavigationService(host, new IncludeSymbolCache(),
            new NavigationOptions { HierarchicalDocumentSymbol = false });

        var json = Symbols(service, DocumentUri, host.Physical(DocumentUri), Document);
        var flat = JsonDocument.Parse(json!).RootElement;

        var names = NamesOf(flat);
        Check.Contains(names, "frag", "扁平形态也要列出 frag");
        Check.Contains(names, "VertexOutput", "扁平形态也要列出 VertexOutput");

        foreach (var item in flat.EnumerateArray())
        {
            Check.Equal(DocumentUri, item.GetProperty("location").GetProperty("uri").GetString(),
                "扁平形态的 location.uri 必须是本文档");
            Check.True(item.TryGetProperty("containerName", out _), "扁平形态应带 containerName");
            Check.True(!item.TryGetProperty("children", out _), "扁平形态不得出现 children 字段");
        }

        TestCaseHelpers.Report("大纲（扁平）：条目 " + names.Count + " 条");
    }

    private static void OutlineKindsInValueSet()
    {
        var host = new FakeHost();

        // 客户端只认 {1,6,12} —— 结构体(23)、宏(14)、Namespace(3) 都不在集合里
        int[] valueSet = [1, 6, 12];
        var service = new NavigationService(host, new IncludeSymbolCache(),
            new NavigationOptions { HierarchicalDocumentSymbol = true, SymbolKindValueSet = valueSet });

        var json = Symbols(service, DocumentUri, host.Physical(DocumentUri), Document);
        var root = JsonDocument.Parse(json!).RootElement;

        AssertKindsInSet(root, valueSet);
        TestCaseHelpers.Report("大纲 kind 全部落在客户端 valueSet 内（{1,6,12}）");
    }

    // ───────────────────────── 3. F12 ─────────────────────────

    private static void DefinitionLocalFunction()
    {
        var (json, host) = Definition(Document, "VertexOutput vert(VertexOutput input)", "vert");

        Check.True(json is not null, "块内函数名上按 F12 必须给出定义");
        var items = JsonDocument.Parse(json!).RootElement;
        Check.Equal(1, items.GetArrayLength(), "唯一的 vert 只应给一个候选");
        Check.Equal(DocumentUri, items[0].GetProperty("uri").GetString(), "应指向本文档");
        Check.Equal(
            LineNumber(Document, "VertexOutput vert("),
            items[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
            "行号必须落在声明那一行");
        Check.Equal(0, host.Reads, "本文件定义不得触发任何读盘（详设 §五 的 3ms 预算）");
    }

    private static void DefinitionSharedInclude()
    {
        // 光标停在 Pass 内 frag 形参的类型名 VertexOutput 上；该结构体声明在 HLSLINCLUDE 里
        var (json, _) = Definition(Document, "half4 frag(VertexOutput i)", "VertexOutput");

        Check.True(json is not null,
            "HLSLINCLUDE 里的结构体对 Pass 内光标必须可见（Unity 语义；详设 v2.0 第 12 条要求以真实作用域围栏）");

        var items = JsonDocument.Parse(json!).RootElement;
        Check.Equal(
            LineNumber(Document, "struct VertexOutput"),
            items[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
            "应跳到 HLSLINCLUDE 里的 struct 声明行");
    }

    private static void DefinitionCrossFile()
    {
        var (json, _, reads) = DefinitionWarmed(Document, "float3 p = MS_Target(1)", "MS_Target");

        Check.True(json is not null, "include 头文件里的函数按 F12 必须能跳到");
        var items = JsonDocument.Parse(json!).RootElement;
        Check.Equal(new Uri(PackagePhysical).AbsoluteUri, items[0].GetProperty("uri").GetString(),
            "应跳到包内头文件（URI 必须 percent-encoded）");
        Check.Equal(1, items[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
            "MS_Target 的头一个重载在头文件第 2 行（0-based 1）");

        Check.Equal(0, reads, "预热命中后 F12 不得再读盘（详设 §三 的内存契约）");
        TestCaseHelpers.Report("跨文件 F12：预热命中读盘 " + reads + " 次");
    }

    private static void DefinitionOverloads()
    {
        var (json, _, _) = DefinitionWarmed(Document, "float3 p = MS_Target(1)", "MS_Target");

        var items = JsonDocument.Parse(json!).RootElement;
        Check.Equal(2, items.GetArrayLength(),
            "同名重载必须全部返回（1→N；详设 v2.0 第 2 条：取第一条必然跳错重载）");
    }

    private static void DefinitionMacroTwoCandidates()
    {
        var (json, _, _) = DefinitionWarmed(Document, "float3 m = MS_ALIAS;", "MS_ALIAS");

        Check.True(json is not null, "宏别名上按 F12 必须有结果");
        var items = JsonDocument.Parse(json!).RootElement;

        Check.True(items.GetArrayLength() >= 2,
            "宏解包必须给「宏定义处 + 展开后的实现处」两个候选（详设 v2.0 第 6 条）");

        var lines = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            lines.Add(item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32().ToString());
        }

        Check.Contains(lines, "0", "应含宏定义行（头文件第 1 行）");
        Check.Contains(lines, "1", "应含展开后 MS_Target 的实现行（头文件第 2 行）");
        TestCaseHelpers.Report("宏别名 F12：" + items.GetArrayLength() + " 个候选（定义处 + 展开实现处）");
    }

    private static void DefinitionDirtyFileUri()
    {
        var host = new FakeHost();
        host.Open(LocalPhysical, "untitled:Untitled-1");

        var cache = new IncludeSymbolCache();
        var service = new NavigationService(host, cache);
        Warm(host, cache, Document);

        var json = DefinitionJson(service, Document, "float4 q = MS_LocalOnly(1)", "MS_LocalOnly");

        Check.True(json is not null, "脏文件里的符号必须能跳到");
        var uri = JsonDocument.Parse(json!).RootElement[0].GetProperty("uri").GetString();
        Check.Equal("untitled:Untitled-1", uri,
            "目标正被编辑器打开时必须原样回传它的 URI（详设 v2.0 第 10 条）");
    }

    private static void ShapeFollowsClientCapability()
    {
        var host = new FakeHost();

        var withLinks = new NavigationService(host, new IncludeSymbolCache(),
            new NavigationOptions { LinkSupport = true });
        var linkItem = JsonDocument.Parse(
            DefinitionJson(withLinks, Document, "VertexOutput vert(VertexOutput input)", "vert")!).RootElement[0];
        Check.True(linkItem.TryGetProperty("targetUri", out _), "linkSupport=true 时应回 LocationLink（targetUri）");
        Check.True(linkItem.TryGetProperty("originSelectionRange", out _), "LocationLink 应带 originSelectionRange");
        Check.True(linkItem.TryGetProperty("targetSelectionRange", out _), "LocationLink 应带 targetSelectionRange");

        var plain = new NavigationService(host, new IncludeSymbolCache(),
            new NavigationOptions { LinkSupport = false });
        var plainItem = JsonDocument.Parse(
            DefinitionJson(plain, Document, "VertexOutput vert(VertexOutput input)", "vert")!).RootElement[0];
        Check.True(plainItem.TryGetProperty("uri", out _), "linkSupport=false 时应回 Location（uri）");
        Check.True(!plainItem.TryGetProperty("targetUri", out _), "Location 形态不得出现 targetUri");

        // initialize 之后回填能力也必须生效
        withLinks.ApplyCapabilities(linkSupport: false, hierarchicalDocumentSymbols: false, symbolKindValueSet: []);
        var afterApply = JsonDocument.Parse(
            DefinitionJson(withLinks, Document, "VertexOutput vert(VertexOutput input)", "vert")!).RootElement[0];
        Check.True(afterApply.TryGetProperty("uri", out _),
            "ApplyCapabilities 回填后形状必须跟着变（客户端能力只有 initialize 才知道）");
    }

    private static void CyclicIncludeSafe()
    {
        var host = new FakeHost();
        host.Add(SelfPhysical, SelfHeader);

        var document = """
            Shader "T/Cyclic"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "Self.hlsl"
                        float4 frag() : SV_Target { return MS_Cyclic(1); }
                        ENDHLSL
                    }
                }
            }
            """;

        var cache = new IncludeSymbolCache();
        var service = new NavigationService(host, cache);
        Warm(host, cache, document);

        var json = DefinitionJson(service, document, "return MS_Cyclic(1)", "MS_Cyclic");
        Check.True(json is not null, "自我包含的头文件里，符号仍必须能跳到（不得死循环）");
    }

    // ───────────────────────── 假宿主 ─────────────────────────

    /// <summary>内存文件系统 + 脏文件登记。所有键归一化成正斜杠形态，避免 Windows 反斜杠不一致。</summary>
    private sealed class FakeHost : INavigationHost
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _open = new(StringComparer.OrdinalIgnoreCase);

        public int Reads;

        public FakeHost()
        {
            Add(PackagePhysical, Header);
            Add(LocalPhysical, LocalHeader);
        }

        public void Add(string physicalPath, string text) => _files[Norm(physicalPath)] = text;

        public void Open(string physicalPath, string uri) => _open[Norm(physicalPath)] = uri;

        public string Physical(string uri) => Norm(UriToPath(uri));

        public static string Norm(string path) => path.Replace('\\', '/');

        private static string UriToPath(string uri)
        {
            var text = uri;
            if (text.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                text = text["file:///".Length..];
            }

            return Uri.UnescapeDataString(text);
        }

        public bool TryResolve(string virtualPath, out string physicalPath)
        {
            physicalPath = string.Empty;

            if (!virtualPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var rest = virtualPath["Packages/".Length..];
            var slash = rest.IndexOf('/');
            if (slash <= 0)
            {
                return false;
            }

            if (!rest[..slash].StartsWith("com.test.shaders", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var packageRoot = PackagePhysical[..PackagePhysical.IndexOf("/ShaderLibrary/", StringComparison.Ordinal)];
            var candidate = packageRoot + "/" + rest[(slash + 1)..];

            if (!_files.ContainsKey(candidate))
            {
                return false;
            }

            physicalPath = candidate;
            return true;
        }

        public bool TryResolveRelative(string currentPhysicalPath, string target, out string physicalPath)
        {
            physicalPath = string.Empty;

            var current = Norm(currentPhysicalPath);
            var directory = current[..(current.LastIndexOf('/') + 1)];
            var candidate = Norm(directory + target);

            if (!_files.ContainsKey(candidate))
            {
                return false;
            }

            physicalPath = candidate;
            return true;
        }

        public bool TryRead(string physicalPath, out string text)
        {
            Reads++;
            return _files.TryGetValue(Norm(physicalPath), out text!);
        }

        public long GetStamp(string physicalPath) => 1;

        public bool TryGetPhysicalPath(string uri, out string physicalPath)
        {
            physicalPath = Physical(uri);
            return physicalPath.Length > 0;
        }

        public bool TryGetOpenDocument(string physicalPath, out string uri, out string text)
        {
            if (_open.TryGetValue(Norm(physicalPath), out var foundUri))
            {
                uri = foundUri;
                text = _files.TryGetValue(Norm(physicalPath), out var content) ? content : string.Empty;
                return true;
            }

            uri = string.Empty;
            text = string.Empty;
            return false;
        }

        public bool Exists(string physicalPath) => _files.ContainsKey(Norm(physicalPath));

        public string ToFileUri(string physicalPath) => new Uri(Norm(physicalPath)).AbsoluteUri;
    }

    /// <summary>手动模式的预热器：同步跑完，自检确定性（同 IncludeSymbolTests 的说明）。</summary>
    private static readonly IncludeSymbolOptions ManualWarm = new() { RunWorker = false };

    private static void Warm(FakeHost host, IncludeSymbolCache cache, string document)
    {
        using var warmer = new IncludeSymbolWarmer(host, cache, ManualWarm);
        warmer.Enqueue(DocumentUri, host.Physical(DocumentUri), document);
        warmer.Drain();
    }

    // ───────────────────────── 调用辅助 ─────────────────────────

    private static string? Links(NavigationService service, string uri, string filePath, string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (!service.ProvideDocumentLinks(uri, filePath, text, writer))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? Symbols(NavigationService service, string uri, string filePath, string text)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (!service.ProvideDocumentSymbols(uri, filePath, text, writer))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? Resolve(NavigationService service, string rawLink)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            var bytes = Encoding.UTF8.GetBytes(rawLink);
            if (!service.ResolveDocumentLink(new ReadOnlySequence<byte>(bytes), writer))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static string? DefinitionJson(NavigationService service, string document, string anchor, string symbol)
    {
        var (line, character) = CursorAt(document, anchor, symbol);
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (!service.ProvideDefinition(new PositionRequest(DocumentUri, document, line, character), writer))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static (string? Json, FakeHost Host) Definition(string document, string anchor, string symbol)
    {
        var host = new FakeHost();
        var service = new NavigationService(host, new IncludeSymbolCache());
        return (DefinitionJson(service, document, anchor, symbol), host);
    }

    private static (string? Json, FakeHost Host, int Reads) DefinitionWarmed(string document, string anchor, string symbol)
    {
        var host = new FakeHost();
        var cache = new IncludeSymbolCache();
        var service = new NavigationService(host, cache);
        Warm(host, cache, document);

        var before = host.Reads;
        var json = DefinitionJson(service, document, anchor, symbol);
        return (json, host, host.Reads - before);
    }

    /// <summary>把光标放在锚点内 <paramref name="symbol"/> 之后。</summary>
    private static (int Line, int Character) CursorAt(string text, string anchor, string symbol)
    {
        var at = text.IndexOf(anchor, StringComparison.Ordinal);
        Check.True(at >= 0, "语料缺少锚点：" + anchor);

        var symbolAt = text.IndexOf(symbol, at, StringComparison.Ordinal);
        Check.True(symbolAt >= 0 && symbolAt < at + anchor.Length, "锚点内找不到符号：" + symbol);

        return SplitPosition(text, symbolAt + symbol.Length);
    }

    private static (int Line, int Character) SplitPosition(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;

        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, offset - lineStart);
    }

    private static int LineNumber(string text, string marker)
    {
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        Check.True(at >= 0, "语料缺少标记：" + marker);

        var line = 0;
        for (var i = 0; i < at; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static string LineOfText(string text, int line)
    {
        var lines = text.Split('\n');
        return line >= 0 && line < lines.Length ? lines[line] : string.Empty;
    }

    private static List<string> NamesOf(JsonElement array)
    {
        var names = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            {
                names.Add(name.GetString()!);
            }
        }

        return names;
    }

    /// <summary>断言 range ⊇ selectionRange，且父 range 包含全部子 range（详设 v2.0 第 8 条）。</summary>
    private static void AssertContainment(JsonElement node, string label)
    {
        var range = node.GetProperty("range");
        Check.True(Contains(range, node.GetProperty("selectionRange")), label + "：range 必须包含 selectionRange");

        if (!node.TryGetProperty("children", out var children))
        {
            return;
        }

        foreach (var child in children.EnumerateArray())
        {
            Check.True(Contains(range, child.GetProperty("range")),
                label + "：父 range 必须包含子 range（否则大纲会错层）");
            AssertContainment(child, label + "/" + child.GetProperty("name").GetString());
        }
    }

    private static bool Contains(JsonElement outer, JsonElement inner)
    {
        var outerStart = Pos(outer.GetProperty("start"));
        var outerEnd = Pos(outer.GetProperty("end"));
        var innerStart = Pos(inner.GetProperty("start"));
        var innerEnd = Pos(inner.GetProperty("end"));

        return outerStart.Line < innerStart.Line
            || (outerStart.Line == innerStart.Line && outerStart.Character <= innerStart.Character)
                ? outerEnd.Line > innerEnd.Line
                    || (outerEnd.Line == innerEnd.Line && outerEnd.Character >= innerEnd.Character)
                : false;
    }

    private static (int Line, int Character) Pos(JsonElement point)
        => (point.GetProperty("line").GetInt32(), point.GetProperty("character").GetInt32());

    private static void AssertKindsInSet(JsonElement array, int[] allowed)
    {
        foreach (var item in array.EnumerateArray())
        {
            if (!item.TryGetProperty("kind", out var kindElement))
            {
                continue;
            }

            var kind = kindElement.GetInt32();
            var ok = false;
            foreach (var value in allowed)
            {
                if (value == kind)
                {
                    ok = true;
                    break;
                }
            }

            Check.True(ok, "kind " + kind + " 不在客户端 valueSet 内（详设 v2.0 第 8 条）");

            if (item.TryGetProperty("children", out var children))
            {
                AssertKindsInSet(children, allowed);
            }
        }
    }
}
