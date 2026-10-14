using MicroShader.DiagnosticEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 4（NativeCompilerGateway）的固定用例：真实 dxcompiler.dll 加载、原生编译、
/// 诊断文本读取与解析，以及「include 解析基准」的判定实验。
/// </summary>
internal static class DxcGatewayTests
{
    public static void Register()
    {
        TestSuite.Add("Dxc.Load", "绝对路径加载 dxcompiler.dll 并读出真实版本", LoadLibrary);
        TestSuite.Add("Dxc.Minimal", "最小 HLSL 在自研垫片下真编译产出 DXIL", MinimalCompile);
        TestSuite.Add("Dxc.Diagnostic", "错误诊断经 COM UTF-8 通道读取并解析出文件/行/列", DiagnosticText);
        TestSuite.Add("Dxc.Utf16", "UTF-16 零拷贝输入不产生替换字符", Utf16Input);
        TestSuite.Add("Dxc.Column", "DXC 列号 = 1-based UTF-8 字节偏移", ColumnUnit);
        TestSuite.Add("Dxc.IncludeBase", "基准实验：include 解析究竟依赖什么", IncludeBaseExperiment);
    }

    private const string DxcDirOverrideEnv = "MICROSHADER_DXC_DIR";

    /// <summary>开发期 dxcompiler.dll 目录：环境变量优先，否则从输出目录向上找 MicroShader.sln。</summary>
    internal static string DxcDirectory()
    {
        var overridden = Environment.GetEnvironmentVariable(DxcDirOverrideEnv);
        if (!string.IsNullOrEmpty(overridden) && File.Exists(Path.Combine(overridden, "dxcompiler.dll")))
        {
            return overridden;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MicroShader.sln")))
            {
                return Path.Combine(dir.FullName, "probes", "dxc-aot", "native");
            }

            dir = dir.Parent;
        }

        throw new SkipException("未找到 MicroShader.sln，无法定位探针 dxcompiler.dll");
    }

    internal static NativeCompilerGateway Open()
    {
        if (!NativeCompilerGateway.TryCreate(DxcDirectory(), out var gateway, out var error, maxThreads: 2) || gateway is null)
        {
            throw new SkipException("dxcompiler.dll 不可用：" + error);
        }

        return gateway;
    }

    private static void LoadLibrary()
    {
        using var gateway = Open();
        Check.True(gateway.DxcVersion.Major >= 1, $"DXC 版本应 >= 1.0，实际 {gateway.DxcVersion}");
        TestCaseHelpers.Report($"DXC 版本 {gateway.DxcVersion}，实例池上限 {gateway.MaxThreads}，加载目录 {DxcDirectory()}");
    }

    private static void MinimalCompile()
    {
        using var gateway = Open();
        var result = gateway.Compile(
            "float4 main() : SV_Target { return float4(1, 0, 0, 1); }",
            ["-T", "ps_6_0", "-E", "main"]);

        Check.True(!result.Failed, $"最小 shader 应编译成功：status={result.Status} text={result.DiagnosticText}");
        Check.True(result.ObjectSize > 0, "应产出非空 DXIL 对象");
        TestCaseHelpers.Report($"最小 shader → DXIL {result.ObjectSize} 字节，{result.ElapsedMicroseconds / 1000.0:F2} ms");
    }

    private static void DiagnosticText()
    {
        using var gateway = Open();
        // 第 2 行是未声明标识符；第 1 行含中文注释，验证 UTF-8 通道不乱码。
        var result = gateway.Compile(
            "// 中文注释：未声明的标识符\nfloat4 main() : SV_Target { return undefined_symbol_x; }\n",
            ["-T", "ps_6_0", "-E", "main"]);

        Check.True(result.Failed, "未声明标识符应编译失败");

        var parsed = DxcDiagnosticParser.Parse(result.DiagnosticText);
        Check.True(parsed.Count > 0, "应解析出至少一条诊断：" + result.DiagnosticText);
        var first = parsed[0];
        Check.Equal(2, first.Line, "诊断应落在第 2 行");
        Check.Equal(36, first.Column, "标识符 'undefined_symbol_x' 起始列应为 36");
        Check.True(first.Message.Contains("undefined_symbol_x", StringComparison.Ordinal), "消息应含标识符名");
        Check.Equal(MicroShader.Domain.DiagnosticSeverity.Error, first.Severity, "级别应为 Error");

        TestCaseHelpers.Report($"解析结果：{first.File}:{first.Line}:{first.Column} {first.Severity} {first.Message}");
    }

    private static void Utf16Input()
    {
        using var gateway = Open();
        var result = gateway.Compile(
            "// 中文注释：编码验证\nfloat4 main() : SV_Target { return float4(1, 0, 0, 1); }\n",
            ["-T", "ps_6_0", "-E", "main"]);

        Check.True(!result.Failed, "带中文注释的 shader 应编译成功：" + result.DiagnosticText);
        Check.True(!result.DiagnosticText.Contains('\uFFFD'), "不应出现替换字符");
    }

    private static void ColumnUnit()
    {
        using var gateway = Open();
        // 😀 = UTF-8 4 字节 / UTF-16 2 单元，位于出错行的注释里。
        // 前缀 "/* " (3B) + 😀 (4B) + " */ " (4B) = 11 字节；UTF-16 下同样前缀 = 9 单元。
        // 之后 "float4 main() : SV_Target { return " 共 35 字节 → 标识符列 = 47（UTF-8 字节）/ 45（UTF-16 单元）。
        var result = gateway.Compile(
            "/* 😀 */ float4 main() : SV_Target { return undefined_symbol_x; }\n",
            ["-T", "ps_6_0", "-E", "main"]);

        Check.True(result.Failed, "应编译失败");
        var parsed = DxcDiagnosticParser.Parse(result.DiagnosticText);
        Check.True(parsed.Count > 0, "应解析出诊断");
        Check.Equal(1, parsed[0].Line, "应落在第 1 行");
        Check.Equal(47, parsed[0].Column,
            "列号应为 47（UTF-8 字节偏移）；若为 45 则说明是 UTF-16 单元，ADR-013 结论需推翻");

        TestCaseHelpers.Report($"含 emoji 行的诊断列号 = {parsed[0].Column}（UTF-8 字节 47 / UTF-16 单元 45）");
    }

    /// <summary>
    /// 判定实验：把「相对 include 能否解析」拆成 handler / argv[0] / -I / #line 文件名四个变量。
    /// 仅报告结论，不作为通过判据（真正的契约在 CompilerRealWorldTests 里断言）。
    /// </summary>
    private static void IncludeBaseExperiment()
    {
        using var gateway = Open();
        var root = Path.Combine(Path.GetTempPath(), "MicroShaderDxcIncludeBase", Guid.NewGuid().ToString("N"));
        var shaderDir = Path.Combine(root, "Assets", "Shaders");
        Directory.CreateDirectory(shaderDir);

        var localHlsl = Path.Combine(shaderDir, "Local.hlsl");
        var shaderPath = Path.Combine(shaderDir, "Rel.shader");
        File.WriteAllText(localHlsl, "#define LOCAL_VALUE 1\n");
        File.WriteAllText(shaderPath, "// 仅作物理位置，内容不参与编译\n");

        const string body = "float4 main() : SV_Target { return LOCAL_VALUE; }";
        var quotedVirtual = "#line 1 \"Assets/Shaders/Rel.shader\"\n#include \"Local.hlsl\"\n" + body;
        var quotedPhysical = "#line 1 \"" + shaderPath.Replace('\\', '/') + "\"\n#include \"Local.hlsl\"\n" + body;
        var quotedNoLine = "#include \"Local.hlsl\"\n" + body;
        var angle = "#line 1 \"Assets/Shaders/Rel.shader\"\n#include <Local.hlsl>\n" + body;
        var absolutePath = "#line 1 \"Assets/Shaders/Rel.shader\"\n#include \"" + localHlsl.Replace('\\', '/') + "\"\n" + body;

        try
        {
            Report1("1  无 handler | argv[0]=物理 | #line 物理", gateway.Compile(quotedPhysical, Name(shaderPath, "-T", "ps_6_0", "-E", "main"), false));
            Report1("2  无 handler | argv[0]=物理 | -I 目录", gateway.Compile(quotedNoLine, Name(shaderPath, "-T", "ps_6_0", "-E", "main", "-I", shaderDir), false));
            Report1("3  handler   | argv[0]=物理 | 无 #line 无 -I", gateway.Compile(quotedNoLine, Name(shaderPath, "-T", "ps_6_0", "-E", "main"), true));
            Report1("4  handler   | argv[0]=物理 | #line 物理 无 -I", gateway.Compile(quotedPhysical, Name(shaderPath, "-T", "ps_6_0", "-E", "main"), true));
            Report1("5  handler   | argv[0]=物理 | #line 虚拟 无 -I", gateway.Compile(quotedVirtual, Name(shaderPath, "-T", "ps_6_0", "-E", "main"), true));
            Report1("6  handler   | argv[0]=物理 | #line 虚拟 + -I 目录", gateway.Compile(quotedVirtual, Name(shaderPath, "-T", "ps_6_0", "-E", "main", "-I", shaderDir), true));
            Report1("7  handler   | argv[0]=无    | #line 虚拟 + -I 目录", gateway.Compile(quotedVirtual, Name(null, "-T", "ps_6_0", "-E", "main", "-I", shaderDir), true));
            Report1("8  handler   | argv[0]=物理 | 尖括号 <Local.hlsl> + -I 目录", gateway.Compile(angle, Name(shaderPath, "-T", "ps_6_0", "-E", "main", "-I", shaderDir), true));
            Report1("9  handler   | argv[0]=物理 | include 绝对物理路径", gateway.Compile(absolutePath, Name(shaderPath, "-T", "ps_6_0", "-E", "main"), true));
            Report1("10 handler   | argv[0]=物理 | #line 虚拟 + -I目录(连写)", gateway.Compile(quotedVirtual, Name(shaderPath, "-T", "ps_6_0", "-E", "main", "-I" + shaderDir), true));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static void Report1(string label, DxcCompileResult r)
    {
        var parsed = DxcDiagnosticParser.Parse(r.DiagnosticText);
        var where = parsed.Count > 0 && parsed[0].HasLocation ? $"{parsed[0].File}:{parsed[0].Line}" : "(无位置)";
        TestCaseHelpers.Report($"{label} → {(r.Failed ? "失败" : "成功")} obj={r.ObjectSize}B {where}");
    }

    private static string[] Name(string? sourceName, params string[] rest)
    {
        var list = new List<string>();
        if (sourceName is not null)
        {
            list.Add(sourceName);
        }

        list.AddRange(rest);
        return list.ToArray();
    }
}
