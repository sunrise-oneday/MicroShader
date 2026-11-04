namespace MicroShader.Domain;

/// <summary>
/// 一条 "#include" / "#include_with_pragmas" 指令。
/// 本模块只负责「如实记录」；路径解析（VFS 重定向）属于模块 2。
/// </summary>
public sealed class IncludeDirective
{
    /// <summary>指令中原样书写的路径（已去掉引号，未做任何归一化）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>1-based 物理绝对行号。</summary>
    public int LineNumber { get; init; }

    /// <summary>列号（1-based，指向路径起始的引号）。</summary>
    public int Column { get; init; }

    /// <summary>是否为 Unity 私有指令 "#include_with_pragmas"（DXC 不识别，模块 3 需降级重写）。</summary>
    public bool IsWithPragmas { get; init; }
}
