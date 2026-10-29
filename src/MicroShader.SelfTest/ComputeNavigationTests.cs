using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.Domain;
using MicroShader.IntelliSenseEngine;
using MicroShader.NavigationEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// compute 文件的 F12：kernel 函数名 → 定义行。
/// </summary>
/// <remarks>
/// 锁的行为：kernel 沙盒里整篇是一个伪 snippet（PassIndex = -1、IsOrphan = true）。
/// 若定义追踪依赖 Pass 归属来限定搜索范围，compute 里的函数名就永远跳不动 ——
/// 而这恰恰是 compute 作者最常用的导航动作（`#pragma kernel CSCopy` 那行按 F12 找实现）。
/// </remarks>
internal static class ComputeNavigationTests
{
    private const string Suite = "ComputeNav";
    private const string Uri = "file:///selftest/Copy.compute";

    public static void Register()
    {
        TestSuite.Add(Suite, "KernelNameJumpsToDefinition", KernelNameJumpsToDefinition);
        TestSuite.Add(Suite, "StructMemberJumpsToField", StructMemberJumpsToField);
    }

    /// <summary>compute 语料：假 #pragma 那行与函数定义相隔多行。</summary>
    private const string Doc = """
        #pragma kernel CSCopy

        RWStructuredBuffer<float4> outputBuffer;
        RWStructuredBuffer<float4> inputBuffer;

        struct Payload
        {
            float4 color;
            float weight;
        };

        [numthreads(8, 8, 1)]
        void CSCopy(uint3 id : SV_DispatchThreadID)
        {
            Payload p;
            p.color = inputBuffer[id.x];
            outputBuffer[id.x] = p.color;
        }
        """;

    // ── 1. kernel 名 → 函数定义 ──

    private static void KernelNameJumpsToDefinition()
    {
        var json = Definition(Doc, "#pragma kernel CSCopy", "CSCopy");
        Check.True(json is not null, "kernel 名上按 F12 必须给出定义");

        using var document = JsonDocument.Parse(json!);
        var items = document.RootElement;
        Check.Equal(1, items.GetArrayLength(), "唯一的 CSCopy 只应给一个候选");
        Check.Equal(Uri, items[0].GetProperty("uri").GetString(), "应指向本文档");

        var expected = Corpus.LineNumberContaining(Doc, "void CSCopy(") - 1;
        Check.Equal(
            expected,
            items[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
            "行号必须落在 void CSCopy( 那一行");
        TestCaseHelpers.Report("F12：pragma 行 → 第 " + (expected + 1) + " 行定义");
    }

    // ── 2. 结构体成员 → 字段声明 ──

    private static void StructMemberJumpsToField()
    {
        var json = Definition(Doc, "p.color = inputBuffer", "color");
        Check.True(json is not null, "结构体成员上按 F12 必须给出定义");

        using var document = JsonDocument.Parse(json!);
        var items = document.RootElement;
        Check.True(items.GetArrayLength() >= 1, "至少给出一个字段候选");

        var expected = Corpus.LineNumberContaining(Doc, "float4 color;") - 1;
        Check.Equal(
            expected,
            items[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(),
            "行号必须落在 float4 color; 那一行");
        TestCaseHelpers.Report("F12：p.color → 第 " + (expected + 1) + " 行字段");
    }

    // ── 辅助 ──

    private static string? Definition(string text, string anchor, string symbol)
    {
        var service = new NavigationService(new ComputeHost(text), new IncludeSymbolCache());
        var (line, character) = CursorAt(text, anchor, symbol);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (!service.ProvideDefinition(new PositionRequest(Uri, text, line, character), writer))
            {
                return null;
            }
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>把光标放在锚点内 <paramref name="symbol"/> 之后。</summary>
    private static (int Line, int Character) CursorAt(string text, string anchor, string symbol)
    {
        var at = text.IndexOf(anchor, StringComparison.Ordinal);
        Check.True(at >= 0, "语料缺少锚点：" + anchor);

        var symbolAt = text.IndexOf(symbol, at, StringComparison.Ordinal);
        Check.True(symbolAt >= 0 && symbolAt < at + anchor.Length, "锚点内找不到符号：" + symbol);

        var absolute = symbolAt + symbol.Length / 2;
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

    /// <summary>单文件宿主：compute 语料没有 #include，只需要「同一份文本」这一件事。</summary>
    private sealed class ComputeHost : INavigationHost
    {
        private readonly string _text;
        private readonly string _path;

        public ComputeHost(string text)
        {
            _text = text;
            _path = "/selftest/Copy.compute";
        }

        public bool TryResolve(string currentPhysicalPath, string virtualPath, out string physicalPath)
        {
            physicalPath = string.Empty;
            return false;
        }

        public bool TryResolveRelative(string currentPhysicalPath, string target, out string physicalPath)
        {
            physicalPath = string.Empty;
            return false;
        }

        public bool TryRead(string physicalPath, out string text)
        {
            text = _text;
            return true;
        }

        public long GetStamp(string physicalPath) => 1;

        public bool TryGetPhysicalPath(string uri, out string physicalPath)
        {
            physicalPath = _path;
            return true;
        }

        public bool TryGetOpenDocument(string physicalPath, out string uri, out string text)
        {
            uri = Uri;
            text = _text;
            return true;
        }

        public string ToFileUri(string physicalPath) => Uri;

        public bool Exists(string physicalPath) => true;
    }
}