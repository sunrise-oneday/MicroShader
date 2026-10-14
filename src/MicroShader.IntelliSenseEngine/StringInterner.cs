namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// 缓存作用域的字符串去重表：让同一标识符（如 "positionCS"）在 256 个块索引里
/// 只保留一个 <see cref="string"/> 实例，而不是每个索引各存一份。
/// </summary>
/// <remarks>
/// "为什么不用 <see cref="string.Internal(string?)"/>"：intern 池是
/// <b>AppDomain 级、永久驻留、永不清除</b>的。LSP 服务进程长期运行，每个打开过的
/// shader 的标识符都会永久钉在 intern 池里 → 无界增长。本类型随 <see cref="HlslBlockIndexCache"/>
/// 一起创建、一起回收：缓存释放时整表失去引用，GC 正常回收。
///
/// "上界论证"：HLSL 标识符词表有限。实测全语料 363 个块只有 1,455 个唯一 name、
/// 110 个唯一 detail，去重表稳定在这个词表全集（约 67 KB），不会随块周转而增长——
/// 因为新块里出现的标识符几乎总已在表里。
///
/// "线程约定"：与 <see cref="HlslBlockIndexCache"/> 一致，只由会话线程串行调用，
/// 无锁。
/// </remarks>
public sealed class StringInterner
{
    private readonly Dictionary<string, string> _table = new(1024, StringComparer.Ordinal);

    /// <summary>
    /// 取或登记一个字符串的规范实例。传入的 <paramref name="value"/> 若已存在，
    /// 返回既有实例；否则以 <paramref name="value"/> 本身登记并返回。
    /// </summary>
    /// <param name="value">待去重的字符串（非 null）。</param>
    /// <returns>该字符串的唯一规范实例。</returns>
    public string Intern(string value)
    {
        if (_table.TryGetValue(value, out var existing))
        {
            return existing;
        }

        _table[value] = value;
        return value;
    }

    /// <summary>
    /// 从 <see cref="ReadOnlySpan{T}"/> 取或登记一个字符串的规范实例。
    /// 避免调用方先 <c>.ToString()</c> 再查表的双重分配。
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

        var s = value.ToString();
        _table[s] = s;
        return s;
    }

    /// <summary>当前去重表中的唯一字符串数（诊断/排障用）。</summary>
    public int Count => _table.Count;
}
