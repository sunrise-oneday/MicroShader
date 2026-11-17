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
/// 判定：FNV-1a 64 哈希先筛，命中后逐字符精确比对，绝不误命中。
/// 存储：块正文不再统一存 UTF-16 字符串副本。ASCII 块存 1 byte/char 的 UTF-8
///      字节数组；含非 ASCII 的块（中文注释等）也尝试 UTF-8 编码 —— 只要 UTF-8 字节
///      数不超过 UTF-16 字节数（2 × 字符数）就存字节，否则（纯 CJK 等极端情况）退回
///      字符串。逐字节比较时对 ASCII 走快路径（直接 char↔byte 截断），对非 ASCII
///      走流式 UTF-8 解码器，零分配。
/// 记账：按块正文长度计费，条数与字符数双封顶，驻留始终有界。
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
        var hash = HashOf(span, out var isAscii);

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

        var added = CreateEntry(span, hash, isAscii, built);

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

    private static Entry CreateEntry(ReadOnlySpan<char> span, long hash, bool isAscii, HlslDocumentIndex index)
    {
        if (span.Length == 0)
        {
            return new Entry { Text = string.Empty, Length = 0, Hash = hash, Index = index };
        }

        if (isAscii)
        {
            // ASCII: 1 byte per char，直接截断，无需编码器。
            var utf8 = new byte[span.Length];
            for (var i = 0; i < span.Length; i++)
            {
                utf8[i] = (byte)span[i];
            }

            return new Entry { Utf8 = utf8, Length = span.Length, Hash = hash, Index = index };
        }

        // 含非 ASCII 的块：尝试 UTF-8 编码。中文注释混合 ASCII 代码时仍能省到
        // 1~1.5 byte/char，远低于 UTF-16 的 2 byte/char。
        var byteCount = Encoding.UTF8.GetByteCount(span);
        if (byteCount <= span.Length * 2)
        {
            var utf8 = new byte[byteCount];
            Encoding.UTF8.GetBytes(span, utf8);
            return new Entry { Utf8 = utf8, Length = span.Length, Hash = hash, Index = index };
        }

        // 纯 CJK 等极端：UTF-8 比 UTF-16 更大，退回字符串副本。
        return new Entry
        {
            Text = span.ToString(),
            Length = span.Length,
            Hash = hash,
            Index = index,
        };
    }

    private static bool ContentEquals(Entry entry, ReadOnlySpan<char> span)
    {
        if (entry.Utf8 is { } utf8)
        {
            // ASCII 快路径：utf8.Length == entry.Length 意味着 1 byte/char。
            if (utf8.Length == entry.Length)
            {
                for (var i = 0; i < span.Length; i++)
                {
                    var c = span[i];
                    if (c > 0x7F || utf8[i] != (byte)c)
                    {
                        return false;
                    }
                }

                return true;
            }

            // 非 ASCII 的 UTF-8：流式解码逐字节比对，零分配。
            return Utf8Equals(utf8, span);
        }

        return span.SequenceEqual(entry.Text);
    }

    /// <summary>
    /// 逐字节解码 UTF-8 并与 UTF-16 字符比较，零分配。
    /// </summary>
    /// <remarks>
    /// <see cref="Encoding.UTF8"/>.GetBytes 产出的字节一定是良构 UTF-8，所以这里
    /// 只需处理 1~4 字节序列。对非良构输入（理论上不会出现）返回 false，效果是
    /// 安全的缓存未命中 —— 绝不误命中。
    /// </remarks>
    private static bool Utf8Equals(ReadOnlySpan<byte> utf8, ReadOnlySpan<char> chars)
    {
        var ci = 0;
        var bi = 0;
        var ulen = utf8.Length;
        var clen = chars.Length;

        while (bi < ulen)
        {
            if (ci >= clen)
            {
                return false;
            }

            int b0 = utf8[bi];
            uint code;
            int seq;

            if (b0 < 0x80)
            {
                code = (uint)b0;
                seq = 1;
            }
            else if ((b0 & 0xE0) == 0xC0)
            {
                if (bi + 1 >= ulen)
                {
                    return false;
                }

                code = (uint)(((b0 & 0x1F) << 6) | (utf8[bi + 1] & 0x3F));
                seq = 2;
            }
            else if ((b0 & 0xF0) == 0xE0)
            {
                if (bi + 2 >= ulen)
                {
                    return false;
                }

                code = (uint)(((b0 & 0x0F) << 12) | ((utf8[bi + 1] & 0x3F) << 6) | (utf8[bi + 2] & 0x3F));
                seq = 3;
            }
            else if ((b0 & 0xF8) == 0xF0)
            {
                if (bi + 3 >= ulen)
                {
                    return false;
                }

                code = (uint)(((b0 & 0x07) << 18)
                    | ((utf8[bi + 1] & 0x3F) << 12)
                    | ((utf8[bi + 2] & 0x3F) << 6)
                    | (utf8[bi + 3] & 0x3F));
                seq = 4;
            }
            else
            {
                return false;
            }

            bi += seq;

            if (code <= 0xFFFF)
            {
                // BMP 字符。非代理对：直接比较。
                if (code is >= 0xD800 and <= 0xDFFF)
                {
                    // 良构 UTF-8 不含孤立代理；出现即说明输入异常 → 不匹配。
                    return false;
                }

                if (chars[ci++] != (char)code)
                {
                    return false;
                }
            }
            else
            {
                // 辅助平面 → UTF-16 代理对。
                code -= 0x10000;
                if (ci + 1 >= clen)
                {
                    return false;
                }

                if (chars[ci++] != (char)(0xD800 + (code >> 10)))
                {
                    return false;
                }

                if (chars[ci++] != (char)(0xDC00 + (code & 0x3FF)))
                {
                    return false;
                }
            }
        }

        return ci == clen;
    }

    /// <summary>FNV-1a 64 位；只用于「先筛掉绝大多数不相等」，真正判定相等靠逐字符比对。</summary>
    public static long HashOf(ReadOnlySpan<char> span) => HashOf(span, out _);

    private static long HashOf(ReadOnlySpan<char> span, out bool isAscii)
    {
        var hash = unchecked((long)0xCBF29CE484222325UL);
        var ascii = true;

        foreach (var c in span)
        {
            if (c > 0x7F)
            {
                ascii = false;
            }

            hash ^= c;
            hash = unchecked(hash * 0x100000001B3L);
        }

        isAscii = ascii;
        return hash;
    }

    private sealed class Entry
    {
        /// <summary>块正文的 UTF-8 字节（ASCII 和含非 ASCII 的块共用此路径）；与 <see cref="Text"/> 二选一。</summary>
        public byte[]? Utf8;

        /// <summary>空块或纯 CJK（UTF-8 比 UTF-16 更大）的块的 UTF-16 副本；与 <see cref="Utf8"/> 二选一。</summary>
        public string? Text;

        /// <summary>块正文的 UTF-16 字符数（记账与长度快筛用）。</summary>
        public int Length;

        public long Hash;
        public HlslDocumentIndex Index = null!;
    }
}
