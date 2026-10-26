namespace MicroShader.DiagnosticEngine;

/// <summary>装配器选项。</summary>
public sealed class AssemblerOptions
{
    /// <summary>强制使用的平台宏集合（自检与黄金测试用）；为 null 时按三层来源自动解算。</summary>
    public PlatformMacroSet? PlatformMacros { get; init; }

    /// <summary>是否把平台宏写进注入段。</summary>
    public bool IncludePlatformMacros { get; init; } = true;

    /// <summary>是否把阶段宏 + 关键字矩阵写进注入段。</summary>
    public bool IncludeKeywordMatrix { get; init; } = true;

    /// <summary>
    /// 是否把 Unity 桥接上报的动态宏（<c>globalDefines</c>）写进注入段，并用它广播的
    /// 活动关键字去改进「每个 set 选哪个变体」的选择（模块 11B）。
    /// </summary>
    /// <remarks>
    /// 默认 true，但**离线零漂移**：桥接不在线（或未上报任何宏）时一个字节也不写，
    /// 注入段与引入本开关前逐字节相同 —— 黄金判据（URP Lit.shader 零 Error）因此不受影响。
    /// </remarks>
    public bool IncludeDynamicMacros { get; init; } = true;

    /// <summary>
    /// 组装时是否探盘校验每条 include 的真实存在性。
    /// </summary>
    /// <remarks>默认 "false" = 编译热路径（纯路径演算，零文件 I/O）。报告层用 "true"。</remarks>
    public bool VerifyIncludesOnDisk { get; init; }

    /// <summary>遇到无法解析的 include 时是否中止该编译单元（默认中止，绝不把虚拟路径喂给 DXC）。</summary>
    public bool AbortOnUnresolvedInclude { get; init; } = true;

    /// <summary>强制覆盖 "#line" 虚拟路径（自检用）。</summary>
    public string? ForcedVirtualPath { get; init; }

    /// <summary>目标平台（用于版本×平台表）。</summary>
    public string Platform { get; init; } = PlatformMacroResolver.DefaultPlatform;
}
