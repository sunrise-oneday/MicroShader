namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// 虚拟路径 → 物理路径的解析与读取。由宿主（组合根）实现 —— 引擎层不依赖任何工程布局知识。
/// </summary>
/// <remarks>
/// 之所以抽成接口："MicroShader.Server" 已经有 VFS 索引（包拓扑 + 物理路径），
/// 而 "MicroShader.IntelliSenseEngine" 不该反向依赖它（依赖方向必须是 Server → Engine）。
/// </remarks>
public interface IIncludeFileSource
{
    /// <summary>把 "Packages/..." 之类的虚拟路径解析成物理路径。</summary>
    bool TryResolve(string virtualPath, out string physicalPath);

    /// <summary>
    /// 相对 include（"#include "Common.hlsl""）：按「包含方所在目录」解析。
    /// </summary>
    /// <remarks>
    /// URP 头里大量使用相对 include。缺了这条路，这些文件会被静默跳过，
    /// 其类型（"Light" 所在的 "RealtimeLights.hlsl" 就是被相对 include 拉进来的）永远补不出来。
    /// </remarks>
    bool TryResolveRelative(string currentPhysicalPath, string target, out string physicalPath);

    /// <summary>读取物理文件文本。</summary>
    bool TryRead(string physicalPath, out string text);

    /// <summary>取文件写入戳（用于缓存失效）；取不到返回 0。</summary>
    long GetStamp(string physicalPath);
}

/// <summary>include 链符号预热的可调参数。</summary>
public sealed class IncludeSymbolOptions
{
    /// <summary>单次预热最多扫描的文件数。</summary>
    public int MaxFiles { get; init; } = 256;

    /// <summary>单次预热最多读取的字节数。</summary>
    public int MaxBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>include 递归的最大深度。</summary>
    public int MaxDepth { get; init; } = 8;

    /// <summary>
    /// B 兜底：补全缓存 miss 时，最多同步扫描几个「当前块的直连 include」。
    /// 0 = 关闭兜底（纯 C 方案，miss 一律降级）。
    /// </summary>
    public int SyncFallbackFiles { get; init; } = 2;

    /// <summary>整体开关；false 等于完全不启用（引擎行为与不注入缓存时一致）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>是否启动后台预热 worker；false = 仅手动触发（测试用）。</summary>
    public bool RunWorker { get; init; } = true;
}

/// <summary>
/// include 链符号缓存：文件层（可跨文档共享）+ 文档层（按 include 顺序合并）。
/// </summary>
/// <remarks>
/// "为什么分两层"：同一个头文件会被很多 .shader 共享（"Lighting.hlsl" 之于 URP），
/// 文件层按「物理路径 + 写入戳」缓存其结构体定义，避免重复扫描；文档层存「本文档的 include 顺序」，
/// 因为同名类型在不同文件里定义不同（"Varyings" 就是这样），必须由"最近的"定义胜出。
/// "合并规则：先到先得"。预热按 BFS 顺序喂文件，直连的比深层先到 ⇒ 最近的定义胜出。
/// 若允许覆盖，深层的定义会把浅层的挤掉 —— 那是错的。
/// "线程安全"：预热在后台线程写、补全在会话线程读，全部走同一把锁。
/// 读路径只做两次字典查找，不产生分配。
/// </remarks>
public sealed class IncludeSymbolCache
{
    private sealed class FileEntry
    {
        public long Stamp;

        public Dictionary<string, HlslStruct> Structs = new(StringComparer.Ordinal);

        // 符号快照（声明表 / 行首表 / 宏事件流）。补全兜底只写结构体表时为 null，
        // 此时导航侧会走自己的现场补扫路径重建完整快照。
        public FileSymbols? Symbols;
    }

    private sealed class DocEntry
    {
        public string[] Files = [];

        public Dictionary<string, HlslStruct> ByType = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DocEntry> _docs = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>文件层条目数（诊断/测试用）。</summary>
    public int FileCount
    {
        get
        {
            lock (_gate)
            {
                return _files.Count;
            }
        }
    }

    /// <summary>文档层条目数（诊断/测试用）。</summary>
    public int DocumentCount
    {
        get
        {
            lock (_gate)
            {
                return _docs.Count;
            }
        }
    }

    /// <summary>按文档查类型定义。"补全路径的读入口，绝不触发 I/O。"</summary>
    public bool TryGet(string documentUri, string typeName, out HlslStruct structDecl)
    {
        lock (_gate)
        {
            if (_docs.TryGetValue(documentUri, out var doc) && doc.ByType.TryGetValue(typeName, out var found))
            {
                structDecl = found;
                return true;
            }
        }

        structDecl = null!;
        return false;
    }

    /// <summary>文件层命中判定（供预热器跳过未变化的文件）。</summary>
    public bool TryGetFile(string physicalPath, long stamp, out Dictionary<string, HlslStruct> structs)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(physicalPath, out var entry) && entry.Stamp == stamp)
            {
                structs = entry.Structs;
                return true;
            }
        }

        structs = null!;
        return false;
    }

    /// <summary>
    /// 写入一个文件的结构体定义（补全的同步兜底用）。只带结构体表、不带符号快照。
    /// </summary>
    public void PutFile(string physicalPath, long stamp, Dictionary<string, HlslStruct> structs)
    {
        lock (_gate)
        {
            _files[physicalPath] = new FileEntry { Stamp = stamp, Structs = structs };
        }
    }

    /// <summary>
    /// 写入一个文件的完整符号快照（预热器用）。
    /// </summary>
    /// <remarks>
    /// 两个写入口必须落进同一份存储：此前符号快照单独存了一份字典，结果补全兜底写的条目
    /// 导航看不见、预热写的条目 FileCount 数不到，两边各拿半份真相。
    /// </remarks>
    public void PutFile(string physicalPath, FileSymbols symbols)
    {
        lock (_gate)
        {
            _files[physicalPath] = new FileEntry
            {
                Stamp = symbols.Stamp,
                Structs = symbols.Structs,
                Symbols = symbols,
            };
        }
    }

    /// <summary>按物理路径取出符号快照；该文件只有结构体表（没做过完整扫描）时返回 false。</summary>
    public bool TryGetFileByPath(string physicalPath, out FileSymbols symbols)
    {
        lock (_gate)
        {
            if (_files.TryGetValue(physicalPath, out var entry) && entry.Symbols is not null)
            {
                symbols = entry.Symbols;
                return true;
            }
        }

        symbols = null!;
        return false;
    }

    /// <summary>取出某文档预热好的文件列表（BFS 顺序）。</summary>
    public bool TryGetDocumentFiles(string documentUri, out string[] files)
    {
        lock (_gate)
        {
            if (_docs.TryGetValue(documentUri, out var doc))
            {
                files = doc.Files;
                return true;
            }
        }

        files = [];
        return false;
    }

    /// <summary>按 include 顺序（BFS）确定本文档的类型表：先到先得。</summary>
    public void SetDocument(string documentUri, IReadOnlyList<string> orderedFiles)
    {
        lock (_gate)
        {
            var byType = new Dictionary<string, HlslStruct>(StringComparer.Ordinal);
            foreach (var file in orderedFiles)
            {
                if (!_files.TryGetValue(file, out var entry))
                {
                    continue;
                }

                foreach (var pair in entry.Structs)
                {
                    byType.TryAdd(pair.Key, pair.Value);
                }
            }

            _docs[documentUri] = new DocEntry { Files = [.. orderedFiles], ByType = byType };
        }
    }

    /// <summary>文档关闭时丢弃其文档层条目（文件层保留，其他文档还能用）。</summary>
    public void InvalidateDocument(string documentUri)
    {
        lock (_gate)
        {
            _docs.Remove(documentUri);
        }
    }

    /// <summary>整体清空（超过预算上限时使用，保证有界）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _files.Clear();
            _docs.Clear();
        }
    }

    /// <summary>当前文档已合并的类型数（测试用）。</summary>
    public int TypeCountOf(string documentUri)
    {
        lock (_gate)
        {
            return _docs.TryGetValue(documentUri, out var doc) ? doc.ByType.Count : 0;
        }
    }

    /// <summary>
    /// 一个文件的符号快照：声明表 + 行首偏移表 + 写入戳。
    /// 由 DefinitionTracer 用于跨文件 F12 跳转。
    /// </summary>
    public sealed class FileSymbols
    {
        /// <summary>结构体定义表（补全的文档层合并需要它）。</summary>
        public Dictionary<string, HlslStruct> Structs = new(StringComparer.Ordinal);

        /// <summary>符号名 → 声明列表（一个名字可能有多处重载）。</summary>
        public Dictionary<string, List<HlslDecl>> Declarations = new(StringComparer.Ordinal);

        /// <summary>行首偏移表（由 LineOffsets.Build 生成）。</summary>
        public int[] LineStarts = [];

        /// <summary>文件写入戳（用于缓存失效判定）。</summary>
        public long Stamp;

        /// <summary>宏可见性事件流（#define / #undef，按出现顺序）。</summary>
        public List<HlslMacroOp> MacroOps = [];

        /// <summary>
        /// 从文件文本构建符号快照：解析结构体声明并记录行首表。
        /// 位置信息通过在原文中搜索定义名获得。
        /// </summary>
        public static FileSymbols Build(string content, long stamp)
        {
            ArgumentNullException.ThrowIfNull(content);

            // 索引器一次扫描给出全部信息：结构体、声明表（含函数/变量/宏，带准确偏移）、
            // 宏事件流与行首表。预热器与补全兜底必须共用这一个工厂，两边各建各的会让
            // 补全写进缓存的条目缺声明与行表，导航命中后拿到空表。
            var index = HlslDocumentIndex.Build(content);

            var structs = new Dictionary<string, HlslStruct>(StringComparer.Ordinal);
            foreach (var pair in index.Structs)
            {
                structs[pair.Key] = pair.Value;
            }

            var declarations = new Dictionary<string, List<HlslDecl>>(StringComparer.Ordinal);
            foreach (var pair in index.Declarations)
            {
                declarations[pair.Key] = pair.Value;
            }

            return new FileSymbols
            {
                Stamp = stamp,
                Structs = structs,
                Declarations = declarations,
                MacroOps = [.. index.MacroOps],
                LineStarts = LineOffsets.Build(content),
            };
        }
    }
}

