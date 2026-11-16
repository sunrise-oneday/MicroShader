using MicroShader.Domain;

namespace MicroShader.ShaderLab;

/// <summary>
/// 一篇 ".shader" 的结构化切片结果。可以跨文档复用（<see cref="Reset"/> 后切片对象回到内部池），
/// 因此调用方不得在两次 "Parse" 之间持有旧结果中的对象引用。
/// </summary>
public sealed class ShaderLabParseResult
{
    private readonly List<ShaderPassSnippet> _pool = new();
    private readonly List<ShaderTagEntry> _tagPool = new();

    /// <summary>被切片的文件物理路径（原样）。</summary>
    public string FilePath { get; internal set; } = string.Empty;

    /// <summary>被切片文件的 "file:///" URI。</summary>
    public string TargetUri { get; internal set; } = string.Empty;

    /// <summary>切出的编译单元（不含文件级共享块）。</summary>
    public List<ShaderPassSnippet> Passes { get; } = new();

    /// <summary>文件级共享块（"HLSLINCLUDE"/"CGINCLUDE"），索引即 <see cref="ShaderPassSnippet.ApplicableSharedBlockIndices"/> 的取值。</summary>
    public List<ShaderProgramBlock> SharedBlocks { get; } = new();

    /// <summary>
    /// 文件级共享块自身的 "#pragma" / "#include" 扫描结果，与 <see cref="SharedBlocks"/>
    /// "同序同索引"。
    /// </summary>
    /// <remarks>
    /// "HLSLINCLUDE" 的内容会被拼进本文件内每一个 "HLSLPROGRAM" 块（v2.0 清单第 3 条），
    /// 因此它声明变体关键字、包含头文件的效果与写在 Pass 里"等价"。这些声明必须被扫描下来，
    /// 否则会从宏矩阵里静默消失 —— 「代码写错但编译器给虚假通过」比直接报错危险得多。
    /// </remarks>
    public List<ShaderPassSnippet> SharedSnippets { get; } = new();

    /// <summary>切片阶段自身发现的诊断（未闭合块、未闭合注释等）。</summary>
    public List<ShaderDiagnosticItem> Diagnostics { get; } = new();

    /// <summary>
    /// 本文件中所有 "Tags" 块的标签条目，按出现顺序排列。
    /// 供标签语义校验子系统（模块 9）消费；与 <see cref="Passes"/> 一样随本对象池化复用。
    /// </summary>
    public List<ShaderTagEntry> TagEntries { get; } = new();

    /// <summary>文件内 "SubShader" 个数。</summary>
    public int SubShaderCount { get; internal set; }

    /// <summary>是否存在 EOF 熔断的代码块。</summary>
    public bool HasUnterminatedBlock { get; internal set; }

    /// <summary>是否存在到文件末尾仍未闭合的块注释。</summary>
    public bool HasUnterminatedBlockComment { get; internal set; }

    /// <summary>清空结果并可复用切片对象。</summary>
    public void Reset()
    {
        foreach (var pass in Passes)
        {
            pass.Reset();
            _pool.Add(pass);
        }

        Passes.Clear();
        SharedBlocks.Clear();

        foreach (var shared in SharedSnippets)
        {
            shared.Reset();
            _pool.Add(shared);
        }

        SharedSnippets.Clear();
        Diagnostics.Clear();

        foreach (var tag in TagEntries)
        {
            tag.Reset();
            _tagPool.Add(tag);
        }

        TagEntries.Clear();
        FilePath = string.Empty;
        TargetUri = string.Empty;
        SubShaderCount = 0;
        HasUnterminatedBlock = false;
        HasUnterminatedBlockComment = false;
    }

    internal ShaderPassSnippet RentSnippet()
    {
        if (_pool.Count == 0)
        {
            return new ShaderPassSnippet();
        }

        var last = _pool.Count - 1;
        var snippet = _pool[last];
        _pool.RemoveAt(last);
        return snippet;
    }

    internal void AddDiagnostic(ShaderDiagnosticItem item) => Diagnostics.Add(item);

    /// <summary>从内部池取一个标签条目（热路径零分配）。</summary>
    internal ShaderTagEntry RentTagEntry()
    {
        if (_tagPool.Count == 0)
        {
            return new ShaderTagEntry();
        }

        var last = _tagPool.Count - 1;
        var entry = _tagPool[last];
        _tagPool.RemoveAt(last);
        return entry;
    }
}
