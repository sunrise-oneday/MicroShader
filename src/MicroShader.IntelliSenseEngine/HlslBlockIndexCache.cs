using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// 按「块内容」缓存的单块 HLSL 索引：一次构建、多处复用。
/// </summary>
/// <remarks>
/// 三个消费者：补全（通常只索引光标所在块）、大纲与 F12（每次请求要遍历全部相关块）。
/// 没有这层共享缓存时，大纲每次请求都把每个程序块重新索引一遍，代价是
/// O(块数 × 单块构建) —— 单块构建本身是毫秒/兆字节分配级的操作。
/// 由组合根创建一份并注入给各引擎，避免每个消费者各建一份、同块重复索引。
///
/// 判定：64 位多项式哈希先筛，命中后逐字符精确比对，绝不误命中。
/// 存储：不再复制块正文，直接引用它所在的那个字符串（块都是从文档字符串上切下来的），
///      只记起始下标与长度。缓存驻留因此只是「若干个引用加下标」，而不是等长的正文副本；
///      内容比对也走向量化 SequenceEqual。非字符串来源（char 数组等）理论上不会出现，
///      出现时退回自持 char 副本，不驻留正文以外的任何形态。
/// 记账：按块正文字符数计费，条数与字符数双封顶，驻留始终有界。
/// 淘汰：固定容量环形缓存，满了覆盖「最新」那一条。消费者每轮都按文本顺序整扫块列表，
///      这是「循环顺序访问」形态：淘汰最老（FIFO/LRU）会让游标追着访问序跑、每轮几乎
///      全量失配；淘汰最新则保住上一轮前段（下一轮最先被访问），尾部在少量槽位上
///      轮换 —— 工作集略大于容量时失配数等于「工作集 − 容量」，线性退化而非清零。
///      超大块（超过字符帽的八分之一）直接不入缓存，避免单条顶穿总帽。
///
/// 线程约定：与既有实现一致，只由会话线程串行调用（补全 / 大纲 / F12 都在该线程）；
/// 预热器等后台线程走各自路径，不使用本缓存。
/// </remarks>
public sealed class HlslBlockIndexCache
{
    private readonly int _maxBlocks;
    private readonly int _maxChars;
    private readonly Entry[] _ring;
    private readonly Dictionary<long, Entry> _byHash = new(64);
    private readonly StringInterner _interner = new();
    private int _cachedChars;
    private int _count;

    /// <summary>默认上限按「单文档的块总量」定：条数 256、源字符 4 MB（真机文件通常 1~50 个块）。</summary>
    public HlslBlockIndexCache(int maxBlocks = 256, int maxChars = 4 * 1024 * 1024)
    {
        _maxBlocks = Math.Max(1, maxBlocks);
        _maxChars = Math.Max(4096, maxChars);
        _ring = new Entry[_maxBlocks];
    }

    /// <summary>缓存内部持有的字符串去重表，供 <see cref="HlslDocumentIndex.Build"/> 复用。</summary>
    internal StringInterner Interner => _interner;

    /// <summary>按内容取单块索引：命中直接返回，否则构建并缓存。</summary>
    /// <remarks>
    /// 入参用 <see cref="ReadOnlyMemory{T}"/> 而非 span：调用方（ShaderLab 切片结果、
    /// 补全的整篇兜底）手上都有 memory，且不再有「span 无法跨调用持有」的复制需求，
    /// 统一形态省掉调用点反复切 Span。
    /// </remarks>
    public HlslDocumentIndex GetOrBuild(ReadOnlyMemory<char> block)
    {
        var span = block.Span;
        var hash = HashOf(span);
        return GetOrBuildCore(block, span, hash);
    }

    /// <summary>
    /// 与单参重载相同，但由调用方提供这段内容的哈希。
    /// </summary>
    /// <remarks>
    /// 纯 HLSL 文件里「整篇文本」既是文档又是唯一的块，上层解析时已经算过一次整篇哈希；
    /// 再算一遍就是同一次扫描做了两遍。传错哈希只会导致缓存未命中（命中后还要比内容），
    /// 不会误命中。
    /// </remarks>
    public HlslDocumentIndex GetOrBuild(ReadOnlyMemory<char> block, long knownHash)
        => GetOrBuildCore(block, block.Span, knownHash);

    private HlslDocumentIndex GetOrBuildCore(ReadOnlyMemory<char> block, ReadOnlySpan<char> span, long hash)
    {
        // 哈希查找：命中后逐字符精确比对，绝不误命中。
        if (_byHash.TryGetValue(hash, out var entry)
            && entry.Length == span.Length
            && ContentEquals(entry, span))
        {
            return entry.Index;
        }

        var built = HlslDocumentIndex.Build(span, _interner);

        // 超大块不入缓存：条目开销与内容大致同阶，缓存一个超大块会顶穿总帽；
        // 直接返回本次构建结果（行为与无缓存时一致，只是不驻留）。
        if (span.Length > _maxChars / 8)
        {
            return built;
        }

        while (_count >= _maxBlocks || _cachedChars + span.Length > _maxChars)
        {
            if (_count == 0)
            {
                break;
            }

            EvictNewest();
        }

        var added = CreateEntry(block, span.Length, hash, built);

        _ring[_count] = added;
        _count++;
        _cachedChars += span.Length;
        _byHash[hash] = added;
        return built;
    }

    private void EvictNewest()
    {
        var slot = _count - 1;
        var victim = _ring[slot];
        _ring[slot] = null!;
        _count--;
        _cachedChars -= victim.Length;

        // 只有当前哈希记录仍指向这个受害者时才删；碰撞覆盖后记录可能已属于更新的条目。
        if (_byHash.TryGetValue(victim.Hash, out var current) && ReferenceEquals(current, victim))
        {
            _byHash.Remove(victim.Hash);
        }
    }

    private static Entry CreateEntry(ReadOnlyMemory<char> block, int length, long hash, HlslDocumentIndex index)
    {
        // 块几乎总是从文档字符串上切下来的：直接引用原文，零拷贝。
        // 这样缓存不复制正文（内存比存等长副本更低），比对还能走向量化 SequenceEqual。
        if (MemoryMarshal.TryGetString(block, out var source, out var start, out _))
        {
            return new Entry
            {
                Source = source,
                SourceStart = start,
                Length = length,
                Hash = hash,
                Index = index,
            };
        }

        // 非字符串来源（char 数组等）理论上不会出现，出现时退回自持副本。
        return new Entry
        {
            Chars = block.Span.ToArray(),
            Length = length,
            Hash = hash,
            Index = index,
        };
    }

    private static bool ContentEquals(Entry entry, ReadOnlySpan<char> span)
    {
        if (entry.Source is { } source)
        {
            return span.SequenceEqual(source.AsSpan(entry.SourceStart, entry.Length));
        }

        return span.SequenceEqual(entry.Chars.AsSpan());
    }

    /// <summary>64 位多项式哈希；只用于「先筛掉绝大多数不相等」，真正判定相等靠逐字符比对。</summary>
    /// <remarks>
    /// 双路并行：每轮吞两个 64 位字（四个字符），两条累加链互不依赖，
    /// 乘法延迟被流水线掩盖。旧的逐字符 FNV 每个字符都要等上一次乘法，
    /// 20 KB 文本要 20 微秒；这条路径 5 微秒以内，而它每次请求都要跑。
    /// </remarks>
    public static long HashOf(ReadOnlySpan<char> span)
    {
        const ulong Prime = 0x100000001B3UL;
        const ulong Seed = 0xCBF29CE484222325UL;

        var h0 = Seed;
        var h1 = Seed;

        var words = MemoryMarshal.Cast<char, ulong>(span);
        var i = 0;
        for (; i + 1 < words.Length; i += 2)
        {
            h0 = unchecked(h0 * Prime) ^ words[i];
            h1 = unchecked(h1 * Prime) ^ words[i + 1];
        }

        for (; i < words.Length; i++)
        {
            h0 = unchecked(h0 * Prime) ^ words[i];
        }

        // 长度为奇数时最后一个字符不在字序列里，必须单独混入：
        // 漏掉它会让只差末字符的两段文本撞出同一个哈希（虽然内容比对仍会救回来，
        // 但每次都退化成全量比对）。
        if ((span.Length & 1) != 0)
        {
            h0 = unchecked(h0 * Prime) ^ span[^1];
        }

        // 两条链用旋转混合，避免只有低位参与字典分桶。
        return unchecked((long)((h0 ^ BitOperations.RotateLeft(h1, 29)) * Prime));
    }

    private sealed class Entry
    {
        /// <summary>块所在的原字符串；与 <see cref="Chars"/> 二选一。</summary>
        public string? Source;

        /// <summary>块在原字符串里的起始下标。</summary>
        public int SourceStart;

        /// <summary>非字符串来源的自持副本；与 <see cref="Source"/> 二选一。</summary>
        public char[]? Chars;

        /// <summary>块正文的字符数（记账与长度快筛用）。</summary>
        public int Length;

        public long Hash;
        public HlslDocumentIndex Index = null!;
    }
}
