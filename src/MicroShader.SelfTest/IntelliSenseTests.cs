using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 9（轻量符号索引与即时智能补全）自检。
/// </summary>
/// <remarks>
/// 验收口径对齐详设 v2.0 修正清单：
/// ① 内置知识库只含语言层符号，URP 符号必须运行时现场解析（法律边界，见 "HlslBuiltins" 注释）；
/// ② capabilities 必须成套声明 —— 这里用「注入/不注入 provider」两种会话实测宣告与实现的一致性；
/// ③ 触发必须克制（只宣告 "."）；
/// ④ 降级路径必须显式（注释内不触发、定义在 include 里时返回空而不是猜）。
/// </remarks>
internal static class IntelliSenseTests
{
    private const string Suite = "IntelliSense";

    public static void Register()
    {
        TestSuite.Add(Suite, "Trigger", Trigger);
        TestSuite.Add(Suite, "MemberComplete", MemberComplete);
        TestSuite.Add(Suite, "BuiltinComplete", BuiltinComplete);
        TestSuite.Add(Suite, "Hover", Hover);
        TestSuite.Add(Suite, "Degrade", Degrade);
        TestSuite.Add(Suite, "Capabilities", Capabilities);
        TestSuite.Add(Suite, "MethodNotFound", MethodNotFound);
        TestSuite.Add(Suite, "RegressionRules", RegressionRules);
        TestSuite.Add(Suite, "LifecycleCallbacks", LifecycleCallbacks);
        TestSuite.Add(Suite, "NestedMember", NestedMember);
        TestSuite.Add(Suite, "BareHlsl", BareHlsl);
    }

    // ── 嵌套成员补全（2026-10-01）："v.posOS." 逐级推导字段类型 ────────────────
    private static void NestedMember()
    {
        const string text = """
            Shader "T/Nested"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        struct Inner { float3 posOS; half2 uv; };
                        struct Outer { Inner geo; float w; };
                        float4 frag(Outer v) : SV_Target
                        {
                            float3 a = v.geo.posOS.xyz;
                            float2 b = v.geo.uv;
                            float c = v.geo.missing.x;
                            return 0;
                        }
                        ENDHLSL
                    }
                }
            }
            """;
        var service = new IntelliSenseService();

        var (l1, c1) = Cursor(text, "v.geo.posOS", 6);
        var chain = TriggerContext.Detect(text, l1, c1).MemberChain;
        Check.Equal("v|geo", string.Join('|', chain), "点号链应为 v.geo");

        var inner = All(Completion(service, text, l1, c1) ?? "{}", "label");
        Check.Contains(inner, "posOS", "v.geo. 必须补出 Inner.posOS");
        Check.Contains(inner, "uv", "v.geo. 必须补出 Inner.uv");

        var (l2, c2) = Cursor(text, "v.geo.posOS.xyz", 12);
        var swz = All(Completion(service, text, l2, c2) ?? "{}", "label");
        Check.True(swz.Count > 0, "v.geo.posOS. 必须补出 float3 分量");

        var (l3, c3) = Cursor(text, "v.geo.uv", 2);
        Check.Contains(All(Completion(service, text, l3, c3) ?? "{}", "label"), "geo", "单级 v. 行为不变");

        var (l4, c4) = Cursor(text, "v.geo.missing.x", 14);
        Check.True(Completion(service, text, l4, c4) is null || All(Completion(service, text, l4, c4)!, "label").Count == 0,
            "链中有未知字段 → 降级为空，不猜");
    }

    // ── 纯 HLSL 文件的本文件符号索引（2026-10-05）────────────────────────────
    private static void BareHlsl()
    {
        // .hlsl / .cginc 里没有任何 HLSLPROGRAM / HLSLINCLUDE 标记，切片器不产出块。
        // 修复前索引为空：同一文件里声明过的变量一条都补不出来。
        const string bare = """
            float4 shadowCoord;

            float4 main() : SV_Target
            {
                return sha
            }
            """;
        var service = new IntelliSenseService();

        var (line, col) = Cursor(bare, "return sha", 10);
        var labels = All(Completion(service, bare, line, col) ?? "{}", "label");
        Check.Contains(labels, "shadowCoord", "纯 HLSL 文件必须补出本文件声明的变量");

        // 反向约束：有 SubShader 的 ShaderLab 文件即使没有 program block，
        // 也不能被当成 HLSL 整篇索引 —— 否则 Properties 里的名字会变成 HLSL 候选。
        const string shaderLabOnly = """
            Shader "T/NoProgramBlock"
            {
                Properties { _OnlyInProperties ("x", Float) = 1 }
                SubShader
                {
                    Pass { }
                }
            }
            """;
        var (line2, col2) = Cursor(shaderLabOnly, "_OnlyInProperties", 15);
        var labels2 = All(Completion(service, shaderLabOnly, line2, col2) ?? "{}", "label");
        Check.True(!labels2.Contains("_OnlyInProperties"),
            "有 SubShader 的文件不得被整篇当 HLSL 索引（该名字只出现在 Properties 里）");
    }

    private const string Sample = """
        Shader "T/IntelliSense"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag

                    struct Varyings
                    {
                        float2 uv;
                        float3 positionWS;
                        half4 color;
                    };

                    Varyings i;

                    float4 frag(Varyings input) : SV_Target
                    {
                        float2 t = i.uv;
                        float3 p = input.positionWS;
                        return 0;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // ── 1. 触发器 ────────────────────────────────────────────────────────────

    private static void Trigger()
    {
        const string text = "a\nfloat2 t = i.uv;\n// comment i.\nstring s = \"i.\";\nfloat3 q = input.positionWS;\n";

        var (l1, c1) = Cursor(text, "i.uv;", 2);
        var member = TriggerContext.Detect(text, l1, c1);
        Check.Equal(TriggerKind.MemberAccess, member.Kind, "~~i.~~ 应判为成员访问");
        Check.Equal("i", member.MemberBase, "成员基名");
        Check.Equal(string.Empty, member.Prefix, "无前缀");

        var (l2, c2) = Cursor(text, "input.positionWS", 8);
        var partial = TriggerContext.Detect(text, l2, c2);
        Check.Equal(TriggerKind.MemberAccess, partial.Kind, "~~input.po~~ 应判为带前缀的成员访问");
        Check.Equal("po", partial.Prefix, "成员前缀");
        Check.Equal("input", partial.MemberBase, "成员基名（带前缀）");

        var (l3, c3) = Cursor(text, "// comment i.", 12);
        Check.Equal(TriggerKind.None, TriggerContext.Detect(text, l3, c3).Kind, "行注释内不得触发");

        var (l4, c4) = Cursor(text, "\"i.\";", 3);
        Check.Equal(TriggerKind.None, TriggerContext.Detect(text, l4, c4).Kind, "字符串内不得触发");

        var (l5, c5) = Cursor(text, "float2 t", 6);
        Check.Equal(TriggerKind.IdentifierPrefix, TriggerContext.Detect(text, l5, c5).Kind, "标识符前缀");
        Check.Equal("float2", TriggerContext.Detect(text, l5, c5).Prefix, "前缀内容");
    }

    // ── 2. 成员补全 ──────────────────────────────────────────────────────────

    private static void MemberComplete()
    {
        var service = new IntelliSenseService();
        var (line, character) = Cursor(Sample, "i.uv;", 2);

        var json = Completion(service, Sample, line, character);
        Check.NotNull(json, "~~i.~~ 处必须给出补全");
        Check.Contains(All(json!, "label"), "uv", "必须补出 uv");
        Check.Contains(All(json!, "label"), "positionWS", "必须补出 positionWS");
        Check.Contains(All(json!, "label"), "color", "必须补出 color");
        Check.True(json!.Contains("\"isIncomplete\":false", StringComparison.Ordinal),
            "候选集小，必须回 isIncomplete:false 由客户端本地过滤（详设 v2.0 第 2 条）");

        // 带前缀：input.po → 只剩 positionWS（偏移 8 = "input.po" 之后）
        var (l2, c2) = Cursor(Sample, "input.positionWS", 8);
        var filtered = Completion(service, Sample, l2, c2);
        Check.NotNull(filtered, "~~input.pos~~ 处必须给出补全");
        var labels = All(filtered!, "label");
        Check.Contains(labels, "positionWS", "前缀过滤后仍应含 positionWS");
        Check.DoesNotContain(labels, "uv", "前缀过滤后不应含 uv");
    }

    // ── 3. 内建补全 ──────────────────────────────────────────────────────────

    private static void BuiltinComplete()
    {
        var service = new IntelliSenseService();
        const string text = "Shader \"T\"\n{\n    SubShader\n    {\n        Pass\n        {\n            HLSLPROGRAM\n            Trans\n            ENDHLSL\n        }\n    }\n}\n";
        var (line, character) = Cursor(text, "Trans", 5);

        var json = Completion(service, text, line, character);
        Check.NotNull(json, "~~Trans~~ 前缀必须给出内建候选");
        Check.Contains(All(json!, "label"), "TRANSFORM_TEX", "必须含 TRANSFORM_TEX");
        Check.Contains(All(json!, "label"), "transpose", "大小写不敏感，应含 transpose");

        // 法律边界：内置表不得含 URP 源码符号（只允许语言层 + ShaderLab 关键字）
        var labels2 = All(json!, "label");
        Check.DoesNotContain(labels2, "TransformObjectToHClip", "URP 函数禁止内置（Unity Companion License）");
        Check.DoesNotContain(labels2, "GetVertexPositionInputs", "URP 函数禁止内置");
    }

    // ── 4. 悬停 ──────────────────────────────────────────────────────────────

    private static void Hover()
    {
        var service = new IntelliSenseService();
        var (line, character) = Cursor(Sample, "Varyings input", 3);

        var json = Hover(service, Sample, line, character);
        Check.NotNull(json, "结构体名上必须有悬停内容");
        Check.True(json!.Contains("positionWS", StringComparison.Ordinal), "悬停应列出结构体字段：" + json);

        const string withCall = "Shader \"T\"\n{\n    SubShader\n    {\n        Pass\n        {\n            HLSLPROGRAM\n            float x = saturate(1.0);\n            ENDHLSL\n        }\n    }\n}\n";
        var (l3, c3) = Cursor(withCall, "saturate", 3);
        var hoverBuiltin = Hover(service, withCall, l3, c3);
        Check.NotNull(hoverBuiltin, "内建函数上必须有悬停内容");
        Check.True(hoverBuiltin!.Contains("saturate", StringComparison.Ordinal), "悬停应含签名：" + hoverBuiltin);
    }

    // ── 5. 降级路径 ──────────────────────────────────────────────────────────

    private static void Degrade()
    {
        var service = new IntelliSenseService();

        // ① 注释内的请求 → 无结果
        const string commented = "Shader \"T\"\n{\n    SubShader\n    {\n        Pass\n        {\n            HLSLPROGRAM\n            // 这里写 i. 也不该弹补全\n            ENDHLSL\n        }\n    }\n}\n";
        var (l1, c1) = Cursor(commented, "i. 也不该", 2);
        Check.True(!TryComplete(service, commented, l1, c1), "注释内不得产生补全");

        // ② 变量声明不在本文件（模拟定义在 include 里）→ 空结果而非猜测
        const string unknown = "Shader \"T\"\n{\n    SubShader\n    {\n        Pass\n        {\n            HLSLPROGRAM\n            float2 t = external.x;\n            ENDHLSL\n        }\n    }\n}\n";
        var (l2, c2) = Cursor(unknown, "external.x", 9);
        Check.True(!TryComplete(service, unknown, l2, c2), "未声明的基名必须返回空结果（不猜）");

        // ③ 变量在但结构体定义不在 → 空结果（Varyings 定义在 include 里是真实高频场景）
        const string structMissing = "Shader \"T\"\n{\n    SubShader\n    {\n        Pass\n        {\n            HLSLPROGRAM\n            Varyings input;\n            float2 t = input.uv;\n            ENDHLSL\n        }\n    }\n}\n";
        var (l3, c3) = Cursor(structMissing, "input.uv", 6);
        Check.True(!TryComplete(service, structMissing, l3, c3), "结构体定义不在本文件时必须降级为空");
    }

    // ── 6. 能力成套声明（会话级）──────────────────────────────────────────────

    private static void Capabilities()
    {
        var service = new IntelliSenseService();

        // 注入 provider → 必须成套宣告
        var withProvider = new LspServerSession(
            new FakeAnalyzer().AnalyzeAsync,
            null,
            new IntelliSenseProvider
            {
                Completion = service.ProvideCompletions,
                Hover = service.ProvideHover,
            });

        using (var client = new LspTestClient(withProvider))
        {
            client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"processId\":1,\"rootUri\":null,\"capabilities\":{}}}")
                .AsTask().GetAwaiter().GetResult();

            var init = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            Check.True(init.Contains("\"completionProvider\"", StringComparison.Ordinal), "必须宣告 completionProvider：" + init);
            Check.True(init.Contains("\"hoverProvider\":true", StringComparison.Ordinal), "必须宣告 hoverProvider：" + init);
            Check.True(init.Contains("\"triggerCharacters\":[\".\"]", StringComparison.Ordinal),
                "triggerCharacters 必须只有 '.'（详设 v2.0 第 4 条）：" + init);
            Check.True(init.Contains("\"resolveProvider\":false", StringComparison.Ordinal), "本服务不做 resolve");
        }

        // 不注入 → 必须一个都不宣告（避免"宣告了却不会响应"）
        var without = new LspServerSession(new FakeAnalyzer().AnalyzeAsync);
        using (var client = new LspTestClient(without))
        {
            client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"processId\":1,\"rootUri\":null,\"capabilities\":{}}}")
                .AsTask().GetAwaiter().GetResult();

            var init = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
            Check.True(!init.Contains("completionProvider", StringComparison.Ordinal), "未注入时不得宣告 completionProvider：" + init);
            Check.True(!init.Contains("hoverProvider", StringComparison.Ordinal), "未注入时不得宣告 hoverProvider：" + init);
        }
    }

    // ── 7. 未启用能力必须回 MethodNotFound ──────────────────────────────────

    private static void MethodNotFound()
    {
        var session = new LspServerSession(new FakeAnalyzer().AnalyzeAsync);
        using var client = new LspTestClient(session);

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"processId\":1,\"rootUri\":null,\"capabilities\":{}}}")
            .AsTask().GetAwaiter().GetResult();
        _ = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/completion\",\"params\":{\"textDocument\":{\"uri\":\"file:///x.shader\"},\"position\":{\"line\":0,\"character\":0}}}")
            .AsTask().GetAwaiter().GetResult();

        var response = client.ReceiveAsync().AsTask().GetAwaiter().GetResult();
        Check.True(response.Contains("\"error\"", StringComparison.Ordinal), "未启用能力必须回错误而不是静默丢弃：" + response);
        Check.True(response.Contains("-32601", StringComparison.Ordinal), "错误码必须是 MethodNotFound(-32601)：" + response);
    }

    // ── 工具 ────────────────────────────────────────────────────────────────

// ── 8. 真实语料回归（R1~R6，2026-09-26 加入）──────────────────────────────

    /// <summary>
    /// 用户真实手写 shader 上实测到的六类缺陷的回归用例。
    /// </summary>
    /// <remarks>
    /// 每一条都对应一个"实测过的错误行为"（详见收尾指南 §27/§28）：
    /// R1 调用实参被登记成变量、R2 合并顺序与文本顺序相反、R3 语义标注污染字段解析、
    /// R4 前缀补全不查文档内符号、R5 real* 与模板尖括号、R6 索引缓存不得改变结果。
    /// </remarks>
    private static void RegressionRules()
    {
        var service = new IntelliSenseService();

        // R1：声明之后才出现的同名调用，不得覆盖声明
        const string r1 = """
            Shader "T/R1"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        struct Varyings { float3 positionWS; half4 color; };

                        Varyings vert(Varyings input)
                        {
                            return input;
                        }

                        float4 frag(Varyings i) : SV_Target
                        {
                            GetAdditionalLight(i, 0);
                            return i.color;
                        }
                        ENDHLSL
                    }
                }
            }
            """;

        var (l1, c1) = Cursor(r1, "return i.color;", 9);
        var json1 = Completion(service, r1, l1, c1);
        Check.True(json1 is not null, "R1：调用实参不得覆盖形参绑定（i.color 处应有候选）\n" + json1);
        Check.True(All(json1!, "label").Contains("color"), "R1：应补出 Varyings.color");

        // R2：HLSLINCLUDE 写在前面，其声明必须被后面的 Pass 内声明覆盖
        const string r2 = """
            Shader "T/R2"
            {
                SubShader
                {
                    HLSLINCLUDE
                    static float4 gSprite;
                    void Touch(float4 gSprite) { }
                    ENDHLSL

                    Pass
                    {
                        HLSLPROGRAM
                        struct Varyings { float3 positionWS; half4 color; };
                        Varyings gSprite;
                        float4 frag() : SV_Target { return gSprite.color; }
                        ENDHLSL
                    }
                }
            }
            """;

        var (l2, c2) = Cursor(r2, "return gSprite.color;", 15);
        var json2 = Completion(service, r2, l2, c2);
        Check.True(json2 is not null, "R2：合并顺序必须按文本顺序（后出现的 Pass 内声明应胜出）\n" + json2);
        Check.True(All(json2!, "label").Contains("positionWS"), "R2：应补出 Varyings.positionWS");

        // R3：结构体字段里的语义标注不得吃掉后面的字段
        const string r3 = """
            Shader "T/R3"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        struct V
                        {
                            float4 positionCS : SV_POSITION;
                            half3 normalWS : TEXCOORD0;
                            float2 uv : TEXCOORD1;
                        };
                        float4 frag(V i) : SV_Target { return i.uv.x; }
                        ENDHLSL
                    }
                }
            }
            """;

        var (l3, c3) = Cursor(r3, "return i.uv.x;", 9);
        var json3 = Completion(service, r3, l3, c3);
        Check.True(json3 is not null, "R3：带语义标注的结构体必须补出字段\n" + json3);
        var labels3 = All(json3!, "label");
        Check.True(labels3.Contains("positionCS") && labels3.Contains("normalWS") && labels3.Contains("uv"),
            "R3：三个字段都要在，实际 " + string.Join(",", labels3));

        // R4：前缀补全必须包含本文件符号（含声明式宏登记进来的纹理/sampler 名）
        const string r4 = """
            Shader "T/R4"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        CBUFFER_START(UnityPerMaterial)
                            float _GrassWidth;
                            float _GrassHeight;
                        CBUFFER_END
                        TEXTURE2D(_GrassColorRT);
                        SAMPLER(sampler_GrassColorRT);
                        float4 frag() : SV_Target { _GrassWidth; return 0; }
                        ENDHLSL
                    }
                }
            }
            """;

        var (l4b, c4b) = Cursor(r4, "_GrassWidth; return 0;", 5);   // 光标停在 _Gras 之后
        var json4b = Completion(service, r4, l4b, c4b);
        Check.True(json4b is not null, "R4：本文件符号应参与前缀补全");
        var labels4 = All(json4b!, "label");
        Check.True(labels4.Contains("_GrassWidth") && labels4.Contains("_GrassHeight"),
            "R4：应补出 _Grass* 变量，实际 " + string.Join(",", labels4));
        Check.True(labels4.Contains("_GrassColorRT"),
            "R4/R5：TEXTURE2D(_X) 这类声明式宏应把 _X 登记进来，实际 " + string.Join(",", labels4));

        // R5：real* 是类型；模板尖括号内的类型名不得被登记成变量
        const string r5 = """
            Shader "T/R5"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        struct Varyings { float3 positionWS; };
                        static real4 gTmp;
                        StructuredBuffer<Varyings> gBuf;
                        float4 frag() : SV_Target { return gTmp; }
                        ENDHLSL
                    }
                }
            }
            """;

        var (l5, c5) = Cursor(r5, "return gTmp;", 8);
        var json5 = Completion(service, r5, l5, c5);
        Check.True(json5 is not null, "R5：real4 声明的变量必须被登记");
        var labels5 = All(json5!, "label");
        Check.True(labels5.Contains("gTmp"), "R5：应补出 gTmp，实际 " + string.Join(",", labels5));
        Check.True(labels5.Contains("gBuf"), "R5：StructuredBuffer 变量本身应被登记");
        Check.True(!labels5.Contains("Varyings"), "R5：模板实参里的类型名不得被登记成变量");

        // R7：内建向量的分量补全 + 矩阵 _mRC
        const string r7 = """
            Shader "T/R7"
            {
                SubShader
                {
                    Pass
                    {
                        HLSLPROGRAM
                        float4 gColor;
                        half3 gNormal;
                        real4x4 gMatrix;

                        float4 frag() : SV_Target
                        {
                            float2 t = gColor.rg;
                            half3 n = gNormal.xyz;
                            float g00 = gMatrix._m00;
                            return gColor;
                        }
                        ENDHLSL
                    }
                }
            }
            """;

        var (l7, c7) = Cursor(r7, "float2 t = gColor.rg;", 18);
        var json7 = Completion(service, r7, l7, c7);
        Check.True(json7 is not null, "R7：float4 的成员补全必须给出分量候选\n" + json7);
        var labels7 = All(json7!, "label");
        Check.Contains(labels7, "rgba", "R7：float4 应含 rgba");
        Check.Contains(labels7, "xz", "R7：float4 应含 xz");
        Check.Contains(labels7, "w", "R7：float4 应含单分量 w");

        var (l7b, c7b) = Cursor(r7, "half3 n = gNormal.xyz;", 18);
        var json7b = Completion(service, r7, l7b, c7b);
        Check.True(json7b is not null, "R7：half3 的成员补全必须给出分量候选");
        var labels7b = All(json7b!, "label");
        Check.Contains(labels7b, "rgb", "R7：half3 应含 rgb");
        Check.DoesNotContain(labels7b, "w", "R7：half3 不该给 w");

        var (l7c, c7c) = Cursor(r7, "float g00 = gMatrix._m00;", 20);
        var json7c = Completion(service, r7, l7c, c7c);
        Check.True(json7c is not null, "R7：矩阵的成员补全必须给出 _mRC");
        var labels7c = All(json7c!, "label");
        Check.Contains(labels7c, "_m00", "R7：矩阵应含 _m00");
        Check.Contains(labels7c, "_m33", "R7：real4x4 应含 _m33");
        Check.DoesNotContain(labels7c, "x", "R7：矩阵不该给 swizzle");

        // R6：缓存命中不得改变结果（同文本重复请求结果一致）
        var (l6, c6) = Cursor(r3, "return i.uv.x;", 9);
        var first = Completion(service, r3, l6, c6);
        var second = Completion(service, r3, l6, c6);
        Check.Equal(first, second, "R6：索引缓存命中后结果必须逐字节一致");

        TestCaseHelpers.Report("R1~R7 回归：" + labels3.Count + " 个语义标注字段 / "
            + labels4.Count + " 个文档内符号 / " + labels5.Count + " 个 R5 符号 / "
            + labels7.Count + " 个 float4 分量 / " + labels7c.Count + " 个矩阵分量");
    }

// ── 9. 文档生命周期回调契约（预热链路的入口）────────────────────────────

    /// <summary>
    /// 断言会话在 didOpen / didSave / didClose 时按契约触发对应回调。
    /// </summary>
    /// <remarks>
    /// 这三条回调是 include 链符号预热（与失效）的唯一入口，属于跨模块契约：
    /// 会话侧改了时序、或宿主侧漏接，都会让预热静默失效 —— 而静默失效的表现只是
    /// 「少给候选」，不会报错，所以必须有断言钉住。
    /// </remarks>
    private static void LifecycleCallbacks()
    {
        // 语料刻意不含引号，便于直接拼进 JSON 字面量
        const string shader = "Shader TestShader { SubShader { Pass { HLSLPROGRAM float4 frag() : SV_Target { return 0; } ENDHLSL } } }";

        string? openedUri = null;
        string? openedText = null;
        string? savedUri = null;
        string? savedText = null;
        string? closedUri = null;

        var service = new IntelliSenseService();
        var provider = new IntelliSenseProvider
        {
            Completion = service.ProvideCompletions,
            Hover = service.ProvideHover,
            DocumentOpened = (uri, text) => { openedUri = uri; openedText = text; },
            DocumentSaved = (uri, text) => { savedUri = uri; savedText = text; },
            DocumentClosed = uri => closedUri = uri,
        };

        var session = new LspServerSession(new FakeAnalyzer().AnalyzeAsync, null, provider);
        using var client = new LspTestClient(session);

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"processId\":1,\"rootUri\":null,\"capabilities\":{}}}")
            .AsTask().GetAwaiter().GetResult();

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///t.shader\",\"languageId\":\"shaderlab\",\"version\":1,\"text\":\"" + shader + "\"}}}")
            .AsTask().GetAwaiter().GetResult();

        // 等 didOpen 真正被处理完（也顺带保证后续 didSave 一定在 didOpen 之后）
        Check.True(WaitFor(() => openedUri is not null), "didOpen 必须触发 DocumentOpened");
        Check.Equal("file:///t.shader", openedUri, "DocumentOpened 的 uri");
        Check.Equal(shader, openedText, "DocumentOpened 的 text");

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didSave\",\"params\":{\"textDocument\":{\"uri\":\"file:///t.shader\"}}}")
            .AsTask().GetAwaiter().GetResult();

        Check.True(WaitFor(() => savedUri is not null), "didSave 必须触发 DocumentSaved（增量预热的入口）");
        Check.Equal("file:///t.shader", savedUri, "DocumentSaved 的 uri");
        Check.Equal(shader, savedText, "DocumentSaved 的 text 应取阴影文档全文");

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"file:///t.shader\"}}}")
            .AsTask().GetAwaiter().GetResult();

        Check.True(WaitFor(() => closedUri is not null), "didClose 必须触发 DocumentClosed");
        Check.Equal("file:///t.shader", closedUri, "DocumentClosed 的 uri");

        TestCaseHelpers.Report("生命周期回调：opened / saved / closed 均已按契约触发");
    }

    /// <summary>轮询等待条件成立（通知没有响应帧，无法用读帧同步）。</summary>
    private static bool WaitFor(Func<bool> condition)
    {
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private static bool TryComplete(IntelliSenseService service, string text, int line, int character)
    {
        var writer = new ArrayBufferWriter<byte>();
        using var json = new Utf8JsonWriter(writer);
        return service.ProvideCompletions(new PositionRequest("file:///t.shader", text, line, character), json);
    }

    private static string? Completion(IntelliSenseService service, string text, int line, int character)
    {
        var writer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(writer))
        {
            if (!service.ProvideCompletions(new PositionRequest("file:///t.shader", text, line, character), json))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    private static string? Hover(IntelliSenseService service, string text, int line, int character)
    {
        var writer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(writer))
        {
            if (!service.ProvideHover(new PositionRequest("file:///t.shader", text, line, character), json))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(writer.WrittenSpan);
    }

    /// <summary>从补全结果 JSON 里抽出所有给定字段的取值（测试用，允许分配）。</summary>
    private static List<string> All(string json, string property)
    {
        var found = new List<string>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("items", out var items))
        {
            return found;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
            {
                found.Add(value.GetString()!);
            }
        }

        return found;
    }

    /// <summary>把「marker 第 offset 个字符处」换算成 LSP 的 0-based (line, character)。</summary>
    private static (int Line, int Character) Cursor(string text, string marker, int offset)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Check.True(index >= 0, "测试语料缺少标记：" + marker);
        var absolute = index + offset;

        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < absolute; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, absolute - lineStart);
    }
}
