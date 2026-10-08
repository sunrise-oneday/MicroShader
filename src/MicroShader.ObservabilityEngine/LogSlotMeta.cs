namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 环形缓冲的槽位元数据。"必须恰好 64 字节"（一个缓存行）。
/// </summary>
/// <remarks>
/// 
/// v2.0 清单第 3 条的实测结论：多生产者争抢同一个 "Interlocked" 计数器会因缓存行争用显著降速，
/// 因此计数器与槽位数组都要 padding 到 64B。这里把"每个槽位"的元数据撑到一个完整的缓存行，
/// 使相邻槽位不会挤在同一行上互相驱逐。
/// 
/// 
/// <see cref="Sequence"/> 一个字段同时承担两件事：seqlock 版本号与「这个槽位当前持有哪一条记录」。
/// 编码为 "(全局序号 &lt;&lt; 1) | 写入中标志"：
/// <list type="bullet">
///   <item>偶数 = 写入完成，值 &gt;&gt; 1 即该记录的全局序号；</item>
///   <item>奇数 = 正在写入；</item>
///   <item>0 = 从未写过。</item>
/// </list>
/// 读者拿到偶数后还要复读一次比对，前后不一致说明被并发写入撕裂 → 跳过该槽。
/// 
/// </remarks>
internal struct LogSlotMeta
{
    /// <summary>seqlock 版本号 &amp; 记录标识："(全局序号 &lt;&lt; 1) | 写入中"。</summary>
    public long Sequence;

    /// <summary><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> 的原始值（dump 时换算成挂钟）。</summary>
    public long Timestamp;

    /// <summary><see cref="LogLevel"/> 的字节值。</summary>
    public int Level;

    /// <summary>有效字符数（&lt;= <see cref="LogRingBuffer.MaxMessageChars"/>）。</summary>
    public int Length;

    /// <summary><see cref="LogCategories"/> 的常量 id。</summary>
    public int Category;

    /// <summary>保留位（对齐用，恒为 0）。</summary>
    public int Reserved;

    // 下面 4 个 long 是纯 padding，把结构体撑到 64B。字段是 public 的，
    // 既不参与逻辑也避免「未使用字段」的分析器告警。
    public long Pad0;
    public long Pad1;
    public long Pad2;
    public long Pad3;
}
