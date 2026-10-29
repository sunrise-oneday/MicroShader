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
        TestSuite.Add(Suite, "FallbackEntryUpgradesToFullSnapshot", FallbackEntryUpgradesToFullSnapshot);
        TestSuite.Add(Suite, "RescanAllRefreshes", RescanAllRefreshes);
        TestSuite.Add(Suite, "ReclaimAfterTopologyChange", ReclaimAfterTopologyChange);
        TestSuite.Add(Suite, "InternConcurrent", InternConcurrent);
        TestSuite.Add(Suite, "IncludeScanEquivalence", IncludeScanEquivalence);
        TestSuite.Add(Suite, "IdentifierScanBoundaries", IdentifierScanBoundaries);
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

        public bool TryResolve(string currentPhysicalPath, string virtualPath, out string physicalPath)
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
            => TryResolve(currentPhysicalPath, target, out physicalPath);

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

    // ── 13. 文件层容量上限：常驻只增不减的反面保障 ──

    /// <summary>
    /// 文件层必须有容量上限：写满之后整表回收，而不是一直收下去。
    /// </summary>
    /// <remarks>
    /// 上限只落在文件层：文档层条目数由「打开的文档数」决定（关闭即失效），
    /// 文件层却由「看过的头文件数」决定 —— 编辑器开得越久越大，正是这里要堵的增长线。
    /// 回收是粗粒度的整表清空，不追求精确 LRU：被丢掉的文件下一次预热就会重新扫回来。
    /// </remarks>
    private static void ReclaimAfterTopologyChange()
    {
        var uri = "/shadow/doc.shader";
        var cache = new IncludeSymbolCache();
        var source = new FakeSource("Fake/Varyings.hlsl", "Fake/Varyings.hlsl", VaryingsHlsl);

        using var warmer = new IncludeSymbolWarmer(source, cache, Manual);
        warmer.Enqueue(uri, uri, Doc);
        warmer.Drain();

        Check.True(cache.FileCount > 0, "预热后文件层应当有条目");
        Check.True(cache.DocumentCount > 0, "预热后文档层应当有条目");

        // 模拟「包装好了 / 换了 Unity 版本」：整份拓扑重建成功后的回收。
        warmer.ReclaimForTopologyChange();
        Check.Equal(0, cache.FileCount, "回收后文件层必须归零（只增不删就是这里漏的）");
        Check.Equal(0, cache.DocumentCount, "回收后文档层必须归零");

        // ① 清完立刻请求：走同步兜底现场补扫，仍必须给出候选。
        var service = new IntelliSenseService(
            new IntelliSenseOptions { DocumentPath = uri },
            new IncludeSymbolProvider { Cache = cache, Files = source });

        var json = CompleteWith(service, uri);
        Check.True(json is not null, "清空后第一次请求必须仍能给出候选（F12 / 补全的同步兜底覆盖得到）");
        Check.Contains(Labels(json!), "color", "同步兜底应补出 include 里的字段");
        Check.True(cache.FileCount > 0, "同步兜底应把扫到的文件写回文件缓存");

        // ② 同一文档再次预热：去抖表已随回收清掉，必须真的重扫并重建文档层。
        warmer.Enqueue(uri, uri, Doc);
        warmer.Drain();
        Check.True(cache.DocumentCount > 0, "回收后同一文档再预热必须真的重扫（去抖键未清会整批跳过）");
        Check.True(cache.TryGet(uri, "Varyings", out _), "重扫后应重新查到 Varyings");
    }

    /// <summary>
    /// 补全同步兜底写的"只有结构体表"的条目，不得被当成"已完整扫描"。
    /// </summary>
    /// <remarks>
    /// 兜底只写结构体表就落条目，若预热器按"写入戳相同"跳过，那个文件永远补不上
    /// 声明表与行首表 —— 导航侧虽然会现场补扫自愈，但每次 F12 都要付一次读盘。
    /// </remarks>
    private static void FallbackEntryUpgradesToFullSnapshot()
    {
        var cache = new IncludeSymbolCache();
        const string file = "Fake/Header.hlsl";
        const string content = "struct ProbeStruct { float4 v; };\\nfloat4 ProbeFn() { return 0; }";
        const long stamp = 1;

        var full = IncludeSymbolCache.FileSymbols.Build(content, stamp, cache.Interner);

        // ① 兜底写法：只有结构体表。
        cache.PutFile(file, stamp, full.Structs);
        Check.True(cache.TryGetFile(file, stamp, out var structsOnly), "兜底条目必须能给补全用（结构体表）");
        Check.True(structsOnly.ContainsKey("ProbeStruct"), "兜底条目应含该文件的结构体");
        Check.True(!cache.TryGetFileByPath(file, out _), "只有结构体表时导航必须视为未缓存（现场补扫自愈）");
        Check.True(!cache.HasFullSnapshot(file, stamp), "预热器不得把它当成已完整扫描");

        // ② 写入完整快照后：导航命中、预热可跳过。
        cache.PutFile(file, full);
        Check.True(cache.HasFullSnapshot(file, stamp), "完整快照后预热器应能跳过");
        Check.True(cache.TryGetFileByPath(file, out var symbols) && symbols is not null, "导航应拿到完整快照");

        // ③ 写入戳变了 → 不再算命中（陈旧）。
        Check.True(!cache.HasFullSnapshot(file, stamp + 1), "写入戳变化后必须重扫");
    }

    /// <summary>
    /// 并发登记：预热线程与会话线程同时 Intern 同一批标识符时不得抛异常，
    /// 且同一标识符在全表只有一个实例。
    /// </summary>
    /// <remarks>
    /// 规模为什么是 8 线程 × 500 轮 × 32 个名字：竞态只出现在「未命中 → 写表」那一小段，
    /// 若反复 Intern 固定的一批名字，写入在开头几十次内就结束了，之后全是无锁读，
    /// 修复前的裸字典几乎测不出来。所以每轮换一批新名字（共 16,000 个唯一键），让写入贯穿
    /// 全程；8 个线程抢同一批键，合计 12.8 万次 Intern，插入与扩容交错，修复前必然抛异常
    /// （或把桶链写坏），修复后每个键只登记一次、Count 恰好是 16,000。
    /// </remarks>
    private static void InternConcurrent()
    {
        const int threads = 8;
        const int rounds = 500;
        const int words = 32;

        var interner = new StringInterner();

        // 第一轮结果按线程留档：8 个线程抢的都是同一批新名字，若表做不到「一个值一个实例」，
        // 各线程就会拿回自己造的那份字符串，ReferenceEquals 立刻露馅。
        var firstRound = new string?[threads][];
        Exception? failure = null;
        using var start = new ManualResetEventSlim(false);
        var workers = new Thread[threads];

        for (var t = 0; t < threads; t++)
        {
            var id = t;
            workers[t] = new Thread(() =>
            {
                var mine = new string?[words];
                try
                {
                    start.Wait();
                    for (var round = 0; round < rounds; round++)
                    {
                        for (var w = 0; w < words; w++)
                        {
                            var name = "race_" + round.ToString(System.Globalization.CultureInfo.InvariantCulture)
                                + "_" + w.ToString(System.Globalization.CultureInfo.InvariantCulture);

                            // 两个重载都要走：string 版是补全 / 导航路径，span 版是块索引构建的热路径。
                            var got = (w & 1) == 0 ? interner.Intern(name) : interner.Intern(name.AsSpan());
                            if (!string.Equals(got, name, StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException("Intern 返回了不一致的内容：" + got);
                            }

                            if (round == 0)
                            {
                                mine[w] = got;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }

                firstRound[id] = mine;
            })
            {
                IsBackground = true,
                Name = "interner-race-" + id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };
            workers[t].Start();
        }

        start.Set();
        foreach (var worker in workers)
        {
            worker.Join();
        }

        Check.True(failure is null, "并发 Intern 不得抛异常：" + failure);

        for (var w = 0; w < words; w++)
        {
            var canonical = firstRound[0][w];
            Check.NotNull(canonical, "第一轮的标识符都应拿到非空实例");

            for (var t = 1; t < threads; t++)
            {
                Check.True(
                    ReferenceEquals(canonical, firstRound[t][w]),
                    "标识符 " + w + " 在所有线程必须拿到同一个实例");
            }

            // 事后再查一次：表里留存的必须还是那个实例。
            Check.True(ReferenceEquals(canonical, interner.Intern(canonical!)), "表里应只留一个实例");
        }

        // 键是所有线程共享的，所以唯一键数只按轮次算；多线程重复登记不得把表撑大。
        Check.Equal(rounds * words, interner.Count, "每个键只登记一次");
    }

    // ── 14. include 扫描：向量化改写前后逐项一致 ──

    /// <summary>
    /// include 扫描的等价性：新实现（整篇 SearchValues 找 # + 行内切片）与改造前
    /// （逐行 TrimStart + StartsWith）在同一批输入上必须给出逐项相同的路径序列。
    /// </summary>
    /// <remarks>
    /// 四种形态各一条，另把行首、行尾、全命中、零命中、空串压到边界上：
    /// 注释里的 #include 不得被收、行中的 # 号不得被当成指令、尖括号与引号两种形态都要认、
    /// 未闭合的引号与尖括号必须整行跳过。
    /// </remarks>
    private static void IncludeScanEquivalence()
    {
        var cases = new (string Name, string Text)[]
        {
            ("空串", ""),
            ("零命中：无 # 无换行", "float4 _Color;"),
            ("零命中：多行全无 #", "float4 _A;\r\nfloat4 _B;\nfloat4 _C;"),
            ("全命中：每行一条指令", "#include \"A.hlsl\"\n#include \"B.hlsl\"\n#include \"C.hlsl\""),
            ("注释里的 #include", "// #include \"Fake/Line.hlsl\"\n/* #include \"Fake/Block.hlsl\" */\n  //#include \"Fake/Tight.hlsl\""),
            ("行中的 # 号", "float x = 1; // # 只是注释\n#define HALF 0.5\n#pragma multi_compile _ X\nfloat2 y = float2(1, 2); #nope"),
            ("尖括号", "#include <UnityCG.cginc>\n#include <Packages/com.x/Shader.hlsl>\n#include <Unclosed"),
            ("未闭合引号", "#include \"Fake/Unclosed.hlsl\n#include \"Fake/Ok.hlsl\"\n#include \""),
            ("行首空白与 CRLF", "\t  #include \"Lead.hlsl\"\r\n   #include <Angle.hlsl>\r\n"),
            ("行尾：末行是指令且无换行", "float a;\n#include \"Last.hlsl\""),
            ("#include_with_pragmas", "#include_with_pragmas \"Fake/Pragmas.hlsl\"\n#include_with_pragmas <Fake/AnglePragmas.hlsl>"),
            ("同行多个 # 与空路径", "#include \"Yes.hlsl\" #include \"No2.hlsl\"\n#include \"\"\n#include"),
            ("无 # 的行之后才出现指令", "float4 a;\n// nothing\n\n  #include \"After.hlsl\"\n"),
            ("整篇只有一个 #", "#"),
        };

        foreach (var (name, text) in cases)
        {
            Check.SequenceEqual(ScanLegacy(text), IncludeLineScanner.Scan(text), "include 扫描结果必须与改造前逐项一致：" + name);
        }

        // 除逐项一致外，再把两条最容易被优化改走的语义单独钉住。
        var angle = IncludeLineScanner.Scan("#include <UnityCG.cginc>");
        Check.Equal(1, angle.Count, "尖括号形态应收到一条");
        Check.Equal("UnityCG.cginc", angle[0], "尖括号里的路径原样带出");

        var comment = IncludeLineScanner.Scan("// #include \"Fake/Line.hlsl\"");
        Check.Equal(0, comment.Count, "注释里的 #include 不得被收集");
    }

    // ── 15. 标识符切分：向量化改写前后逐项一致 ──

    /// <summary>
    /// 标识符切分的边界等价性：新实现用 span.IndexOfAnyExcept 一次定位边界，只有两种收尾
    /// （命中非标识符字符，或 -1 表示余下全是标识符）。这里把两种收尾都压到边界上：
    /// 标识符正好落在行首、行尾、整篇（全命中），以及一个标识符都没有（零命中）与空串。
    /// </summary>
    private static void IdentifierScanBoundaries()
    {
        // ① 行首 / 行尾：三条声明的名字偏移必须是切片算出的绝对偏移（7 / 18 / 29）。
        var index = HlslDocumentIndex.Build("float4 _A;\nfloat4 _B;\nfloat4 _C");
        Check.Equal(3, index.Declarations.Count, "三条声明都要登记");
        Check.Equal(7, index.Declarations["_A"][0].Offset, "_A 的名字偏移");
        Check.Equal(18, index.Declarations["_B"][0].Offset, "_B 的名字偏移");
        Check.Equal(29, index.Declarations["_C"][0].Offset, "_C 的名字偏移（贴着篇末，走 -1 收尾）");
        Check.Equal("float4", index.VariablesToType["_C"], "末尾标识符的类型仍要绑定");

        // ② 全命中：整篇就是一个标识符 → 一次扫描吃到篇末，且它不是类型名，什么也不登记。
        var whole = HlslDocumentIndex.Build("abcdefghijklmnop");
        Check.Equal(0, whole.Declarations.Count, "整篇一个标识符时不得登记声明");

        // ③ 零命中：一个标识符字符都没有 → 标识符分支一次也不进。
        var none = HlslDocumentIndex.Build(";;;,,,(){}");
        Check.Equal(0, none.Declarations.Count, "没有标识符时不得登记声明");

        // ④ 空串：循环一次也不进。
        var empty = HlslDocumentIndex.Build("");
        Check.Equal(0, empty.Declarations.Count, "空串不得登记声明");
        Check.Equal(0, empty.Structs.Count, "空串不得登记结构体");

        // ⑤ 只认 ASCII：非 ASCII 字符必须截断标识符，不能被并进名字里。
        var ascii = HlslDocumentIndex.Build("float4 _A中;");
        Check.True(ascii.VariablesToType.ContainsKey("_A"), "非 ASCII 字符前的标识符照常登记");
        Check.True(!ascii.VariablesToType.ContainsKey("_A中"), "非 ASCII 字符不得并进标识符");
    }

    // ── 辅助 ──

    /// <summary>改造前的 include 扫描实现（逐行 TrimStart + StartsWith），等价性断言的基准。</summary>
    // 这份代码是改造前的快照，只服务等价性断言：任何性能改写都必须让它与新实现的输出逐项一致。
    // 刻意保持原样（包括引号优先于尖括号、close > 0 才算闭合这些细节），不要跟着新实现一起改。
    private static List<string> ScanLegacy(string text)
    {
        var found = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var nl = text.IndexOf((char)10, start);
            var end = nl < 0 ? text.Length : nl;
            var line = text.AsSpan(start, end - start).TrimStart();

            if (line.StartsWith("#include", StringComparison.Ordinal))
            {
                var quote = line.IndexOf('"');
                var open = line.IndexOf('<');
                var from = quote >= 0 ? quote + 1 : open + 1;
                if (from > 0)
                {
                    var rest = line[from..];
                    var close = quote >= 0 ? rest.IndexOf('"') : rest.IndexOf('>');
                    if (close > 0)
                    {
                        found.Add(rest[..close].ToString());
                    }
                }
            }

            if (nl < 0)
            {
                break;
            }

            start = nl + 1;
        }

        return found;
    }

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
