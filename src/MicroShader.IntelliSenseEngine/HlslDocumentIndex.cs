using System.Collections.Frozen;
using System.Text;

namespace MicroShader.IntelliSenseEngine;

/// <summary>一个结构体字段。</summary>
public readonly record struct HlslField(string Type, string Name, int Offset = 0);

/// <summary>一个结构体声明。</summary>
public sealed class HlslStruct
{
    public required string Name { get; init; }

    /// <summary>结构体名在原文中的字符偏移（用于导航定位）。</summary>
    public int NameOffset { get; init; }

    /// <summary>字段（按出现顺序，已去重）。</summary>
    public List<HlslField> Fields { get; } = new(8);
}

/// <summary>
/// 单篇文档的 HLSL 符号索引：结构体定义 + 「变量/参数/字段 → 类型」声明表。
/// </summary>
/// <remarks>
/// "为什么不做 AST"：补全必须能在「代码正写到一半」时给出结果（缺分号、括号不配对、
/// 结构体改到一半）。完整 AST 在这时只会整体失败。<see cref="Build"/> 因此是"容错分词"。
/// "2026-09-26 真实语料回归后的规则重写"：旧实现只认「相邻的 类型 token + 标识符」这一条，
/// 于是把"函数/宏调用的实参"也登记成了变量（"GetAdditionalLight(i, positionWS)" 让
/// "i → GetAdditionalLight"）。用户真实手写 shader 实测：79 条登记里只有 2 条是真结构体字段，
/// 41 条是这类误登记 —— 扁平表又是 last-write-wins，真实声明被整体覆盖，成员补全几乎全归零。
/// 现在改为一个"极小的声明状态机"，四条关键规则：
/// <list type="number">
/// <item>"类型后面紧跟左括号 → 那是函数定义或调用，不是变量声明"（R1）。</item>
/// <item>"冒号语义标注整段跳过"（": SV_POSITION" / ": register(t0)"）。
/// 旧实现会把标注里的 "SV_POSITION" 当成类型、把紧随其后的 "half3" 当成字段名（R3）。</item>
/// <item>"尖括号模板实参算类型的一部分"："StructuredBuffer&lt;Varyings&gt; gBuf;"
/// 登记成 "gBuf → StructuredBuffer"，且 "Varyings" 不再被登记成变量（R5）。</item>
/// <item>"声明符列表"："T a, b, c;" 三个都登记（旧实现只登记第一个）。</item>
/// </list>
/// "唯一刻意保留的「宏声明式」例外"：Unity 的 "TEXTURE2D(_BaseTexture);" /
/// "SAMPLER(sampler_BaseTexture);" 在语法上就是一次宏调用。规则 1 会把它们一并丢掉，
/// 而这两个名字是作者天天要补全的。因此额外识别一种窄形态：
/// "全大写宏名 + 括号内恰好一个纯标识符 + 右括号后紧跟分号或逗号"
/// → 登记「实参 → 宏名」。多实参的宏（"SAMPLE_TEXTURE2D_LOD(...)"）不会被误判。
/// "已知边界（如实标注）"：这是 "AST-less 启发式"（详设 v2.0 第 10 条）——
/// 类型定义在 include 的头文件里时，本索引看不到该结构体，此时成员补全会返回「无结果」而不是猜。
/// 检索 include 依赖树的成本远超 5ms 配额，故明确降级。
/// </remarks>
public sealed class HlslDocumentIndex
{
    /// <summary>HLSL 关键字与声明修饰符：既不是类型名，也不是变量名。</summary>
    /// <remarks>
    /// "static" / "const" / "uniform" / "in" / "out" / "inout" 等修饰符
    /// 必须在这里，否则 "out TOutput output" 这种形参会因为 "out" 被当成类型而解错。
    /// </remarks>
    private static readonly FrozenSet<string> Keywords = new[]
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue",
        "return", "discard", "goto", "struct", "class", "enum", "namespace", "typedef", "using",
        "static", "const", "uniform", "volatile", "inline", "precise", "groupshared", "shared",
        "extern", "export", "in", "out", "inout", "linear", "centroid", "nointerpolation",
        "noperspective", "sample", "snorm", "unorm", "row_major", "column_major",
        "true", "false", "new", "delete", "this", "sizeof", "cbuffer", "tbuffer",

        // Unity 的 cbuffer 宏：它们是宏而不是类型。若被当成类型，紧随其后的
        // StructuredBuffer<float3> _Xxx; 会被吃成「名字」，整条声明就丢了（实测过）。
        "CBUFFER_START", "CBUFFER_END",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>关键字的「替身查找」：用 span 直接查，避免对每个 token 都 ToString 分配。</summary>
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> KeywordLookup =
        Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Unity 的「声明式宏」白名单："TEXTURE2D(_Tex);" / "SAMPLER(sampler_Tex);" 这类
    /// 语法上是宏调用、语义上却是声明的写法。
    /// </summary>
    /// <remarks>
    /// "刻意用白名单而不是「全大写即声明宏」"：URP 里 "UNITY_SETUP_INSTANCE_ID(input);" /
    /// "UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);" 同样是「全大写 + 单个标识符实参 + 分号」的形态，
    /// 一旦按形态匹配就会把形参 "input" 覆盖成 "UNITY_SETUP_INSTANCE_ID" ——
    /// 正是本轮要修的那一类误登记。白名单之外一律不猜。
    /// </remarks>
    private static readonly FrozenSet<string> DeclarationMacros = new[]
    {
        "TEXTURE2D", "TEXTURE2D_ARRAY", "TEXTURE2DMS", "TEXTURE2DMS_ARRAY",
        "TEXTURE3D", "TEXTURECUBE", "TEXTURECUBE_ARRAY",
        "TEXTURE2D_X", "TEXTURE2D_X_FLOAT", "SAMPLER", "SAMPLER_X",
        "UNITY_DECLARE_TEX2D", "UNITY_DECLARE_TEX2DARRAY", "UNITY_DECLARE_TEXCUBE",
        "UNITY_DECLARE_TEX2D_NOSAMPLER",
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> DeclarationMacroLookup =
        DeclarationMacros.GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly Dictionary<string, HlslStruct> _structs = new(32, StringComparer.Ordinal);
    private readonly Dictionary<string, string> _variablesToType = new(64, StringComparer.Ordinal);

    // 名字 → 全部声明，1 对多。HLSL/URP 的重载与同名极多（实测函数名跨文件重复 41/984、宏名 177/1162），
    // 若压成 1 对 1，F12 必然跳错重载，所以这里保留全部候选，由导航侧按距离与 include 顺序排序。
    private readonly Dictionary<string, List<HlslDecl>> _declarations = new(64, StringComparer.Ordinal);

    // 类型名字符串池：同一 Build 调用内避免为重复类型名（float4/half3 等）创建多个 string 对象。
    private readonly Dictionary<string, string> _typePool = new(64, StringComparer.Ordinal);

    // 宏可见性事件流：#define 与 #undef 按出现顺序记录。跨文件合并必须按 include 顺序连 #undef 一起求值，
    // 不能只存每个文件的最终状态——同一文件里可能是 定义A、取消A、再定义A。
    private readonly List<HlslMacroOp> _macroOps = new(256);

    public IReadOnlyDictionary<string, HlslStruct> Structs => _structs;

    public IReadOnlyDictionary<string, string> VariablesToType => _variablesToType;

    /// <summary>
    /// 名字 → 全部声明。补全不消费它，F12 导航与文档大纲消费。
    /// </summary>
    public IReadOnlyDictionary<string, List<HlslDecl>> Declarations => _declarations;

    /// <summary>宏可见性事件流（#define / #undef，按出现顺序）。</summary>
    public IReadOnlyList<HlslMacroOp> MacroOps => _macroOps;

    /// <summary>登记一条符号声明（1 对多追加）。</summary>
    internal void AddDeclaration(HlslDecl decl)
    {
        if (!_declarations.TryGetValue(decl.Name, out var list))
        {
            list = [];
            _declarations[decl.Name] = list;
        }

        list.Add(decl);
    }

    /// <summary>登记一条宏事件（#define 或 #undef）。</summary>
    internal void AddMacroOp(HlslMacroOp op) => _macroOps.Add(op);

    /// <summary>合并用：把一个文件的宏事件流按顺序并入本索引。</summary>
    internal void ApplyMacroOps(IReadOnlyList<HlslMacroOp> ops)
    {
        foreach (var op in ops)
        {
            _macroOps.Add(op);
        }
    }

    /// <summary>合并用：写入/覆盖一条「标识符 → 类型」。</summary>
    internal void SetVariable(string name, string type) => _variablesToType[name] = type;

    /// <summary>合并用：取或新建一个结构体条目；后到的带位置条目负责补齐偏移。</summary>
    internal HlslStruct GetOrAddStruct(string name, int nameOffset = 0)
    {
        if (!_structs.TryGetValue(name, out var found))
        {
            found = new HlslStruct { Name = name, NameOffset = nameOffset };
            _structs[name] = found;
        }
        else if (found.NameOffset == 0 && nameOffset > 0)
        {
            // 跨文件合并时先到的可能是没带位置的合并副本，这里把位置补上。
            var merged = new HlslStruct { Name = found.Name, NameOffset = nameOffset };
            merged.Fields.AddRange(found.Fields);
            _structs[name] = merged;
        }

        return _structs[name];
    }

    /// <summary>解析 HLSL 片段里的结构体与变量声明。</summary>
    public static HlslDocumentIndex Build(ReadOnlySpan<char> hlsl)
    {
        var index = new HlslDocumentIndex();
        var sink = new DocumentSink(index);
        ScanDeclarations(hlsl, 0, ref sink);
        return index;
    }

    // ───────────────────────────── 声明接收端 ─────────────────────────────

    /// <summary>声明扫描器的输出端。两种消费者：整篇文档索引、单个结构体的字段表。</summary>
    private interface IDeclarationSink
    {
        /// <summary>该 token 是否可以充当类型名（内建类型，或本文件已声明的结构体名）。</summary>
        bool IsTypeName(ReadOnlySpan<char> token);

        /// <summary>在指定深度层登记一条声明。nameOffset 是名字 token 在文本里的绝对偏移。</summary>
        void OnDeclaration(int depth, ReadOnlySpan<char> type, ReadOnlySpan<char> name, int nameOffset);

        /// <summary>登记一个函数定义（类型名后紧跟左括号且不是调用）。arity 为形参个数。</summary>
        void OnFunction(int depth, ReadOnlySpan<char> returnType, ReadOnlySpan<char> name, int nameOffset, int arity);

        /// <summary>发现一个结构体定义，给出其名、名偏移与花括号内正文。</summary>
        void OnStructBody(ReadOnlySpan<char> name, int nameOffset, ReadOnlySpan<char> body);

        /// <summary>发现一条 #define 宏（只由整篇文档接收端收集）。</summary>
        void OnMacro(HlslMacro macro);

        /// <summary>发现一条宏可见性事件（#define 或 #undef）。</summary>
        void OnMacroOp(HlslMacroOp op);
    }

    /// <summary>整篇文档的接收端：变量表 + 结构体表（结构体正文单独递归扫描）。</summary>
    private struct DocumentSink(HlslDocumentIndex index) : IDeclarationSink
    {
        private readonly HlslDocumentIndex _index = index;

        private readonly Dictionary<string, HlslStruct>.AlternateLookup<ReadOnlySpan<char>> _structs =
            index._structs.GetAlternateLookup<ReadOnlySpan<char>>();

        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _typePool =
            index._typePool.GetAlternateLookup<ReadOnlySpan<char>>();
        public bool IsTypeName(ReadOnlySpan<char> token)
        {
            if (HlslTypes.LooksLikeType(token))
            {
                return true;
            }

            // 小写开头的自定义结构体（v2f / appdata / fragOutput）。用替身查找避免逐 token 分配字符串。
            return _index._structs.Count > 0 && _structs.ContainsKey(token);
        }

        public void OnDeclaration(int depth, ReadOnlySpan<char> type, ReadOnlySpan<char> name, int nameOffset)
        {
            var nameText = name.ToString();
            if (!_typePool.TryGetValue(type, out var typeText))
            {
                typeText = type.ToString();
                _index._typePool[typeText] = typeText;
            }

            _index._variablesToType[nameText] = typeText;
            _index.AddDeclaration(new HlslDecl(HlslDeclKind.Variable, nameText, typeText, nameOffset, name.Length));
        }

        // 函数定义只进声明表，绝不进变量表——那是 R1 修掉的那类误登记
        // （GetAdditionalLight(i, ...) 曾让形参 i 的类型绑定被函数名覆盖）。
        public void OnFunction(int depth, ReadOnlySpan<char> returnType, ReadOnlySpan<char> name, int nameOffset, int arity)
        {
            if (!_typePool.TryGetValue(returnType, out var retType))
            {
                retType = returnType.ToString();
                _index._typePool[retType] = retType;
            }
            _index.AddDeclaration(new HlslDecl(
                HlslDeclKind.Function,
                name.ToString(),
                retType,
                nameOffset,
                name.Length,
                arity));
        }

        public void OnStructBody(ReadOnlySpan<char> name, int nameOffset, ReadOnlySpan<char> body)
        {
            if (name.IsEmpty)
            {
                return;
            }

            var structName = name.ToString();
            var declared = _index.GetOrAddStruct(structName, nameOffset);
            _index.AddDeclaration(new HlslDecl(HlslDeclKind.Struct, structName, "struct", nameOffset, name.Length));

            var fieldSink = new FieldSink(declared, _index);

            // 正文从结构体自己的第一层开始：字段永远出现在 depth == 1。
            ScanDeclarations(body, 1, ref fieldSink);
        }

        public void OnMacro(HlslMacro macro)
        {
            _index.AddDeclaration(new HlslDecl(
                HlslDeclKind.Macro, macro.Name, macro.Body, macro.Offset, macro.Length, macro.Arity));
        }

        public void OnMacroOp(HlslMacroOp op) => _index.AddMacroOp(op);
    }

    /// <summary>单个结构体正文的接收端：只收 depth == 1 的字段。</summary>
    private struct FieldSink(HlslStruct target, HlslDocumentIndex index) : IDeclarationSink
    {
        private readonly HlslStruct _target = target;
        private readonly HlslDocumentIndex _index = index;

        private readonly Dictionary<string, HlslStruct>.AlternateLookup<ReadOnlySpan<char>> _structs =
            index._structs.GetAlternateLookup<ReadOnlySpan<char>>();

        private readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> _typePool =
            index._typePool.GetAlternateLookup<ReadOnlySpan<char>>();
        public bool IsTypeName(ReadOnlySpan<char> token)
        {
            if (HlslTypes.LooksLikeType(token))
            {
                return true;
            }

            return _index._structs.Count > 0 && _structs.ContainsKey(token);
        }

        public void OnDeclaration(int depth, ReadOnlySpan<char> type, ReadOnlySpan<char> name, int nameOffset)
        {
            if (depth != 1)
            {
                return;
            }

            var fieldName = name.ToString();
            foreach (var existing in _target.Fields)
            {
                if (existing.Name.Equals(fieldName, StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (!_typePool.TryGetValue(type, out var fieldType))
            {
                fieldType = type.ToString();
                _index._typePool[fieldType] = fieldType;
            }
            var field = new HlslField(fieldType, fieldName, nameOffset);
            _target.Fields.Add(field);
            _index.AddDeclaration(new HlslDecl(HlslDeclKind.Field, fieldName, field.Type, nameOffset, name.Length));
        }

        // 结构体正文里的函数不收集：字段表只收字段。
        public void OnFunction(int depth, ReadOnlySpan<char> returnType, ReadOnlySpan<char> name, int nameOffset, int arity)
        {
        }

        /// <summary>结构体正文里的嵌套结构体不参与字段收集（与旧实现一致）。</summary>
        public void OnStructBody(ReadOnlySpan<char> name, int nameOffset, ReadOnlySpan<char> body)
        {
        }

        // 宏不可能定义在结构体体内，这里不收集。
        public void OnMacro(HlslMacro macro)
        {
        }

        public void OnMacroOp(HlslMacroOp op)
        {
        }
    }

// ───────────────────────────── 声明扫描器 ─────────────────────────────

    private enum DeclState
    {
        /// <summary>不在声明中。</summary>
        Idle = 0,

        /// <summary>刚读到类型名，下一个标识符就是被声明的名字。</summary>
        AfterType = 1,

        /// <summary>刚登记完一个名字，可能出现「语义标注 / 数组 / 初值 / 下一个声明符」。</summary>
        AfterName = 2,

        /// <summary>刚读到声明符列表里的逗号：下一个标识符可能是新类型，也可能是同类型的下一个名字。</summary>
        AfterComma = 3,
    }

    /// <summary>
    /// 容错声明扫描：单趟线性推进，遇到任何无法理解的字符都跳过并继续。
    /// </summary>
    /// <param name="hlsl">待扫描的 HLSL 片段。</param>
    /// <param name="baseDepth">起始花括号深度（结构体正文从 1 开始）。</param>
    /// <param name="sink">声明接收端。</param>
    private static void ScanDeclarations<TSink>(ReadOnlySpan<char> hlsl, int baseDepth, ref TSink sink)
        where TSink : IDeclarationSink
    {
        var i = 0;
        var depth = baseDepth;
        var state = DeclState.Idle;
        ReadOnlySpan<char> pendingType = default;

        while (i < hlsl.Length)
        {
            if (!TrySkipTrivia(hlsl, ref i) || i >= hlsl.Length)
            {
                return;
            }

            var c = hlsl[i];

            // 预处理指令整行跳过：否则 #define / #pragma 的正文会被当成声明。
            if (c == '#')
            {
                // #define / #undef 采集成宏事件流（F12 的宏别名解包依赖它）；其余指令整行跳过，
                // 否则 #define / #pragma 的正文会被当成声明。
                ScanPreprocessorDirective(hlsl, ref i, ref sink);
                continue;
            }

            if (IsIdentifierStart(c))
            {
                var start = i;
                while (i < hlsl.Length && IsIdentifierChar(hlsl[i]))
                {
                    i++;
                }

                var token = hlsl[start..i];

                if (token.SequenceEqual("struct") || token.SequenceEqual("class"))
                {
                    ScanStructDefinition(hlsl, ref i, ref sink);
                    state = DeclState.Idle;
                    pendingType = default;
                    continue;
                }

                if (KeywordLookup.Contains(token))
                {
                    state = DeclState.Idle;
                    pendingType = default;
                    continue;
                }

                switch (state)
                {
                    case DeclState.AfterType:
                        // 规则 1：类型后面直接跟左括号 → 函数定义或调用，不是变量声明。
                        // 调用（GetAdditionalLight(i, ...)）走的是下面左括号分支：那里的 token 是函数名本身，
                        // 而这里 token 是类型名之后紧跟括号的名字，即真正的函数定义。
                        var following = PeekNonTrivia(hlsl, i);
                        if (following >= 0 && hlsl[following] == '(')
                        {
                            // 只进声明表供 F12 用，绝不进变量表。
                            sink.OnFunction(depth, pendingType, token, start, CountParameters(hlsl, following));
                            state = DeclState.Idle;
                            pendingType = default;
                            continue;
                        }

                        // 类型名后面跟点号 → 这是表达式里的成员访问（如 OutMinMax.x），不是声明。
                        // 没有这条时，像 float Remap(float In, float2 InMinMax, ...) 这种
                        // 大写形参会让 InMinMax.x 里的 x 被登记成类型 InMinMax 的变量。
                        if (following >= 0 && hlsl[following] == '.')
                        {
                            state = DeclState.Idle;
                            pendingType = default;
                            continue;
                        }

                        sink.OnDeclaration(depth, pendingType, token, start);
                        state = DeclState.AfterName;
                        continue;

                    case DeclState.AfterName:
                        // 表达式里又冒出一个标识符（如相邻两个名字）→ 与声明无关。
                        state = DeclState.Idle;
                        pendingType = default;
                        continue;

                    case DeclState.AfterComma:
                        // 逗号后的标识符：后面还跟着一个标识符 → 它是新类型；否则是同类型的下一个名字。
                        if (sink.IsTypeName(token) && NextIsIdentifier(hlsl, i))
                        {
                            pendingType = token;
                            state = DeclState.AfterType;
                            continue;
                        }

                        sink.OnDeclaration(depth, pendingType, token, start);
                        state = DeclState.AfterName;
                        continue;

                    default:
                        if (sink.IsTypeName(token))
                        {
                            pendingType = token;
                            state = DeclState.AfterType;
                        }
                        else
                        {
                            state = DeclState.Idle;
                            pendingType = default;
                        }

                        continue;
                }
            }

            switch (c)
            {
                case '{':
                    depth++;
                    state = DeclState.Idle;
                    pendingType = default;
                    i++;
                    continue;

                case '}':
                    depth--;
                    state = DeclState.Idle;
                    pendingType = default;
                    i++;
                    continue;

                case '(':
                    // 类型名紧跟左括号：可能是 Unity 的声明式宏（TEXTURE2D(_Tex); / SAMPLER(sampler_Tex);），
                    // 也可能是普通调用（GetAdditionalLight(i, …) 的实参一律不登记）。
                    if (state == DeclState.AfterType)
                    {
                        var macroOpen = i;
                        if (TryRegisterMacroDeclaration(hlsl, depth, pendingType, ref macroOpen, ref sink))
                        {
                            i = macroOpen;
                            state = DeclState.Idle;
                            pendingType = default;
                            continue;
                        }
                    }

                    // 进入形参/实参列表：内部按普通声明继续扫（形参本身就是变量）。
                    state = DeclState.Idle;
                    pendingType = default;
                    i++;
                    continue;

                case ')':
                case ']':
                case '>':
                    state = DeclState.Idle;
                    pendingType = default;
                    i++;
                    continue;

                case ',':
                    state = state == DeclState.AfterName ? DeclState.AfterComma : DeclState.Idle;
                    i++;
                    continue;

                case ';':
                    state = DeclState.Idle;
                    pendingType = default;
                    i++;
                    continue;

                case ':':
                    // 规则 2：语义标注整段跳过（SV_POSITION / COLOR / register(t0)）。
                    state = SkipSemanticAnnotation(hlsl, ref i) == ','
                        ? DeclState.AfterComma
                        : DeclState.Idle;
                    pendingType = default;
                    continue;

                case '=':
                    if (state is DeclState.AfterName or DeclState.AfterComma)
                    {
                        state = SkipInitializer(hlsl, ref i) == ','
                            ? DeclState.AfterComma
                            : DeclState.Idle;
                        continue;
                    }

                    i++;
                    continue;

                case '<':
                    if (state == DeclState.AfterType)
                    {
                        // 规则 3：模板实参属于类型的一部分，pendingType 保持外层类型名。
                        i = SkipTemplateArguments(hlsl, i);
                        continue;
                    }

                    i++;
                    continue;

                case '[':
                    i = SkipBrackets(hlsl, i);
                    continue;

                case '*':
                case '&':
                    i++;   // 指针/引用修饰：不改变当前状态
                    continue;

                default:
                    i++;
                    continue;
            }
        }
    }

/// <summary>扫描结构体定义：交出名字与正文，并把游标推到定义之后。</summary>
    private static void ScanStructDefinition<TSink>(ReadOnlySpan<char> hlsl, ref int i, ref TSink sink)
        where TSink : IDeclarationSink
    {
        var p = PeekNonTrivia(hlsl, i);
        ReadOnlySpan<char> name = default;
        var nameOffset = 0;

        if (p >= 0 && IsIdentifierStart(hlsl[p]))
        {
            var end = p;
            while (end < hlsl.Length && IsIdentifierChar(hlsl[end]))
            {
                end++;
            }

            name = hlsl[p..end];
            nameOffset = p;
            i = end;
        }

        // 跳过继承列表与语义标注，找花括号
        var q = i;
        while (true)
        {
            if (!TrySkipTrivia(hlsl, ref q) || q >= hlsl.Length)
            {
                i = hlsl.Length;
                return;
            }

            if (hlsl[q] == '{')
            {
                break;
            }

            if (hlsl[q] == ';')
            {
                i = q + 1;   // 前置声明：无正文
                return;
            }

            q++;
        }

        var bodyStart = q + 1;
        var bodyEnd = MatchBrace(hlsl, q);
        sink.OnStructBody(name, nameOffset, hlsl[bodyStart..Math.Min(bodyEnd, hlsl.Length)]);
        i = Math.Min(hlsl.Length, bodyEnd + 1);
    }

    /// <summary>
    /// 规则 1 的窄例外：识别 "TEXTURE2D(_BaseTexture);" / "SAMPLER(sampler_BaseTexture);"
    /// 这种「宏名 + 单个标识符实参 + 后随分号或逗号」的 Unity 声明式写法。
    /// </summary>
    /// <param name="open">入参为左括号位置，命中时被推到右括号之后。</param>
    /// <returns>是否命中并已登记。</returns>
    private static bool TryRegisterMacroDeclaration<TSink>(
        ReadOnlySpan<char> hlsl,
        int depth,
        ReadOnlySpan<char> macroName,
        ref int open,
        ref TSink sink)
        where TSink : IDeclarationSink
    {
        // 只有白名单里的宏才按「声明」处理，形态匹配（全大写 + 单实参 + 分号）不足以判定。
        if (!DeclarationMacroLookup.Contains(macroName))
        {
            return false;
        }

        var close = MatchParen(hlsl, open);
        if (close >= hlsl.Length)
        {
            return false;
        }

        var after = PeekNonTrivia(hlsl, close + 1);
        if (after < 0 || (hlsl[after] != ';' && hlsl[after] != ','))
        {
            return false;
        }

        // 括号内必须恰好是一个纯标识符
        var argStart = PeekNonTrivia(hlsl, open + 1);
        if (argStart < 0 || argStart >= close || !IsIdentifierStart(hlsl[argStart]))
        {
            return false;
        }

        var argEnd = argStart;
        while (argEnd < hlsl.Length && IsIdentifierChar(hlsl[argEnd]))
        {
            argEnd++;
        }

        if (PeekNonTrivia(hlsl, argEnd) != close)
        {
            return false;
        }

        sink.OnDeclaration(depth, macroName, hlsl[argStart..argEnd], argStart);
        open = close + 1;
        return true;
    }

    /// <summary>跳过冒号语义标注，返回把它终止掉的那个定界符。</summary>
    private static char SkipSemanticAnnotation(ReadOnlySpan<char> hlsl, ref int i)
    {
        i++;   // 吃掉冒号
        var paren = 0;
        var bracket = 0;

        while (i < hlsl.Length)
        {
            var c = hlsl[i];

            if (c == '(')
            {
                paren++;
            }
            else if (c == ')')
            {
                if (paren == 0)
                {
                    return ')';
                }

                paren--;
            }
            else if (c == '[')
            {
                bracket++;
            }
            else if (c == ']')
            {
                if (bracket == 0)
                {
                    return ']';
                }

                bracket--;
            }
            else if (paren == 0 && bracket == 0 && (c == ';' || c == ',' || c == '{' || c == '}'))
            {
                return c;
            }

            i++;
        }

        return (char)0;
    }

    /// <summary>跳过初值表达式，返回把它终止掉的那个定界符（逗号/分号/收尾括号）。</summary>
    private static char SkipInitializer(ReadOnlySpan<char> hlsl, ref int i)
    {
        i++;   // 吃掉等号
        var depth = 0;

        while (i < hlsl.Length)
        {
            var c = hlsl[i];

            if (c is '(' or '[' or '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    return c;
                }

                depth--;
                i++;
                continue;
            }

            if (depth == 0 && (c == ';' || c == ','))
            {
                return c;
            }

            i++;
        }

        return (char)0;
    }

/// <summary>跳过尖括号模板实参，返回右尖括号之后的位置。</summary>
    private static int SkipTemplateArguments(ReadOnlySpan<char> hlsl, int i)
    {
        var depth = 0;

        while (i < hlsl.Length)
        {
            var c = hlsl[i];

            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
                i++;
                if (depth == 0)
                {
                    return i;
                }

                continue;
            }

            i++;
        }

        return hlsl.Length;
    }

    /// <summary>跳过方括号下标，返回右方括号之后的位置。</summary>
    private static int SkipBrackets(ReadOnlySpan<char> hlsl, int i)
    {
        var depth = 0;

        while (i < hlsl.Length)
        {
            var c = hlsl[i];

            if (c == '[')
            {
                depth++;
            }
            else if (c == ']')
            {
                depth--;
                i++;
                if (depth == 0)
                {
                    return i;
                }

                continue;
            }

            i++;
        }

        return hlsl.Length;
    }

    /// <summary>匹配左花括号；返回配对右花括号的位置，未闭合时返回片段长度。</summary>
    private static int MatchBrace(ReadOnlySpan<char> hlsl, int open)
    {
        var i = open + 1;
        var depth = 1;

        while (i < hlsl.Length)
        {
            if (!TrySkipTrivia(hlsl, ref i) || i >= hlsl.Length)
            {
                break;
            }

            if (hlsl[i] == '{')
            {
                depth++;
            }
            else if (hlsl[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return hlsl.Length;
    }

    /// <summary>匹配左圆括号；返回配对右圆括号的位置，未闭合时返回片段长度。</summary>
    private static int MatchParen(ReadOnlySpan<char> hlsl, int open)
    {
        var i = open;
        var depth = 0;

        while (i < hlsl.Length)
        {
            var c = hlsl[i];

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return hlsl.Length;
    }

    /// <summary>跳过空白与注释；返回下一个有效字符位置，到末尾返回 -1。</summary>
    private static int PeekNonTrivia(ReadOnlySpan<char> hlsl, int from)
    {
        var p = from;
        return TrySkipTrivia(hlsl, ref p) && p < hlsl.Length ? p : -1;
    }

    /// <summary>指定位置之后是否存在「一个标识符」且它不是关键字。</summary>
    private static bool NextIsIdentifier(ReadOnlySpan<char> hlsl, int from)
    {
        var p = PeekNonTrivia(hlsl, from);
        if (p < 0 || !IsIdentifierStart(hlsl[p]))
        {
            return false;
        }

        var end = p;
        while (end < hlsl.Length && IsIdentifierChar(hlsl[end]))
        {
            end++;
        }

        return !KeywordLookup.Contains(hlsl[p..end]);
    }

    // 扫描一条预处理指令：#define / #undef 进宏事件流，其余整行跳过。
    // 只采对象式宏的宏体：函数式宏（#define F(x) ...）的宏体不能当别名解包（详设 v2.0 第 6 条）。
    // 判定按 C 的规则：名字后紧跟左括号才算函数式，#define FOO (x) 是对象式。
    // 行尾反斜杠续行照 C 规则拼接，否则多行宏的宏体会被截断成半句。
    // 已知边界：不做条件编译求值，#if 分支内的 #define 一律照收——
    // 后果是可能把不可见的宏当可见，只影响 F12 的候选排序，不影响补全与诊断。
    private static void ScanPreprocessorDirective<TSink>(ReadOnlySpan<char> hlsl, ref int i, ref TSink sink)
        where TSink : IDeclarationSink
    {
        var head = i;

        // 找指令行的逻辑终点（处理反斜杠续行）
        var end = head;
        while (true)
        {
            var nl = hlsl[end..].IndexOf((char)10);
            if (nl < 0)
            {
                end = hlsl.Length;
                break;
            }

            var lineEnd = end + nl;
            var back = lineEnd;
            while (back > head && (hlsl[back - 1] == ' ' || hlsl[back - 1] == (char)9 || hlsl[back - 1] == (char)13))
            {
                back--;
            }

            if (back > head && hlsl[back - 1] == (char)92)
            {
                end = lineEnd + 1;
                continue;
            }

            end = lineEnd;
            break;
        }

        i = end;

        var line = hlsl[head..end];

        var p = 1;
        while (p < line.Length && (line[p] == ' ' || line[p] == (char)9))
        {
            p++;
        }

        if (StartsWithKeyword(line, p, "define"))
        {
            p += 6;
            ScanDefine(hlsl, head, line, p, ref sink);
        }
        else if (StartsWithKeyword(line, p, "undef"))
        {
            p += 5;
            while (p < line.Length && (line[p] == ' ' || line[p] == (char)9))
            {
                p++;
            }

            if (p < line.Length && IsIdentifierStart(line[p]))
            {
                var start = p;
                while (p < line.Length && IsIdentifierChar(line[p]))
                {
                    p++;
                }

                var macro = new HlslMacro(line[start..p].ToString(), string.Empty, head + start, p - start, false, 0);
                sink.OnMacro(macro);
                sink.OnMacroOp(new HlslMacroOp(true, macro));
            }
        }
    }

    // 识别 #define 的名字与宏体，p 指向 define 之后。
    private static void ScanDefine<TSink>(ReadOnlySpan<char> hlsl, int head, ReadOnlySpan<char> line, int p, ref TSink sink)
        where TSink : IDeclarationSink
    {
        while (p < line.Length && (line[p] == ' ' || line[p] == (char)9))
        {
            p++;
        }

        if (p >= line.Length || !IsIdentifierStart(line[p]))
        {
            return;
        }

        var start = p;
        while (p < line.Length && IsIdentifierChar(line[p]))
        {
            p++;
        }

        var name = line[start..p].ToString();
        var nameOffset = head + start;

        // 名字后紧跟左括号，即函数式宏：只记名字与形参个数，宏体留空。
        var functionLike = p < line.Length && line[p] == '(';

        var arity = 0;
        var body = string.Empty;

        if (functionLike)
        {
            var close = MatchParen(line, p);
            arity = CountParameters(line, p);
            p = Math.Min(line.Length, close + 1);
        }
        else
        {
            var bodyStart = p;
            while (bodyStart < line.Length && (line[bodyStart] == ' ' || line[bodyStart] == (char)9))
            {
                bodyStart++;
            }

            body = NormalizeMacroBody(line[bodyStart..]);
        }

        var macro = new HlslMacro(name, body, nameOffset, name.Length, functionLike, arity);
        sink.OnMacro(macro);
        sink.OnMacroOp(new HlslMacroOp(false, macro));
    }

    // 宏体归一化：续行折成单行、去掉续行反斜杠、限长，避免长宏体占内存。
    private static string NormalizeMacroBody(ReadOnlySpan<char> raw)
    {
        var sb = new StringBuilder(Math.Min(raw.Length, 128));

        for (var k = 0; k < raw.Length; k++)
        {
            var c = raw[k];
            if (c == (char)92 && k + 1 < raw.Length && (raw[k + 1] == (char)10 || raw[k + 1] == (char)13))
            {
                k++;
                if (k + 1 < raw.Length && raw[k + 1] == (char)10)
                {
                    k++;
                }

                sb.Append(' ');
                continue;
            }

            if (c is (char)10 or (char)13)
            {
                sb.Append(' ');
                continue;
            }

            sb.Append(c);
            if (sb.Length >= 256)
            {
                break;
            }
        }

        var result = sb.ToString();
        var s = 0;
        var e = result.Length;
        while (s < e && char.IsWhiteSpace(result[s])) s++;
        while (e > s && char.IsWhiteSpace(result[e - 1])) e--;
        return s == 0 && e == result.Length ? result : result.Substring(s, e - s);
    }

    // 判断 line 从 at 起是否是关键字本身（后面不能紧跟标识符字符，避免 defineX 误命中）。
    private static bool StartsWithKeyword(ReadOnlySpan<char> line, int at, string keyword)
        => at + keyword.Length <= line.Length
        && line.Slice(at, keyword.Length).SequenceEqual(keyword)
        && (at + keyword.Length >= line.Length || !IsIdentifierChar(line[at + keyword.Length]));

    // 数形参个数：open 指向左括号；空表返回 0，否则顶层逗号数加 1。
    private static int CountParameters(ReadOnlySpan<char> text, int open)
    {
        var close = MatchParen(text, open);
        var end = Math.Min(close, text.Length);

        var depth = 0;
        var commas = 0;
        var any = false;

        for (var k = open + 1; k < end; k++)
        {
            var c = text[k];

            if (c is '(' or '[' or '<')
            {
                depth++;
            }
            else if (c is ')' or ']' or '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                commas++;
            }
            else if (!char.IsWhiteSpace(c))
            {
                any = true;
            }
        }

        return !any && commas == 0 ? 0 : commas + 1;
    }

    private static void SkipToLineEnd(ReadOnlySpan<char> hlsl, ref int i)
    {
        while (i < hlsl.Length && hlsl[i] != (char)10)
        {
            i++;
        }
    }

    /// <summary>跳过空白与注释（注释与字符串感知，与模块 1 同一口径）。到片段末尾返回 false。</summary>
    private static bool TrySkipTrivia(ReadOnlySpan<char> hlsl, ref int i)
    {
        while (i < hlsl.Length)
        {
            var c = hlsl[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < hlsl.Length && hlsl[i + 1] == '/')
            {
                while (i < hlsl.Length && hlsl[i] != (char)10)
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < hlsl.Length && hlsl[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < hlsl.Length && !(hlsl[i] == '*' && hlsl[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(hlsl.Length, i + 2);
                continue;
            }

            if (c == '"')
            {
                i++;
                while (i < hlsl.Length && hlsl[i] != '"')
                {
                    i += hlsl[i] == (char)92 ? 2 : 1;
                }

                i = Math.Min(hlsl.Length, i + 1);
                continue;
            }

            return true;
        }

        return false;
    }

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsIdentifierChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}


