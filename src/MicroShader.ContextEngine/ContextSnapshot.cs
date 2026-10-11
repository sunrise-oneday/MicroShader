namespace MicroShader.ContextEngine;

/// <summary>
/// 一次对外可见的完整上下文快照：静态 VFS 拓扑 + Unity 动态宏。
/// </summary>
/// <remarks>
/// 读取侧永远只读一个不可变对象，因此不需要任何锁；
/// 更新侧用 CAS 整体置换（见 <see cref="ShaderContextRegistry"/>）。
/// </remarks>
public sealed class ContextSnapshot
{
    internal ContextSnapshot(VfsIndex index, DynamicShaderContext dynamicContext, long version)
    {
        Index = index;
        Dynamic = dynamicContext;
        Version = version;
    }

    /// <summary>静态 VFS 拓扑（包、工程布局、编辑器安装）。</summary>
    public VfsIndex Index { get; }

    /// <summary>Unity 侧动态上下文（关键字/全局宏）。</summary>
    public DynamicShaderContext Dynamic { get; }

    /// <summary>快照版本号，单调递增；用于丢弃过期的异步结果（配合 per-Pass epoch）。</summary>
    public long Version { get; }

    /// <summary>Unity 桥接是否在线。</summary>
    public bool IsUnityLiveConnected => Dynamic.IsLive;

    /// <summary>当前全局宏的只读列表（供宏矩阵注入使用）。</summary>
    public IReadOnlyList<string> GetActiveDefines()
    {
        if (Dynamic.DefinedMacros.IsEmpty)
        {
            return [];
        }

        var result = new string[Dynamic.DefinedMacros.Length];
        Dynamic.DefinedMacros.CopyTo(result);
        return result;
    }
}
