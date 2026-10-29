using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;
using MicroShader.NavigationEngine;
using MicroShader.ObservabilityEngine;

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

    // 陈旧快照用的头文件：同一个函数被挪到第 5 行，行数从 1 变成 5。
    // 拿旧快照（第 1 行）跳过去，落点在新文本里已经是一行注释。
    private const string StaleShiftedHeader =
        "// 头文件被外部改过，声明的行号整体下移\n" +
        "// 1\n" +
        "// 2\n" +
        "// 3\n" +
        "float4 MS_LocalOnly(float4 x) { return x; }\n";

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

    // 结构体字段定位用的最小语料：两个相邻结构体，字段名互不重复。
    // 手写 shader 的常见形态，字段带语义标注（TEXCOORD5 等）且前一个结构体字段数更少。
    private const string FieldDocument = """
        Shader "T/Field"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    struct Attributes
                    {
                        float4 positionOS : POSITION;
                        float4 tangentOS : TANGENT;
                        float2 uv : TEXCOORD0;
                    };

                    struct Varyings
                    {
                        float4 positionCS : SV_POSITION;
                        float2 uv : TEXCOORD0;
                        float3 normalWS : TEXCOORD1;
                        float3 camDirWS : TEXCOORD5;
                    };

                    half4 frag(Varyings IN) : SV_Target
                    {
                        half3 camDir = lerp(IN.camDirWS, abs(IN.camDirWS), 0.5);
                        return half4(camDir, 1);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // 孤儿 Pass 用的语料：程序块直接写在 SubShader 里、不在任何 Pass 内。
    // 这种块的 PassStartLineNumber 是 -1，Pass 节点的行只能退回程序块首行。
    private const string OrphanPassDocument = """
        Shader "T/OrphanPass"
        {
            SubShader
            {
                HLSLPROGRAM
                half4 frag() : SV_Target { return 1; }
                ENDHLSL
            }
        }
        """;

    // 跨文件字段用的语料：结构体定义在 include 头文件里，字段在 shader 正文里被访问。
    private const string FieldHeaderPhysical =
        "D:/proj/Library/PackageCache/com.test.shaders@1.0.0/ShaderLibrary/FieldTypes.hlsl";

    private const string FieldHeader = """
        struct MS_Surface
        {
            float3 normalWS;
            float3 viewDirWS;
            float2 uv;
        };
        """;

    private const string FieldIncludeDocument = """
        Shader "T/FieldInclude"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #include "Packages/com.test.shaders/ShaderLibrary/FieldTypes.hlsl"

                    half4 frag(MS_Surface surface) : SV_Target
                    {
                        half3 n = surface.normalWS * 0.5 + 0.5;
                        return half4(n, 1);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // 成员写入点用的语料：照搬手写 shader 的形态 —— 片元里读的 IN.positionWS，
    // 值来自顶点里那条 o.positionWS = …；另有同名不同结构体的一条写入，用来验类型过滤。
    private const string VaryingWriteDocument = """
        Shader "T/MemberWrite"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    struct Attributes
                    {
                        float4 positionOS : POSITION;
                        float2 uv : TEXCOORD0;
                    };

                    struct Varyings
                    {
                        float4 positionCS : SV_POSITION;
                        float2 uv : TEXCOORD0;
                        float3 positionWS : TEXCOORD4;
                    };

                    struct OtherVaryings
                    {
                        float3 positionWS : TEXCOORD1;
                    };

                    Varyings vert(Attributes IN)
                    {
                        Varyings o;
                        OtherVaryings other;
                        o.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                        o.uv = IN.uv;
                        other.positionWS = 1;
                        return o;
                    }

                    half4 frag(Varyings IN) : SV_Target
                    {
                        DoSomething(IN.positionWS, IN.uv);
                        return 0;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // 结构体写在 HLSLINCLUDE、函数写在 Pass 里的形态（Unity 的共享块会拼进每个 Pass）。
    // 变量与结构体不在同一块里，写入点回溯必须两边都看得到。
    private const string SharedStructWriteDocument = """
        Shader "T/SharedMemberWrite"
        {
            HLSLINCLUDE
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD4;
            };
            ENDHLSL

            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    Varyings vert(Attributes IN)
                    {
                        Varyings o;
                        o.positionWS = IN.positionOS.xyz;
                        return o;
                    }

                    half4 frag(Varyings IN) : SV_Target
                    {
                        DoSomething(IN.positionWS);
                        return 0;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // 脏（未存盘）头文件语料：磁盘上 3 行，编辑器缓冲区里 8 行。
    // 声明在磁盘文本的第 2 行（0-based 1）、内存文本的第 7 行（0-based 6）——
    // 行数不同，且偏移也随之前移，拿磁盘快照的行表换算必然落错。
    private const string DirtyHeaderOnDisk = """
        // 磁盘版：作者刚打开这个头文件
        float4 MS_LocalOnly(float4 x) { return x; }
        // 还没动过
        """;

    private const string DirtyHeaderInMemory = """
        // 内存版：作者在声明上方插了几行注释，还没存盘
        //
        //
        //
        //
        //
        float4 MS_LocalOnly(float4 x) { return x; }
        // 下方也多了一行
        """;

    // 宏别名写在 HLSLINCLUDE 里：共享块会拼进每个 Pass，所以在 Pass 内按 F12 必须
    // 同时拿到「宏定义处」与「展开后的实现处」。
    private const string SharedMacroDocument = """
        Shader "T/SharedMacro"
        {
            HLSLINCLUDE
            #define MS_SHARED_ALIAS MS_SHARED_IMPL
            float3 MS_SHARED_IMPL(float3 p) { return p; }
            ENDHLSL

            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    float3 frag() : SV_Target
                    {
                        return MS_SHARED_ALIAS(1);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // 宏可见性顺序用例语料：B 里先 include D，所以正文顺序是 B、D、C，而 BFS 顺序是 B、C、D。
    // D 与 C 都重定义同名宏 MS_ORDER_IMPL，各指向本文件里的实现函数 —— 两种顺序结论相反。
    private const string IncludeOrderBPhysical = "D:/proj/Assets/Shaders/OrderB.hlsl";

    private const string IncludeOrderCPhysical = "D:/proj/Assets/Shaders/OrderC.hlsl";

    private const string IncludeOrderDPhysical = "D:/proj/Assets/Shaders/OrderD.hlsl";

    private const string IncludeOrderB = """
        #include "OrderD.hlsl"
        float3 MS_ORDER_IN_B(float3 p) { return p; }
        """;

    private const string IncludeOrderC = """
        #define MS_ORDER_IMPL MS_ORDER_IN_C
        float3 MS_ORDER_IN_C(float3 p) { return p * 2; }
        """;

    private const string IncludeOrderD = """
        #define MS_ORDER_IMPL MS_ORDER_IN_D
        float3 MS_ORDER_IN_D(float3 p) { return p * 3; }
        """;

    private const string IncludeOrderDocument = """
        Shader "T/MacroOrder"
        {
            HLSLINCLUDE
            #define MS_ORDER_ALIAS MS_ORDER_IMPL
            ENDHLSL

            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #include "OrderB.hlsl"
                    #include "OrderC.hlsl"
                    #pragma fragment frag

                    half4 frag() : SV_Target
                    {
                        return half4(MS_ORDER_ALIAS(1), 1);
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
        TestSuite.Add(Suite, "OutlineStructFieldPosition", OutlineStructFieldPosition);
        TestSuite.Add(Suite, "OutlineOrphanPassStartLine", OutlineOrphanPassStartLine);
        TestSuite.Add(Suite, "DefinitionLocalFunction", DefinitionLocalFunction);
        TestSuite.Add(Suite, "DefinitionSharedInclude", DefinitionSharedInclude);
        TestSuite.Add(Suite, "DefinitionCrossFile", DefinitionCrossFile);
        TestSuite.Add(Suite, "DefinitionStructField", DefinitionStructField);
        TestSuite.Add(Suite, "DefinitionCrossFileStructField", DefinitionCrossFileStructField);
        TestSuite.Add(Suite, "DefinitionMemberWritePoint", DefinitionMemberWritePoint);
        TestSuite.Add(Suite, "DefinitionMemberWriteByType", DefinitionMemberWriteByType);
        TestSuite.Add(Suite, "DefinitionMemberWriteSharedStruct", DefinitionMemberWriteSharedStruct);
        TestSuite.Add(Suite, "DefinitionOverloads", DefinitionOverloads);
        TestSuite.Add(Suite, "DefinitionMacroTwoCandidates", DefinitionMacroTwoCandidates);
        TestSuite.Add(Suite, "DefinitionDirtyFileUri", DefinitionDirtyFileUri);
        TestSuite.Add(Suite, "DefinitionDirtyHeaderLine", DefinitionDirtyHeaderLine);
        TestSuite.Add(Suite, "DefinitionSharedMacroAlias", DefinitionSharedMacroAlias);
        TestSuite.Add(Suite, "OpenDocumentCaseInsensitive", OpenDocumentCaseInsensitive);
        TestSuite.Add(Suite, "ShapeFollowsClientCapability", ShapeFollowsClientCapability);
        TestSuite.Add(Suite, "CyclicIncludeSafe", CyclicIncludeSafe);
        TestSuite.Add(Suite, "DocumentLinksIncludeWithPragmas", DocumentLinksIncludeWithPragmas);
        TestSuite.Add(Suite, "DefinitionPrefersNearerInclude", DefinitionPrefersNearerInclude);
        TestSuite.Add(Suite, "DefinitionMacroIncludeTextOrder", DefinitionMacroIncludeTextOrder);
        TestSuite.Add(Suite, "DefinitionMacroMissingSnapshotTraced", DefinitionMacroMissingSnapshotTraced);
        TestSuite.Add(Suite, "DefinitionStaleSnapshotSkipped", DefinitionStaleSnapshotSkipped);
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

    /// <summary>#include_with_pragmas 也必须给出超链接（URP 的 Lit 系列全靠它）。</summary>
    /// <remarks>
    /// 下划线既是 Unity 后缀的分隔符、又属于标识符字符：边界判定要是把下划线一并当标识符，
    /// 整行会被直接丢弃，URP 那类 shader 的 include 一条链接都没有。用例同时钉住反方向 ——
    /// 不带下划线的长标识符 includeSomething 不得被当成 include。
    /// </remarks>
    private static void DocumentLinksIncludeWithPragmas()
    {
        const string document = """
            Shader "T/Pragmas"
            {
                HLSLINCLUDE
                #include_with_pragmas "Packages/com.test.shaders/ShaderLibrary/Common.hlsl"
                #include_with_pragmas <Local.hlsl>
                #includeSomething "Nope.hlsl"
                ENDHLSL
            }
            """;

        var host = new FakeHost();
        var service = new NavigationService(host, new IncludeSymbolCache());

        var json = Links(service, DocumentUri, host.Physical(DocumentUri), document);
        Check.True(json is not null, "#include_with_pragmas 必须被识别成 include（否则整篇文档零链接）");

        var links = JsonDocument.Parse(json!).RootElement;
        Check.Equal(2, links.GetArrayLength(), "引号与尖括号两种写法各一条；includeSomething 不得占位");

        Check.Equal("Packages/com.test.shaders/ShaderLibrary/Common.hlsl",
            links[0].GetProperty("data").GetProperty("target").GetString(), "引号写法必须取到路径");
        Check.Equal("Local.hlsl",
            links[1].GetProperty("data").GetProperty("target").GetString(), "尖括号写法必须取到路径");

        // range 与普通 include 同一口径：只覆盖引号/尖括号内的路径，不含分隔符
        var range = links[1].GetProperty("range");
        var lineText = LineOfText(document, range.GetProperty("start").GetProperty("line").GetInt32());
        var from = range.GetProperty("start").GetProperty("character").GetInt32();
        var covered = lineText.Substring(from, range.GetProperty("end").GetProperty("character").GetInt32() - from);
        Check.Equal("Local.hlsl", covered, "尖括号写法的 range 只覆盖路径本身");

        Check.Equal(0, host.Reads, "首屏仍然必须零 I/O");
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

    /// <summary>大纲里的结构体字段节点必须落在字段自身的声明行上。</summary>
    /// <remarks>
    /// 字段节点直接用索引里的偏移换算位置，与 F12 共用同一份数据：偏移基准一错，
    /// 大纲里点字段也会跳到前一个结构体上去。锁住它，避免只修好 F12 而把大纲留在错位上。
    /// </remarks>
    private static void OutlineStructFieldPosition()
    {
        var host = new FakeHost();
        var service = new NavigationService(host, new IncludeSymbolCache(),
            new NavigationOptions { HierarchicalDocumentSymbol = true });

        var json = Symbols(service, DocumentUri, host.Physical(DocumentUri), FieldDocument);
        Check.True(json is not null, "必须给出大纲");

        var root = JsonDocument.Parse(json!).RootElement;
        var varyings = FindByName(root[0], "Varyings");
        Check.True(varyings is not null, "大纲里必须有 Varyings 结构体");

        var field = FindByName(varyings!.Value, "camDirWS");
        Check.True(field is not null, "大纲里必须有 camDirWS 字段节点");

        var line = field!.Value.GetProperty("selectionRange").GetProperty("start").GetProperty("line").GetInt32();
        Check.Equal(LineNumber(FieldDocument, "float3 camDirWS : TEXCOORD5;"), line,
            "字段节点必须定位到字段声明行");
    }

    /// <summary>孤儿 Pass（SubShader 里不在任何 Pass 内的程序块）的起始行只能换算一次。</summary>
    /// <remarks>
    /// 这类块的 PassStartLineNumber 是 -1（未知），Pass 节点的行退回程序块首行 —— 而那条退路
    /// 用的 MinLine 返回的已经是 0-based。修复前这条分支又套了一次 LspLine，等于把 0-based
    /// 再减 1：Pass 节点整体落到上一行，range 与 selectionRange 都跟着前移。
    /// </remarks>
    private static void OutlineOrphanPassStartLine()
    {
        var host = new FakeHost();
        var service = new NavigationService(host, new IncludeSymbolCache(),
            new NavigationOptions { HierarchicalDocumentSymbol = true });

        var json = Symbols(service, DocumentUri, host.Physical(DocumentUri), OrphanPassDocument);
        Check.True(json is not null, "必须给出大纲");

        var root = JsonDocument.Parse(json!).RootElement;
        var pass = FindByName(root[0], "Pass -1");
        Check.True(pass is not null, "SubShader 里不在 Pass 内的程序块必须单独成一个 Pass 节点");

        var expected = LineNumber(OrphanPassDocument, "HLSLPROGRAM");
        var startLine = pass!.Value.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();
        var selectLine = pass.Value.GetProperty("selectionRange").GetProperty("start").GetProperty("line").GetInt32();

        Check.Equal(expected, startLine,
            "孤儿 Pass 的 range 起点必须是程序块首行（修复前会再减一行，落到上一行去）");
        Check.Equal(expected, selectLine,
            "孤儿 Pass 的 selectionRange 起点同样只能是程序块首行");
        TestCaseHelpers.Report("孤儿 Pass 大纲：range 起点第 " + startLine + " 行（程序块首行）");
    }

    /// <summary>在大纲树里按名字找节点（先序）。</summary>
    private static JsonElement? FindByName(JsonElement node, string name)
    {
        if (node.GetProperty("name").GetString() == name)
        {
            return node;
        }

        if (!node.TryGetProperty("children", out var children))
        {
            return null;
        }

        foreach (var child in children.EnumerateArray())
        {
            var found = FindByName(child, name);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
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

    /// <summary>跨文件候选必须保持 include 的「近 → 远」次序。</summary>
    /// <remarks>
    /// 两个头文件各写一份同名重载：近的那份是直连 include，远的那份隔了一层。文件名刻意让
    /// 远的那份在字母序上更靠前（AFar &lt; ZNear）—— 旧实现里跨文件候选的距离恒为 0，
    /// 排序落到 URI 字母序上，第一个候选就是更远的那个，F12 一跳就跳错文件。
    /// </remarks>
    private static void DefinitionPrefersNearerInclude()
    {
        const string nearPhysical = "D:/proj/Assets/Shaders/ZNear.hlsl";
        const string farPhysical = "D:/proj/Assets/Shaders/AFar.hlsl";

        // 近端头文件再 include 远端头文件：远端因此落进闭包的第二层。
        const string nearHeader = "#include \"AFar.hlsl\"\nfloat3 MS_Order(float3 p) { return p; }\n";
        const string farHeader = "float3 MS_Order(float3 p, float w) { return p * w; }\n";

        const string document = """
            Shader "T/Order"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        #include "ZNear.hlsl"
                        half4 frag() : SV_Target { return MS_Order(1); }
                        ENDHLSL
                    }
                }
            }
            """;

        var host = new FakeHost();
        host.Add(nearPhysical, nearHeader);
        host.Add(farPhysical, farHeader);

        var cache = new IncludeSymbolCache();
        var service = new NavigationService(host, cache);
        Warm(host, cache, document);

        var json = DefinitionJson(service, document, "return MS_Order(1)", "MS_Order");
        Check.True(json is not null, "两个头文件里的同名重载都必须给出候选");

        var items = JsonDocument.Parse(json!).RootElement;
        Check.Equal(2, items.GetArrayLength(), "两份重载各一条候选");

        Check.Equal(new Uri(nearPhysical).AbsoluteUri, items[0].GetProperty("uri").GetString(),
            "第一个候选必须来自更近的 include（ZNear 直连、AFar 隔一层）");
        Check.Equal(new Uri(farPhysical).AbsoluteUri, items[1].GetProperty("uri").GetString(),
            "更远的 include 排在后面");
    }

    /// <summary>
    /// 宏可见性必须按真实 include 文本顺序求值：B 里先 include D，正文顺序就是 B、D、C，
    /// 于是 C 的 MS_ORDER_IMPL 覆盖 D 的，别名应解到 C 里的实现。
    /// </summary>
    /// <remarks>
    /// 修复前闭包那一段按预热的 BFS 顺序（B、C、D）施加宏事件，D 反而最后覆盖，
    /// 别名解包会指向 D 里的实现 —— 与预处理器的结论相反。
    /// </remarks>
    private static void DefinitionMacroIncludeTextOrder()
    {
        var host = new FakeHost();
        host.Add(IncludeOrderBPhysical, IncludeOrderB);
        host.Add(IncludeOrderCPhysical, IncludeOrderC);
        host.Add(IncludeOrderDPhysical, IncludeOrderD);

        var cache = new IncludeSymbolCache();
        var service = new NavigationService(host, cache);
        Warm(host, cache, IncludeOrderDocument);

        var json = DefinitionJson(service, IncludeOrderDocument, "return half4(MS_ORDER_ALIAS(1)", "MS_ORDER_ALIAS");
        Check.True(json is not null, "宏别名上按 F12 必须有结果");

        var items = JsonDocument.Parse(json!).RootElement;
        var uris = new List<string>();
        var lines = new List<int>();

        foreach (var item in items.EnumerateArray())
        {
            uris.Add(item.GetProperty("uri").GetString() ?? string.Empty);
            lines.Add(item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32());
        }

        var cUri = new Uri(IncludeOrderCPhysical).AbsoluteUri;
        var impl = uris.IndexOf(cUri);

        Check.True(impl >= 0,
            "展开后的实现必须落在 C 里：文本顺序 C 后于 D，它的 #define 才是生效的那份");
        Check.Equal(LineNumber(IncludeOrderC, "float3 MS_ORDER_IN_C"), lines[impl],
            "落点必须是 C 里那条实现声明");
        Check.DoesNotContain(uris, new Uri(IncludeOrderDPhysical).AbsoluteUri,
            "D 的同名宏已被 C 覆盖，不该出现在候选里（BFS 顺序下这里会指向 D）");
        TestCaseHelpers.Report("宏可见性按文本顺序：" + items.GetArrayLength() + " 个候选，实现在 C");
    }

    /// <summary>
    /// 缺符号快照的 include 文件不能静默跳过：宏可见性因此下降时必须留下追踪。
    /// </summary>
    /// <remarks>
    /// 刻意不预热：此时闭包里一个头文件快照都没有，求值必然降级。
    /// 断言的是追踪里那条「宏可见性下降」—— 修复前这段是静默 continue，现场只剩
    /// 「F12 少了一条候选」这种无从下手的结果，查不出是哪个文件缺快照。
    /// 追踪开关是进程级静态量（DiagTrace.Init 没有关闭入口），本用例把它指到临时文件，
    /// 结束时删掉；后续用例不读这份追踪，多出来的落盘不影响任何断言。
    /// </remarks>
    private static void DefinitionMacroMissingSnapshotTraced()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), "microshader_macro_" + Guid.NewGuid().ToString("N") + ".log");
        DiagTrace.Init(tracePath);

        try
        {
            var host = new FakeHost();
            host.Add(IncludeOrderBPhysical, IncludeOrderB);
            host.Add(IncludeOrderCPhysical, IncludeOrderC);
            host.Add(IncludeOrderDPhysical, IncludeOrderD);

            var service = new NavigationService(host, new IncludeSymbolCache());

            // 不预热：include 链上所有头文件都没有符号快照。
            DefinitionJson(service, IncludeOrderDocument, "return half4(MS_ORDER_ALIAS(1)", "MS_ORDER_ALIAS");

            Check.True(File.Exists(tracePath), "用例前提：追踪开关必须真的落盘（Init 失败时无从断言）");
            var trace = File.ReadAllText(tracePath);

            Check.True(trace.Contains("宏可见性下降", StringComparison.Ordinal),
                "缺快照导致的可见性下降必须有痕迹（修复前这里是静默跳过）");
            Check.True(trace.Contains("OrderB.hlsl", StringComparison.Ordinal),
                "痕迹里要点出被跳过的具体文件，否则排障时无从下手");
        }
        finally
        {
            try
            {
                if (File.Exists(tracePath))
                {
                    File.Delete(tracePath);
                }
            }
            catch
            {
                // 清理失败不影响上面的结论
            }
        }
    }

    /// <summary>陈旧快照 + 补扫预算耗尽：宁可不给候选，也不拿旧行号当答案。</summary>
    /// <remarks>
    /// 场景是「头文件在磁盘上被外部改过，声明的行号整体下移，而缓存里还是上一次的快照」。
    /// 预算耗尽时无法重扫，修复前那条分支既不重扫也不跳过，接着用旧行号算位置 ——
    /// F12 会落到一行无关注释上，而且没有任何提示。修复后整份文件跳过：给不出位置，
    /// 但也不会给错位置。用例后半段放开预算再跑一次，确认这份语料确实还能跳到新行，
    /// 免得「没有候选」其实是因为锚点或语料本身不对。
    /// </remarks>
    private static void DefinitionStaleSnapshotSkipped()
    {
        var host = new FakeHost();
        var cache = new IncludeSymbolCache();

        // 预算 0：一次跨文件补扫都不许做，陈旧快照除了「跳过」没有别的出路。
        var service = new NavigationService(host, cache, new NavigationOptions { MaxCrossFileProbes = 0 });
        Warm(host, cache, Document);

        // 预热之后磁盘上的头文件被改过：函数移到第 5 行，写入戳随之变化。
        host.Add(LocalPhysical, StaleShiftedHeader);
        host.BumpStamp(LocalPhysical);

        var staleJson = DefinitionJson(service, Document, "float4 q = MS_LocalOnly(1)", "MS_LocalOnly");
        Check.True(staleJson is null,
            "预算耗尽时不得回出基于陈旧快照的候选（修复前给的是旧行号，落点在新文本里已是注释）");

        var probed = new NavigationService(host, cache);
        var freshJson = DefinitionJson(probed, Document, "float4 q = MS_LocalOnly(1)", "MS_LocalOnly");
        Check.True(freshJson is not null, "放开预算后必须能补扫出候选（用例前提）");

        var line = JsonDocument.Parse(freshJson!).RootElement[0]
            .GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();
        Check.Equal(LineNumber(StaleShiftedHeader, "float4 MS_LocalOnly"), line,
            "补扫后的落点必须是新文本里的声明行");

        TestCaseHelpers.Report("陈旧快照：预算 0 时零候选，放开预算后落到第 " + line + " 行");
    }

    /// <summary>结构体字段上按 F12：必须跳到字段自身，不能落到别的结构体上。</summary>
    /// <remarks>
    /// 真实手写 shader 里 "Attributes" 与 "Varyings" 往往相邻且字段名不同（"camDirWS" 只在
    /// "Varyings" 里）。字段声明的偏移必须相对整块文本，落错基准就会指到前一个结构体的某一行上。
    /// </remarks>
    private static void DefinitionStructField()
    {
        var (json, host) = Definition(FieldDocument, "half3 camDir = lerp(IN.camDirWS", "camDirWS");

        Check.True(json is not null, "结构体字段上按 F12 必须给出定义");
        var items = JsonDocument.Parse(json!).RootElement;
        Check.Equal(1, items.GetArrayLength(), "camDirWS 在本文件只声明一次，只应给一个候选");

        var expectedLine = LineNumber(FieldDocument, "float3 camDirWS : TEXCOORD5;");
        var expectedCharacter = LineOfText(FieldDocument, expectedLine).IndexOf("camDirWS", StringComparison.Ordinal);
        var start = items[0].GetProperty("range").GetProperty("start");

        Check.Equal(expectedLine, start.GetProperty("line").GetInt32(), "行号必须落在字段声明那一行");
        Check.Equal(expectedCharacter, start.GetProperty("character").GetInt32(), "列号必须落在字段名首字符");
        Check.Equal(0, host.Reads, "本文件定义不得触发任何读盘");
    }

    /// <summary>结构体定义在 include 头文件里、字段在本文件被访问时，F12 必须给到字段自身。</summary>
    /// <remarks>
    /// 锁的是「跨文件字段」这条路径：回溯器的跨文件分支原先整类跳过 Field 声明，
    /// 只留结构体名，于是 "surface.normalWS" 这种访问按 F12 一条候选都没有（返回 NO RESULT）。
    /// 用例同时断言候选唯一与行号落在字段那一行 —— 字段偏移若退回到「相对结构体正文」，
    /// 行号会整体前移，这里会立刻失败。
    /// </remarks>
    private static void DefinitionCrossFileStructField()
    {
        var (json, _, reads) = DefinitionWarmed(FieldIncludeDocument, "surface.normalWS *", "normalWS");

        Check.True(json is not null, "include 头文件里定义的字段按 F12 必须能跳到");
        var items = JsonDocument.Parse(json!).RootElement;
        Check.Equal(1, items.GetArrayLength(), "normalWS 在头文件里只声明一次，只应给一个候选");

        Check.Equal(new Uri(FieldHeaderPhysical).AbsoluteUri, items[0].GetProperty("uri").GetString(),
            "候选应指向定义该字段的头文件");

        var fieldLine = LineNumber(FieldHeader, "float3 normalWS;");
        var start = items[0].GetProperty("range").GetProperty("start");
        Check.Equal(fieldLine, start.GetProperty("line").GetInt32(),
            "行号必须落在字段声明那一行（基准是整份头文件，不是结构体正文）");
        Check.Equal(LineOfText(FieldHeader, fieldLine).IndexOf("normalWS", StringComparison.Ordinal),
            start.GetProperty("character").GetInt32(), "列号必须落在字段名首字符");

        Check.Equal(0, reads, "预热命中后 F12 不得再读盘");
    }

    /// <summary>片元里读顶点写进来的成员时，F12 必须先给顶点那条赋值。</summary>
    /// <remarks>
    /// 用户报的就是这条：在片元里对 IN.positionWS 按 F12，只有结构体里的字段声明，
    /// 顶点里写它的那一行一条都不给。声明只说明类型，值从哪来要看写入点，所以写入点排第一。
    /// 用例同时锁住「成员访问本身不得被登记成声明」—— 缺那条修正时，候选里会多出一条
    /// 指向光标自己所在行的自指结果，看起来像跳转没反应。
    /// </remarks>
    private static void DefinitionMemberWritePoint()
    {
        var (json, host) = Definition(VaryingWriteDocument, "DoSomething(IN.positionWS", "positionWS");

        Check.True(json is not null, "片元里读 IN.positionWS 按 F12 必须有结果");
        var items = JsonDocument.Parse(json!).RootElement;

        var writeLine = LineNumber(VaryingWriteDocument, "o.positionWS = TransformObjectToWorld");
        var fieldLine = LineNumber(VaryingWriteDocument, "float3 positionWS : TEXCOORD4;");
        var cursorLine = LineNumber(VaryingWriteDocument, "DoSomething(IN.positionWS");

        Check.True(items.GetArrayLength() >= 2, "至少要有「写入点 + 字段声明」两条候选");

        var first = items[0].GetProperty("range").GetProperty("start");
        Check.Equal(writeLine, first.GetProperty("line").GetInt32(),
            "第一个候选必须是顶点里写它的那一行");
        Check.Equal(LineOfText(VaryingWriteDocument, writeLine).IndexOf("positionWS", StringComparison.Ordinal),
            first.GetProperty("character").GetInt32(), "列号必须落在写入语句的字段名上");

        var lines = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            lines.Add(item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32().ToString());
        }

        Check.Contains(lines, fieldLine.ToString(), "结构体字段声明必须仍然在候选里（它是类型的出处）");
        Check.True(!lines.Contains(cursorLine.ToString()), "成员访问本身不是声明，不得在光标这一行给候选");
        Check.Equal(0, host.Reads, "本文件内的写入点不得触发读盘");
    }

    /// <summary>只有同类型的接收者才算写入点：同名字段挂在别的结构体上不得混进来。</summary>
    /// <remarks>
    /// 写入点按「接收者的类型」过滤，而不是按字段名。否则一个 Varyings 的读取会连带把
    /// 任何同名字段（这里另一套 Varyings）的赋值都列出来，候选越多越像乱猜。
    /// </remarks>
    private static void DefinitionMemberWriteByType()
    {
        var (json, _) = Definition(VaryingWriteDocument, "DoSomething(IN.positionWS", "positionWS");

        Check.True(json is not null, "片元里读 IN.positionWS 按 F12 必须有结果");
        var items = JsonDocument.Parse(json!).RootElement;

        var otherLine = LineNumber(VaryingWriteDocument, "other.positionWS = 1;");

        foreach (var item in items.EnumerateArray())
        {
            Check.True(
                item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32() != otherLine,
                "别的结构体上的同名字段写入不得混进来");
        }
    }

    /// <summary>结构体在 HLSLINCLUDE、函数在 Pass 里时，写入点同样要能找到。</summary>
    /// <remarks>
    /// 变量表与结构体表不在同一个块里：变量是 Pass 里的形参和局部变量，结构体在共享块。
    /// 回溯器必须两边合起来看，否则这类手写 shader（结构体全写在 HLSLINCLUDE）一条写入点都没有。
    /// </remarks>
    private static void DefinitionMemberWriteSharedStruct()
    {
        var (json, _) = Definition(SharedStructWriteDocument, "DoSomething(IN.positionWS", "positionWS");

        Check.True(json is not null, "共享块里的结构体 + Pass 里的读取，按 F12 必须有结果");
        var items = JsonDocument.Parse(json!).RootElement;

        var writeLine = LineNumber(SharedStructWriteDocument, "o.positionWS = IN.positionOS.xyz;");
        var start = items[0].GetProperty("range").GetProperty("start");
        Check.Equal(writeLine, start.GetProperty("line").GetInt32(),
            "第一个候选必须是顶点里写它的那一行（跨共享块也要能对上）");
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

    /// <summary>
    /// 脏头文件：目标是打开的文档时，行号必须按编辑器内存文本算，不能按磁盘快照的行表算。
    /// </summary>
    /// <remarks>
    /// 场景就是「作者改了头文件还没存盘」：磁盘 3 行、缓冲区 8 行，同一个函数在两份文本里
    /// 的行号与偏移都不同。修复前位置用缓存里（按磁盘文本建的）行表换算、URI 却回给缓冲区，
    /// 跳过去落在磁盘文本的第 1 行 —— 那是上一行注释，函数根本不在那儿。
    /// </remarks>
    private static void DefinitionDirtyHeaderLine()
    {
        var host = new FakeHost();
        host.Add(LocalPhysical, DirtyHeaderOnDisk);
        host.Open(LocalPhysical, "file:///D:/proj/Assets/Shaders/Local.hlsl", DirtyHeaderInMemory);

        var cache = new IncludeSymbolCache();
        var service = new NavigationService(host, cache);

        // 预热照旧按磁盘文本建符号表 —— 这正是需要被纠正的那份快照。
        Warm(host, cache, Document);

        var json = DefinitionJson(service, Document, "float4 q = MS_LocalOnly(1)", "MS_LocalOnly");
        Check.True(json is not null, "头文件里的函数必须能从本文档跳到");

        var item = JsonDocument.Parse(json!).RootElement[0];
        var line = item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32();
        var expected = LineNumber(DirtyHeaderInMemory, "float4 MS_LocalOnly");

        Check.Equal(expected, line,
            "跳转行必须按内存文本换算（修复前用的是磁盘行表，会落在第 1 行）");
        Check.Equal("float4 MS_LocalOnly(float4 x) { return x; }", LineOfText(DirtyHeaderInMemory, line),
            "落点那一行必须正是内存文本里的声明");
        var character = item.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32();
        Check.Equal("MS_LocalOnly",
            LineOfText(DirtyHeaderInMemory, line).Substring(character, "MS_LocalOnly".Length),
            "落点字符必须正好压在内存文本里的声明名上");
    }

    /// <summary>
    /// 宏别名写在 HLSLINCLUDE 里时，F12 必须同时给出「宏定义处」与「展开后的实现处」。
    /// </summary>
    /// <remarks>
    /// 修复前本地块一命中宏就走早退，别名解包整段被跳过；而且宏可见性只遍历 include 文件、
    /// 不含本文档的宏事件流 —— 于是结果里只剩 #define 那一行，实现处永远拿不到。
    /// </remarks>
    private static void DefinitionSharedMacroAlias()
    {
        var (json, _) = Definition(SharedMacroDocument, "return MS_SHARED_ALIAS(1)", "MS_SHARED_ALIAS");

        Check.True(json is not null, "共享块里的宏别名上按 F12 必须有结果");
        var items = JsonDocument.Parse(json!).RootElement;

        var lines = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            lines.Add(item.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32().ToString());
        }

        Check.Contains(lines, LineNumber(SharedMacroDocument, "#define MS_SHARED_ALIAS").ToString(),
            "必须含宏定义行（修复前只有这一条）");
        Check.Contains(lines, LineNumber(SharedMacroDocument, "float3 MS_SHARED_IMPL").ToString(),
            "必须含展开后的实现行（详设 v2.0 第 6 条要求的第二个候选）");
        Check.Equal(2, items.GetArrayLength(), "一个宏别名正好两个候选，不多不少");
        TestCaseHelpers.Report("共享块宏别名 F12：" + items.GetArrayLength() + " 个候选（定义处 + 实现处）");
    }

    /// <summary>
    /// 同一份文件以大小写不同的物理路径进来时，仍必须认出「正被打开」的那一份。
    /// </summary>
    /// <remarks>
    /// 走真实会话：修复前 TryGetOpenDocument 用序数比较，"d:\proj\…" 与 "D:\proj\…" 判不相等，
    /// 结果是脏文件跳转退回按磁盘跳 —— 落点是磁盘行号，而不是编辑器里的那一行。
    /// 这里只改路径比较，URI 一侧不受影响：命中后回传的仍是客户端 didOpen 时的原始 URI。
    /// </remarks>
    private static void OpenDocumentCaseInsensitive()
    {
        using var client = new LspTestClient(new LspServerSession(new FakeAnalyzer().AnalyzeAsync));

        client.SendAtomicAsync(LspCorpus.Initialize(1)).AsTask().GetAwaiter().GetResult();
        client.SendAtomicAsync(LspCorpus.Initialized()).AsTask().GetAwaiter().GetResult();
        // 报文用原始字符串写，省掉一长串反斜杠转义；正文里的引号交给 JsonString 处理。
        var didOpen = """
            {"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":__URI__,"languageId":"shaderlab","version":1,"text":__TEXT__}}}
            """;
        didOpen = didOpen
            .Replace("__URI__", LspCorpus.JsonString(DocumentUri), StringComparison.Ordinal)
            .Replace("__TEXT__", LspCorpus.JsonString(Document), StringComparison.Ordinal);
        client.SendAtomicAsync(didOpen).AsTask().GetAwaiter().GetResult();

        // 影子仓储里登记的是 LspUri.ToPath 的结果，分隔符是反斜杠 —— 查询侧保持一致，
        // 本用例只考大小写。
        var physical = DocumentPhysical.Replace('/', '\\');
        var lookup = (IOpenDocumentLookup)client.Session;

        // 会话的读循环在另一个任务上跑，先等 didOpen 落进影子仓储。
        var deadline = Environment.TickCount64 + 5000;
        while (!lookup.TryGetOpenDocument(physical, out _, out _) && Environment.TickCount64 < deadline)
        {
            Thread.Sleep(10);
        }

        Check.True(lookup.TryGetOpenDocument(physical, out _, out _),
            "用例前提：didOpen 之后必须能按原样路径找到已打开文档");

        Check.True(lookup.TryGetOpenDocument(physical.ToLowerInvariant(), out var uri, out var text),
            "大小写不同的同一路径必须仍能认出已打开文档（修复前这里返回 false）");
        Check.Equal(DocumentUri, uri, "命中时必须原样回传客户端 didOpen 时的 URI");
        Check.Equal(Document.Length, text.Length, "命中时必须给出内存文本");
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

        // 已打开文档的缓冲区文本；没登记时退回磁盘文本（未编辑过的打开就是这样）。
        private readonly Dictionary<string, string> _openText = new(StringComparer.OrdinalIgnoreCase);

        public int Reads;

        public FakeHost()
        {
            Add(PackagePhysical, Header);
            Add(LocalPhysical, LocalHeader);
            Add(FieldHeaderPhysical, FieldHeader);
        }

        public void Add(string physicalPath, string text) => _files[Norm(physicalPath)] = text;

        public void Open(string physicalPath, string uri) => _open[Norm(physicalPath)] = uri;

        /// <summary>打开但未存盘：缓冲区文本与磁盘文本可以不一致。</summary>
        public void Open(string physicalPath, string uri, string memoryText)
        {
            var key = Norm(physicalPath);
            _open[key] = uri;
            _openText[key] = memoryText;
        }

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

        public bool TryResolve(string currentPhysicalPath, string virtualPath, out string physicalPath)
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

        // 写入戳：默认全部相同（都是 1）。用例改写磁盘内容后调 BumpStamp 模拟「文件被外部改过」，
        // 这样「缓存里的旧快照」与「磁盘上的新文本」才会被判成不一致。
        private readonly Dictionary<string, long> _stamps = new(StringComparer.OrdinalIgnoreCase);

        public void BumpStamp(string physicalPath)
        {
            var key = Norm(physicalPath);
            _stamps[key] = _stamps.TryGetValue(key, out var current) ? current + 1 : 2;
        }

        public long GetStamp(string physicalPath)
            => _stamps.TryGetValue(Norm(physicalPath), out var stamp) ? stamp : 1;

        public bool TryGetPhysicalPath(string uri, out string physicalPath)
        {
            physicalPath = Physical(uri);
            return physicalPath.Length > 0;
        }

        public bool TryGetOpenDocument(string physicalPath, out string uri, out string text)
        {
            var key = Norm(physicalPath);
            if (_open.TryGetValue(key, out var foundUri))
            {
                uri = foundUri;
                text = _openText.TryGetValue(key, out var buffer)
                    ? buffer
                    : _files.TryGetValue(key, out var content) ? content : string.Empty;
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
        // 语料是源码里的 raw string，随文件行尾一起是 CRLF；按 \n 切出来的每行末尾留着 \r，
        // 与字面量比较时是不可见的不等，断言会以「期望与实际看起来一模一样」的形式失败。
        return line >= 0 && line < lines.Length ? lines[line].TrimEnd('\r') : string.Empty;
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
