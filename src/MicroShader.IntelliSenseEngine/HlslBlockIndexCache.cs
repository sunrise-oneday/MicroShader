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
/// 判定：FNV-1a 64 哈希先筛，哈希桶内再逐字符精确比对，绝不误命中。
/// 淘汰：固定容量环形缓存，条数与源字符数双封顶；满了覆盖「最新」那一条。
///      消费者每轮都按文本顺序整扫块列表，这是「循环顺序访问」形态：
///      淘汰最老（FIFO/LRU）会让游标追着访问序跑、每轮几乎全量失配；
///      淘汰最新则保住上一轮前段（下一轮最先被访问），尾部在少量槽位上轮换 ——
///      工作集略大于容量时失配数等于「工作集 − 容量」，线性退化而非清零。
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
    private readonly Dictionary<long, List<Entry>> _byHash = new(64);
    private int _cachedChars;
    private int _count;

    /// <summary>默认上限按「单文档的块总量」定：条数 256、源字符 4 MB（真机文件通常 1~50 个块）。</summary>
    public HlslBlockIndexCache(int maxBlocks = 256, int maxChars = 4 * 1024 * 1024)
    {
        _maxBlocks = Math.Max(1, maxBlocks);
        _maxChars = Math.Max(4096, maxChars);
        _ring = new Entry[_maxBlocks];
    }

    /// <summary>按内容取单块索引：命中直接返回，否则构建并缓存。</summary>
    public HlslDocumentIndex GetOrBuild(ReadOnlySpan<char> block)
    {
        var hash = HashOf(block);

        // 哈希桶查找：碰撞时逐字符精确比对，绝不误命中。
        if (_byHash.TryGetValue(hash, out var bucket))
        {
            for (var i = 0; i < bucket.Count; i++)
            {
                var entry = bucket[i];
                if (entry.Content.Length == block.Length && block.SequenceEqual(entry.Content))
                {
                    return entry.Index;
                }
            }
        }

        var built = HlslDocumentIndex.Build(block);

        // 超大块不入缓存：条目开销与内容大致同阶，缓存一个超大块会顶穿总帽；
        // 直接返回本次构建结果（行为与无缓存时一致，只是不驻留）。
        if (block.Length > _maxChars / 8)
        {
            return built;
        }

        while (_count >= _maxBlocks || _cachedChars + block.Length > _maxChars)
        {
            if (_count == 0)
            {
                break;
            }

            EvictNewest();
        }

        var added = new Entry { Content = block.ToString(), Hash = hash, Index = built };
        _ring[_count] = added;
        _count++;
        _cachedChars += block.Length;

        if (!_byHash.TryGetValue(hash, out var slot))
        {
            slot = [];
            _byHash[hash] = slot;
        }

        slot.Add(added);
        return built;
    }

    private void EvictNewest()
    {
        var slot = _count - 1;
        var victim = _ring[slot];
        _ring[slot] = null!;
        _count--;
        _cachedChars -= victim.Content.Length;

        if (_byHash.TryGetValue(victim.Hash, out var bucket))
        {
            bucket.Remove(victim);
            if (bucket.Count == 0)
            {
                _byHash.Remove(victim.Hash);
            }
        }
    }

    /// <summary>FNV-1a 64 位；只用于「先筛掉绝大多数不相等」，真正判定相等靠逐字符比对。</summary>
    public static long HashOf(ReadOnlySpan<char> span)
    {
        var hash = unchecked((long)0xCBF29CE484222325UL);

        foreach (var c in span)
        {
            hash ^= c;
            hash = unchecked(hash * 0x100000001B3L);
        }

        return hash;
    }

    private sealed class Entry
    {
        public string Content = string.Empty;
        public long Hash;
        public HlslDocumentIndex Index = null!;
    }
}
