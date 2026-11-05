using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 定容无锁环形日志缓冲（MPSC：多生产者写，单消费者读）。
/// 语义是"黑匣子"：写满后自然覆盖最旧记录，512KB 硬上限，"绝不扩容、生产者绝不自旋"。
/// </summary>
/// <remarks>
/// "布局"："LogSlotMeta[1024]"（65,536 B = 1024 × 64B）+ "char[1024 × 224]"（458,752 B）
/// = 524,288 B = 正好 512KB。<see cref="LogSlotMeta"/> 恰好 64 字节，使每个槽位的元数据独占
/// 一个缓存行（v2.0 清单第 3 条：多生产者争抢同一缓存行会显著降速）。
///
/// "写入协议（seqlock，覆盖最旧）"：
/// <list type="number">
///   <item>原子领取全局序号 "claim = Interlocked.Increment"，槽位下标 = "claim &amp; mask"；</item>
///   <item>把槽位的 <see cref="LogSlotMeta.Sequence"/> 置为 "(claim &lt;&lt; 1) | 1"（奇数 = 写入中）；</item>
///   <item>写时间戳 / 级别 / 长度 / 分类 / 正文；</item>
///   <item>把 "Sequence" 置为 "claim &lt;&lt; 1"（偶数 = 完成）。</item>
/// </list>
/// 读者读到奇数、或读到前后两次 "Sequence" 不一致，即判定该槽正在被撕裂写入并跳过。
///
/// "为什么 Sequence 同时编码序号"：槽位被回绕覆盖后，读者需要一个办法确认
/// 「槽里躺的到底是不是我要找的那一条」。把全局序号编进 seqlock 版本号，读者比对
/// "Sequence &gt;&gt; 1 == 目标序号" 即可，不需要第二个字段、也不需要额外一次原子读。
///
/// "已知取舍"：生产者不自旋，因此极端争用下同一槽位可能被两个生产者同时写，
/// 结果是该条记录被读者判定为「撕裂」而跳过（丢读，不会读到半截数据）。这是黑匣子语义
/// 明确接受的代价 —— 见 v2.0 清单第 2 条与第 12 条第③点（丢读率尚未实测）。
///
/// "时间窗口"（v2.0 第 5 条）：窗口 = 容量 ÷ 当前速率。实测约 15.6M 条/秒时
/// 1024 槽只保留 ~68 微秒；正常速率（几百条/秒）下约数秒。要求「崩溃前 N 秒」必须
/// 按预期速率反推容量，或对高频模块采样。
/// </remarks>
public sealed class LogRingBuffer
{
    /// <summary>槽位数（必须是 2 的幂，位运算取模的前提）。</summary>
    public const int DefaultCapacity = 1024;

    /// <summary>每槽可存的 UTF-16 字符数。</summary>
    public const int MaxMessageChars = 224;

    /// <summary>每槽字节数：64B 元数据 + 224 字符 × 2B。</summary>
    public const int SlotBytes = 512;

    /// <summary>默认总字节数：1024 × 512B = 512KB。</summary>
    public const int DefaultTotalBytes = DefaultCapacity * SlotBytes;

    private readonly int _capacity;
    private readonly int _mask;
    private readonly LogSlotMeta[] _slots;
    private readonly char[] _text;

    /// <summary>
    /// 全局领取计数器，独占一段缓存行。用 "long[16]" 而不是标量字段，是为了让
    /// 这个每写必争的计数器与槽位数组（以及对象头）落在不同的缓存行上；同时也避免
    /// 声明纯 padding 私有字段触发「字段从未使用」的编译告警。
    /// </summary>
    private readonly long[] _counterLine = new long[16];

    private LogSanitizer _sanitizer;

    public LogRingBuffer(int capacity = DefaultCapacity, LogSanitizer? sanitizer = null)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "容量必须是 2 的幂（位运算取模的前提）。");
        }

        _capacity = capacity;
        _mask = capacity - 1;
        _slots = new LogSlotMeta[capacity];
        _text = new char[capacity * MaxMessageChars];
        _sanitizer = sanitizer ?? LogSanitizer.Empty;
    }

    public int Capacity => _capacity;

    /// <summary>本缓冲实际占用的字节数（自检据此断言 512KB 预算）。</summary>
    public long AllocatedBytes => ((long)_slots.Length * Unsafe.SizeOf<LogSlotMeta>())
                                + ((long)_text.Length * sizeof(char));

    /// <summary>累计写入条数（含已被覆盖的）。原子读，不取锁。</summary>
    public long TotalWritten => Volatile.Read(ref _counterLine[0]);

    /// <summary>当前生效的脱敏器；可原子替换（"Interlocked.Exchange" 语义由引用赋值保证）。</summary>
    public LogSanitizer Sanitizer
    {
        get => _sanitizer;
        set => _sanitizer = value ?? LogSanitizer.Empty;
    }

    /// <summary>写入一条记录。"零托管堆分配、无锁、不阻塞"。</summary>
    public void Write(LogLevel level, int category, ReadOnlySpan<char> message)
    {
        // 脱敏在一条独立的栈缓冲路径上完成，不让栈引用与参数变量合流
        // （CS8352：把 stackalloc 得到的 span 赋给「由参数初始化」的变量会被 ref-safety 拒绝）。
        LogSanitizer sanitizer = _sanitizer;
        if (sanitizer.HasRoots && message.Length > 0)
        {
            Span<char> scratch = stackalloc char[MaxMessageChars];
            int scrubbed = sanitizer.Apply(message, scratch);
            WriteCore(level, category, scratch[..scrubbed]);
            return;
        }

        WriteCore(level, category, message);
    }

    /// <summary>写入的核心路径。<paramref name="message"/> 声明为 scoped：本方法绝不外泄它。</summary>
    private void WriteCore(LogLevel level, int category, scoped ReadOnlySpan<char> message)
    {
        if (message.Length > MaxMessageChars)
        {
            message = message[..MaxMessageChars];
        }

        long claim = Interlocked.Increment(ref _counterLine[0]);
        int index = (int)(claim & _mask);
        ref LogSlotMeta slot = ref _slots[index];

        // 奇数 = 写入中。同时把全局序号编进去。
        Volatile.Write(ref slot.Sequence, (claim << 1) | 1L);

        slot.Timestamp = Stopwatch.GetTimestamp();
        slot.Level = (int)level;
        slot.Length = message.Length;
        slot.Category = category;

        message.CopyTo(_text.AsSpan(index * MaxMessageChars));

        // 偶数 = 完成。release 语义保证上面的正文写入对读者先行可见。
        Volatile.Write(ref slot.Sequence, claim << 1);
    }

    /// <summary>
    /// 读取指定全局序号的记录，"零分配"（正文拷进调用者的 span）。
    /// 序号已被回绕覆盖、或该槽正处于撕裂写入时返回 "false"。
    /// </summary>
    public bool TryRead(
        long globalSequence,
        Span<char> destination,
        out int length,
        out LogLevel level,
        out int category,
        out long timestamp)
    {
        length = 0;
        level = LogLevel.Trace;
        category = LogCategories.None;
        timestamp = 0;

        if (globalSequence <= 0) return false;

        int index = (int)(globalSequence & _mask);
        ref LogSlotMeta slot = ref _slots[index];

        long before = Volatile.Read(ref slot.Sequence);
        if (before == 0 || (before & 1L) != 0 || (before >> 1) != globalSequence) return false;

        int stored = slot.Length;
        if ((uint)stored > MaxMessageChars) return false;

        level = (LogLevel)slot.Level;
        category = slot.Category;
        timestamp = slot.Timestamp;

        int copy = Math.Min(stored, destination.Length);
        _text.AsSpan(index * MaxMessageChars, copy).CopyTo(destination);

        if (Volatile.Read(ref slot.Sequence) != before) return false;

        length = copy;
        return true;
    }

    /// <summary>
    /// 线性化展开："最新 → 最旧"。只在 dump / 自检路径调用，允许在这里产生字符串分配
    /// （v2.0 第 10 条口径：「写入端零分配、读取端仅在 dump 时一次性拷贝」）。
    /// </summary>
    public int Snapshot(List<LogRecord> destination, int maxRecords = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (maxRecords <= 0) return 0;

        long newest = Volatile.Read(ref _counterLine[0]);

        // 回绕上界：序号 <= newest - capacity 的记录一定已被同槽位的新记录覆盖，
        // 不设这个下界会让 dump 在跑了几百万条之后退化成 O(总条数)。
        long oldest = Math.Max(1L, newest - _capacity + 1);

        int count = 0;
        Span<char> buffer = stackalloc char[MaxMessageChars];

        for (long g = newest; g >= oldest && count < maxRecords; g--)
        {
            if (!TryRead(g, buffer, out int length, out LogLevel level, out int category, out long timestamp))
            {
                continue;
            }

            // dump 阶段兜底扫描（脱敏的第二次机会；入队时已做过一次，这里是幂等的）。
            string text = _sanitizer.ApplyToString(buffer[..length]);
            destination.Add(new LogRecord(g, timestamp, level, category, text));
            count++;
        }

        return count;
    }

    /// <summary>只取条数，不构造字符串（自检用得上的轻量探针）。</summary>
    public int CountReadable()
    {
        long newest = Volatile.Read(ref _counterLine[0]);
        long oldest = Math.Max(1L, newest - _capacity + 1);
        int count = 0;
        for (long g = newest; g >= oldest; g--)
        {
            int index = (int)(g & _mask);
            long s = Volatile.Read(ref _slots[index].Sequence);
            if (s == 0 || (s & 1L) != 0 || (s >> 1) != g) continue;
            count++;
        }
        return count;
    }
}
