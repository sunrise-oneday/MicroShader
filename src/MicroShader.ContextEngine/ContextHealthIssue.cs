namespace MicroShader.ContextEngine;

/// <summary>上下文引擎的健康问题级别。</summary>
public enum ContextHealthLevel : byte
{
    /// <summary>信息：功能已降级但仍有可用结果（例如 manifest 破损后走全量扫盘）。</summary>
    Info = 0,

    /// <summary>警告：能力受限，可能导致部分头文件无法解析。</summary>
    Warning = 1,
}

/// <summary>
/// 上下文引擎自身的健康问题（扫盘降级、安装路径未定位等）。
/// 这类问题"不是"用户的着色器错误，不应出现在编辑器红线上，只进日志与自检报告。
/// </summary>
public sealed class ContextHealthIssue
{
    public ContextHealthLevel Level { get; init; } = ContextHealthLevel.Info;

    /// <summary>稳定错误码（VFS 系列）。</summary>
    public string Code { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;

    /// <summary>相关路径（可为空）。</summary>
    public string? Path { get; init; }

    public override string ToString() => $"[{Code}] {Message}" + (Path is null ? string.Empty : $" ({Path})");
}

/// <summary>VFS 稳定错误码。</summary>
public static class ContextEngineDiagnosticCodes
{
    /// <summary>"Packages/manifest.json" 不是合法 JSON，已降级为全量扫盘。</summary>
    public const string MalformedManifest = "VFS0001";

    /// <summary>"packages-lock.json" 缺失或非法，包来源判定降级为目录名推导。</summary>
    public const string MissingLockFile = "VFS0002";

    /// <summary>未能定位 Unity 编辑器安装目录（CGIncludes / BuiltInPackages 不可用）。</summary>
    public const string UnityInstallNotFound = "VFS0003";

    /// <summary>某个包的物理目录缺失（manifest 声明了但磁盘上没有）。</summary>
    public const string PackageDirectoryMissing = "VFS0004";

    /// <summary>包目录命名无法推导包名。</summary>
    public const string UnrecognizedNamePackageDirectory = "VFS0005";
}
