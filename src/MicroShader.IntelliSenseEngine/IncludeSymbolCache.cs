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
    /// <summary>
    /// 把 "Packages/..." 之类的虚拟路径解析成物理路径。
    /// </summary>
    /// <remarks>
    /// 必须带上「当前文件」：同一个虚拟路径在不同 Unity 工程里的物理落点不同，
    /// 而一个语言服务器窗口里可能同时开着多个工程的文件 —— 缺了它就只能用启动时
    /// 定下的那一个工程拓扑，父目录工作区下必然解析错（报「包不在当前工程的包拓扑中」）。
    /// </remarks>
    bool TryResolve(string currentPhysicalPath, string virtualPath, out string physicalPath);

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
    private readonly StringInterner _interner = new();
    private readonly object _gate = new();

    /// <summary>
    /// 文件层缓存。容量由「拓扑变化时整表回收」这一条控制，不再设条数上限。
    /// </summary>
    /// <remarks>
    /// 曾经有过「超过 N 条就整表清空」的写法，被撤掉了：预热是逐文件写入的，清空点必然落在
    /// 一轮包含链的中段 —— 本轮 SetDocument 只能拿残余项建文档层，缺的正是最该补的近层结构体，
    /// 而且不会自愈（include 集合没变，预热不会再跑这一份）。条数上限换来的那点内存，
    /// 不值得用「补全与 F12 在某些文档上永久残缺」去换。
    /// </remarks>
    public IncludeSymbolCache()
    {
    }

    /// <summary>缓存作用域的字符串去重表，供 <see cref="FileSymbols.Build"/> 复用。</summary>
    public StringInterner Interner => _interner;

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
            PutLocked(physicalPath, new FileEntry { Stamp = stamp, Structs = structs });
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
            PutLocked(physicalPath, new FileEntry
            {
                Stamp = symbols.Stamp,
                Structs = symbols.Structs,
                Symbols = symbols,
            });
        }
    }

    /// <summary>
    /// 该文件是否已有"完整符号快照"且写入戳未变（预热器的跳过判据）。
    /// </summary>
    /// <remarks>
    /// 为什么不能只用 <see cref="TryGetFile"/>：补全的同步兜底只写结构体表就落条目，
    /// 若预热器按"写入戳相同"就跳过，那个文件永远不会补齐声明表与行首表 ——
    /// 导航侧每次都要现场补扫一次（能自愈，但每次都付一次读盘）。
    /// </remarks>
    public bool HasFullSnapshot(string physicalPath, long stamp)
    {
        lock (_gate)
        {
            return _files.TryGetValue(physicalPath, out var entry)
                && entry.Stamp == stamp
                && entry.Symbols is not null;
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

    /// <summary>整体清空：工程拓扑重建之后调用，让文件层与文档层都从零重建。</summary>
    //
    // 什么时候清：只在工程拓扑重建成功之后（装包、升级包、删包、改 manifest / packages-lock、
    // 切 Unity 版本）。包目录与版本一变，旧快照里的声明偏移与 include 边就可能指向已经不存在
    // 的目录，留着它比丢掉更糟 —— F12 会跳到不存在的行，补全则可能拿旧版本的结构体。
    // 清完第一次请求会慢多少：直连 include 由导航 / 补全既有的同步兜底现场补扫（受
    // SyncFallbackFiles 预算限制，一次最多两个直连 include，几十微秒到几毫秒），扫到的文件会
    // 写回缓存，后续请求直接命中；更深一层与文档层要等下一次后台预热重扫补齐（一次文档打开的
    // 预热量级），这段窗口里只可能暂时少给候选，不会给错候选。
    // 本方法只管缓存；预热去抖状态在 IncludeSymbolWarmer 那一侧，两处必须一起清。
    public void Clear()
    {
        lock (_gate)
        {
            _files.Clear();
            _docs.Clear();
        }
    }

    /// <summary>写入文件层条目（调用方必须已持锁）。</summary>
    // 这里刻意不设「超过 N 条就整表清空」的上限：预热是逐文件写的，清空点会落在一轮 include 链
    // 的中段，本轮随后建的文档层就只剩残缺的近层结构体，而且不会自愈（include 集合没变，预热
    // 不会再跑这一份）。回收交给「工程拓扑变化」这条确定时机（见 ReclaimForTopologyChange）。
    private void PutLocked(string physicalPath, FileEntry entry)
        => _files[physicalPath] = entry;

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
        /// 本文件的 "#include" 目标（原样，按出现顺序）。
        /// </summary>
        /// <remarks>
        /// 导航侧按 include 文本顺序求值宏可见性时要靠它往下钻：快照里没有这张边表，
        /// 就只能退回预热的 BFS 顺序，同名宏谁覆盖谁会和预处理器相反。
        /// </remarks>
        public List<string> Includes = [];

        /// <summary>
        /// 从文件文本构建符号快照：解析结构体声明并记录行首表。
        /// 位置信息通过在原文中搜索定义名获得。
        /// </summary>
        public static FileSymbols Build(string content, long stamp)
            => Build(content, stamp, null);

        public static FileSymbols Build(string content, long stamp, StringInterner? interner)
        {
            ArgumentNullException.ThrowIfNull(content);

            // 索引器一次扫描给出全部信息：结构体、声明表（含函数/变量/宏，带准确偏移）、
            // 宏事件流与行首表。预热器与补全兜底必须共用这一个工厂，两边各建各的会让
            // 补全写进缓存的条目缺声明与行表，导航命中后拿到空表。
            var index = HlslDocumentIndex.Build(content, interner);

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
                Includes = IncludeLineScanner.Scan(content),
                LineStarts = LineOffsets.Build(content),
            };
        }
    }
}

/// <summary>
/// 从原始文本里扫出 "#include" / "#include_with_pragmas" 的目标路径（原样，按出现顺序）。
/// </summary>
/// <remarks>
/// 刻意只做「路径收集」这一件事，Unity 的指令语义（降级重写、条件包含）仍归模块 1/3 负责；
/// 这里漏收的后果只是「少预热一个文件」，不会给出错误候选。
/// 预热器与文件快照共用这一份实现：两边各扫一份，宏求值（导航侧）与预热（补全侧）
/// 看到的 include 顺序就可能不一致，而宏可见性正好按这个顺序覆盖同名定义。
/// </remarks>
public static class IncludeLineScanner
{
    /// <summary>按出现顺序收集 include 目标路径，不做归一化。</summary>
    public static List<string> Scan(string text)
    {
        var found = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var nl = text.IndexOf((char)10, start);
            var end = nl < 0 ? text.Length : nl;
            var line = text.AsSpan(start, end - start).TrimStart();

            if (line.StartsWith("#include", StringComparison.Ordinal))
            {
                var quote = line.IndexOf('"');
                var open = line.IndexOf('<');
                var from = quote >= 0 ? quote + 1 : open + 1;
                if (from > 0)
                {
                    var rest = line[from..];
                    var close = quote >= 0 ? rest.IndexOf('"') : rest.IndexOf('>');
                    if (close > 0)
                    {
                        found.Add(rest[..close].ToString());
                    }
                }
            }

            if (nl < 0)
            {
                break;
            }

            start = nl + 1;
        }

        return found;
    }
}

