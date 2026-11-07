using System.Diagnostics;
using System.Text;
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
        TestSuite.Add("Dxc.ParserEcho", "源码回显里的 error: 字样不得伪造成诊断", ParserIgnoresSourceEcho);
        TestSuite.Add("Dxc.SlotCreate", "IDxcUtils 创建失败时释放 compiler 并标记 InvocationFailed", SlotCreationFailure);
        TestSuite.Add("Dxc.TrimIdle", "空闲回收只留指定数量的热实例，且绝不清空实例池", TrimIdleSlots);
        TestSuite.Add("Dxc.TrimBorrowed", "空闲回收不碰正在使用的实例，在飞编译不受影响", TrimIdleWhileBorrowed);
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

    internal static NativeCompilerGateway Open(int maxThreads = 2)
    {
        if (!NativeCompilerGateway.TryCreate(DxcDirectory(), out var gateway, out var error, maxThreads: maxThreads) || gateway is null)
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
    /// 回归：DXC 诊断后面会照抄出错那一行的源码（回显），回显里本来就可能出现 "error: " 字样
    /// （例如注释里写着 "// error: 说明"）。解析器只按「文件位置 + 级别 + 文案」严格匹配，
    /// 回显行没有文件位置前缀，必须被丢掉。
    ///
    /// 修复前的错误结果：回显行被解析成 File="" / Line=0 的 Error，归属阶段把「无文件」判成
    /// 工具自身缺陷（InternalErrors），于是在用户文件上凭空多出 2 条红线 —— 用例里的 5 条
    /// 真实诊断会变成 7 条，且多出来的 2 条消息里带着源码片段。
    /// </summary>
    private static void ParserIgnoresSourceEcho()
    {
        var chinesePath = "C:/Users/dev/工程 (备份)/Shaders/My Shader.shader";
        string[] lines =
        [
            // 1 真实诊断：路径含空格、括号、中文（DXC 实际形态）
            chinesePath + ":2:36: error: use of undeclared identifier 'undefined_x'",
            // 2 回显行（第 0 列，且含 "error: " 字样）—— 不得成为诊断
            "return undefined_x; // error: 回显里的字样不得成为诊断",
            // 3 脱字符行
            "                   ^",
            // 4 真实诊断：warning
            "C:/Users/dev/工程 (备份)/Shaders/Sub Folder/A B.shader:5:3: warning: 变量 'a' 未使用",
            // 5 回显行（带缩进）—— 不得成为诊断
            "    int a = 1; // error: 缩进回显同样不得成为诊断",
            "    ^",
            // 6 真实诊断：MSVC 括号形态
            "My Shader.hlsl(7,5): error: 括号形态的位置前缀也要认",
            // 7 真实诊断：文案本身含 "error: " 字样，不得被当成两个前缀切开
            "C:/工作/二级目录/b.hlsl:9:1: error: 文案里也会出现 error: 这样的字样",
            // 8 真实诊断：DXC 确实会给出没有位置的诊断（实测 missing entry point definition）
            "error: missing entry point definition",
        ];

        var text = string.Join('\n', lines);
        var parsed = DxcDiagnosticParser.Parse(text);

        Check.Equal(5, parsed.Count,
            "只应解析出 5 条真实诊断（回显行里的 error: 字样不得计入；修复前为 7 条）");
        Check.True(parsed.All(d => !d.Message.Contains("回显", StringComparison.Ordinal)),
            "回显的源码片段不得出现在任何诊断文案里：" + string.Join(" | ", parsed.Select(d => d.Message)));

        Check.Equal(chinesePath, parsed[0].File, "含空格/括号/中文的路径必须完整保留");
        Check.Equal(2, parsed[0].Line, "第 1 条应落在第 2 行");
        Check.Equal(36, parsed[0].Column, "第 1 条列号应为 36");
        Check.Equal(MicroShader.Domain.DiagnosticSeverity.Error, parsed[0].Severity, "第 1 条级别应为 Error");

        Check.Equal(5, parsed[1].Line, "warning 应落在第 5 行（回显行不得占位）");
        Check.Equal(3, parsed[1].Column, "warning 列号应为 3");
        Check.Equal(MicroShader.Domain.DiagnosticSeverity.Warning, parsed[1].Severity, "第 2 条级别应为 Warning");

        Check.Equal("My Shader.hlsl", parsed[2].File, "括号形态应解析出文件名");
        Check.Equal(7, parsed[2].Line, "括号形态应解析出行号");
        Check.Equal(5, parsed[2].Column, "括号形态应解析出列号");

        Check.Equal(9, parsed[3].Line, "文案含 error: 的诊断仍应落在第 9 行");
        Check.True(parsed[3].Message.Contains("文案里也会出现 error: 这样的字样", StringComparison.Ordinal),
            "文案里自带的 error: 字样必须整段保留，不能被当成分隔点：" + parsed[3].Message);

        Check.False(parsed[4].HasLocation, "无位置诊断应如实保持无位置（不得伪造出文件名）");
        Check.Equal("missing entry point definition", parsed[4].Message, "无位置诊断的文案应原样保留");

        TestCaseHelpers.Report(
            $"混合文本 {text.Split('\n').Length} 行 → 诊断 {parsed.Count} 条："
            + string.Join(" | ", parsed.Select(d => d.ToString())));
    }

    /// <summary>
    /// 回归：IDxcUtils 创建失败时的处理。真机无法让 DxcCreateInstance 只对 utils 失败，
    /// 因此用例直接打可测的失败分支（TryCreateInstances）与结果形态（InstanceUnavailable）。
    ///
    /// 修复前的错误结果：RentSlot 里 tryCreateCompiler 成功后 utils 失败会直接抛
    /// InvalidOperationException —— 已 AddRef 的 compiler 没人释放（每失败一次泄漏一个 COM 实例），
    /// 且整篇分析被异常打断（一个单元的失败让同一文件其他单元也拿不到诊断）。
    /// </summary>
    private static void SlotCreationFailure()
    {
        var released = new List<nint>();
        var created = NativeCompilerGateway.TryCreateInstances(
            (out nint instance) =>
            {
                instance = 0x1234;
                return true;
            },
            (out nint instance) =>
            {
                instance = 0;
                return false;
            },
            released.Add,
            out var compiler,
            out var utils,
            out var error);

        Check.False(created, "utils 创建失败时整体必须失败");
        Check.Equal<nint>(0, compiler, "失败时不得带出半成品 compiler 指针");
        Check.Equal<nint>(0, utils, "失败时 utils 指针必须为 0");
        Check.True(error.Contains("IDxcUtils", StringComparison.Ordinal), "失败原因必须点名 IDxcUtils：" + error);
        Check.Equal(1, released.Count, "已创建的 compiler 必须被释放恰好一次（修复前 0 次，COM 实例泄漏）");
        Check.Equal<nint>(0x1234, released[0], "释放的必须正是已经建好的那个 compiler 实例");

        // 反向门禁：两侧都成功时不得释放任何东西（否则会放出野指针）。
        var okReleased = new List<nint>();
        var ok = NativeCompilerGateway.TryCreateInstances(
            (out nint instance) =>
            {
                instance = 0x22;
                return true;
            },
            (out nint instance) =>
            {
                instance = 0x33;
                return true;
            },
            okReleased.Add,
            out var okCompiler,
            out var okUtils,
            out _);

        Check.True(ok, "两侧都成功时整体必须成功");
        Check.Equal<nint>(0x22, okCompiler, "应带出 compiler 指针");
        Check.Equal<nint>(0x33, okUtils, "应带出 utils 指针");
        Check.Equal(0, okReleased.Count, "成功路径不得释放任何实例");

        // 结果形态：必须是「调用未发生」，否则 0 条诊断会被当成干净结果写进缓存。
        var unavailable = NativeCompilerGateway.InstanceUnavailable("无法创建 IDxcUtils 实例", 4242);
        Check.True(unavailable.InvocationFailed, "实例创建失败必须带 InvocationFailed 标记（修复前是抛异常）");
        Check.True(unavailable.Failed, "必须计为编译失败");
        Check.Equal(0, unavailable.ObjectSize, "失败时不得声称产出了对象");
        Check.Equal(4242, unavailable.ElapsedMicroseconds, "耗时应透传");
        Check.True(unavailable.DiagnosticText.Contains("IDxcUtils", StringComparison.Ordinal),
            "说明文字必须带上失败原因：" + unavailable.DiagnosticText);
        Check.Equal(0, DxcDiagnosticParser.Parse(unavailable.DiagnosticText).Count,
            "这段说明文字不得被解析成诊断，否则会绕过 InvocationFailed 分支变成普通错误");

        TestCaseHelpers.Report(
            $"utils 失败 → released={released.Count}（0x{released[0]:X}），InvocationFailed={unavailable.InvocationFailed}");
    }

    /// <summary>
    /// 回归：空闲回收只留指定数量的热实例，并且「至少留一个」是硬约束。
    /// </summary>
    /// <remarks>
    /// 用例形状：先用并发编译把实例池撑大（每一路同时在跑的编译各自建一枚原生实例），
    /// 再回收，断言池里恰好剩 keepAtLeast 枚，且留下来的那枚仍然能编译。
    /// 「0 / 负数按 1 处理」单独断言：这条兜底一旦破，池会被清空，
    /// 下一次诊断就要重走一遍实例创建，表现为「空闲回来第一下特别慢」。
    /// 先编译一次再回收也是判据的一部分：留下的实例必须完好，
    /// 否则说明回收把还在池里的那枚也动了（释放后指针还挂着，复编译就是原生崩溃）。
    /// </remarks>
    private static void TrimIdleSlots()
    {
        using var gateway = Open(maxThreads: 4);
        string[] arguments = ["-T", "ps_6_0", "-E", "main"];
        var shader = BulkShader(400);

        var grown = GrowPool(gateway, shader, arguments, 4);
        Check.True(grown >= 2, "并发编译应让实例池长出多个槽，实际只有 " + grown + " 枚，本用例无法证明回收有效");

        var trimmed = gateway.TrimIdleSlots(1);
        Check.Equal(grown - 1, trimmed, "应释放到只剩 1 枚，实际释放 " + trimmed + " 枚");
        Check.Equal(1, gateway.LiveSlotCount, "回收后池里必须恰好剩 1 枚热实例");

        // keepAtLeast 传 0 或负数一律按 1 处理：只剩 1 枚时不得再动。
        Check.Equal(0, gateway.TrimIdleSlots(0), "keepAtLeast=0 时池里只剩 1 枚，不得再释放");
        Check.Equal(1, gateway.LiveSlotCount, "keepAtLeast=0 也必须留 1 枚（清空等于让下次诊断重新创建实例）");
        Check.Equal(0, gateway.TrimIdleSlots(-5), "keepAtLeast=-5 按 1 处理，同样不得再释放");
        Check.Equal(1, gateway.LiveSlotCount, "keepAtLeast=-5 也必须留 1 枚");

        // 留下的实例必须完好：这里再编译一次就是最直接的探针。
        var after = gateway.Compile("float4 main() : SV_Target { return float4(1, 0, 0, 1); }", arguments);
        Check.True(!after.Failed, "回收之后热实例必须仍可编译：" + after.DiagnosticText);

        TestCaseHelpers.Report(
            $"并发撑到 {grown} 枚 → 回收 {trimmed} 枚，剩 {gateway.LiveSlotCount} 枚；"
            + $"热实例复编译 {(after.Failed ? "失败" : "成功")}");
    }

    /// <summary>
    /// 回归：空闲回收绝不能碰正在被租借的实例。
    /// </summary>
    /// <remarks>
    /// 构造方法：池里先备两枚空闲槽，再让一条线程进入 Compile。
    /// Compile 一开始就把槽从池里取走（TryTake），所以「池里少一枚」就是「它确实在用」的确定证据 ——
    /// 不需要靠 sleep 猜时序。
    /// 这时回收 keepAtLeast=1：被借走的那枚不在池里，回收只可能动剩下那一枚空闲的。
    /// 判据有两条：（一）回收后仍至少有 1 枚空闲槽（等于「在用数 + keep」这个下限），
    /// （二）那条在飞编译必须完好 —— 若回收真的释放了在用的实例，
    /// DXC 会在已经归还的 COM 指针上调 vtable，轻则编译失败，重则原生访问冲突把进程带走。
    /// 那条线程只跑一次编译，因此回收之后它不会再从池里取槽，「至少 1 枚空闲」不会因它重新租借而抖动。
    /// </remarks>
    private static void TrimIdleWhileBorrowed()
    {
        using var gateway = Open(maxThreads: 4);
        string[] arguments = ["-T", "ps_6_0", "-E", "main"];
        var shader = BulkShader(1200);

        var warm = GrowPool(gateway, shader, arguments, 2);
        Check.True(warm >= 2, "需要至少两枚空闲槽才能构造「借出一枚、回收另一枚」，实际 " + warm + " 枚");

        DxcCompileResult? inUse = null;
        var worker = new Thread(() => inUse = gateway.Compile(shader, arguments));
        worker.Start();

        // 等「池里少一枚」：这一刻租借已经发生，编译还没结束。
        var observed = false;
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 5000 && worker.IsAlive)
        {
            if (gateway.LiveSlotCount < warm)
            {
                observed = true;
                break;
            }

            Thread.SpinWait(40);
        }

        var trimmed = gateway.TrimIdleSlots(1);
        var idleWhileBusy = gateway.LiveSlotCount;

        worker.Join();

        // 抓不到借出窗口说明这轮编译太快，构造不出竞态 —— 软跳过，而不是判失败（否则机器越快越容易红）。
        if (!observed)
        {
            throw new SkipException("本轮没抓到实例借出的时间窗（编译太快），跳过这条竞态用例");
        }
        Check.True(idleWhileBusy >= 1, "回收后至少留 1 枚空闲槽（在用数 + keep 的下限），实际 " + idleWhileBusy);
        Check.True(trimmed <= warm - 1, "回收数量不得超过空闲槽能给的部分，实际释放 " + trimmed + " 枚");
        Check.True(inUse.HasValue && !inUse.Value.Failed, "在飞编译不得因空闲回收失败：" + (inUse?.DiagnosticText ?? "结果为空"));
        Check.True(gateway.LiveSlotCount >= 1, "全部归还后池里必须仍有实例");

        TestCaseHelpers.Report(
            $"借出期间回收 {trimmed} 枚（池里留 {idleWhileBusy} 枚），在飞编译 {(inUse.HasValue && !inUse.Value.Failed ? "正常" : "异常")}，"
            + $"结束后池里 {gateway.LiveSlotCount} 枚");
    }

    /// <summary>
    /// 把实例池撑到至少 n 个空闲槽：n 条线程在 Barrier 后同时进 Compile，
    /// 同时抢不到槽就会各自新建一枚 —— 池里没有空闲槽时并发多少路就能长出多少枚。
    /// 单轮没长够就重试（线程调度可能让某一路来得太晚，直接复用了别人刚还回来的槽）。
    /// </summary>
    private static int GrowPool(NativeCompilerGateway gateway, string shader, string[] arguments, int n)
    {
        for (var attempt = 0; attempt < 4 && gateway.LiveSlotCount < n; attempt++)
        {
            using var barrier = new Barrier(n);
            var threads = new Thread[n];
            for (var i = 0; i < n; i++)
            {
                threads[i] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    gateway.Compile(shader, arguments);
                });
                threads[i].Start();
            }

            foreach (var thread in threads)
            {
                thread.Join();
            }
        }

        return gateway.LiveSlotCount;
    }

    /// <summary>
    /// 造一段「有点重」的 shader：编译耗时随函数数线性增长，用来撑开「实例正在被使用」的时间窗。
    /// 只做纯粹的算术函数，保证任何 DXC 版本都能编过。
    /// </summary>
    private static string BulkShader(int functions)
    {
        var sb = new StringBuilder(functions * 64);
        for (var i = 0; i < functions; i++)
        {
            sb.Append("float4 helper").Append(i).Append("(float4 x) { return x * 1.5 + ").Append(i).Append(".0; }\n");
        }

        sb.Append("float4 main() : SV_Target { float4 s = 1;\n");
        for (var i = 0; i < functions; i++)
        {
            sb.Append("s = helper").Append(i).Append("(s);\n");
        }

        sb.Append("return s; }\n");
        return sb.ToString();
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
