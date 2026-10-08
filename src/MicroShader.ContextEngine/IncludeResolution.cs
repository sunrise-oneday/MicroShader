namespace MicroShader.ContextEngine;

/// <summary>命中 "#include" 的路径规则。</summary>
public enum IncludeRuleKind : byte
{
    None = 0,

    /// <summary>规则 1："Packages/&lt;pkg&gt;/..." 虚拟前缀命中 5 类包根之一。</summary>
    PackageVirtualPrefix = 1,

    /// <summary>规则 2："Assets/..." 前缀，相对工程根。</summary>
    AssetsPrefix = 2,

    /// <summary>规则 3：裸名/相对路径，相对包含方文件所在目录。</summary>
    RelativeToIncludingFile = 3,

    /// <summary>规则 4：裸名兜底到 "&lt;Editor&gt;/Data/CGIncludes/&lt;裸名&gt;"。</summary>
    CGIncludesFallback = 4,
}

/// <summary>未命中的原因。</summary>
public enum IncludeMissKind : byte
{
    None = 0,

    /// <summary>"Packages/" 前缀里的包名不在 VFS 拓扑中（包未安装 / 拼写错误）。</summary>
    UnknownPackage = 1,

    /// <summary>路径算得出来，但磁盘上没有这个文件 —— 即「依赖缺失」。</summary>
    NotFound = 2,
}

/// <summary>一次 "#include" 解析的结果。</summary>
public readonly struct IncludeResolution
{
    public bool Found { get; init; }

    /// <summary>命中的物理路径（归一化、正斜杠）。未命中时为 null。</summary>
    public string? PhysicalPath { get; init; }

    public IncludeRuleKind Rule { get; init; }

    public IncludeMissKind Miss { get; init; }

    /// <summary>当 <see cref="Miss"/> 为 <see cref="IncludeMissKind.UnknownPackage"/> 时的包名。</summary>
    public string? MissingPackageName { get; init; }

    /// <summary>
    /// 生成用户可读的「依赖缺失」说明。
    /// </summary>
    /// <remarks>
    /// 措辞纪律（见模块 5 的降噪要求）：必须说明这是"依赖扫描层"的结论，
    /// 且"不得"建议用户改用 "#if defined(K)" —— 那是把工具的能力缺陷转嫁给用户代码。
    /// </remarks>
    public string DescribeMissing(ReadOnlySpan<char> includePath)
    {
        var name = includePath.ToString();
        return Miss switch
        {
            IncludeMissKind.UnknownPackage =>
                $"依赖缺失：无法解析 #include \"{name}\" —— 虚拟路径 Packages/ 下的包 '{MissingPackageName}' 不在当前工程的包拓扑中（未安装、未恢复或拼写有误）。",
            IncludeMissKind.NotFound =>
                $"依赖缺失：无法定位 #include \"{name}\" —— 按工程包拓扑与编辑器 CGIncludes 目录均未找到该文件。",
            _ => $"依赖缺失：无法解析 #include \"{name}\"。",
        };
    }
}
