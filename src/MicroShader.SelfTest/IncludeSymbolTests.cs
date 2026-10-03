using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 9 · include 链符号（C 后台预热 + B 同步兜底）自检。
/// </summary>
/// <remarks>
/// 最关键的一条："不注入 provider 时，行为必须与注入前逐字节一致"（§28.5 的退化保证）。
/// 因此这里先把「无 provider」的结果存下来，再拿「注入但缓存冷」的结果与之逐字节比对。
/// </remarks>
internal static class IncludeSymbolTests
{
    private const string Suite = "IncludeSymbol";

    // 手动模式参数：不启后台消费线程，由 Drain 同步驱动，测试因此是确定性的。
    private static readonly IncludeSymbolOptions Manual = new() { RunWorker = false };

    public static void Register()
    {
        TestSuite.Add(Suite, "NoProviderUnchanged", NoProviderUnchanged);
        TestSuite.Add(Suite, "ColdCacheDegrades", ColdCacheDegrades);
        TestSuite.Add(Suite, "WarmCacheCompletes", WarmCacheCompletes);
        TestSuite.Add(Suite, "SyncFallback", SyncFallback);
        TestSuite.Add(Suite, "InvalidateDocument", InvalidateDocument);
    }

    // ── 语料：文档里用 Varyings，但 Varyings 定义在 include 里 ──

    private const string Doc = """
        Shader "T/IncludeSymbol"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #include "Fake/Varyings.hlsl"
                    Varyings i;
                    float4 frag() : SV_Target { return i.color; }
                    ENDHLSL
                }
            }
        }
        """;

    private const string VaryingsHlsl = "struct Varyings { float4 positionCS; float2 uv; half4 color; };";

    private sealed class FakeSource : IIncludeFileSource
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        public FakeSource(string virtualPath, string physicalPath, string text)
            => _files[physicalPath] = text;

        public bool TryResolve(string virtualPath, out string physicalPath)
        {
            if (_files.ContainsKey(virtualPath))
            {
                physicalPath = virtualPath;
                return true;
            }

            physicalPath = string.Empty;
            return false;
        }

        public bool TryResolveRelative(string currentPhysicalPath, string target, out string physicalPath)
            => TryResolve(target, out physicalPath);

        public bool TryRead(string physicalPath, out string text)
        {
            if (_files.TryGetValue(physicalPath, out var found))
            {
                text = found;
                return true;
            }

            text = string.Empty;
            return false;
        }

        public long GetStamp(string physicalPath) => 1;
    }

    // ── 1. 不注入 provider：结果必须与注入但缓存冷时逐字节一致 ──

    private static void NoProviderUnchanged()
    {
        var uri = "/shadow/doc.shader";
        var baseline = CompleteWith(new IntelliSenseService(), uri);

        var cold = CompleteWith(new IntelliSenseService(new IntelliSenseOptions { DocumentPath = uri },
            new IncludeSymbolProvider { Cache = new IncludeSymbolCache() }), uri);

        Check.Equal(baseline, cold, "缓存冷 + 无文件源时，输出必须与不注入 provider 时逐字节一致");
        Check.True(baseline is null, "本语料的成员绑定在 include 里，注入前应当降级为 null");
    }

    // ── 2. 空缓存文档层：单独验证「查不到就降级」 ──

    private static void ColdCacheDegrades()
    {
        var cache = new IncludeSymbolCache();
        Check.True(!cache.TryGet("/shadow/doc.shader", "Varyings", out _), "空缓存必须查不到");
        Check.Equal(0, cache.DocumentCount, "空缓存文档层为 0");
        Check.Equal(0, cache.FileCount, "空缓存文件层为 0");
    }

    // ── 3. C 预热：Drain 之后 include 内类型可补全 ──

    private static void WarmCacheCompletes()
    {
        var uri = "/shadow/doc.shader";
        var cache = new IncludeSymbolCache();
        var source = new FakeSource("Fake/Varyings.hlsl", "Fake/Varyings.hlsl", VaryingsHlsl);

        using var warmer = new IncludeSymbolWarmer(source, cache, Manual);
        warmer.Enqueue(uri, uri, Doc);
        warmer.Drain();

        Check.True(cache.DocumentCount >= 1, "预热后文档层应当有条目");
        Check.True(cache.FileCount >= 1, "预热后文件层应当有条目");
        Check.True(cache.TryGet(uri, "Varyings", out var decl), "预热后应当能查到 Varyings");
        Check.Equal(3, decl.Fields.Count, "Varyings 应有 3 个字段");

        var service = new IntelliSenseService(new IntelliSenseOptions { DocumentPath = uri },
            new IncludeSymbolProvider { Cache = cache });

        var json = CompleteWith(service, uri);
        Check.True(json is not null, "预热后 i.color 处必须给出候选");
        var labels = Labels(json!);
        Check.Contains(labels, "positionCS", "应补出 positionCS");
        Check.Contains(labels, "uv", "应补出 uv");
        Check.Contains(labels, "color", "应补出 color");
        TestCaseHelpers.Report("预热后候选 " + labels.Count + " 条：" + string.Join(",", labels));
    }

    // ── 4. B 兜底：不预热也能靠同步扫直连 include 出候选 ──

    private static void SyncFallback()
    {
        var uri = "/shadow/doc.shader";
        var cache = new IncludeSymbolCache();
        var source = new FakeSource("Fake/Varyings.hlsl", "Fake/Varyings.hlsl", VaryingsHlsl);

        var service = new IntelliSenseService(
            new IntelliSenseOptions { DocumentPath = uri },
            new IncludeSymbolProvider { Cache = cache, Files = source });

        var json = CompleteWith(service, uri);
        Check.True(json is not null, "同步兜底必须给出候选（未预热）");
        Check.Contains(Labels(json!), "color", "同步兜底应补出 color");
        Check.Equal(1, cache.FileCount, "同步兜底应把扫到的文件写回文件缓存");
        Check.Equal(0, cache.DocumentCount, "纯兜底不建文档层条目（文档层由预热器负责）");
    }

    // ── 5. 文档关闭后回到降级 ──

    private static void InvalidateDocument()
    {
        var uri = "/shadow/doc.shader";
        var cache = new IncludeSymbolCache();
        var source = new FakeSource("Fake/Varyings.hlsl", "Fake/Varyings.hlsl", VaryingsHlsl);

        using (var warmer = new IncludeSymbolWarmer(source, cache, Manual))
        {
            warmer.Enqueue(uri, uri, Doc);
            warmer.Drain();

            var service = new IntelliSenseService(new IntelliSenseOptions { DocumentPath = uri },
                new IncludeSymbolProvider { Cache = cache });
            Check.True(CompleteWith(service, uri) is not null, "预热后应当有候选");

            warmer.Invalidate(uri);
            Check.True(CompleteWith(service, uri) is null, "关闭文档后必须回到降级（null）");
        }
    }

    // ── 辅助 ──

    private static string? CompleteWith(IntelliSenseService service, string documentPath)
    {
        var line = 0;
        var lineStart = 0;
        var marker = "return i.color;";
        var index = Doc.IndexOf(marker, StringComparison.Ordinal);
        for (var k = 0; k < index; k++)
        {
            if (Doc[k] == (char)10)
            {
                line++;
                lineStart = k + 1;
            }
        }

        var character = index + "return i.".Length - lineStart;

        var writer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(writer))
        {
            if (!service.ProvideCompletions(new PositionRequest(documentPath, Doc, line, character), json))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    private static List<string> Labels(string json)
    {
        var found = new List<string>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items))
        {
            return found;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
            {
                found.Add(label.GetString()!);
            }
        }

        return found;
    }
}
