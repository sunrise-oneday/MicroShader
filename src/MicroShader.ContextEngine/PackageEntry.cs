namespace MicroShader.ContextEngine;

/// <summary>VFS 包来源种类（v2.0 修正版：5 类）。</summary>
public enum PackageSourceKind : byte
{
    Unknown = 0,

    /// <summary>① "Packages/" 实体目录（Embedded，含被开发者魔改的 URP）。优先级最高。</summary>
    Embedded = 1,

    /// <summary>② "Library/PackageCache/" —— registry 包（"name@1.2.3"）或 git 包（"name@shorthash"）。</summary>
    PackageCache = 2,

    /// <summary>③ "manifest.json" 中 "file:" 引用的本地包（"source: local"）。</summary>
    LocalFile = 3,

    /// <summary>④ git 包（"source: git"）。</summary>
    Git = 4,

    /// <summary>⑤ "&lt;Editor&gt;/Data/Resources/PackageManager/BuiltInPackages/"。</summary>
    BuiltIn = 5,
}

/// <summary>
/// 一个已解析到物理目录的包。
/// </summary>
public sealed class PackageEntry
{
    /// <summary>包名，如 "com.unity.render-pipelines.universal"。</summary>
    public required string Name { get; init; }

    /// <summary>"manifest.json" 里的版本声明原文（可能是 "14.0.12" / "file:xxx" / git URL）；未知时为空。</summary>
    public string VersionSpec { get; init; } = string.Empty;

    /// <summary>"packages-lock.json" 的 "version" 字段（权威版本或 "file:"/git 描述）。</summary>
    public string LockVersion { get; init; } = string.Empty;

    /// <summary>解析出的包来源种类。</summary>
    public PackageSourceKind Source { get; init; }

    /// <summary>包根目录物理绝对路径（归一化：正斜杠形式）。</summary>
    public required string PhysicalRoot { get; init; }

    /// <summary>
    /// "&lt;Editor&gt;/Data/Resources/PackageManager/BuiltInPackages" 这类内建包根；
    /// 为 null 表示该包是工程级实体目录。
    /// </summary>
    public string? ContainerRoot { get; init; }

    /// <summary>该来源在解析优先级中的序号（数字越小越优先）。</summary>
    public int Priority => Source switch
    {
        PackageSourceKind.Embedded => 0,
        PackageSourceKind.LocalFile => 1,
        PackageSourceKind.Git => 2,
        PackageSourceKind.PackageCache => 3,
        PackageSourceKind.BuiltIn => 4,
        _ => 9,
    };

    public override string ToString() => $"{Name} ({Source}) -> {PhysicalRoot}";
}
