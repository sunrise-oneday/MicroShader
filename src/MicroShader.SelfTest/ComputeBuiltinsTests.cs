using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// compute 专有补全语义：RW* 资源类型、原子操作、屏障函数、线程语义与 pragma 入口名。
/// </summary>
/// <remarks>
/// 锁的行为：compute 文件作者要敲的符号本来一个都不在内建表里 ——
/// 输入 "RWStr" 零候选、"Interlocked" 零候选、": SV_" 零候选，
/// 这会让人以为补全在 compute 文件里根本没启用。
/// 同时钉住 "#pragma kernel " 后的函数名补全：这个位置 TriggerContext 拿不到前缀，
/// 必须有一条独立的触发路径，否则「compute 最该有补全的位置」反而最空。
/// </remarks>
internal static class ComputeBuiltinsTests
{
    private const string Suite = "ComputeBuiltin";

    public static void Register()
    {
        TestSuite.Add(Suite, "RwTypes", RwTypes);
        TestSuite.Add(Suite, "RasterizerOrderedTypes", RasterizerOrderedTypes);
        TestSuite.Add(Suite, "AtomicFunctions", AtomicFunctions);
        TestSuite.Add(Suite, "BarrierFunctions", BarrierFunctions);
        TestSuite.Add(Suite, "ThreadSemantics", ThreadSemantics);
        TestSuite.Add(Suite, "StorageModifiers", StorageModifiers);
        TestSuite.Add(Suite, "PragmaKernelEntry", PragmaKernelEntry);
        TestSuite.Add(Suite, "PragmaEntryWithoutFunction", PragmaEntryWithoutFunction);
    }

    /// <summary>compute 语料：一个真实形状的 kernel（光标位置由标记串定位）。</summary>
    private const string Doc = """
        #pragma kernel CSMain

        RWStructuredBuffer<float4> outputBuffer;
        Texture2D<float4> inputTex;
        SamplerState linearClamp;
        RasterizerOrderedTexture2D<float4> orderedTarget;

        struct Payload
        {
            float4 color;
            float weight;
        };

        groupshared float sharedWeight;

        [numthreads(8, 8, 1)]
        void CSMain(uint3 id : SV_DispatchThreadID)
        {
            Payload p;
            p.color = inputTex.SampleLevel(linearClamp, float2(id.xy) / 1024.0, 0);
            outputBuffer[id.xy] = p.color;
            InterlockedAdd(counter, 1);
            GroupMemoryBarrier();
            DeviceMemoryBarrier();
        }
        """;

    /// <summary>没有任何函数的 compute 语料：用来验证「给不出候选就不弹窗」。</summary>
    private const string NoFunctionDoc = """
        #pragma kernel 
        RWStructuredBuffer<float4> outputBuffer;
        """;

    // ── 1. RW* 资源类型 ──

    private static void RwTypes()
    {
        var items = ItemsAt(Doc, "RWStr", 5);
        var labels = Labels(items);
        Check.Contains(labels, "RWStructuredBuffer", "输入 RWStr 必须补出 RWStructuredBuffer");
        Check.True(
            items.Any(i => Label(i) == "RWStructuredBuffer" && SortText(i).StartsWith("0_", StringComparison.Ordinal)),
            "RWStructuredBuffer 是类型，sortText 必须排 0_ 档（压过同前缀的宏）");
        TestCaseHelpers.Report("RWStr 候选 " + labels.Count + " 条");
    }

    // ── 2. RasterizerOrdered* 资源族 ──

    private static void RasterizerOrderedTypes()
    {
        var labels = Labels(ItemsAt(Doc, "RasterizerOrderedT", 18));
        Check.Contains(labels, "RasterizerOrderedTexture1D", "必须给出 RasterizerOrderedTexture1D");
        Check.Contains(labels, "RasterizerOrderedTexture2D", "必须给出 RasterizerOrderedTexture2D");
        Check.Contains(labels, "RasterizerOrderedTexture3D", "必须给出 RasterizerOrderedTexture3D");
        TestCaseHelpers.Report("RasterizerOrderedT 候选 " + labels.Count + " 条");
    }

    // ── 3. 原子操作 ──

    private static void AtomicFunctions()
    {
        var items = ItemsAt(Doc, "Interlocked", 11);
        var labels = Labels(items);
        Check.Contains(labels, "InterlockedAdd", "必须给出 InterlockedAdd");
        Check.Contains(labels, "InterlockedCompareExchange", "必须给出 InterlockedCompareExchange");
        Check.Contains(labels, "InterlockedExchange", "必须给出 InterlockedExchange");
        Check.True(
            items.Any(i => Label(i) == "InterlockedAdd" && SortText(i).StartsWith("1_", StringComparison.Ordinal)),
            "原子操作是函数，sortText 走 1_ 档");
        TestCaseHelpers.Report("Interlocked 候选 " + labels.Count + " 条");
    }

    // ── 4. 屏障函数 ──

    private static void BarrierFunctions()
    {
        var labels = Labels(ItemsAt(Doc, "GroupMemoryBarrier", 18));
        Check.Contains(labels, "GroupMemoryBarrier", "必须给出 GroupMemoryBarrier");
        Check.Contains(labels, "GroupMemoryBarrierWithGroupSync", "必须给出 GroupMemoryBarrierWithGroupSync");

        var device = Labels(ItemsAt(Doc, "DeviceMemoryBarrier", 19));
        Check.Contains(device, "DeviceMemoryBarrier", "必须给出 DeviceMemoryBarrier");
        Check.Contains(device, "DeviceMemoryBarrierWithGroupSync", "必须给出 DeviceMemoryBarrierWithGroupSync");
        TestCaseHelpers.Report("屏障候选：Group " + labels.Count + " 条 / Device " + device.Count + " 条");
    }

    // ── 5. 线程语义 SV_ ──

    /// <remarks>
    /// 位置取 "SV_DispatchThreadID" 的第 3 个字符后（即 "SV_" 之后）。
    /// 语义名也是标识符前缀，进了内建表就自然出现在 prefix 补全里。
    /// </remarks>
    private static void ThreadSemantics()
    {
        var labels = Labels(ItemsAt(Doc, "SV_DispatchThreadID", 3));
        Check.Contains(labels, "SV_DispatchThreadID", "在 SV_ 处必须补出 SV_DispatchThreadID");
        Check.Contains(labels, "SV_GroupID", "在 SV_ 处必须补出 SV_GroupID");
        Check.Contains(labels, "SV_GroupThreadID", "在 SV_ 处必须补出 SV_GroupThreadID");
        Check.Contains(labels, "SV_GroupIndex", "在 SV_ 处必须补出 SV_GroupIndex");
        TestCaseHelpers.Report(
            "SV_ 候选：" + string.Join(",", labels.Where(l => l.StartsWith("SV_", StringComparison.Ordinal))));
    }

    // ── 6. 存储修饰符 ──

    private static void StorageModifiers()
    {
        var labels = Labels(ItemsAt(Doc, "groupshared", 11));
        Check.Contains(labels, "groupshared", "输入 groupshared 必须补出 groupshared");

        var threads = Labels(ItemsAt(Doc, "numthreads", 10));
        Check.Contains(threads, "numthreads", "输入 numthreads 必须补出 numthreads");
        TestCaseHelpers.Report("修饰符候选：group " + labels.Count + " 条 / numth " + threads.Count + " 条");
    }

    // ── 7. "#pragma kernel " 后的函数名补全 ──

    /// <remarks>
    /// 光标取第一行 "#pragma kernel CSMain" 里 "kernel " 之后（即 CSMain 之前）。
    /// 这个位置 TriggerContext 返回 None（无标识符前缀），必须由独立路径接住。
    /// </remarks>
    private static void PragmaKernelEntry()
    {
        var labels = LabelsAt(Doc, "#pragma kernel ", "#pragma kernel ".Length);
        Check.Contains(labels, "CSMain", "`#pragma kernel ` 后必须补出本文件的 CSMain");
        Check.DoesNotContain(labels, "Payload", "结构体不是入口函数，不得出现在 pragma 入口候选里");
        TestCaseHelpers.Report("#pragma kernel 候选：" + string.Join(",", labels));
    }

    // ── 8. 没有函数可给时不产生空弹窗 ──

    private static void PragmaEntryWithoutFunction()
    {
        var labels = LabelsAt(NoFunctionDoc, "#pragma kernel ", "#pragma kernel ".Length);
        Check.Equal(
            0,
            labels.Count,
            "前缀位置没有任何本文件函数时必须返回空（不弹窗），实际：" + string.Join(",", labels));
    }

    // ── 辅助 ──

    private static List<JsonElement> ItemsAt(string text, string marker, int offsetInMarker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Check.True(index >= 0, "语料缺少标记：" + marker);
        return Complete(text, index + offsetInMarker);
    }

    private static List<string> LabelsAt(string text, string marker, int offsetInMarker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        Check.True(index >= 0, "语料缺少标记：" + marker);
        return Labels(Complete(text, index + offsetInMarker));
    }

    private static List<JsonElement> Complete(string text, int absolute)
    {
        var (line, character) = OffsetToPosition(text, absolute);
        var writer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(writer))
        {
            if (!new IntelliSenseService().ProvideCompletions(
                    new PositionRequest("file:///Selftest.compute", text, line, character), json))
            {
                return [];
            }
        }

        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(writer.WrittenSpan));
        var items = new List<JsonElement>();
        if (!document.RootElement.TryGetProperty("items", out var array))
        {
            return items;
        }

        foreach (var item in array.EnumerateArray())
        {
            items.Add(item.Clone());
        }

        return items;
    }

    private static (int Line, int Character) OffsetToPosition(string text, int absolute)
    {
        var line = 0;
        var lineStart = 0;
        for (var i = 0; i < absolute && i < text.Length; i++)
        {
            if (text[i] == (char)10)
            {
                line++;
                lineStart = i + 1;
            }
        }

        return (line, absolute - lineStart);
    }

    private static List<string> Labels(List<JsonElement> items)
    {
        var labels = new List<string>(items.Count);
        foreach (var item in items)
        {
            labels.Add(Label(item));
        }

        return labels;
    }

    private static string Label(JsonElement item)
        => item.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String
            ? label.GetString()!
            : string.Empty;

    private static string SortText(JsonElement item)
        => item.TryGetProperty("sortText", out var sortText) && sortText.ValueKind == JsonValueKind.String
            ? sortText.GetString()!
            : string.Empty;
}