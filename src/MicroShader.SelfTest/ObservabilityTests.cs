using System.Runtime.CompilerServices;
using System.Text.Json;
using MicroShader.ObservabilityEngine;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 7（ObservabilityEngine：环形无锁日志与排障自诊断中枢）自检。
/// </summary>
/// <remarks>
/// 覆盖口径对应规格书模块 I 的 v2.0 修正清单：容量/字节预算闭合（第 1 条）、
/// seqlock + 覆盖最旧且生产者不自旋（第 2 条）、零托管堆分配（第 3、9 条）、
/// 入队即脱敏（第 7 条）、dump 原子落盘与磁盘满降级（第 8 条）、
/// 通道过滤与窗口限流（第 11 条）。
/// </remarks>
internal static class ObservabilityTests
{
    public static void Register()
    {
        TestSuite.Add("Log", "Layout", Layout);
        TestSuite.Add("Log", "WriteRead", WriteRead);
        TestSuite.Add("Log", "Truncate", Truncate);
        TestSuite.Add("Log", "Overwrite", Overwrite);
        TestSuite.Add("Log", "ZeroAlloc", ZeroAlloc);
        TestSuite.Add("Log", "Concurrent", Concurrent);
        TestSuite.Add("Log", "Interpolated", Interpolated);
        TestSuite.Add("Log", "SanitizeAtEnqueue", SanitizeAtEnqueue);

        TestSuite.Add("Dump", "Atomic", DumpAtomic);
        TestSuite.Add("Dump", "Escaping", DumpEscaping);
        TestSuite.Add("Dump", "DiskFullFallback", DumpDiskFullFallback);

        TestSuite.Add("Sink", "Throttle", SinkThrottle);
        TestSuite.Add("Sink", "Filter", SinkFilter);

        TestSuite.Add("AirGap", "NotInstalledInSelfTest", AirGapNotInstalled);
        TestSuite.Add("AirGap", "DumpRootShape", AirGapDumpRootShape);
    }

    // ── 布局与预算 ─────────────────────────────────────────────────────────────

    private static void Layout()
    {
        var ring = new LogRingBuffer();

        Check.Equal(512, LogRingBuffer.SlotBytes, "每槽必须是 512B");
        Check.Equal(1024, LogRingBuffer.DefaultCapacity, "默认容量必须是 1024");
        Check.Equal(524288, LogRingBuffer.DefaultTotalBytes, "总预算必须正好是 512KB");

        // 实测断言：元数据必须恰好占一个缓存行（v2.0 第 3 条）。
        Check.Equal(64, Unsafe.SizeOf<LogSlotMeta>(), "LogSlotMeta 必须恰好 64 字节（一个缓存行）");

        Check.Equal(524288L, ring.AllocatedBytes, "环形缓冲实际占用必须是 524,288 字节（512KB）");
        Check.Equal(224, LogRingBuffer.MaxMessageChars, "每槽字符容量");
    }

    // ── 基本读写 ───────────────────────────────────────────────────────────────

    private static void WriteRead()
    {
        var ring = new LogRingBuffer(16);
        ring.Write(LogLevel.Info, LogCategories.Vfs, "alpha");
        ring.Write(LogLevel.Warning, LogCategories.Compiler, "beta");
        ring.Write(LogLevel.Error, LogCategories.Transport, "gamma");

        Check.Equal(3L, ring.TotalWritten, "累计写入条数");

        Span<char> buffer = stackalloc char[LogRingBuffer.MaxMessageChars];

        Check.True(ring.TryRead(1, buffer, out var n1, out var l1, out var c1, out _), "读第 1 条");
        Check.Equal("alpha", buffer[..n1].ToString(), "第 1 条正文");
        Check.Equal(LogLevel.Info, l1, "第 1 条级别");
        Check.Equal(LogCategories.Vfs, c1, "第 1 条分类");

        Check.True(ring.TryRead(2, buffer, out var n2, out var l2, out _, out _), "读第 2 条");
        Check.Equal("beta", buffer[..n2].ToString(), "第 2 条正文");
        Check.Equal(LogLevel.Warning, l2, "第 2 条级别");

        Check.True(ring.TryRead(3, buffer, out var n3, out var l3, out _, out _), "读第 3 条");
        Check.Equal("gamma", buffer[..n3].ToString(), "第 3 条正文");
        Check.Equal(LogLevel.Error, l3, "第 3 条级别");

        Check.True(!ring.TryRead(4, buffer, out _, out _, out _, out _), "越界序号必须读不到");
        Check.True(!ring.TryRead(0, buffer, out _, out _, out _, out _), "序号 0 是无记录（seqlock 的未写过标志）");

        List<LogRecord> records = [];
        var count = ring.Snapshot(records);
        Check.Equal(3, count, "Snapshot 条数");
        Check.SequenceEqual(["gamma", "beta", "alpha"], records.Select(r => r.Message), "Snapshot 必须按最新到最旧");
    }

    private static void Truncate()
    {
        var ring = new LogRingBuffer(4);
        var overlong = new string('x', LogRingBuffer.MaxMessageChars + 50);
        ring.Write(LogLevel.Info, LogCategories.Test, overlong);

        Span<char> buffer = stackalloc char[LogRingBuffer.MaxMessageChars];
        Check.True(ring.TryRead(1, buffer, out var n, out _, out _, out _), "读被截断的记录");
        Check.Equal(LogRingBuffer.MaxMessageChars, n, "超长正文必须截断到槽容量");
    }

    private static void Overwrite()
    {
        var ring = new LogRingBuffer(8);

        // 写 20 条、容量 8 → 只有最后 8 条应当可读。
        // 注意 stackalloc 必须留在循环外（CA2014：循环内 stackalloc 会累积栈帧）。
        Span<char> text = stackalloc char[8];
        for (var i = 1; i <= 20; i++)
        {
            var written = 0;
            if (!i.TryFormat(text, out written, default, null)) written = 0;
            ring.Write(LogLevel.Info, LogCategories.Test, text[..written]);
        }

        Check.Equal(20L, ring.TotalWritten, "累计条数不受覆盖影响");
        Check.Equal(8, ring.CountReadable(), "可读条数必须等于容量（覆盖最旧）");

        Span<char> buffer = stackalloc char[LogRingBuffer.MaxMessageChars];
        Check.True(!ring.TryRead(12, buffer, out _, out _, out _, out _), "第 12 条必须已被覆盖");
        Check.True(ring.TryRead(13, buffer, out var n13, out _, out _, out _), "第 13 条是存活窗口里最旧的一条");
        Check.Equal("13", buffer[..n13].ToString(), "覆盖边界处的正文");
        Check.True(ring.TryRead(20, buffer, out var n20, out _, out _, out _), "第 20 条（最新）");
        Check.Equal("20", buffer[..n20].ToString(), "最新一条的正文");
    }

    // ── 零分配 ─────────────────────────────────────────────────────────────────

    private static void ZeroAlloc()
    {
        var ring = new LogRingBuffer(1024, Log.BuildDefaultSanitizer());
        const string payload = "resolved Packages/com.unity.render-pipelines.universal/Shaders/Lit.shader ok";

        // 预热：填满线程静态缓冲、触发 JIT。
        for (var i = 0; i < 2048; i++) ring.Write(LogLevel.Debug, LogCategories.Vfs, payload);

        var before = GC.GetAllocatedBytesForCurrentThread();
        const int iterations = 20000;
        for (var i = 0; i < iterations; i++)
        {
            ring.Write(LogLevel.Debug, LogCategories.Vfs, payload);
        }

        var delta = GC.GetAllocatedBytesForCurrentThread() - before;
        Check.Equal(0L, delta, iterations + " 次热路径写入必须零托管堆分配（NFR-4）");

        // 插值路径同样必须零分配。
        Log.Reset(new LogRingBuffer(1024));
        try
        {
            for (var i = 0; i < 512; i++) Log.Info(LogCategories.Test, $"warm {i} {i * 1.5:F3}");

            var before2 = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                Log.Info(LogCategories.Test, $"item={i} ratio={i * 1.5:F3} name={"abc"}");
            }

            var delta2 = GC.GetAllocatedBytesForCurrentThread() - before2;
            Check.Equal(0L, delta2, iterations + " 次插值写入必须零托管堆分配");
        }
        finally
        {
            Log.Reset();
        }
    }

    // ── 并发 ───────────────────────────────────────────────────────────────────

    private static void Concurrent()
    {
        const int threads = 4;
        const int perThread = 20000;
        var ring = new LogRingBuffer(1024);
        using var start = new ManualResetEventSlim(false);
        var workers = new Thread[threads];
        var failures = 0;

        for (var t = 0; t < threads; t++)
        {
            var id = t;
            workers[t] = new Thread(() =>
            {
                Span<char> text = stackalloc char[LogRingBuffer.MaxMessageChars];
                var prefix = "worker-" + id.ToString() + "-";
                start.Wait();
                for (var i = 0; i < perThread; i++)
                {
                    prefix.AsSpan().CopyTo(text);
                    var written = prefix.Length;
                    if (!i.TryFormat(text[written..], out var digits, default, null)) digits = 0;
                    written += digits;
                    ring.Write(LogLevel.Info, LogCategories.Test, text[..written]);
                }
            })
            { IsBackground = true };
            workers[t].Start();
        }

        start.Set();
        foreach (var worker in workers) worker.Join();

        Check.Equal((long)threads * perThread, ring.TotalWritten, "4 线程各自领取的序号总数");

        List<LogRecord> records = [];
        ring.Snapshot(records);

        Check.True(records.Count > 0, "并发后必须还能读到记录");
        Check.True(
            records.Count >= 1024 * 9 / 10,
            "可读条数 " + records.Count + " 不应远低于容量 1024（撕裂丢读应极少）");

        // 撕裂检测：每条记录都必须是完整形态 "worker-<id>-<n>"，不允许半截文本。
        foreach (var record in records)
        {
            var parts = record.Message.Split('-');
            if (parts.Length != 3
                || !string.Equals(parts[0], "worker", StringComparison.Ordinal)
                || !int.TryParse(parts[1], out var workerId)
                || !int.TryParse(parts[2], out var index))
            {
                failures++;
                continue;
            }

            if (workerId is < 0 or >= threads || index is < 0 or >= perThread) failures++;
        }

        Check.Equal(0, failures, "并发下不得读到撕裂或越界的记录（seqlock 保护）");

        for (var i = 1; i < records.Count; i++)
        {
            Check.True(
                records[i].GlobalSequence < records[i - 1].GlobalSequence,
                "Snapshot 必须严格按序号递减");
        }
    }

    // ── 门面与插值 ─────────────────────────────────────────────────────────────

    private static void Interpolated()
    {
        Log.Reset(new LogRingBuffer(64));
        try
        {
            Log.Info(LogCategories.Compiler, $"compiled {7} units in {1.25:F2} ms, entry={"main"}");
            Log.Warn(LogCategories.Vfs, "plain span path");
            Log.Error(LogCategories.Transport, $"pid={4242}");
            Log.Info(LogCategories.Test, $"[{42,6}]|{"ab",-4}|");

            List<LogRecord> records = [];
            Log.Ring.Snapshot(records);
            Check.Equal(4, records.Count, "门面写入条数");

            Check.Equal(
                "[    42]|ab  |",
                records[0].Message,
                "对齐说明符（正数右对齐 / 负数左对齐）");
            Check.Equal("pid=4242", records[1].Message, "单参数插值");
            Check.Equal("plain span path", records[2].Message, "ReadOnlySpan 重载不走插值处理器");
            Check.Equal(
                "compiled 7 units in 1.25 ms, entry=main",
                records[3].Message,
                "插值文本必须与 string.Format 口径一致（含格式说明符）");

            // MinimumLevel / Enabled 必须真的挡在热路径入口。
            Log.MinimumLevel = LogLevel.Error;
            Log.Info(LogCategories.Test, "filtered-out");
            Log.MinimumLevel = LogLevel.Trace;

            records.Clear();
            Log.Ring.Snapshot(records);
            Check.Equal(4, records.Count, "低于 MinimumLevel 的记录不得进环");
        }
        finally
        {
            Log.Reset();
        }
    }

    private static void SanitizeAtEnqueue()
    {
        const string root = @"C:\Users\somebody\SecretProject";
        var ring = new LogRingBuffer(8, new LogSanitizer([root]));

        ring.Write(LogLevel.Info, LogCategories.Vfs, root + @"\Assets\A.shader");
        ring.Write(LogLevel.Info, LogCategories.Vfs, "C:/Users/somebody/SecretProject/Assets/B.shader");
        ring.Write(LogLevel.Info, LogCategories.Vfs, "unrelated text");

        Span<char> buffer = stackalloc char[LogRingBuffer.MaxMessageChars];

        Check.True(ring.TryRead(1, buffer, out var n1, out _, out _, out _), "读第 1 条");
        Check.Equal(@"~/Assets\A.shader", buffer[..n1].ToString(), "反斜杠路径必须被替换（入队即脱敏）");

        Check.True(ring.TryRead(2, buffer, out var n2, out _, out _, out _), "读第 2 条");
        Check.Equal("~/Assets/B.shader", buffer[..n2].ToString(), "正斜杠路径必须被同样识别");

        Check.True(ring.TryRead(3, buffer, out var n3, out _, out _, out _), "读第 3 条");
        Check.Equal("unrelated text", buffer[..n3].ToString(), "不含敏感根的正文原样保留");

        // 更长的根优先命中：否则会切出半截路径。
        var layered = new LogRingBuffer(4, new LogSanitizer([@"C:\Users\somebody", root]));
        layered.Write(LogLevel.Info, LogCategories.Test, root + @"\x");
        Check.True(layered.TryRead(1, buffer, out var n4, out _, out _, out _), "读分层脱敏结果");
        Check.Equal("~/x", buffer[..n4].ToString(), "长根必须先于短根命中，且只剩一个占位符");

        Check.True(Log.BuildDefaultSanitizer().HasRoots, "默认脱敏器应当至少包含用户主目录或当前目录");
    }

    // ── 转储 ───────────────────────────────────────────────────────────────────

    private static void DumpAtomic()
    {
        var directory = Path.Combine(Path.GetTempPath(), "microshader_dump_" + Guid.NewGuid().ToString("N"));
        var originalRoot = CrashFlightRecorder.DumpRoot;

        try
        {
            CrashFlightRecorder.DumpRoot = directory;
            Log.Reset(new LogRingBuffer(64));

            Log.Error(LogCategories.Compiler, "boom: 0xC0000005");
            Log.Info(LogCategories.Vfs, "resolved Lit.shader");

            CrashFlightRecorder.RegisterContributor(new FakeContributor());

            var path = CrashFlightRecorder.Dump("selftest");
            Check.NotNull(path, "dump 必须成功返回路径");
            Check.True(File.Exists(path!), "dump 文件必须存在");
            Check.True(path!.StartsWith(directory, StringComparison.Ordinal), "dump 必须落在配置的目录");
            Check.Equal(0, Directory.GetFiles(directory, "*.tmp").Length, "原子落盘不得残留 .tmp");

            var json = File.ReadAllText(path);

            // 用独立的 JSON 读取器校验，而不是自己写一遍断言 —— 转义正确性的最强证据。
            using var document = JsonDocument.Parse(json);
            var rootElement = document.RootElement;

            Check.Equal(1, rootElement.GetProperty("schema").GetInt32(), "schema 版本");
            Check.Equal("selftest", rootElement.GetProperty("reason").GetString(), "dump 原因");
            Check.Equal(64, rootElement.GetProperty("capacity").GetInt32(), "capacity 字段（本用例用了 64 槽的隔离缓冲）");

            var logs = rootElement.GetProperty("logs");
            Check.Equal(2, logs.GetArrayLength(), "环形缓冲里的两条日志都必须落盘");

            var messages = new List<string>();
            foreach (var entry in logs.EnumerateArray()) messages.Add(entry.GetProperty("message").GetString()!);

            Check.Contains(messages, "boom: 0xC0000005", "Error 记录必须原样落盘");
            Check.Contains(messages, "resolved Lit.shader", "Info 记录必须原样落盘");

            var contributor = rootElement.GetProperty("contributors").GetProperty("Fake");
            Check.Equal(42, contributor.GetProperty("answer").GetInt32(), "贡献者数值字段");
            Check.Equal("contributor payload", contributor.GetProperty("note").GetString(), "贡献者字符串字段");

            Check.True(CrashFlightRecorder.LastFailure is null, "成功后 LastFailure 必须清空");
        }
        finally
        {
            CrashFlightRecorder.DumpRoot = originalRoot;
            Log.Reset();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void DumpEscaping()
    {
        var directory = Path.Combine(Path.GetTempPath(), "microshader_dump_" + Guid.NewGuid().ToString("N"));
        var originalRoot = CrashFlightRecorder.DumpRoot;

        // 把所有会破坏 JSON 的字符都塞进一条日志，再做一次真正的往返解析。
        var tricky = "quote=\" back=\\ newline=\n tab=\t ctrl=\u0001 slash=/ unicode=\u4e2d\u6587 emoji=\U0001F600";

        try
        {
            CrashFlightRecorder.DumpRoot = directory;
            Log.Reset(new LogRingBuffer(16));
            Log.Error(LogCategories.Test, tricky);

            var path = CrashFlightRecorder.Dump("escaping");
            Check.NotNull(path, "转义用例必须落盘成功");

            var json = File.ReadAllText(path!);
            Check.True(!json.Contains('\u0001'), "控制字符绝不允许原样落盘");

            using var document = JsonDocument.Parse(json);
            var logs = document.RootElement.GetProperty("logs");
            Check.Equal(1, logs.GetArrayLength(), "一条记录");

            Check.Equal(
                tricky,
                logs[0].GetProperty("message").GetString(),
                "转义后的 JSON 必须能无损还原原始文本（含引号/反斜杠/换行/制表/控制字符/emoji）");
        }
        finally
        {
            CrashFlightRecorder.DumpRoot = originalRoot;
            Log.Reset();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static void DumpDiskFullFallback()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "microshader_blocker_" + Guid.NewGuid().ToString("N"));
        var originalRoot = CrashFlightRecorder.DumpRoot;
        var originalFallback = CrashFlightRecorder.CriticalFallback;
        var notified = new List<string>();

        try
        {
            // 用一个「文件」当 dump 目录：Directory.CreateDirectory 必然失败，等价模拟磁盘不可写。
            File.WriteAllText(blocker, "not a directory");
            CrashFlightRecorder.DumpRoot = blocker;
            CrashFlightRecorder.CriticalFallback = notified.Add;
            Log.Reset(new LogRingBuffer(16));
            Log.Error(LogCategories.Test, "critical failure");

            var path = CrashFlightRecorder.Dump("diskfull");

            Check.True(path is null, "落盘失败必须返回 null，而不是抛异常");
            Check.NotNull(CrashFlightRecorder.LastFailure, "必须记录失败原因");
            Check.Equal(1, notified.Count, "必须触发一次降级通道（window/showMessage）");
            Check.True(
                notified[0].Contains("critical failure", StringComparison.Ordinal),
                "降级通知必须携带内存黑匣子里的关键错误");
            Check.True(notified[0].Length < 4096, "降级通知必须截断，避免超长弹窗");
        }
        finally
        {
            CrashFlightRecorder.DumpRoot = originalRoot;
            CrashFlightRecorder.CriticalFallback = originalFallback;
            Log.Reset();
            if (File.Exists(blocker)) File.Delete(blocker);
        }
    }

    // ── 出站限流 ───────────────────────────────────────────────────────────────

    private static void SinkThrottle()
    {
        var inner = new CollectingSink();
        var sink = new LspChannelThrottledSink(inner, windowLimit: 20, window: TimeSpan.FromMinutes(5));

        for (var i = 0; i < 100; i++) sink.Emit(LogLevel.Info, LogCategories.Test, "info");

        Check.Equal(20, inner.Messages.Count, "窗口内只放行 20 条");
        Check.Equal(80, sink.SuppressedCount, "其余 80 条被聚合");

        Check.True(sink.Pump(), "Pump 必须吐出一条聚合消息");
        Check.Equal(21, inner.Messages.Count, "聚合消息计入出站");
        Check.True(inner.Messages[^1].Message.Contains("80 条", StringComparison.Ordinal), "聚合文案必须报出被吞条数");
        Check.Equal(0, sink.SuppressedCount, "Pump 之后计数归零");
        Check.True(!sink.Pump(), "无积压时 Pump 不得再吐");

        sink.Emit(LogLevel.Warning, LogCategories.Test, "after pump");
        Check.Equal(22, inner.Messages.Count, "Pump 重置窗口后应恢复放行");
    }

    private static void SinkFilter()
    {
        var inner = new CollectingSink();
        var sink = new LspChannelThrottledSink(inner, windowLimit: 20, window: TimeSpan.FromMinutes(5));

        sink.Emit(LogLevel.Trace, LogCategories.Test, "trace-1");
        sink.Emit(LogLevel.Debug, LogCategories.Test, "debug-1");
        sink.Emit(LogLevel.Info, LogCategories.Test, "info-1");
        sink.Emit(LogLevel.Warning, LogCategories.Test, "warn-1");
        sink.Emit(LogLevel.Error, LogCategories.Test, "error-1");

        Check.Equal(3, inner.Messages.Count, "window/logMessage 只放行 Info/Warning/Error");
        Check.Equal("info-1", inner.Messages[0].Message, "放行顺序");
        Check.Equal("error-1", inner.Messages[^1].Message, "放行顺序");
        Check.Equal(1, inner.Traces.Count, "Trace 必须走 $/logTrace 通道");
        Check.Equal("trace-1", inner.Traces[0].Message, "Trace 内容");

        // 内层不支持 $/logTrace 时，Trace 必须被彻底丢弃，而不是退回 window/logMessage。
        var plain = new PlainSink();
        var sink2 = new LspChannelThrottledSink(plain, windowLimit: 20, window: TimeSpan.FromMinutes(5));
        sink2.Emit(LogLevel.Trace, LogCategories.Test, "trace-dropped");
        sink2.Emit(LogLevel.Info, LogCategories.Test, "info-kept");
        Check.Equal(1, plain.Count, "不支持 $/logTrace 的内层不得收到 Trace");
    }

    // ── 标准 I/O 屏障 ──────────────────────────────────────────────────────────

    private static void AirGapNotInstalled()
    {
        // 自检 CLI 绝不能安装屏障，否则测试输出会被自己掐掉。
        // 这条断言同时是一份可执行的纪律：谁在 Program.cs 里加了 Install，这里立刻变红。
        Check.True(!StdioAirGapGuard.Installed, "自检 CLI 不得安装 StdioAirGapGuard（会掐掉测试输出）");
        Check.True(StdioAirGapGuard.CapturedStdout is null, "未安装时不得捕获 stdout");
        Check.True(StdioAirGapGuard.StderrRedirectPath is null, "未安装时不得重定向 stderr");
    }

    private static void AirGapDumpRootShape()
    {
        var root = CrashFlightRecorder.ResolveDumpRoot();
        Check.True(root.Contains("MicroShader", StringComparison.Ordinal), "dump 根必须带 MicroShader 目录名");
        Check.True(root.Contains("dumps", StringComparison.Ordinal), "dump 根必须是 dumps 目录（不是 Temp）");
        Check.Equal(root, CrashFlightRecorder.DumpRoot, "默认 DumpRoot 必须等于解析结果");
    }

    // ── 测试替身 ───────────────────────────────────────────────────────────────

    private sealed class CollectingSink : ILogSink, ILogTraceSink
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public List<(int Category, string Message)> Traces { get; } = [];

        public void Emit(LogLevel level, int category, ReadOnlySpan<char> message)
            => Messages.Add((level, message.ToString()));

        public void EmitTrace(int category, ReadOnlySpan<char> message)
            => Traces.Add((category, message.ToString()));
    }

    private sealed class PlainSink : ILogSink
    {
        public int Count { get; private set; }

        public void Emit(LogLevel level, int category, ReadOnlySpan<char> message) => Count++;
    }

    private sealed class FakeContributor : IDumpContributor
    {
        public string Name => "Fake";

        public void Contribute(DumpJsonWriter writer)
        {
            writer.Number("answer", 42);
            writer.String("note", "contributor payload");
        }
    }
}
