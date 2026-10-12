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
        TestSuite.Add(Suite, "MacroArgumentTrigger", MacroArgumentTrigger);
        TestSuite.Add(Suite, "CbufferArgumentNames", CbufferArgumentNames);
        TestSuite.Add(Suite, "SamplerArgumentNames", SamplerArgumentNames);
        TestSuite.Add(Suite, "TextureStDerived", TextureStDerived);
        TestSuite.Add(Suite, "IncludeFunctionAndStruct", IncludeFunctionAndStruct);
        TestSuite.Add(Suite, "WarmerIncludeSetDebounce", WarmerIncludeSetDebounce);
        TestSuite.Add(Suite, "RescanAllRefreshes", RescanAllRefreshes);
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

    /// <summary>闭包语料：URP 样的转换函数与结构体（形状取自真实 SpaceTransforms.hlsl 的登记结果）。</summary>
    private const string UrpHlsl = """
        float4 TransformObjectToHClip(float3 positionOS) { return float4(positionOS, 1); }
        struct VertexPositionInputs { float3 positionWS; float3 positionVS; };
        """;

    /// <summary>引用上者闭包的文档；"Transform" / "Vertex" 两处是补全光标锚点。</summary>
    private const string UrpDoc = """
        Shader "T/Urp"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #include "Fake/Urp.hlsl"
                    float4 v = Transform
                    Vertex
                    ENDHLSL
                }
            }
        }
        """;

    private sealed class FakeSource : IIncludeFileSource
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>TryRead 被调次数（去抖/重扫用例的观测量）。</summary>
        public int ReadCount;

        /// <summary>全体文件的写入戳（RescanAll 用例把它改大模拟「磁盘上变了」）。</summary>
        public long Stamp = 1;

        public FakeSource(string virtualPath, string physicalPath, string text)
            => _files[physicalPath] = text;

        /// <summary>替换某文件的磁盘内容（模拟头文件被编辑）。</summary>
        public void Write(string physicalPath, string text) => _files[physicalPath] = text;

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
                ReadCount++;
                return true;
            }

            text = string.Empty;
            return false;
        }

        public long GetStamp(string physicalPath) => Stamp;
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

    // ── 6. 宏实参触发（"CBUFFER_START(" / "SAMPLER(" 括号内）──

    private static void MacroArgumentTrigger()
    {
        var t1 = "CBUFFER_START(";
        var l1 = TriggerContext.Detect(t1, 0, t1.Length);
        Check.Equal(TriggerKind.MacroArgument, l1.Kind, "\"CBUFFER_START(\" 后应判为宏实参");
        Check.Equal("CBUFFER_START", l1.CallName, "调用名");
        Check.Equal("", l1.Prefix, "无前缀");

        var t2 = "CBUFFER_START(Uni";
        var l2 = TriggerContext.Detect(t2, 0, t2.Length);
        Check.Equal(TriggerKind.MacroArgument, l2.Kind, "带前缀的宏实参");
        Check.Equal("Uni", l2.Prefix, "前缀内容");

        var t3 = "SAMPLER(sampler_Base";
        var l3 = TriggerContext.Detect(t3, 0, t3.Length);
        Check.Equal(TriggerKind.MacroArgument, l3.Kind, "SAMPLER 宏实参");
        Check.Equal("SAMPLER", l3.CallName, "调用名");
        Check.Equal("sampler_Base", l3.Prefix, "前缀内容");

        var t4 = "CBUFFER_START ( ";
        Check.Equal(TriggerKind.MacroArgument, TriggerContext.Detect(t4, 0, t4.Length).Kind, "闭括号前有空格也应识别");

        Check.Equal(TriggerKind.None, TriggerContext.Detect("Foo(", 0, 4).Kind, "未知宏的括号内不得触发（保持原行为）");
        Check.Equal(TriggerKind.None, TriggerContext.Detect("// CBUFFER_START(", 0, 18).Kind, "注释内不得触发");

        var t7 = "CBUFFER_START(UnityPerMaterial)";
        Check.Equal(TriggerKind.None, TriggerContext.Detect(t7, 0, t7.Length).Kind, "闭括号之后不得触发");
    }

    // ── 7. CBUFFER_START( 括号内：UnityPer* 名单（UnityPerMaterial 第一）──

    private static void CbufferArgumentNames()
    {
        var text = "CBUFFER_START(";
        var json = CompleteAt(new IntelliSenseService(), "/d/doc.shader", text, text.Length);
        Check.True(json is not null, "括号内必须给出候选");

        var labels = Labels(json!);
        Check.Equal("UnityPerMaterial", labels[0], "UnityPerMaterial 必须排在第一位");
        Check.Contains(labels, "UnityPerDraw", "应含引擎缓冲区名");
        Check.True(labels.Count <= 8, "括号内只给语义候选（不倒全量），实际 " + labels.Count + " 条");

        var partial = "CBUFFER_START(Uni";
        var json2 = CompleteAt(new IntelliSenseService(), "/d/doc.shader", partial, partial.Length);
        Check.True(json2 is not null, "带前缀同样要有候选");
        Check.Equal("UnityPerMaterial", Labels(json2!)[0], "前缀过滤后 UnityPerMaterial 仍第一");
    }

    // ── 8. SAMPLER( 括号内：已声明 sampler 名 + 推导约定名 ──

    private static void SamplerArgumentNames()
    {
        // 情形 A：纹理已声明、sampler 还没写 → 推导 sampler_MainTex。
        var pending = "TEXTURE2D(_MainTex);\nSAMPLER(";
        var json = CompleteAt(new IntelliSenseService(), "/d/doc.shader", pending, pending.Length);
        Check.True(json is not null, "未声明 sampler 时必须给出推导候选");
        Check.Contains(Labels(json!), "sampler_MainTex", "应推导出 sampler + 纹理名");

        // 情形 B：已经写过 SAMPLER(sampler_MainTex) → 以已声明名给出，且不叠加推导重复项。
        var declared = "TEXTURE2D(_MainTex);\nSAMPLER(sampler_MainTex);\nSAMPLER(";
        var json2 = CompleteAt(new IntelliSenseService(), "/d/doc.shader", declared, declared.Length);
        Check.True(json2 is not null, "已声明 sampler 时必须给出候选");
        var labels2 = Labels(json2!);
        Check.Contains(labels2, "sampler_MainTex", "已声明的 sampler 名应在候选里");
        Check.Equal(1, labels2.Count, "已声明后不得叠加推导重复项，实际 " + string.Join(",", labels2));
    }

    // ── 9. 纹理派生 _ST/_TexelSize ──

    private static void TextureStDerived()
    {
        var text = "TEXTURE2D(_MainTex);\nfloat4 _Mai";
        var json = CompleteAt(new IntelliSenseService(), "/d/doc.shader", text, text.Length);
        Check.True(json is not null, "写 _Mai 时必须给出候选");
        var labels = Labels(json!);
        Check.Contains(labels, "_MainTex_ST", "应推导出 _MainTex_ST");
        Check.Contains(labels, "_MainTex_TexelSize", "应推导出 _MainTex_TexelSize");

        // 已显式声明过 _MainTex_ST → 不重复推导（文档变量本身仍在，计 1 条）。
        var declared = "TEXTURE2D(_MainTex);\nfloat4 _MainTex_ST;\nfloat4 _Mai";
        var json2 = CompleteAt(new IntelliSenseService(), "/d/doc.shader", declared, declared.Length);
        var labels2 = Labels(json2!);
        Check.Contains(labels2, "_MainTex_TexelSize", "未声明项仍应推导");
        Check.Equal(1, CountOf(labels2, "_MainTex_ST"), "已声明项不得重复给出");
    }

    // ── 10. include 闭包的函数与结构体 ──

    private static void IncludeFunctionAndStruct()
    {
        var uri = "/shadow/doc.shader";
        var cache = new IncludeSymbolCache();
        var source = new FakeSource("Fake/Urp.hlsl", "Fake/Urp.hlsl", UrpHlsl);

        using var warmer = new IncludeSymbolWarmer(source, cache, Manual);
        warmer.Enqueue(uri, uri, UrpDoc);
        warmer.Drain();

        var service = new IntelliSenseService(new IntelliSenseOptions { DocumentPath = uri },
            new IncludeSymbolProvider { Cache = cache, Files = source });

        var prefix = "Transform";
        var offset = UrpDoc.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
        var json = CompleteAt(service, uri, UrpDoc, offset);
        Check.True(json is not null, "闭包函数前缀必须给出候选");
        var labels = Labels(json!);
        Check.Contains(labels, "TransformObjectToHClip", "应补出 URP 转换函数");
        Check.Equal(1, CountOf(labels, "TransformObjectToHClip"), "同名函数不得重复给出");

        var structPrefix = "Vertex";
        var structOffset = UrpDoc.IndexOf(structPrefix, StringComparison.Ordinal) + structPrefix.Length;
        var json2 = CompleteAt(service, uri, UrpDoc, structOffset);
        Check.True(json2 is not null, "闭包结构体前缀必须给出候选");
        Check.Contains(Labels(json2!), "VertexPositionInputs", "应补出闭包结构体");
    }

    // ── 11. 预热去抖：include 集合未变时不得重扫 ──

    private static void WarmerIncludeSetDebounce()
    {
        var uri = "/shadow/doc.shader";
        var cache = new IncludeSymbolCache();
        var source = new FakeSource("Fake/Varyings.hlsl", "Fake/Varyings.hlsl", VaryingsHlsl);
        source.Write("Fake/Extra.hlsl", "float4 _Extra;");

        using var warmer = new IncludeSymbolWarmer(source, cache, Manual);
        warmer.Enqueue(uri, uri, Doc);
        warmer.Drain();
        var readsAfterFirst = source.ReadCount;
        Check.True(readsAfterFirst >= 1, "首次预热必须读 include 文件");

        // 正文变化、include 集合未变 → 跳过（不产生任何读取）。
        warmer.Enqueue(uri, uri, Doc + "\n// extra\n");
        warmer.Drain();
        Check.Equal(readsAfterFirst, source.ReadCount, "include 集合未变时不得重扫");

        // include 集合变化（指向另一个文件）→ 必须重扫。
        var changed = Doc.Replace("Fake/Varyings.hlsl", "Fake/Extra.hlsl");
        warmer.Enqueue(uri, uri, changed);
        warmer.Drain();
        Check.True(source.ReadCount > readsAfterFirst, "include 集合变化必须重扫");
    }

    // ── 12. RescanAll：磁盘变化后按写入戳重扫 ──

    private static void RescanAllRefreshes()
    {
        var uri = "/shadow/doc.shader";
        var cache = new IncludeSymbolCache();
        var source = new FakeSource("Fake/Fn.hlsl", "Fake/Fn.hlsl", "float4 OldFn() { return 0; }");

        const string fnDoc = "Shader \"T\"\n{\n    SubShader\n    {\n        Pass\n        {\n            HLSLPROGRAM\n            #include \"Fake/Fn.hlsl\"\n            OldFn();\n            ENDHLSL\n        }\n    }\n}";

        using var warmer = new IncludeSymbolWarmer(source, cache, Manual);
        warmer.Enqueue(uri, uri, fnDoc);
        warmer.Drain();
        Check.True(cache.TryGetFileByPath("Fake/Fn.hlsl", out var before), "预热后应有文件符号");
        Check.True(before.Declarations.ContainsKey("OldFn"), "应含旧函数");

        // 头文件在磁盘上变了（内容与写入戳都变）。
        source.Write("Fake/Fn.hlsl", "float4 NewFn() { return 0; }");
        source.Stamp = 2;

        warmer.RescanAll();
        warmer.Drain();

        Check.True(cache.TryGetFileByPath("Fake/Fn.hlsl", out var after), "重扫后应有文件符号");
        Check.True(after.Declarations.ContainsKey("NewFn"), "重扫后应含新函数");
        Check.True(!after.Declarations.ContainsKey("OldFn"), "重扫后不得残留旧函数");
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

    /// <summary>在任意偏移处发一次补全请求；返回原始 JSON（未触发为 null）。</summary>
    private static string? CompleteAt(IntelliSenseService service, string uri, string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < offset && i < text.Length; i++)
        {
            if (text[i] == (char)10)
            {
                line++;
                lineStart = i + 1;
            }
        }

        var writer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(writer))
        {
            if (!service.ProvideCompletions(new PositionRequest(uri, text, line, offset - lineStart), json))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    /// <summary>统计候选里某名字出现的次数（重复给出的回归断言用）。</summary>
    private static int CountOf(List<string> labels, string name)
    {
        var count = 0;
        foreach (var label in labels)
        {
            if (label.Equals(name, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }
}
