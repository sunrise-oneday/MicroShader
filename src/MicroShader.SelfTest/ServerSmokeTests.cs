using System.Diagnostics;
using System.Text;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 12 的端到端交付门禁：直接驱动"已发布的 Native AOT 原生 exe"跑一遍完整 LSP 会话。
/// </summary>
/// <remarks>
/// "为什么这条用例不可替代"：其余 136 条用例都在 "JIT" 下把模块当"库"调用。
/// 它们覆盖不到三件只有交付形态才会暴露的事：
/// <list type="number">
/// <item>"stdio 屏障是否真的生效" —— stdout 上是否出现了任何非 LSP 帧的字节；</item>
/// <item>"NativeAOT 下的原生互操作是否可用" —— 自研 COM vtable 垫片 + 绝对路径
///   "LoadLibraryW" 在 ILC 产出的机器码里是否还能拿到 "DxcCreateInstance"；</item>
/// <item>"退出码语义" —— 收到过 "shutdown" 才能返回 0（详设 v2.0 第 5 条）。</item>
/// </list>
/// 产物不存在时本用例 <see cref="SkipException"/>（JIT 开发流不该被它拦住）；
/// 交付流水线用 "--filter Server" 单独跑它并把它当作熔断门禁。
/// </remarks>
internal static class ServerSmokeTests
{
    private const string Suite = "Server";
    private const string ExeName = "UnityShaderLsp.exe";

    public static void Register()
    {
        TestSuite.Add(Suite, "AotSmoke", AotRoundTrip);
        TestSuite.Add(Suite, "Arguments", ArgumentHandling);
        TestSuite.Add(Suite, "ParentPidWatchdog", ParentPidWatchdog);
    }

    /// <summary>定位已发布的 AOT exe（发布脚本的默认输出目录）。</summary>
    private static string? TryFindPublishedExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MicroShader.sln")))
            {
                var candidate = Path.Combine(dir.FullName, "dist", "stg", "bin", ExeName);
                return File.Exists(candidate) ? candidate : null;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // ── 1. 参数处理（不需要发布产物也能跑，用 JIT 形态的 exe）────────────────

    private static void ArgumentHandling()
    {
        var exe = TryFindPublishedExe() ?? TryFindJitExe()
            ?? throw new SkipException("既没有发布产物也没有 JIT 产物可测");

        // --version 必须真的加载 DXC（详设 v2.0 第 6 条），而不是只证明 .NET 能启动。
        var (code, stdout, _) = Run(exe, ["--version"], TimeSpan.FromSeconds(60));
        Check.Equal(0, code, "--version 必须返回 0\n" + stdout);
        Check.True(stdout.Contains("DXC:", StringComparison.Ordinal), "--version 必须打印 DXC 版本行\n" + stdout);
        Check.True(stdout.Contains("UnityShaderLsp", StringComparison.Ordinal), "--version 必须打印服务名");

        // --help 走 stdout（诊断模式），退出码 0。
        var (helpCode, helpOut, _) = Run(exe, ["--help"], TimeSpan.FromSeconds(30));
        Check.Equal(0, helpCode, "--help 必须返回 0");
        Check.True(helpOut.Contains("--project", StringComparison.Ordinal), "--help 必须列出参数");

        // 未知参数 → 参数错误码 2（绝不能带着半截配置去跑服务）。
        var (badCode, _, badErr) = Run(exe, ["--nonsense"], TimeSpan.FromSeconds(30));
        Check.Equal(2, badCode, "未知参数必须返回参数错误码 2\n" + badErr);
    }

    // ── 2. AOT 原生 exe 的完整 LSP 回环 ──────────────────────────────────────

    private static void AotRoundTrip()
    {
        var publishedExe = TryFindPublishedExe();
        var exe = publishedExe ?? TryFindJitExe()
            ?? throw new SkipException("既没有发布产物也没有 JIT 产物可测");

        // 发布形态必须自证「原生依赖与 exe 同目录」；JIT 形态不做这条断言（它靠仓库探针目录）。
        if (publishedExe is not null)
        {
            var dxcBeside = File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "dxcompiler.dll"));
            Check.True(dxcBeside, "交付目录必须与 dxcompiler.dll 同目录（否则就是靠系统搜版本）");
        }

        var shaderPath = Corpus.PathOf("BrokenUrp.shader");
        var shaderText = File.ReadAllText(shaderPath);
        var uri = FileUri.FromPath(shaderPath);

        var projectRoot = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT");
        var args = new List<string>();
        if (!string.IsNullOrWhiteSpace(projectRoot) && Directory.Exists(projectRoot))
        {
            args.Add("--project");
            args.Add(projectRoot);
        }

        using var server = SpawnServer(exe, args);

        // ① 握手
        server.Send(Frame("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"processId":1,"rootUri":null,"capabilities":{}}}"""));
        // 注意：会话在握手前就可能先推 window/logMessage（日志出站是独立旁路），
        // 因此必须按 id 过滤到真正的响应帧，不能假设「下一帧就是 initialize 的 result」。
        var init = server.ReadUntil(static f => f.Contains("\"id\":1", StringComparison.Ordinal), TimeSpan.FromSeconds(60));
        Check.True(init.Contains("\"result\"", StringComparison.Ordinal), "initialize 必须有 result\n" + init);
        Check.True(init.Contains("serverInfo", StringComparison.Ordinal), "initialize 结果必须带 serverInfo\n" + init);

        server.Send(Frame("""{"jsonrpc":"2.0","method":"initialized","params":{}}"""));

        // ② 打开语料：必须收到 publishDiagnostics，且含模块 9 的标签诊断
        var didOpen = "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{"
            + "\"uri\":" + JsonQuote(uri) + ",\"languageId\":\"shaderlab\",\"version\":1,\"text\":" + JsonQuote(shaderText) + "}}}";
        server.Send(Frame(didOpen));

        var published = server.ReadUntil(
            static frame => frame.Contains("textDocument/publishDiagnostics", StringComparison.Ordinal),
            TimeSpan.FromSeconds(180));

        Check.True(published.Length > 0, "didOpen 后必须收到 publishDiagnostics");
        Check.True(published.Contains("SL0101", StringComparison.Ordinal), "必须在原生 exe 里报出 SL0101（管线标记拼写）\n" + published);
        Check.True(published.Contains("SL0103", StringComparison.Ordinal), "必须在原生 exe 里报出 SL0103（LightMode 拼写）\n" + published);
        Check.True(published.Contains("MS0001", StringComparison.Ordinal), "必须报出 MS0001（include 死链）");

        // ③ 关闭握手：收到过 shutdown 才允许返回 0
        server.Send(Frame("""{"jsonrpc":"2.0","id":2,"method":"shutdown"}"""));
        var shutdownResult = server.ReadUntil(static f => f.Contains("\"id\":2", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        Check.True(shutdownResult.Length > 0, "shutdown 必须有响应");

        server.Send(Frame("""{"jsonrpc":"2.0","method":"exit"}"""));

        var exitCode = server.WaitForExit(TimeSpan.FromSeconds(30));
        Check.Equal(0, exitCode, "收到 shutdown 后正常退出必须返回 0");

        // ④ 金标准：stdout 上除了合法 LSP 帧，一个杂散字节都不许有（stdio 屏障的实证）
        Check.True(server.StrayBytes == 0,
            $"stdout 上出现了 {server.StrayBytes} 个不属于任何 LSP 帧的字节 —— stdio 屏障失守");

        Check.True(server.FrameCount >= 3, $"至少应有 initialize / publishDiagnostics / shutdown 三个帧，实际 {server.FrameCount}");

        TestCaseHelpers.Report(
            $"{(publishedExe is null ? "JIT" : "AOT")} exe 回环：帧数 {server.FrameCount}，杂散字节 {server.StrayBytes}，"
            + $"诊断含 SL0101+SL0103+MS0001，退出码 {exitCode}");
    }

    // ── 进程与分帧 ──────────────────────────────────────────────────────────

    private sealed class ServerProcess : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _stdin;
        private readonly Stream _stdout;
        private readonly MemoryStream _buffer = new();
        private readonly StringBuilder _all = new();

        public ServerProcess(Process process)
        {
            _process = process;
            _stdin = process.StandardInput.BaseStream;
            _stdout = process.StandardOutput.BaseStream;
        }

        public int FrameCount { get; private set; }

        public long StrayBytes { get; private set; }

        public void Send(byte[] frame)
        {
            _stdin.Write(frame);
            _stdin.Flush();
        }

        /// <summary>
        /// 读一个 Content-Length 帧。"任何"落在帧边界之外的字节都会被记成 <see cref="StrayBytes"/> ——
        /// 这正是「stdout 只承载协议」这条硬约束的检测手段。
        /// </summary>
        public string ReadFrame(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                var parsed = TryParseFrame(out var payload, out var stray);
                if (parsed)
                {
                    FrameCount++;
                    StrayBytes += stray;
                    return payload;
                }

                StrayBytes += stray;
                if (DateTime.UtcNow > deadline)
                {
                    return string.Empty;
                }

                var chunk = new byte[8192];
                var read = ReadWithTimeout(chunk, deadline);
                if (read <= 0)
                {
                    return string.Empty;
                }

                _buffer.Write(chunk, 0, read);
            }
        }

        public string ReadUntil(Func<string, bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var frame = ReadFrame(deadline - DateTime.UtcNow);
                if (frame.Length == 0)
                {
                    return string.Empty;
                }

                if (predicate(frame))
                {
                    return frame;
                }
            }

            return string.Empty;
        }

        public int WaitForExit(TimeSpan timeout)
        {
            if (!_process.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { _process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw new AssertionException("服务进程未在超时内退出");
            }

            return _process.ExitCode;
        }

        public string Diagnostics => _all.ToString();

        public void Dispose()
        {
            try { _stdin.Dispose(); } catch (IOException) { }
            try { _stdout.Dispose(); } catch (IOException) { }
            _process.Dispose();
            _buffer.Dispose();
        }

        private int ReadWithTimeout(byte[] chunk, DateTime deadline)
        {
            var task = _stdout.ReadAsync(chunk, 0, chunk.Length);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return 0;
            }

            if (!task.Wait(remaining))
            {
                return 0;
            }

            return task.Result;
        }

        private bool TryParseFrame(out string payload, out long stray)
        {
            payload = string.Empty;
            stray = 0;

            var data = _buffer.GetBuffer();
            var length = (int)_buffer.Length;

            var headerEnd = IndexOf(data, length, "\r\n\r\n"u8);
            if (headerEnd < 0)
            {
                return false;
            }

            var header = Encoding.ASCII.GetString(data, 0, headerEnd);
            var contentLength = -1;
            foreach (var line in header.Split("\r\n"))
            {
                const string prefix = "Content-Length:";
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _ = int.TryParse(line.AsSpan(prefix.Length).Trim(), out contentLength);
                }
            }

            if (contentLength < 0)
            {
                // 不是合法帧头：整段算杂散字节。
                stray = headerEnd + 4;
                Consume((int)stray);
                return false;
            }

            if (length < headerEnd + 4 + contentLength)
            {
                return false;
            }

            payload = Encoding.UTF8.GetString(data, headerEnd + 4, contentLength);
            _all.Append(payload).Append('\n');
            Consume(headerEnd + 4 + contentLength);
            return true;
        }

        private void Consume(int count)
        {
            var data = _buffer.GetBuffer();
            var remaining = (int)_buffer.Length - count;
            Buffer.BlockCopy(data, count, data, 0, remaining);
            _buffer.SetLength(remaining);
        }

        private static int IndexOf(byte[] haystack, int length, ReadOnlySpan<byte> needle)
        {
            for (var i = 0; i + needle.Length <= length; i++)
            {
                if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                {
                    return i;
                }
            }

            return -1;
        }
    }

    private static ServerProcess SpawnServer(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        var process = Process.Start(info) ?? throw new AssertionException("无法启动服务进程：" + exe);
        return new ServerProcess(process);
    }

    private static byte[] Frame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var header = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + "\r\n\r\n");
        var frame = new byte[header.Length + body.Length];
        header.CopyTo(frame, 0);
        body.CopyTo(frame, header.Length);
        return frame;
    }

    private static string JsonQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 16);
        builder.Append('"');
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (ch < ' ') builder.Append("\\u").Append(((int)ch).ToString("x4"));
                    else builder.Append(ch);
                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static (int Code, string Stdout, string Stderr) Run(string exe, string[] args, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info) ?? throw new AssertionException("无法启动：" + exe);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new AssertionException("进程未在超时内退出：" + exe);
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }

    /// <summary>JIT 形态的 exe（dotnet build 的输出），用于不需要原生形态的参数用例。</summary>
    // ── 防孤儿轮询（详设 v2.0 第 1b 条）：客户端强杀后子进程必须自行退出 ──

    /// <summary>
    /// 用两个真实子进程验证父进程 PID 轮询：死父 PID 必须在轮询周期内自行退出，
    /// 活父 PID 不得被误杀。两个方向都验证，防止实现退化成「一律退出」或「一律不退」。
    /// </summary>
    /// <remarks>
    /// stdin 保持打开且不写入：排除 EOF 退出的干扰，让退出只能来自父 PID 轮询。
    /// stdin EOF 自杀（第 1a 条）由会话主循环的 EndOfStream 分支覆盖，两者互补。
    /// </remarks>
    private static void ParentPidWatchdog()
    {
        // JIT 形态即可：轮询逻辑与发布形态完全一致，不必依赖交付产物
        var exe = TryFindJitExe();
        if (exe is null)
        {
            throw new SkipException("找不到 JIT 形态的 UnityShaderLsp.exe，请先构建 MicroShader.Server");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        // DXC 定位口径与 AotSmoke 一致：现场包采集需要工程环境变量
        var unityProject = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT");
        if (!string.IsNullOrEmpty(unityProject))
        {
            psi.EnvironmentVariables["MICROSHADER_UNITY_PROJECT"] = unityProject;
        }

        // ① 死父 PID：首轮轮询（约 5 秒）后必须自行退出
        psi.EnvironmentVariables["MICROSHADER_PARENT_PID"] = "99999999";
        using var deadParent = Process.Start(psi)!;
        var stderrTail = string.Empty;
        deadParent.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrTail += e.Data + "\n"; };
        deadParent.BeginErrorReadLine();

        if (!deadParent.WaitForExit(30_000))
        {
            deadParent.Kill();
            throw new AssertionException("死父 PID 30 秒内未退出，防孤儿轮询未生效。stderr 尾部: "
                + stderrTail[^Math.Min(600, stderrTail.Length)..]);
        }

        // ② 活父 PID（本测试进程）：短时间内不得被误杀
        psi.EnvironmentVariables["MICROSHADER_PARENT_PID"] = Environment.ProcessId.ToString();
        using var liveParent = Process.Start(psi)!;

        Thread.Sleep(8_000);
        if (liveParent.HasExited)
        {
            throw new AssertionException("活父 PID 8 秒内被误杀，轮询的进程名校验有缺陷");
        }

        liveParent.Kill();
        liveParent.WaitForExit(5_000);

        TestCaseHelpers.Report("父 PID 轮询：死父按期退出，活父存活，两方向均符合预期");
    }

    private static string? TryFindJitExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MicroShader.sln")))
            {
                var candidate = Path.Combine(dir.FullName, "src", "MicroShader.Server", "bin", "Release", "net10.0", ExeName);
                return File.Exists(candidate) ? candidate : null;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
