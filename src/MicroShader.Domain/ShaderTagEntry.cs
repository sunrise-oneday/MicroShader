namespace MicroShader.Domain;

/// <summary>标签所在的 ShaderLab 层级。</summary>
/// <remarks>
/// LightMode 只在 Pass 级有意义，RenderPipeline 只在 SubShader 级有意义 —— 层级必须随标签一起记录，
/// 否则校验器无法判断该用哪张表，也无法解释「这条标签放错层了」。
/// </remarks>
public enum ShaderTagScope : byte
{
    Shader = 0,
    SubShader = 1,
    Pass = 2,
}

/// <summary>
/// 一个 ShaderLab 标签条目（""name" = "value""），由模块 1 在扫描 "Tags" 块时产出。
/// </summary>
/// <remarks>
/// 行号/列号口径与 <see cref="IncludeDirective"/> 一致：行号 1-based，列号 1-based 且指向
/// "引号之后"的首个字符；长度不含引号。这样诊断可以直接落在标签值上而不是整行。
/// 实例可复用（<see cref="Reset"/>），随 "ShaderLabParseResult" 一起池化。
/// </remarks>
public sealed class ShaderTagEntry
{
    /// <summary>标签名，不含引号（如 "LightMode"）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>标签值，不含引号（如 "UniversalForward"）。</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>标签名首字符的 1-based 物理行号。</summary>
    public int NameLine { get; set; }

    /// <summary>标签名首字符的 1-based 列号（指向引号之后）。</summary>
    public int NameColumn { get; set; }

    /// <summary>标签名字符数（不含引号）。</summary>
    public int NameLength { get; set; }

    /// <summary>标签值首字符的 1-based 物理行号。</summary>
    public int ValueLine { get; set; }

    /// <summary>标签值首字符的 1-based 列号（指向引号之后）。</summary>
    public int ValueColumn { get; set; }

    /// <summary>标签值字符数（不含引号）。</summary>
    public int ValueLength { get; set; }

    /// <summary>标签所在层级。</summary>
    public ShaderTagScope Scope { get; set; }

    /// <summary>同一层级、同一 SubShader 内的第几条标签（0-based，按出现顺序）。</summary>
    public int ScopeOrdinal { get; set; }

    /// <summary>所属 SubShader 序号（0-based）；不在任何 SubShader 内时为 "-1"。</summary>
    public int SubShaderIndex { get; set; } = -1;

    /// <summary>清空以便复用。</summary>
    public void Reset()
    {
        Name = string.Empty;
        Value = string.Empty;
        NameLine = 0;
        NameColumn = 0;
        NameLength = 0;
        ValueLine = 0;
        ValueColumn = 0;
        ValueLength = 0;
        Scope = ShaderTagScope.Shader;
        ScopeOrdinal = 0;
        SubShaderIndex = -1;
    }
}
