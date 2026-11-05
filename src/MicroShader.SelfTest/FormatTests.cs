using System.Buffers;
using System.Text;
using System.Text.Json;
using MicroShader.CoordinationEngine.Scheduling;
using MicroShader.CoordinationEngine.Session;
using MicroShader.FormatEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 14 · 格式化自检。
/// </summary>
/// <remarks>
/// 覆盖重点不是输出好不好看，而是三条不能退让的性质。
/// 第一，默认 auto 下一个括号都不动，只重排缩进，这是对用户那句原本格式也是对的的直接回答；
/// 第二，注释、字符串、预处理器里的括号不参与任何结构判断；
/// 第三，格式化幂等，再格式化一次结果不变。
/// 再加一条真实样本（CarPearl.hlsl 的形状）：Allman 风格的用户文件在 auto 下必须逐字节不变。
/// </remarks>
internal static class FormatTests
{
    private const string Suite = "Format";

    public static void Register()
    {
        TestSuite.Add(Suite, "auto 只重排缩进、不动括号", AutoOnlyReindents);
        TestSuite.Add(Suite, "auto 对 Allman 样本逐字节不变", AutoLeavesAllmanSampleUntouched);
        TestSuite.Add(Suite, "kr 把独占一行的开括号并回上一行", KrMergesLoneOpenBrace);
        TestSuite.Add(Suite, "kr 把 else 并回闭括号那一行", KrMergesElseOntoClosingBrace);
        TestSuite.Add(Suite, "allman 把行末开括号拆成独占一行", AllmanSplitsTrailingOpenBrace);
        TestSuite.Add(Suite, "allman 把 } else { 拆成三行", AllmanSplitsClosingBraceElse);
        TestSuite.Add(Suite, "allman 的 else 也走独占一行", AllmanElseBraceOnOwnLine);
        TestSuite.Add(Suite, "allman 拆行时保留开括号后的行尾注释", AllmanKeepsCommentAfterBrace);
        TestSuite.Add(Suite, "kr 不把开括号并进带行尾注释的上一行", KrDoesNotSwallowBraceIntoComment);
        TestSuite.Add(Suite, "初始化器花括号不参与风格转换", InitializerBraceIsNotABlockBrace);
        TestSuite.Add(Suite, "单行块不被拆开", SingleLineBlockIsLeftAlone);
        TestSuite.Add(Suite, "字符串与注释里的花括号不影响深度", BracesInsideStringsAndCommentsAreInvisible);
        TestSuite.Add(Suite, "注释里的 else 不会被拆开", ElseInsideCommentIsNotRestructured);
        TestSuite.Add(Suite, "跨行块注释保留原缩进", BlockCommentContinuationKeepsIndent);
        TestSuite.Add(Suite, "预处理器行不重排缩进、不数括号", PreprocessorLinesAreUntouched);
        TestSuite.Add(Suite, "case 与 default 减一级缩进", CaseAndDefaultDedent);
        TestSuite.Add(Suite, "多行参数列表按续行保留原缩进", MultiLineParameterListKeepsIndent);
        TestSuite.Add(Suite, "行尾空白被去掉", TrailingWhitespaceIsTrimmed);
        TestSuite.Add(Suite, "insertSpaces=false 用制表符", TabsWhenInsertSpacesIsFalse);
        TestSuite.Add(Suite, "空输入不抛异常", EmptyInputIsSafe);
        TestSuite.Add(Suite, "无改动时产出零条编辑", NoChangeYieldsNoEdits);
        TestSuite.Add(Suite, "行数变化时退化为整文档编辑", LineCountChangeFallsBackToWholeDocumentEdit);
        TestSuite.Add(Suite, "三种风格各自幂等", AllStylesAreIdempotent);
        TestSuite.Add(Suite, "ProvideFormatting 写出合法 TextEdit 数组", ProvideFormattingWritesValidTextEdits);
        TestSuite.Add(Suite, "未启用时声明与实现都不出现", ProviderDisabledMeansNoCapability);
        TestSuite.Add(Suite, "协议回环：formatting 走通并宣告能力", FormattingOverWire);
        TestSuite.Add(Suite, "协议回环：未注入时回 MethodNotFound", FormattingMethodNotFoundWhenNotInjected);
        TestSuite.Add(Suite, "协议回环：文档未打开时回 null", FormattingUnopenedDocumentReturnsNull);
        TestSuite.Add(Suite, "协议回环：括号风格由服务端配置决定", FormattingHonorsServerBraceStyle);
    }

    // ------------------------------------------------------------------ auto

    private static void AutoOnlyReindents()
    {
        var source = Lines(
            "void f()",
            "{",
            "        g();",
            "}");

        var expected = Lines(
            "void f()",
            "{",
            "    g();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Auto), "auto 只重排缩进");
    }

    private static void AutoLeavesAllmanSampleUntouched()
    {
        // CarPearl.hlsl 的形状：4 空格缩进、预处理器在 0 列、顶部中文注释、
        // 函数开括号独占一行、多行参数列表末参带开括号。
        var source = Lines(
            "// 珍珠漆视角响应",
            "// 参数：法线 / 视线",
            "#ifndef CAR_PEARL_HLSL",
            "#define CAR_PEARL_HLSL",
            "",
            "float CarPearlViewResponse(float3 normalWS, float3 viewDirectionWS)",
            "{",
            "    float NoV = saturate(dot(normalWS, viewDirectionWS));",
            "    float grazing = 1.0 - NoV;",
            "    float grazing5 = grazing * grazing * grazing * grazing * grazing;",
            "    return NoV * (1.0 - grazing);",
            "}",
            "",
            "void CarPearlOnly_float(",
            "    float3 normalWS,",
            "    float3 viewDirectionWS,",
            "    out float3 Out)",
            "{",
            "    Out = CarPearlViewResponse(normalWS, viewDirectionWS);",
            "}",
            "#endif");

        Check.Equal(source, Format(source, BraceStyle.Auto), "auto 不得改动 Allman 文件");
        Check.Equal(0, HlslFormatter.FormatEdits(source, Options(BraceStyle.Auto)).Count, "auto 下不应产出编辑");
    }

    // ------------------------------------------------------------------ kr

    private static void KrMergesLoneOpenBrace()
    {
        var source = Lines(
            "void f()",
            "{",
            "    g();",
            "}");

        var expected = Lines(
            "void f() {",
            "    g();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Kr), "K&R 并回开括号");
    }

    private static void KrMergesElseOntoClosingBrace()
    {
        var source = Lines(
            "if (a)",
            "{",
            "    g();",
            "}",
            "else",
            "{",
            "    h();",
            "}");

        var expected = Lines(
            "if (a) {",
            "    g();",
            "} else {",
            "    h();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Kr), "K&R 合并 else");
    }

    // ------------------------------------------------------------------ allman

    private static void AllmanSplitsTrailingOpenBrace()
    {
        var source = Lines(
            "void f() {",
            "    g();",
            "}");

        var expected = Lines(
            "void f()",
            "{",
            "    g();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Allman), "Allman 拆出开括号");
    }

    private static void AllmanSplitsClosingBraceElse()
    {
        var source = Lines(
            "if (a) {",
            "    g();",
            "} else {",
            "    h();",
            "}");

        var expected = Lines(
            "if (a)",
            "{",
            "    g();",
            "}",
            "else",
            "{",
            "    h();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Allman), "Allman 拆开 } else {");
    }

    private static void AllmanElseBraceOnOwnLine()
    {
        var source = Lines(
            "if (a) {",
            "    g();",
            "}",
            "else {",
            "    h();",
            "}");

        var expected = Lines(
            "if (a)",
            "{",
            "    g();",
            "}",
            "else",
            "{",
            "    h();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Allman), "Allman 的 else 独占一行");
    }

    // ------------------------------------------------------------------ 不参与转换的括号

    private static void InitializerBraceIsNotABlockBrace()
    {
        var source = Lines(
            "static const float kTable[] =",
            "{",
            "    1.0, 2.0,",
            "    3.0, 4.0",
            "};");

        Check.Equal(source, Format(source, BraceStyle.Kr), "初始化器开括号不该被并回上一行");
        Check.Equal(source, Format(source, BraceStyle.Allman), "初始化器开括号不该被拆开");
    }

    private static void SingleLineBlockIsLeftAlone()
    {
        var source = Lines(
            "void f() { g(); }",
            "void h() { return; }");

        Check.Equal(source, Format(source, BraceStyle.Allman), "单行块不拆");
        Check.Equal(source, Format(source, BraceStyle.Kr), "单行块不动");
    }

    // ------------------------------------------------------------------ 注释与字符串

    private static void BracesInsideStringsAndCommentsAreInvisible()
    {
        var source = Lines(
            "void f()",
            "{",
            "    float s = \"{\";",
            "    // }",
            "    g();",
            "}");

        Check.Equal(source, Format(source, BraceStyle.Auto), "字符串与行注释里的括号不影响深度");

        var commentOpen = Lines(
            "void f()",
            "{",
            "    // {",
            "        g();",
            "}");

        var expected = Lines(
            "void f()",
            "{",
            "    // {",
            "    g();",
            "}");

        Check.Equal(expected, Format(commentOpen, BraceStyle.Auto), "注释里的 { 不该抬高后续行深度");
    }

    private static void ElseInsideCommentIsNotRestructured()
    {
        var source = Lines(
            "void f()",
            "{",
            "    // } else {",
            "    g();",
            "}");

        Check.Equal(source, Format(source, BraceStyle.Allman), "注释里的 else 不该被拆");
    }

    private static void BlockCommentContinuationKeepsIndent()
    {
        var source = Lines(
            "void f()",
            "{",
            "    /*",
            "        } else {",
            "    */",
            "        g();",
            "}");

        var expected = Lines(
            "void f()",
            "{",
            "    /*",
            "        } else {",
            "    */",
            "    g();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Auto), "块注释续行保留原缩进");
    }

    // ------------------------------------------------------------------ 预处理器与续行

    private static void PreprocessorLinesAreUntouched()
    {
        var source = Lines(
            "void f()",
            "{",
            "#define BRACE {",
            "        g();",
            "}");

        var expected = Lines(
            "void f()",
            "{",
            "#define BRACE {",
            "    g();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Auto), "预处理器行不重排缩进、其括号不计数");
    }

    private static void CaseAndDefaultDedent()
    {
        var source = Lines(
            "void f()",
            "{",
            "    switch (x)",
            "    {",
            "        case 1:",
            "            g();",
            "            break;",
            "        default:",
            "            h();",
            "    }",
            "}");

        var expected = Lines(
            "void f()",
            "{",
            "    switch (x)",
            "    {",
            "    case 1:",
            "        g();",
            "        break;",
            "    default:",
            "        h();",
            "    }",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Auto), "case/default 减一级缩进");
    }

    private static void MultiLineParameterListKeepsIndent()
    {
        var source = Lines(
            "void f(",
            "        float a,",
            "        float b)",
            "{",
            "    g();",
            "}");

        Check.Equal(source, Format(source, BraceStyle.Auto), "续行保留原缩进");
    }

    // ------------------------------------------------------------------ 空白与参数

    private static void TrailingWhitespaceIsTrimmed()
    {
        var source = "void f()\n{\n    g();   \n}  \n";

        var expected = Lines(
            "void f()",
            "{",
            "    g();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Auto), "行尾空白被去掉");
    }

    private static void TabsWhenInsertSpacesIsFalse()
    {
        var source = Lines(
            "void f()",
            "{",
            "    g();",
            "}");

        var expected = "void f()\n{\n\tg();\n}\n";

        Check.Equal(
            expected,
            Format(source, BraceStyle.Auto, tabSize: 4, insertSpaces: false),
            "insertSpaces=false 时用制表符");
    }

    private static void EmptyInputIsSafe()
    {
        Check.Equal(string.Empty, Format(string.Empty, BraceStyle.Auto), "空输入");
        Check.Equal(string.Empty, Format(string.Empty, BraceStyle.Allman), "空输入（Allman）");
        Check.Equal(string.Empty, Format(string.Empty, BraceStyle.Kr), "空输入（K&R）");
    }

    private static void AllmanKeepsCommentAfterBrace()
    {
        // 开括号后面的注释必须跟着括号走：拆行只该搬括号，不该删注释。
        var source = Lines(
            "void f() { // 说明",
            "    g();",
            "}",
            "",
            "if (a) {",
            "    g();",
            "} else { /* 另一个分支 */",
            "    h();",
            "}");

        var expected = Lines(
            "void f()",
            "{ // 说明",
            "    g();",
            "}",
            "",
            "if (a)",
            "{",
            "    g();",
            "}",
            "else",
            "{ /* 另一个分支 */",
            "    h();",
            "}");

        Check.Equal(expected, Format(source, BraceStyle.Allman), "Allman 拆行不得丢注释");
    }

    private static void KrDoesNotSwallowBraceIntoComment()
    {
        // 上一行以行尾注释收场时不能并括号：并进去的开括号会被注释吞掉，
        // 块等于没开，后面所有行的缩进一起塌。
        var source = Lines(
            "void f()   // 说明",
            "{",
            "    g();",
            "}");

        Check.Equal(source, Format(source, BraceStyle.Kr), "K&R 遇到行尾注释应保持原样");
        Check.Equal(0, HlslFormatter.FormatEdits(source, Options(BraceStyle.Kr)).Count,
            "不并括号时不产出编辑（缩进本来就对）");
    }

    // ------------------------------------------------------------------ 编辑产出

    private static void NoChangeYieldsNoEdits()
    {
        var source = Lines(
            "void f()",
            "{",
            "    g();",
            "}");

        Check.Equal(0, HlslFormatter.FormatEdits(source, Options(BraceStyle.Auto)).Count, "无改动应产出零条编辑");
    }

    private static void LineCountChangeFallsBackToWholeDocumentEdit()
    {
        var source = Lines(
            "void f() {",
            "    g();",
            "}");

        var edits = HlslFormatter.FormatEdits(source, Options(BraceStyle.Allman));

        Check.Equal(1, edits.Count, "行数变化应退化为一条整文档编辑");
        Check.Equal(0, edits[0].StartLine, "起始行");
        Check.Equal(0, edits[0].StartCharacter, "起始列");
        Check.Equal(
            Lines(
                "void f()",
                "{",
                "    g();",
                "}"),
            edits[0].NewText,
            "整文档新文本");
    }

    // ------------------------------------------------------------------ 幂等

    private static void AllStylesAreIdempotent()
    {
        var source = Lines(
            "// 中文注释，行尾有空白   ",
            "#ifndef X",
            "#define X {",
            "#endif",
            "",
            "static const float k[] =",
            "{",
            "    1.0, 2.0,",
            "    3.0, 4.0",
            "};",
            "",
            "void f(",
            "        float a,",
            "        float b)",
            "{",
            "    if (a)",
            "    {",
            "        g();",
            "    }",
            "    else",
            "    {",
            "        h();",
            "    }",
            "    /*",
            "        } else {",
            "    */",
            "    float s = \"{\";",
            "}");

        foreach (var style in new[] { BraceStyle.Auto, BraceStyle.Allman, BraceStyle.Kr })
        {
            var once = Format(source, style);
            var twice = Format(once, style);
            Check.Equal(once, twice, $"风格 {style} 必须幂等");
        }
    }

    // ------------------------------------------------------------------ LSP 写出

    private static void ProvideFormattingWritesValidTextEdits()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            var service = new FormatService();
            var produced = service.ProvideFormatting(
                "file:///t.hlsl",
                "t.hlsl",
                Lines(
                    "void f()",
                    "{",
                    "        g();",
                    "}"),
                Options(BraceStyle.Auto),
                writer);
            writer.Flush();

            Check.True(produced, "应当产出编辑");

            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            Check.Equal(1, document.RootElement.GetArrayLength(), "应当只有一行需要改");

            var edit = document.RootElement[0];
            Check.Equal(2, edit.GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(), "起始行");
            Check.Equal(0, edit.GetProperty("range").GetProperty("start").GetProperty("character").GetInt32(), "起始列");
            Check.Equal(2, edit.GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(), "结束行");
            Check.Equal("    g();", edit.GetProperty("newText").GetString(), "newText");
        }

        // 无改动时必须返回 false（调用方据此回 null，而不是回一个空数组）。
        var clean = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(clean))
        {
            var service = new FormatService();
            var produced = service.ProvideFormatting(
                "file:///t.hlsl",
                "t.hlsl",
                Lines(
                    "void f()",
                    "{",
                    "    g();",
                    "}"),
                Options(BraceStyle.Auto),
                writer);
            writer.Flush();

            Check.False(produced, "无改动应返回 false");
        }
    }

    private static void ProviderDisabledMeansNoCapability()
    {
        var disabled = new FormatProvider();
        Check.False(disabled.IsEnabled, "没有注入写出器时能力视为未启用");
        Check.Equal(BraceStyle.Auto, disabled.BraceStyle, "默认括号风格是 auto");

        var enabled = new FormatProvider { Formatting = new FormatService().ProvideFormatting, BraceStyle = BraceStyle.Kr };
        Check.True(enabled.IsEnabled, "注入写出器后能力启用");
        Check.Equal(BraceStyle.Kr, enabled.BraceStyle, "括号风格透传");
    }

    // ------------------------------------------------------------------ 协议回环

    /// <summary>
    /// 走真实 LspServerSession：能力宣告、方法分发、参数解析、编辑产出整条链。
    /// </summary>
    /// <remarks>
    /// 上面几条直接调 FormatService，只能证明格式化器本身对；
    /// 这里证明「接线对」—— 少声明一个能力、少一个分发分支、参数读错，都会在这里露出来。
    /// </remarks>
    private static void FormattingOverWire()
    {
        using var client = new LspTestClient(new LspServerSession(
            new FakeAnalyzer().AnalyzeAsync,
            null,
            null,
            null,
            new FormatProvider { Formatting = new FormatService().ProvideFormatting, BraceStyle = BraceStyle.Auto }));

        var response = Request(client, 1, "initialize", """{"processId":1,"rootUri":null,"capabilities":{}}""");
        var capabilities = JsonDocument.Parse(response).RootElement.GetProperty("result").GetProperty("capabilities");
        Check.True(capabilities.GetProperty("documentFormattingProvider").GetBoolean(),
            "注入格式化能力后必须宣告 documentFormattingProvider");

        Open(client, WireUri, WireDocument);

        var formatted = Request(client, 7, "textDocument/formatting",
            "{\"textDocument\":{\"uri\":" + Json(WireUri) + "},\"options\":{\"tabSize\":4,\"insertSpaces\":true}}");

        var edits = JsonDocument.Parse(formatted).RootElement.GetProperty("result");
        Check.Equal(JsonValueKind.Array, edits.ValueKind, "formatting 必须回 TextEdit 数组");
        Check.Equal(1, edits.GetArrayLength(), "只有一行缩进是错的");
        Check.Equal("    g();", edits[0].GetProperty("newText").GetString(), "newText");
    }

    private static void FormattingMethodNotFoundWhenNotInjected()
    {
        using var client = new LspTestClient(new LspServerSession(new FakeAnalyzer().AnalyzeAsync));
        var response = Request(client, 1, "initialize", """{"processId":1,"rootUri":null,"capabilities":{}}""");
        var capabilities = JsonDocument.Parse(response).RootElement.GetProperty("result").GetProperty("capabilities");

        Check.True(!capabilities.TryGetProperty("documentFormattingProvider", out _),
            "未注入格式化能力时绝不能宣告（宣告了却不会响应 = 客户端永久挂起）");

        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"textDocument/formatting\",\"params\":{}}")
            .AsTask().GetAwaiter().GetResult();

        var error = JsonDocument.Parse(ReceiveFrame(client, 9)).RootElement.GetProperty("error");
        Check.Equal(-32601, error.GetProperty("code").GetInt32(),
            "未启用时 formatting 必须回 MethodNotFound(-32601)，不能静默丢弃");
    }

    private static void FormattingUnopenedDocumentReturnsNull()
    {
        using var client = new LspTestClient(new LspServerSession(
            new FakeAnalyzer().AnalyzeAsync,
            null,
            null,
            null,
            new FormatProvider { Formatting = new FormatService().ProvideFormatting }));

        _ = Request(client, 1, "initialize", """{"processId":1,"rootUri":null,"capabilities":{}}""");

        var response = Request(client, 3, "textDocument/formatting",
            "{\"textDocument\":{\"uri\":" + Json(WireUri) + "},\"options\":{\"tabSize\":4,\"insertSpaces\":true}}");

        Check.Equal(JsonValueKind.Null,
            JsonDocument.Parse(response).RootElement.GetProperty("result").ValueKind,
            "文档未打开时回 null（合法的无结果），不是报错也不是空数组");
    }

    /// <summary>
    /// 括号风格是服务端启动参数（--brace-style），不是客户端 options 能改的。
    /// </summary>
    /// <remarks>
    /// 客户端的 options 里没有风格这个标准字段；风格由扩展配置转成命令行参数，
    /// 在进程启动时固定。这条断言把「风格确实来自服务端配置」钉住，
    /// 免得日后有人把它挪进 options 而破坏既有配置路径。
    /// </remarks>
    private static void FormattingHonorsServerBraceStyle()
    {
        using var client = new LspTestClient(new LspServerSession(
            new FakeAnalyzer().AnalyzeAsync,
            null,
            null,
            null,
            new FormatProvider { Formatting = new FormatService().ProvideFormatting, BraceStyle = BraceStyle.Kr }));

        _ = Request(client, 1, "initialize", """{"processId":1,"rootUri":null,"capabilities":{}}""");
        Open(client, WireUri, WireDocument);

        var response = Request(client, 7, "textDocument/formatting",
            "{\"textDocument\":{\"uri\":" + Json(WireUri) + "},\"options\":{\"tabSize\":4,\"insertSpaces\":true}}");

        var edits = JsonDocument.Parse(response).RootElement.GetProperty("result");
        Check.Equal(1, edits.GetArrayLength(), "K&R 合并开括号后行数变化，退化为整文档编辑");
        Check.Equal("void f() {\n    g();\n}", edits[0].GetProperty("newText").GetString(),
            "开括号应并回签名行（整文档新文本）");
        Check.Equal(0, edits[0].GetProperty("range").GetProperty("start").GetProperty("line").GetInt32(), "整文档起始行");
        Check.Equal(3, edits[0].GetProperty("range").GetProperty("end").GetProperty("line").GetInt32(),
            "整文档结束行（原文 3 行、无结尾换行 → 末行整行替换）");
        Check.Equal(1, edits[0].GetProperty("range").GetProperty("end").GetProperty("character").GetInt32(),
            "整文档结束列");
    }

    private const string WireUri = "file:///D:/proj/Assets/Shaders/Fmt.hlsl";

    private const string WireDocument = """
        void f()
        {
                g();
        }
        """;

    private static void Open(LspTestClient client, string uri, string text)
    {
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{"
                + "\"uri\":" + Json(uri) + ",\"languageId\":\"hlsl\",\"version\":1,\"text\":" + Json(text) + "}}}")
            .AsTask().GetAwaiter().GetResult();
    }

    private static string Request(LspTestClient client, int id, string method, string parameters)
    {
        client.SendAtomicAsync("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":" + Json(method) + ",\"params\":" + parameters + "}")
            .AsTask().GetAwaiter().GetResult();

        return ReceiveFrame(client, id);
    }

    /// <summary>收到指定 id 的那一帧为止（didOpen 会异步推诊断，帧序不保证）。</summary>
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

    // ------------------------------------------------------------------ 工具

    private static FormatOptions Options(BraceStyle style)
        => new() { BraceStyle = style };

    private static string Format(string source, BraceStyle style, int tabSize = 4, bool insertSpaces = true)
        => HlslFormatter.Format(
            source,
            new FormatOptions
            {
                BraceStyle = style,
                TabSize = tabSize,
                InsertSpaces = insertSpaces,
            });

    private static string Lines(params string[] lines)
        => string.Join("\n", lines) + "\n";
}
