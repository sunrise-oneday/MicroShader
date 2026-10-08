using System.Text;
using System.Text.Json;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.IntelliSenseEngine;

/// <summary>
/// IntelliSense 服务：把补全/悬停请求变成写进 LSP 响应 writer 的 JSON。
/// </summary>
/// <remarks>
/// "零 DTO、零反射序列化"（ADR-026）：结果直接写进调用方给的 "Utf8JsonWriter"，
/// 因此 Native AOT 下不存在「缺序列化元数据」的失败模式。
/// "isIncomplete 取 false 的理由"（详设 v2.0 第 2 条）：该条要求
/// 「一旦返回 isIncomplete:true，就必须配套按前缀过滤的重入分支，否则会退化成每次按键返回全量的往返风暴」。
/// 本服务候选集很小（内建约 90 条 + 本文件符号数十条），因此直接返回
/// "完整候选集 + isIncomplete:false"，由客户端本地过滤。若候选集将来涨到上千条，
/// 必须改为服务端前缀过滤 + isIncomplete:true 并补重入分支。
/// "降级路径"（详设 v2.0 第 10 条）：逆向作用域推导是 AST-less 启发式。
/// 变量或类型定义在 include 头文件里时本服务看不到，此时返回 "false"（→ 协议层写 null）
/// 而不是猜 —— 宁可没有候选，也不给错误候选。
/// "无结果路径零分配"：先计数，命中 0 条直接返回；只有真的要产出候选时才写 JSON。
/// </remarks>
public sealed class IntelliSenseService
{
    // LSP CompletionItemKind
    private const int KindFunction = 3;
    private const int KindField = 5;
    private const int KindVariable = 6;
    private const int KindKeyword = 14;
    private const int KindStruct = 22;
    private const int KindConstant = 21;

    private readonly IntelliSenseOptions _options;
    private readonly ShaderLabParseResult _parseResult = new();
    private readonly ShaderLabStateMachine _machine = new();
    private readonly List<BlockSpan> _blockBuffer = [];
    private readonly List<HlslDocumentIndex> _indexBuffer = [];
    private readonly HlslBlockIndexCache _blockIndexCache;
    private readonly List<string> _nameBuffer = [];

    /// <summary>本次请求要给出的宏候选（本文件 + include 闭包，已去重排序前）。</summary>
    private readonly List<string> _macroNames = [];

    /// <summary>宏名去重（同一个宏可能在本文件与多个头文件里都出现）。</summary>
    private readonly HashSet<string> _macroSeen = new(StringComparer.Ordinal);

    /// <summary>上次 Parse 的全文哈希与文本长度——命中则跳过 ShaderLabStateMachine.Parse。</summary>
    /// <remarks>
    /// "为什么有效"：用户打字时每次 didChange 都把整份文档送进来，但绝大多数编辑只改了一个块，
    /// ShaderLab 状态机的全文行扫描（O(文档行数)）是重复劳动。按全文哈希缓存结果，
    /// 文本不变时（hover 后紧接补全、Ctrl+Space 连按）直接跳过 Parse。
    /// 文本变了时必须重 Parse（块的偏移/数量可能变），但块内容缓存（R6）会挡住未变块的重建。
    /// </remarks>
    private long _parsedTextHash;
    private int _parsedTextLength = -1;

    /// <summary>
    /// 上次「块索引实例序列 → 合并结果」的记忆。命中即可跳过逐块合并。
    /// 由于单块索引是按内容缓存的，实例引用相等等价于内容与顺序都未变 —— 精确，不会误命中。
    /// </summary>
    private HlslDocumentIndex[] _memoBlocks = [];
    private HlslDocumentIndex? _memoMerged;

    /// <summary>include 链符号能力；"null" = 不启用（默认，行为与注入前完全一致）。</summary>
    private readonly IncludeSymbolProvider? _includeSymbols;

    public IntelliSenseService(
        IntelliSenseOptions? options = null,
        IncludeSymbolProvider? includeSymbols = null,
        HlslBlockIndexCache? blockIndexCache = null)
    {
        _options = options ?? new IntelliSenseOptions();
        _includeSymbols = includeSymbols;
        _blockIndexCache = blockIndexCache ?? new HlslBlockIndexCache();
    }

    /// <summary>"textDocument/completion" 的实现。</summary>
    public bool ProvideCompletions(PositionRequest request, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var trigger = TriggerContext.Detect(request.Text, request.Line, request.Character);
        if (trigger.Kind == TriggerKind.None)
        {
            return false;
        }

        if (trigger.Kind == TriggerKind.MemberAccess)
        {
            return ProvideMemberCompletions(request, trigger, writer);
        }

        return ProvidePrefixCompletions(request, trigger, writer);
    }

    /// <summary>成员补全："base." → 本文件结构体的字段，或内建向量/矩阵的分量。</summary>
    /// <remarks>
    /// "R7（2026-09-26）"：此前只查结构体表，"float4 slope;" 的 "slope." 直接降级为空 ——
    /// 而分量摆弄（"slope.r" / "pivot.xz" / "i.color.rgb"）恰恰是 shader 里最高频的成员访问。
    /// 现在结构体未命中时再试内建向量/矩阵，仍未命中才降级。
    /// </remarks>
    private bool ProvideMemberCompletions(PositionRequest request, TriggerContext trigger, Utf8JsonWriter writer)
    {
        var index = BuildIndex(request.Text);

        // 基变量的类型来源：① 本文件声明 ② 引擎内置 uniform 表。
        // ② 是真实语料验收暴露的缺口：_Time / _WorldSpaceCameraPos / unity_ObjectToWorld
        // 这类变量既不在本文件、也不在任何 include 链里（引擎注入），只能靠内置表。
        // 嵌套链优先（"v.posOS." → v 的类型 → posOS 字段类型）；链解析失败再退回旧的单级判定，
        // 保证旧行为（字段名也登记在声明表里）不回退。
        if (trigger.MemberChain.Length > 1
            && TryResolveChainType(request, index, trigger.MemberChain, out var chainType))
        {
            return WriteTypeMembers(request, index, chainType, trigger.Prefix, writer);
        }

        string? resolvedType = null;
        if (!index.VariablesToType.TryGetValue(trigger.MemberBase, out resolvedType)
            && !HlslBuiltinUniforms.TryGetType(trigger.MemberBase, out resolvedType))
        {
            return false;   // 既不在本文件，也不是引擎内置 uniform → 降级
        }

        return WriteTypeMembers(request, index, resolvedType!, trigger.Prefix, writer);
    }

    /// <summary>按类型名写成员候选：本文件结构体 → 现场包缓存 → 同步兜底 → 内建向量族。</summary>
    private bool WriteTypeMembers(PositionRequest request, HlslDocumentIndex index, string typeName, string prefix, Utf8JsonWriter writer)
    {
        if (TryFindStruct(request, index, typeName, out var structDecl)
            && WriteStructMembers(structDecl, prefix, writer))
        {
            return true;
        }

        // 结构体与现场包都没有时，只有内建向量族还能给候选。
        return WriteBuiltinMembers(typeName, prefix, writer);
    }

    /// <summary>结构体查找的三个来源，顺序与成本递增一致。</summary>
    private bool TryFindStruct(PositionRequest request, HlslDocumentIndex index, string typeName, out HlslStruct found)
    {
        if (index.Structs.TryGetValue(typeName, out found!))
        {
            return true;
        }

        // ① 后台预热好的现场包符号（只读缓存，不碰磁盘）
        if (_includeSymbols is not null && _includeSymbols.Cache.TryGet(request.Uri, typeName, out found!))
        {
            return true;
        }

        // ② B 兜底：同步扫有限个直连 include 再试一次（仅当宿主给了文件源）
        return _includeSymbols?.Files is not null
            && _includeSymbols.Options.SyncFallbackFiles > 0
            && TrySyncIncludeFallback(typeName, out found);
    }

    /// <summary>沿点号链逐级推导类型："v.posOS" → typeof(v) 的 posOS 字段类型。</summary>
    /// <remarks>链深上限 8，防病态输入；任一级解析不到即返回 false（交给单级旧逻辑）。</remarks>
    private bool TryResolveChainType(PositionRequest request, HlslDocumentIndex index, string[] chain, out string typeName)
    {
        typeName = string.Empty;
        if (chain.Length > 8)
        {
            return false;
        }

        if (!index.VariablesToType.TryGetValue(chain[0], out var current)
            && !HlslBuiltinUniforms.TryGetType(chain[0], out current))
        {
            return false;
        }

        for (var i = 1; i < chain.Length; i++)
        {
            if (!TryFindStruct(request, index, current!, out var decl))
            {
                return false;
            }

            string? next = null;
            foreach (var field in decl.Fields)
            {
                if (string.Equals(field.Name, chain[i], StringComparison.Ordinal))
                {
                    next = field.Type;
                    break;
                }
            }

            if (next is null)
            {
                return false;
            }

            current = next;
        }

        typeName = current!;
        return true;
    }

    /// <summary>
    /// B 兜底：同步扫描"有限个"「当前块的直连 include」，命中即返回。
    /// </summary>
    /// <remarks>
    /// 存在的理由：预热是离线的，文档打开后用户可能刚敲下一个新的 "#include"，
    /// 在防抖分析把它送进预热队列之前，缓存里没有它。这里同步补一个文件（默认上限 2 个），
    /// 代价是几十微秒到几毫秒，仍在 5 ms 配额的量级内；扫到的文件同时写回缓存，后续请求不必再扫。
    /// </remarks>
    private bool TrySyncIncludeFallback(string typeName, out HlslStruct found)
    {
        found = null!;

        var provider = _includeSymbols!;
        var files = provider.Files!;
        var budget = provider.Options.SyncFallbackFiles;
        var scanned = 0;

        foreach (var target in EnumerateIncludes())
        {
            if (scanned >= budget)
            {
                break;
            }

            if (!files.TryResolve(target, out var physical)
                && !files.TryResolveRelative(_options.DocumentPath, target, out physical))
            {
                continue;
            }

            scanned++;
            var stamp = files.GetStamp(physical);

            if (!provider.Cache.TryGetFile(physical, stamp, out var structs))
            {
                if (!files.TryRead(physical, out var text))
                {
                    continue;
                }

                var built = HlslDocumentIndex.Build(text.AsSpan());
                structs = new Dictionary<string, HlslStruct>(StringComparer.Ordinal);
                foreach (var pair in built.Structs)
                {
                    structs[pair.Key] = pair.Value;
                }

                provider.Cache.PutFile(physical, stamp, structs);
            }

            if (structs.TryGetValue(typeName, out var hit))
            {
                found = hit;
                return true;
            }
        }

        return false;
    }

    /// <summary>枚举当前文档所有程序块（含共享块）的 include 目标。</summary>
    private IEnumerable<string> EnumerateIncludes()
    {
        foreach (var snippet in _parseResult.Passes)
        {
            foreach (var include in snippet.Includes)
            {
                yield return include.Path;
            }
        }

        foreach (var snippet in _parseResult.SharedSnippets)
        {
            foreach (var include in snippet.Includes)
            {
                yield return include.Path;
            }
        }
    }

    /// <summary>写出结构体字段候选。</summary>
    private static bool WriteStructMembers(HlslStruct structDecl, string prefix, Utf8JsonWriter writer)
    {
        var matched = 0;
        foreach (var field in structDecl.Fields)
        {
            if (Matches(field.Name, prefix))
            {
                matched++;
            }
        }

        if (matched == 0)
        {
            return false;
        }

        writer.WriteStartObject();
        writer.WriteBoolean("isIncomplete", false);
        writer.WritePropertyName("items");
        writer.WriteStartArray();

        foreach (var field in structDecl.Fields)
        {
            if (!Matches(field.Name, prefix))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("label", field.Name);
            writer.WriteNumber("kind", KindField);
            writer.WriteString("detail", field.Type);
            writer.WriteString("sortText", "0_" + field.Name);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        return true;
    }

    /// <summary>
    /// 写出内建标量/向量/矩阵的分量候选；类型不是内建向量族时返回 false（继续走降级）。
    /// </summary>
    /// <remarks>
    /// 向量给 swizzle（"r/g/b/a" + "x/y/z/w" + 高频组合），矩阵给 "_mRC" 分量
    /// （DXC 与 Unity 的通用写法，如 "UNITY_MATRIX_M._m00"）。
    /// 矩阵名用栈上缓冲拼出，两次扫描（先计数后写出）保持「无结果路径零分配」与「结果集小才写 JSON」两条既有约束。
    /// </remarks>
    private static bool WriteBuiltinMembers(string typeName, string prefix, Utf8JsonWriter writer)
    {
        if (!HlslTypes.TryParseShape(typeName, out var scalar, out var rows, out var cols))
        {
            return false;
        }

        if (cols > 1)
        {
            return WriteMatrixMembers(scalar, rows, cols, prefix, writer);
        }

        var swizzles = HlslSwizzles.ForArity(rows);

        var matched = 0;
        foreach (var name in swizzles)
        {
            if (MatchesSpan(name, prefix))
            {
                matched++;
            }
        }

        if (matched == 0)
        {
            return false;
        }

        writer.WriteStartObject();
        writer.WriteBoolean("isIncomplete", false);
        writer.WritePropertyName("items");
        writer.WriteStartArray();

        foreach (var name in swizzles)
        {
            if (!MatchesSpan(name, prefix))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("label", name);
            writer.WriteNumber("kind", KindField);
            writer.WriteString("detail", ScalarOfLength(scalar, name.Length));
            writer.WriteString("sortText", "0_" + name);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        return true;
    }

    /// <summary>矩阵分量："_mRC"，按行列展开（如 "float4x4" → "_m00".."_m33"）。</summary>
    private static bool WriteMatrixMembers(string scalar, int rows, int cols, string prefix, Utf8JsonWriter writer)
    {
        Span<char> member = stackalloc char[4];
        member[0] = '_';
        member[1] = 'm';

        var matched = 0;
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                member[2] = (char)('0' + r);
                member[3] = (char)('0' + c);
                if (MatchesSpan(member, prefix))
                {
                    matched++;
                }
            }
        }

        if (matched == 0)
        {
            return false;
        }

        writer.WriteStartObject();
        writer.WriteBoolean("isIncomplete", false);
        writer.WritePropertyName("items");
        writer.WriteStartArray();

        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                member[2] = (char)('0' + r);
                member[3] = (char)('0' + c);
                if (!MatchesSpan(member, prefix))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteString("label", member);
                writer.WriteNumber("kind", KindField);
                writer.WriteString("detail", scalar);
                writer.WriteString("sortText", "0_" + new string(member));
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        return true;
    }

    /// <summary>分量结果的类型名："float4" 的 "xy" → "float2"；单分量就是标量本身（没有 float1）。</summary>
    private static string ScalarOfLength(string scalar, int length) => length == 1 ? scalar : scalar + length.ToString();

    /// <summary>span 形态的前缀匹配（矩阵分量名用栈上缓冲拼出，避免逐候选分配字符串）。</summary>
    private static bool MatchesSpan(ReadOnlySpan<char> candidate, string prefix)
        => prefix.Length == 0
        || (candidate.Length >= prefix.Length
            && candidate[..prefix.Length].Equals(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 标识符前缀补全："本文件登记的符号" + HLSL 内建函数 / ShaderLab 关键字。
    /// </summary>
    /// <remarks>
    /// "2026-09-26 修正（R4）"：旧实现只查 <see cref="HlslBuiltins"/> 的约 90 条内建，
    /// 真实语料实测「输入 "_Grass" → 0 候选」，而这一个文件里就有 20 多个 "_Grass*" / "_Wind*"。
    /// 现在把本文件登记的变量与结构体一并给出（变量 kind=Variable、结构体 kind=Struct），
    /// sortText 用 "0_" 前缀排在内建之前 —— 自己文件里的符号永远比内建更可能被用到。
    /// 候选集仍然很小，故保持 isIncomplete:false（见类型注释）。
    /// </remarks>
    private bool ProvidePrefixCompletions(PositionRequest request, TriggerContext trigger, Utf8JsonWriter writer)
    {
        var index = BuildIndex(request.Text);

        // 先计数，命中 0 条直接返回（无结果路径零分配）
        var documentMatches = 0;
        foreach (var name in index.VariablesToType.Keys)
        {
            if (Matches(name, trigger.Prefix))
            {
                documentMatches++;
            }
        }

        foreach (var name in index.Structs.Keys)
        {
            if (Matches(name, trigger.Prefix))
            {
                documentMatches++;
            }
        }

        var builtinMatches = 0;
        foreach (var builtin in HlslBuiltins.All())
        {
            if (Matches(builtin.Name, trigger.Prefix))
            {
                builtinMatches++;
            }
        }

        // 引擎内置 uniform 也参与前缀补全（输入 _Ti → _Time / _TimeParameters）。
        // 本文件已声明同名变量时跳过，避免与「文档内符号」重复给出。
        var uniformMatches = 0;
        foreach (var name in HlslBuiltinUniforms.SortedAll())
        {
            if (!index.VariablesToType.ContainsKey(name) && Matches(name, trigger.Prefix))
            {
                uniformMatches++;
            }
        }

        // 宏候选在这里一次性收集（本文件 + include 闭包），下面直接用它计数与输出。
        CollectMacros(request.Uri, index, trigger.Prefix);

        if (documentMatches + builtinMatches + uniformMatches + _macroNames.Count == 0)
        {
            return false;
        }

        writer.WriteStartObject();
        writer.WriteBoolean("isIncomplete", false);
        writer.WritePropertyName("items");
        writer.WriteStartArray();

        // ① 本文件变量
        WriteDocumentNames(index.VariablesToType.Keys, trigger.Prefix, KindVariable, isStruct: false, index, writer);

        // ② 本文件结构体
        WriteDocumentNames(index.Structs.Keys, trigger.Prefix, KindStruct, isStruct: true, index, writer);

        // ③ 宏（#define）—— 排在内建之前：它们与文档符号同属「工程/语言环境」，
        //    而且这正是「UNITY_REVERSED_Z 这类名字得手敲」的直接对症项。
        WriteMacros(writer);

        // ④ 内建
        foreach (var builtin in HlslBuiltins.All())
        {
            if (!Matches(builtin.Name, trigger.Prefix))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("label", builtin.Name);
            writer.WriteNumber("kind", builtin.LspKind);
            writer.WriteString("detail", builtin.Signature);
            writer.WriteString("sortText", "1_" + builtin.Name);
            writer.WriteEndObject();
        }

        // ⑤ 引擎内置 uniform（同样排在内建函数一档：它们是引擎符号，不是作者写的符号）
        foreach (var name in HlslBuiltinUniforms.SortedAll())
        {
            if (index.VariablesToType.ContainsKey(name) || !Matches(name, trigger.Prefix))
            {
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("label", name);
            writer.WriteNumber("kind", KindVariable);
            writer.WriteString("detail", HlslBuiltinUniforms.TypeOf(name) + "（引擎内置）");
            writer.WriteString("sortText", "1_" + name);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        return true;
    }

    /// <summary>按名字字典序写出一批「本文件符号」，保证输出稳定（字典本身无序）。</summary>
    /// <summary>收集宏候选：本文件 + include 闭包。</summary>
    /// <remarks>
    /// **为什么宏要单独一条路径**：它不是变量也不是结构体，`WriteDocumentNames` 只认
    /// 「名字 → 类型」两张表；宏在 Declarations 里与变量/结构体混在一起，得按
    /// `HlslDeclKind.Macro` 过滤。
    ///
    /// **为什么必须带 include 闭包**：Unity/URP 的宏（`UNITY_REVERSED_Z`、
    /// `SAMPLE_TEXTURE2D_SHADOW` …）几乎全定义在包内头文件里。只给本文件的宏
    /// 等于没解决「宏补不出来」。闭包数据直接取自 include 符号缓存（预热器与本地兜底
    /// 共用的那一份），本路径不额外读盘；缓存还没预热好时闭包为空，下次请求就有了。
    ///
    /// **本版不做宏可见性求值**（`#undef`、条件编译一律忽略）。真值需要按 include 顺序
    /// 对 `#define`/`#undef` 事件流求值（详设 v2.0 第 6、7 条），是独立的一块；
    /// 这里宁可多给也不漏给 —— 漏给的症状正是「宏得手敲」。
    /// </remarks>
    private void CollectMacros(string uri, HlslDocumentIndex index, string prefix)
    {
        _macroNames.Clear();
        _macroSeen.Clear();

        CollectMacrosFrom(index.Declarations, index, prefix);

        if (_includeSymbols?.Cache is { } cache && cache.TryGetDocumentFiles(uri, out var files))
        {
            foreach (var file in files)
            {
                if (!cache.TryGetFileByPath(file, out var symbols))
                {
                    continue;
                }

                CollectMacrosFrom(symbols.Declarations, index, prefix);
            }
        }
    }

    private void CollectMacrosFrom(
        IReadOnlyDictionary<string, List<HlslDecl>> declarations,
        HlslDocumentIndex index,
        string prefix)
    {
        foreach (var pair in declarations)
        {
            if (!IsMacroDeclaration(pair.Value))
            {
                continue;
            }

            // 与文档变量/结构体同名的宏跳过，避免同一个 label 出现两次。
            if (index.VariablesToType.ContainsKey(pair.Key) || index.Structs.ContainsKey(pair.Key))
            {
                continue;
            }

            if (!Matches(pair.Key, prefix) || !_macroSeen.Add(pair.Key))
            {
                continue;
            }

            _macroNames.Add(pair.Key);
        }
    }

    /// <summary>该名字是否存在宏声明（同名可能同时有函数/变量声明，需要逐个看）。</summary>
    private static bool IsMacroDeclaration(List<HlslDecl> declarations)
    {
        for (var i = 0; i < declarations.Count; i++)
        {
            if (declarations[i].Kind == HlslDeclKind.Macro)
            {
                return true;
            }
        }

        return false;
    }

    private void WriteMacros(Utf8JsonWriter writer)
    {
        if (_macroNames.Count == 0)
        {
            return;
        }

        _macroNames.Sort(StringComparer.Ordinal);

        foreach (var name in _macroNames)
        {
            writer.WriteStartObject();
            writer.WriteString("label", name);
            writer.WriteNumber("kind", KindConstant);
            writer.WriteString("detail", "宏（#define）");
            writer.WriteString("sortText", "0_" + name);
            writer.WriteEndObject();
        }
    }

    private void WriteDocumentNames(
        IEnumerable<string> names,
        string prefix,
        int kind,
        bool isStruct,
        HlslDocumentIndex index,
        Utf8JsonWriter writer)
    {
        _nameBuffer.Clear();
        foreach (var name in names)
        {
            if (Matches(name, prefix))
            {
                _nameBuffer.Add(name);
            }
        }

        if (_nameBuffer.Count == 0)
        {
            return;
        }

        _nameBuffer.Sort(StringComparer.Ordinal);

        foreach (var name in _nameBuffer)
        {
            writer.WriteStartObject();
            writer.WriteString("label", name);
            writer.WriteNumber("kind", kind);
            writer.WriteString(
                "detail",
                isStruct
                    ? "struct（本文件）"
                    : index.VariablesToType.TryGetValue(name, out var type) ? type : "本文件");
            writer.WriteString("sortText", "0_" + name);
            writer.WriteEndObject();
        }
    }

/// <summary>"textDocument/hover" 的实现。</summary>
    public bool ProvideHover(PositionRequest request, Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var word = WordAt(request.Text, request.Line, request.Character);
        if (word.Length == 0)
        {
            return false;
        }

        var markdown = Describe(request.Text, word);
        if (markdown is null)
        {
            return false;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("contents");
        writer.WriteStartObject();
        writer.WriteString("kind", "markdown");
        writer.WriteString("value", markdown);
        writer.WriteEndObject();
        writer.WriteEndObject();
        return true;
    }

    /// <summary>Markdown 代码围栏（三个反引号）。用 (char)96 拼出，避免源码里出现反引号带来的转义风险。</summary>
    private static readonly string Fence = new string((char)96, 3);

    private static readonly string NewLine = ((char)10).ToString();

    private string? Describe(string text, string word)
    {
        var index = BuildIndex(text);

        if (index.Structs.TryGetValue(word, out var structDecl))
        {
            var sb = new StringBuilder();
            sb.Append(Fence).Append("hlsl").Append(NewLine).Append("struct ").Append(word).Append(NewLine).Append('{').Append(NewLine);
            foreach (var field in structDecl.Fields)
            {
                sb.Append("    ").Append(field.Type).Append(' ').Append(field.Name).Append(';').Append(NewLine);
            }

            sb.Append('}').Append(';').Append(NewLine).Append(Fence);
            return sb.ToString();
        }

        if (index.VariablesToType.TryGetValue(word, out var typeName))
        {
            return Fence + "hlsl" + NewLine + typeName + " " + word + NewLine + Fence;
        }

        foreach (var builtin in HlslBuiltins.All())
        {
            if (builtin.Name.Equals(word, StringComparison.Ordinal))
            {
                return Fence + "hlsl" + NewLine + builtin.Signature + NewLine + Fence + NewLine + NewLine + builtin.Description;
            }
        }

        return null;
    }

    /// <summary>取光标处的完整标识符（悬停目标）。</summary>
    private static string WordAt(string text, int line, int character)
    {
        var lineStart = 0;
        var currentLine = 0;
        while (currentLine < line && lineStart < text.Length)
        {
            var nl = text.IndexOf((char)10, lineStart);
            if (nl < 0)
            {
                return string.Empty;
            }

            lineStart = nl + 1;
            currentLine++;
        }

        if (currentLine != line)
        {
            return string.Empty;
        }

        var cursor = Math.Min(text.Length, lineStart + character);
        var start = cursor;
        var end = cursor;
        while (start > lineStart && IsIdentifierChar(text[start - 1]))
        {
            start--;
        }

        while (end < text.Length && IsIdentifierChar(text[end]))
        {
            end++;
        }

        return text[start..end];
    }

/// <summary>一个代码块在源文本中的位置与内容切片。</summary>
    private readonly struct BlockSpan(int offset, ReadOnlyMemory<char> text)
    {
        public int Offset { get; } = offset;

        public ReadOnlyMemory<char> Text { get; } = text;
    }

    /// <summary>按全文构造 HLSL 符号索引（只覆盖本文件的代码块）。</summary>
    /// <remarks>
    /// "R2 · 合并顺序必须等于文本顺序"：HLSLINCLUDE 写在文件里 Pass "之前"，而扁平表是
    /// last-write-wins，因此必须按 "ContentStartOffset" 升序合并。旧实现写死「先 Pass 后 Shared」，
    /// 于是文本上更早的 HLSLINCLUDE 声明反而覆盖了更晚的 Pass 内声明 —— 真实语料实测：
    /// frag 形参 "i"（应为 VertexOutput）被 HLSLINCLUDE 里的 "GetAdditionalLight(i, …)"
    /// 覆盖成 "i → GetAdditionalLight"，成员补全直接归零。
    /// "R6 · 两级缓存"：① 单块索引按"内容"缓存（哈希 + 逐字符精确比对，绝不误命中）；
    /// ② 若本次的块索引"实例序列"与上次完全一致（引用相等），直接复用上次的合并结果，
    /// 跳过 O(声明总数) 的逐块合并。含义：改 "Properties" 不动 HLSL 块、或同一文本上重复请求
    /// （hover 后紧接补全、Ctrl+Space 连按）都不再重建索引。改 HLSL 块本身仍需重建该块。
    /// "净效果"：450 KB 单块的 "Build" 约 8.2 ms / 7.2 MB 分配，
    /// 缓存命中后只剩哈希比对（Parse + 块构建全跳过）。
    /// "三层缓存"：① 全文哈希缓存跳过 Parse；② 单块内容哈希缓存跳过 HlslDocumentIndex.Build；
    /// ③ 块索引实例序列引用相等跳过 Merge。
    /// </remarks>
    private HlslDocumentIndex BuildIndex(string text)
    {
        // 全文哈希缓存：文本不变时跳过 ShaderLabStateMachine.Parse（全文行扫描）。
        // 哈希碰撞由文本长度 + 后续逐块内容比对兜底，不会误命中。
        var textHash = HlslBlockIndexCache.HashOf(text.AsSpan());
        if (textHash != _parsedTextHash || text.Length != _parsedTextLength)
        {
            // machine.Parse 内部会调 into.Reset()，不需要手动清。
            _machine.Parse(_options.DocumentPath, text, _parseResult);
            _parsedTextHash = textHash;
            _parsedTextLength = text.Length;
        }

        CollectBlocksInTextOrder();

        if (_blockBuffer.Count == 0 && _parseResult.SubShaderCount == 0)
        {
            // 纯 HLSL / Cg 文件（.hlsl、.cginc、被 include 的头文件）里没有任何 ShaderLab 标记，
            // 切片器不会产出 Pass 或共享块。不兜底的话索引就是空的 —— 表现为
            // 「文件里明明声明过的变量，一条补全都不出」，而且看起来像工具没生效。
            // 把整篇当单块：BuildIndex 收到的就是原文，偏移 0 与文档坐标一致。
            //
            // 为什么用 SubShaderCount == 0 而不是只判 Count == 0：真实的 .shader 文件
            // 即使没有 program block、甚至块未闭合，也一定检测得到 SubShader。那些文件
            // 不该按 HLSL 索引 —— 否则会把 ShaderLab 关键字当成 HLSL 符号喂给补全与跳转。
            _blockBuffer.Add(new BlockSpan(0, text.AsMemory()));
        }

        _indexBuffer.Clear();
        foreach (var block in _blockBuffer)
        {
            _indexBuffer.Add(GetOrBuildBlockIndex(block.Text.Span));
        }

        if (TryReuseMerged())
        {
            return _memoMerged!;
        }

        var merged = new HlslDocumentIndex();
        foreach (var index in _indexBuffer)
        {
            Merge(merged, index);
        }

        _memoMerged = merged;
        _memoBlocks = _indexBuffer.ToArray();
        return merged;
    }

    /// <summary>把 Pass 块与共享块按文件内出现顺序排好（R2）。</summary>
    private void CollectBlocksInTextOrder()
    {
        _blockBuffer.Clear();

        foreach (var snippet in _parseResult.Passes)
        {
            _blockBuffer.Add(new BlockSpan(snippet.ContentStartOffset, snippet.RawHlslBlock));
        }

        foreach (var shared in _parseResult.SharedSnippets)
        {
            _blockBuffer.Add(new BlockSpan(shared.ContentStartOffset, shared.RawHlslBlock));
        }

        _blockBuffer.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));
    }

    /// <summary>按内容取单块索引：命中缓存直接返回，否则构建并缓存（R6）。</summary>
    /// <remarks>
    /// "哈希桶查找"：旧实现 foreach 遍历最多 16 个条目做哈希比对 + 逐字符 SequenceEqual。
    /// 改为按哈希值分桶的字典查找——O(1) 命中，省掉线性遍历。碰撞仍由逐字符比对兜底。
    /// </remarks>
    /// <summary>按内容取单块索引：命中共享缓存直接返回，否则构建并缓存。</summary>
    /// <remarks>
    /// 缓存本体已提升为共享组件（HlslBlockIndexCache）：补全、大纲、F12 共用一份 ——
    /// 大纲每次请求都要遍历全部块，若各自重建，代价是 O(块数 × 单块构建)。
    /// </remarks>
    private HlslDocumentIndex GetOrBuildBlockIndex(ReadOnlySpan<char> span)
        => _blockIndexCache.GetOrBuild(span);

    /// <summary>上一次的块索引实例序列是否与本次完全一致（引用相等 ⇒ 内容与顺序均一致）。</summary>
    private bool TryReuseMerged()
    {
        if (_memoMerged is null || _memoBlocks.Length != _indexBuffer.Count)
        {
            return false;
        }

        for (var i = 0; i < _memoBlocks.Length; i++)
        {
            if (!ReferenceEquals(_memoBlocks[i], _indexBuffer[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 把多个代码块（HLSLPROGRAM / CGPROGRAM / 共享块）各自的索引合成一份。
    /// </summary>
    /// <remarks>
    /// 详设 §六.4 要求「CGPROGRAM 与 HLSLPROGRAM 之间存在符号隔离墙」。本版"刻意不做隔离"：
    /// 同一文件里两块同时存在的场景极罕见，而隔离需要为每个块维护独立作用域与 BlockScopeId
    /// （并把补全请求映射到所属块），成本远大于收益。当前行为是「并集」——如实标注为已知偏差，
    /// 待真实语料出现该场景后再补隔离。
    /// </remarks>
    private static void Merge(HlslDocumentIndex into, HlslDocumentIndex from)
    {
        foreach (var pair in from.VariablesToType)
        {
            into.SetVariable(pair.Key, pair.Value);
        }

        foreach (var pair in from.Structs)
        {
            var existing = into.GetOrAddStruct(pair.Key);
            foreach (var field in pair.Value.Fields)
            {
                if (!existing.Fields.Contains(field))
                {
                    existing.Fields.Add(field);
                }
            }
        }

        // 声明表也必须合并：宏只存在于这张表里（VariablesToType 与 Structs 都不含它），
        // 不合并的话「整篇文档索引」里一个宏都没有 —— 本文件写的 #define 永远补不出来。
        // 逐条去重：同名声明本就允许 1→N（重载），但重复项会让 F12 候选与文档大纲出现重条。
        foreach (var pair in from.Declarations)
        {
            if (!into.Declarations.TryGetValue(pair.Key, out var target))
            {
                foreach (var decl in pair.Value)
                {
                    into.AddDeclaration(decl);
                }

                continue;
            }

            foreach (var decl in pair.Value)
            {
                if (!target.Contains(decl))
                {
                    into.AddDeclaration(decl);
                }
            }
        }
    }

    private static bool Matches(string candidate, string prefix) =>
        prefix.Length == 0 || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}

/// <summary>IntelliSense 可调参数。</summary>
public sealed class IntelliSenseOptions
{
    /// <summary>伪文档路径（模块 1 切片器要求一个路径才能生成 TargetUri）。</summary>
    public string DocumentPath { get; init; } = "<intellisense>";
}


