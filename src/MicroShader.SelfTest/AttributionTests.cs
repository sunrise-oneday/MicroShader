using MicroShader.ContextEngine;
using MicroShader.DiagnosticEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 5（降噪过滤与 include 错误重映射）的行为用例，直接驱动 DiagnosticAttributor：
/// pragma-message 过滤、直接 include 重映射、深层依赖重映射、未匹配兜底、注入段内部错误、去重。
/// 输入用 DXC 诊断原文（解析器是输入边界），不依赖原生 DXC。
/// </summary>
internal static class AttributionTests
{
    private const string Suite = "Attribution";

    public static void Register()
    {
        TestSuite.Add(Suite, "PragmaMessageFiltered", PragmaMessageFiltered);
        TestSuite.Add(Suite, "MainFileMapped", MainFileMapped);
        TestSuite.Add(Suite, "DirectIncludeRemapped", DirectIncludeRemapped);
        TestSuite.Add(Suite, "DeepDependencyRemapped", DeepDependencyRemapped);
        TestSuite.Add(Suite, "UnmappedFallback", UnmappedFallback);
        TestSuite.Add(Suite, "ProloguePlaceholder", ProloguePlaceholder);
        TestSuite.Add(Suite, "DuplicateSuppressed", DuplicateSuppressed);
    }

    private static ShaderContextRegistry EmptyRegistry() => new();

    private static AssembledShaderText Assemble(string fixture)
    {
        var path = Corpus.PathOf(fixture);
        var text = Corpus.Read(fixture);
        var parse = new ShaderLabStateMachine().Parse(path, text);
        var document = new SourceShaderDocument
        {
            FileUri = parse.TargetUri,
            FilePath = path,
            FullText = text,
            Version = 1,
        };

        var assembler = new VirtualTextAssembler(EmptyRegistry());
        var result = assembler.Assemble(document, parse, parse.Passes[0], ShaderStage.Fragment);
        if (!result.Succeeded)
        {
            throw new AssertionException("装配失败: " + result.FailureDetail);
        }

        return result.Text!;
    }

    // 与 ShaderDiagnosticEngine.Bindings 同口径：include 解析交给模块 2（相对路径走磁盘）
    private static IncludeBinding Binding(string fixture, string marker)
    {
        var path = Corpus.PathOf(fixture);
        var text = Corpus.Read(fixture);
        var parse = new ShaderLabStateMachine().Parse(path, text);
        var include = parse.Passes[0].Includes[0];
        var resolution = IncludeResolver.Resolve(VfsIndex.Empty, include.Path.AsSpan(), path.AsSpan(), verifyOnDisk: true);
        return new IncludeBinding(include.LineNumber, include.Column, include.Path,
            resolution.Found ? resolution.PhysicalPath : null);
    }

    private static string Diag(string file, int line, int column, string severity, string message)
        => file + ":" + line + ":" + column + ": " + severity + ": " + message;

    private static DiagnosticAttribution Attribute(
        AssembledShaderText text,
        string diagnosticText,
        IncludeBinding[]? includes = null,
        int unitStartSourceLine = 1)
    {
        var result = new DxcCompileResult
        {
            Status = 0,
            CompileHr = 0,
            DiagnosticText = diagnosticText,
            ObjectSize = 0,
            ElapsedMicroseconds = 0,
        };

        return new DiagnosticAttributor().Attribute(
            result, text, includes ?? [], "file:///attr.shader", unitStartSourceLine);
    }

    private static int LineOf(string fixture, string marker)
    {
        var text = Corpus.Read(fixture);
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new AssertionException("fixture 缺少标记: " + marker);
        }

        var line = 1;
        for (var i = 0; i < at; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    // 1. pragma-message 警告必须被过滤（按源码行文本判定）。
    //    这里同时钉住一个真实缺陷：过滤判定曾把源行号直接当渲染行号用，
    //    被装配注入段的偏移错开，导致过滤永远打不中。
    private static void PragmaMessageFiltered()
    {
        using var text = Assemble("PragmaMessage.shader");
        var pragmaLine = LineOf("PragmaMessage.shader", "#pragma message");

        var attribution = Attribute(
            text,
            Diag(text.VirtualPath, pragmaLine, 1, "warning", "hello from pragma") + "\n");

        Check.Equal(1, attribution.RawCount, "原始诊断 1 条");
        Check.Equal(1, attribution.DroppedPragmaMessages, "必须命中 pragma-message 过滤");
        Check.Equal(0, attribution.Items.Count, "过滤后不得残留条目");
    }

    // 2. 主文件诊断：DXC 的行号已经过 #line 映射，直接就是源行
    private static void MainFileMapped()
    {
        using var text = Assemble("PragmaMessage.shader");
        var codeLine = LineOf("PragmaMessage.shader", "void vert()");

        var attribution = Attribute(
            text,
            Diag(text.VirtualPath, codeLine, 18, "error", "syntax error at token") + "\n");

        Check.Equal(1, attribution.Items.Count, "主文件诊断必须保留");
        var item = attribution.Items[0];
        Check.Equal(codeLine, item.Line, "行号必须等于源行");
        Check.Equal(18, item.Column, "ASCII 列必须逐字节一致");
    }

    // 3. 直接 include：重映射到用户文档的 #include 行
    private static void DirectIncludeRemapped()
    {
        const string fixture = "AttributionInclude.shader";
        using var text = Assemble(fixture);
        var includeLine = LineOf(fixture, "#include");
        var binding = Binding(fixture, "#include");
        Check.True(binding.PhysicalPath is not null, "相对 include 必须能在磁盘上解析");

        var attribution = Attribute(
            text,
            Diag(binding.PhysicalPath!, 55, 3, "warning", "implicit truncation of vector type") + "\n",
            [binding],
            unitStartSourceLine: 11);

        Check.Equal(1, attribution.Items.Count, "外部诊断必须保留（重映射而非丢弃）");
        var item = attribution.Items[0];
        Check.Equal(includeLine, item.Line, "必须重映射到 #include 所在行");
        Check.True(item.RelatedFilePath is not null, "必须附带关联文件");
        Check.Equal(55, item.RelatedLine, "必须附带关联行");
        // 位置落在 #include 行上，消息若不标来源，读起来像 include 本身有问题
        Check.True(item.Message.Contains("头文件错误"), "精确命中必须标注来源，与深层/未匹配两个分支对齐");
        Check.True(item.Message.Contains(":55]"), "标注必须带外部文件的行号");
    }

    // 4. 深层依赖：诊断文件在直接 include 的目录之下，重映射到 include 行并带上下文
    private static void DeepDependencyRemapped()
    {
        const string fixture = "AttributionInclude.shader";
        using var text = Assemble(fixture);
        var includeLine = LineOf(fixture, "#include");
        var binding = Binding(fixture, "#include");
        var dir = Path.GetDirectoryName(binding.PhysicalPath!);
        var deep = Path.Combine(dir!, "Deep.hlsl");

        var attribution = Attribute(
            text,
            Diag(deep, 77, 1, "error", "undeclared identifier") + "\n",
            [binding],
            unitStartSourceLine: 11);

        Check.Equal(1, attribution.Items.Count, "深层依赖诊断必须保留");
        var item = attribution.Items[0];
        Check.Equal(includeLine, item.Line, "必须重映射到 include 行");
        Check.True(item.Message.Contains("Deep.hlsl:77"), "必须带深层文件与行号上下文");
        Check.True(item.Message.Contains("Core.hlsl"), "必须说明经由哪条 include 引入");
    }

    // 5. 未匹配的外部文件：兜底到单元起始行并明确标注
    private static void UnmappedFallback()
    {
        using var text = Assemble("AttributionInclude.shader");

        var attribution = Attribute(
            text,
            Diag("D:/elsewhere/X.hlsl", 5, 1, "error", "mystery") + "\n",
            unitStartSourceLine: 11);

        Check.Equal(1, attribution.Unmapped, "必须计入未匹配");
        Check.Equal(1, attribution.Items.Count, "兜底仍须产出条目");
        Check.Equal(11, attribution.Items[0].Line, "兜底行 = 单元起始行");
        Check.True(attribution.Items[0].Message.Contains("外部头文件错误"), "消息必须标注来源");
    }

    // 6. DXC 占位文件名（注入段）→ 内部错误 MS0007
    private static void ProloguePlaceholder()
    {
        using var text = Assemble("AttributionInclude.shader");

        var attribution = Attribute(
            text,
            Diag("hlsl.hlsl", 1, 1, "error", "bad injection") + "\n");

        Check.Equal(1, attribution.InternalErrors, "必须计入内部错误");
        Check.Equal(DiagnosticEngineCodes.AssemblyPrologue, attribution.Items[0].Code, "必须是 MS0007");
    }

    // 7. 相同位置与消息的重复诊断必须去重
    private static void DuplicateSuppressed()
    {
        using var text = Assemble("PragmaMessage.shader");
        var codeLine = LineOf("PragmaMessage.shader", "void vert()");
        var one = Diag(text.VirtualPath, codeLine, 18, "error", "same") + "\n";

        var attribution = Attribute(text, one + one);

        Check.Equal(2, attribution.RawCount, "原始 2 条");
        Check.Equal(1, attribution.Items.Count, "去重后 1 条");
    }
}