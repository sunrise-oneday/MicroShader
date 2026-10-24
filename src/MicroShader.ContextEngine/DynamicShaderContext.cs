using System.Collections.Immutable;

namespace MicroShader.ContextEngine;

/// <summary>
/// Unity 侧运行时上下文（动态宏、着色器关键字、活跃状态）。
/// </summary>
/// <remarks>
/// 本类型是"不可变"的，更新走「写时复制 + 整体置换」。
/// 数据来源是 Unity 桥接（LSP 客户端 → 编辑器 Inspector 的 Keyword 勾选状态），
/// 协议见 Contract 2："{event:"onShaderContextChanged", timestamp, data:{activeKeywords:[], globalDefines:{defined:[], undefined:[]}}}"。
/// 构建目标与图形 API 不在帧里，取自桥接写的发现文件；帧到达时由客户端叠加上来。
/// </remarks>
public sealed class DynamicShaderContext
{
    /// <summary>尚未收到任何桥接数据时的上下文。</summary>
    public static DynamicShaderContext Empty { get; } = new()
    {
        ActiveKeywords = [],
        DefinedMacros = [],
        UndefinedMacros = [],
        IsLive = false,
        UpdatedAtUtc = default,
    };

    /// <summary>当前变体激活的着色器关键字。</summary>
    public ImmutableArray<string> ActiveKeywords { get; init; } = [];

    /// <summary>全局已定义宏（"globalDefines.defined"）。</summary>
    public ImmutableArray<string> DefinedMacros { get; init; } = [];

    /// <summary>全局显式未定义宏（"globalDefines.undefined"）。</summary>
    public ImmutableArray<string> UndefinedMacros { get; init; } = [];

    /// <summary>Unity 桥接当前是否在线。</summary>
    public bool IsLive { get; init; }

    /// <summary>
    /// 活动构建目标（Unity 的 activeBuildTarget，例如 StandaloneWindows64）。
    /// 帧里没有这个字段，它来自发现文件；取不到时为 null。
    /// </summary>
    public string? ActiveBuildTarget { get; init; }

    /// <summary>
    /// 构建目标对应的图形 API（例如 Direct3D11 / Vulkan / Metal）。
    /// 与 ActiveBuildTarget 同源，取不到时为 null。平台宏解算只认它，不认编辑器自己跑在哪个后端。
    /// </summary>
    public string? GraphicsApi { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>
    /// 复制一份并换上平台事实。
    /// </summary>
    /// <remarks>
    /// 平台事实来自发现文件而不是桥接帧，帧到达时把它叠加上来；
    /// 其余字段原样搬运（本类型没有 with 表达式可用，只能显式拷贝）。
    /// </remarks>
    public DynamicShaderContext WithPlatform(string? activeBuildTarget, string? graphicsApi) => new()
    {
        ActiveKeywords = ActiveKeywords,
        DefinedMacros = DefinedMacros,
        UndefinedMacros = UndefinedMacros,
        IsLive = IsLive,
        UpdatedAtUtc = UpdatedAtUtc,
        ActiveBuildTarget = activeBuildTarget,
        GraphicsApi = graphicsApi,
    };
}
