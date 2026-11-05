using System.Collections.Concurrent;

namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// 缓存作用域的字符串去重表：让同一标识符（如 "positionCS"）在 256 个块索引里
/// 只保留一个 <see cref="string"/> 实例，而不是每个索引各存一份。
/// </summary>
/// <remarks>
/// "为什么不用 string.Intern"：intern 池是 AppDomain 级、永久驻留、永不清除的。
/// LSP 服务进程长期运行，每个打开过的 shader 的标识符都会永久钉在 intern 池里 → 无界增长。
/// 本类型随 <see cref="HlslBlockIndexCache"/> 一起创建、一起回收：缓存释放时整表失去引用，
/// GC 正常回收。
///
/// "上界论证"：HLSL 标识符词表有限。实测全语料 363 个块只有 1,455 个唯一 name、
/// 110 个唯一 detail，去重表稳定在这个词表全集（约 67 KB），不会随块周转而增长——
/// 因为新块里出现的标识符几乎总已在表里。
///
/// "线程约定：任何线程都可以并发调用，跨线程安全"。实际有两类调用方：会话线程（块索引
/// 构建，也就是补全 / 大纲 / F12 的那条路，以及 include 缓存的同步兜底）和后台预热线程
/// （IncludeSymbolWarmer 扫头文件）。两边在冷启动阶段都会写表，之后基本只读，所以这里
/// 用并发字典而不是一把全局锁 —— 命中是无锁读（热路径上每个 token 都要过一次），
/// 只有第一次登记某个标识符时才取一次桶级锁。
///
/// "身份保证"：同一个值在表里只有一个实例。并发登记同一个标识符时先插入者胜出，
/// 后到者拿回先插入的那个实例，因此返回的引用可能与传入的字符串不是同一个对象
/// （内容一定相等），调用方不得依赖 ReferenceEquals(返回值, 入参)。
/// </remarks>
public sealed class StringInterner
{
    // 初始容量按实测词表量级预留（约 1.5 K 项），避免预热期反复扩容；
    // 并发级别跟随核数，未命中时只锁其中一个桶。
    private readonly ConcurrentDictionary<string, string> _table =
        new(Environment.ProcessorCount, 1024, StringComparer.Ordinal);

    /// <summary>
    /// 取或登记一个字符串的规范实例。传入的 <paramref name="value"/> 若已存在，
    /// 返回既有实例；否则以 <paramref name="value"/> 本身登记并返回。
    /// </summary>
    /// <param name="value">待去重的字符串（非 null）。</param>
    /// <returns>该字符串的唯一规范实例。</returns>
    public string Intern(string value)
    {
        // 命中走无锁读（热路径几乎都是命中）；未命中才进 GetOrAdd，并发登记同一个值时
        // 由它收敛到先插入的实例。
        if (_table.TryGetValue(value, out var existing))
        {
            return existing;
        }

        return _table.GetOrAdd(value, value);
    }

    /// <summary>
    /// 从 <see cref="ReadOnlySpan{T}"/> 取或登记一个字符串的规范实例。
    /// 避免调用方先 ToString 再查表的双重分配。
    /// </summary>
    /// <param name="value">待去重的字符片段。</param>
    /// <returns>该片段对应的唯一规范 <see cref="string"/> 实例。</returns>
    public string Intern(ReadOnlySpan<char> value)
    {
        // 用 AlternateLookup 走 span 查找，命中时不分配；未命中才 ToString 一次。
        var lookup = _table.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(value, out var existing))
        {
            return existing;
        }

        // 未命中：先落成字符串再登记。并发下两方可能各建一份内容相同的字符串，
        // GetOrAdd 让它们收敛到同一个实例，输的那份随即变成垃圾。
        var s = value.ToString();
        return _table.GetOrAdd(s, s);
    }

    /// <summary>当前去重表中的唯一字符串数（诊断/排障用；并发写入期间是一次近似快照）。</summary>
    public int Count => _table.Count;
}
