using System.Collections.Immutable;

namespace MicroShader.ContextEngine;

/// <summary>
/// Unity 侧运行时上下文（动态宏、着色器关键字、活跃状态）。
/// </summary>
/// <remarks>
/// 本类型是"不可变"的，更新走「写时复制 + 整体置换」。
/// 数据来源是 Unity 桥接（LSP 客户端 → 编辑器 Inspector 的 Keyword 勾选状态），
/// 协议见 Contract 2："{event:"onShaderContextChanged", timestamp, data:{activeKeywords:[], globalDefines:{defined:[], undefined:[]}}}"。
/// 桥接本身属于后续模块，这里只提供不可变的承载结构与默认空值。
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

    public DateTimeOffset UpdatedAtUtc { get; init; }
}
