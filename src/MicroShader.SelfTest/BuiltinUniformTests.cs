using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 9 · Unity 引擎内置 uniform 表自检。
/// </summary>
/// <remarks>
/// 覆盖真实语料验收暴露的缺口："_Time.y" / "unity_ObjectToWorld._m00" /
/// "_WorldSpaceCameraPos.x" 这类访问的基变量既不在本文件、也不在任何 include 链里。
/// 同时断言「本文件同名声明优先」——内置表只在查不到声明时兜底。
/// </remarks>
internal static class BuiltinUniformTests
{
    private const string Suite = "BuiltinUniform";

    public static void Register()
    {
        TestSuite.Add(Suite, "TimeSwizzle", TimeSwizzle);
        TestSuite.Add(Suite, "MatrixMember", MatrixMember);
        TestSuite.Add(Suite, "CameraPosArity", CameraPosArity);
        TestSuite.Add(Suite, "PrefixUniform", PrefixUniform);
        TestSuite.Add(Suite, "LocalDeclarationWins", LocalDeclarationWins);
    }

    private const string Doc = """
        Shader "T/BuiltinUniform"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    float4 frag() : SV_Target
                    {
                        float a = _Time.y;
                        float b = unity_ObjectToWorld._m00;
                        float c = _WorldSpaceCameraPos.x;
                        return 0;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    private const string LocalDoc = """
        Shader "T/BuiltinShadow"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    static float2 _Time;
                    float4 frag() : SV_Target
                    {
                        float a = _Time.y;
                        return 0;
                    }
                    ENDHLSL
                }
            }
        }
        """;

    // ── 1. _Time.y：float4 的 swizzle ──

    private static void TimeSwizzle()
    {
        var labels = LabelsAt(Doc, "_Time.y", 6);
        Check.Contains(labels, "y", "内置 float4 _Time 应给出分量 y");
        Check.Contains(labels, "xyzw", "内置 float4 _Time 应给出 xyzw");
        Check.Contains(labels, "w", "内置 float4 _Time 应给出 w");
        TestCaseHelpers.Report("_Time. 候选 " + labels.Count + " 条");
    }

    // ── 2. unity_ObjectToWorld._m00：矩阵分量 ──

    private static void MatrixMember()
    {
        var labels = LabelsAt(Doc, "unity_ObjectToWorld._m00", 20);
        Check.Contains(labels, "_m00", "内置 float4x4 应给出 _m00");
        Check.Contains(labels, "_m33", "内置 float4x4 应给出 _m33");
        Check.DoesNotContain(labels, "x", "矩阵不该给 swizzle");
    }

    // ── 3. _WorldSpaceCameraPos：float3 → 13 条，且不含 w ──

    private static void CameraPosArity()
    {
        var labels = LabelsAt(Doc, "_WorldSpaceCameraPos.x", 21);
        Check.Contains(labels, "z", "内置 float3 应给出 z");
        Check.DoesNotContain(labels, "w", "内置 float3 不该给 w");
        Check.Equal(13, labels.Count, "float3 的分量候选应为 13 条");
    }

    // ── 4. 前缀：_Ti → _Time / _TimeParameters ──

    private static void PrefixUniform()
    {
        var labels = LabelsAt(Doc, "_Time.y", 3);
        Check.Contains(labels, "_Time", "前缀 _Ti 应补出内置 _Time");
        Check.Contains(labels, "_TimeParameters", "前缀 _Ti 应补出内置 _TimeParameters");
        Check.Equal(1, CountOf(labels, "_Time"), "内置 uniform 不得重复给出");
    }

    // ── 5. 本文件同名声明优先（内置表只兜底）──

    private static void LocalDeclarationWins()
    {
        var labels = LabelsAt(LocalDoc, "_Time.y", 6);
        Check.Contains(labels, "xy", "本文件 float2 _Time 应给 xy");
        Check.DoesNotContain(labels, "xyz", "本文件声明为 float2，不该给出 float4 的 xyz");
        Check.DoesNotContain(labels, "w", "本文件声明为 float2，不该给出 w");
    }

    // ── 辅助 ──

    private static List<string> LabelsAt(string text, string marker, int offset)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Check.True(index >= 0, "语料缺少标记：" + marker);

        var absolute = index + offset;
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < absolute; i++)
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
            if (!new IntelliSenseService().ProvideCompletions(
                    new PositionRequest("file:///builtin.shader", text, line, absolute - lineStart), json))
            {
                return [];
            }
        }

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(writer.WrittenSpan));
        var labels = new List<string>();
        if (!document.RootElement.TryGetProperty("items", out var items))
        {
            return labels;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
            {
                labels.Add(label.GetString()!);
            }
        }

        return labels;
    }

    private static int CountOf(List<string> labels, string name)
    {
        var n = 0;
        foreach (var label in labels)
        {
            if (label.Equals(name, StringComparison.Ordinal))
            {
                n++;
            }
        }

        return n;
    }
}
