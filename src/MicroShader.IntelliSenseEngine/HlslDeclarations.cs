namespace MicroShader.IntelliSenseEngine;

/// <summary>符号声明的种类（供 F12 定位与文档大纲使用）。</summary>
public enum HlslDeclKind
{
    /// <summary>变量 / 形参 / 全局常量。</summary>
    Variable = 0,

    /// <summary>结构体字段。</summary>
    Field = 1,

    /// <summary>函数定义。</summary>
    Function = 2,

    /// <summary>结构体 / 类定义。</summary>
    Struct = 3,

    /// <summary>"#define" 宏。</summary>
    Macro = 4,
}

/// <summary>
/// 一条符号声明的位置与形状。索引里「名字 → 声明」是 "1→N" 的。
/// </summary>
/// <remarks>
/// 
/// "为什么必须 1→N"（详设 v2.0 第 2 条）：HLSL/URP 大量重载与同名。
/// 实测量化：函数名在多文件重复 "41 / 984"（"frag" 在 30 个文件里定义），
/// 宏名重复 "177 / 1162"。若索引按「后写覆盖」压成 1→1，F12 必然跳错重载。
/// 因此这里保留全部候选，由消费侧先按「当前块的 include 闭包」过滤，再排序。
/// 
/// "位置用偏移而不是行列"：行号在打字期随时漂移，且跨文件时各自的行表不同。
/// 存绝对字符偏移（UTF-16 code unit 计数，与 LSP 出口口径一致）由消费侧换算，
/// 换算所需的行首表由预热阶段一并构建（详设 §三 的「紧凑位置只读表」）。
/// </remarks>
public readonly record struct HlslDecl(
    HlslDeclKind Kind,
    string Name,
    string Detail,
    int Offset,
    int Length,
    int Arity = -1)
{
    /// <summary>名字的结束偏移（独占）。</summary>
    public int EndOffset => Offset + Length;

    /// <summary>偏移是否覆盖指定字符位置（用于「光标落在哪个符号上」）。</summary>
    public bool Contains(int position) => position >= Offset && position <= EndOffset;
}

/// <summary>一条 "#define" 宏。</summary>
/// <remarks>
/// "只记对象式宏的宏体"：函数式宏（"#define FOO(x) ..."）的宏体不能当「别名」解包，
/// 详设 v2.0 第 6 条明确要求跳过。"IsFunctionLike" 为 "true" 时 <see cref="Body"/> 为空串。
/// </remarks>
public readonly record struct HlslMacro(
    string Name,
    string Body,
    int Offset,
    int Length,
    bool IsFunctionLike,
    int Arity)
{
    public int EndOffset => Offset + Length;

    public bool Contains(int position) => position >= Offset && position <= EndOffset;
}

/// <summary>
/// 宏可见性事件："#define" 或 "#undef"。
/// </summary>
/// <remarks>
/// 宏可见性"不能"用「文件级最终状态表」表达 —— 同一文件内可能是
/// "#define A 1" → "#undef A" → "#define A 2"，而跨文件合并必须
/// 「按 include 顺序连同 "#undef" 一起求值」（详设 v2.0 第 6、7 条）。
/// 因此按出现顺序记录事件流。
/// </remarks>
public readonly record struct HlslMacroOp(bool IsUndef, HlslMacro Macro);
