using MicroShader.Domain;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 一个原生编译单元的渲染文本 + 行映射表 + 虚拟路径。
/// </summary>
/// <remarks>
/// "所有权"：内部字符缓冲租自 <see cref="System.Buffers.ArrayPool{T}"/>，本对象即所有权持有者。
/// 模块 4 拿到 <see cref="Span"/> 后 "fixed" pin 成 "DxcBuffer"，调用返回后必须立刻
/// <see cref="Dispose"/> —— 池化缓冲在 "Dispose" 之前一直有效，之后一律失效。
/// 热路径上"不要"调用 <see cref="GetText"/>（它会复制一份完整字符串）；它只服务于自检与排障。
/// </remarks>
public sealed class AssembledShaderText : IDisposable
{
    private RentedCharBuffer? _buffer;

    internal AssembledShaderText(
        RentedCharBuffer buffer,
        RenderedLineMap lineMap,
        string physicalPath,
        string virtualPath,
        ShaderStageInfo stage)
    {
        _buffer = buffer;
        LineMap = lineMap;
        PhysicalPath = physicalPath;
        VirtualPath = virtualPath;
        Stage = stage;
    }

    /// <summary>源文件物理路径（正斜杠归一化）。</summary>
    public string PhysicalPath { get; }

    /// <summary>"#line" 使用的虚拟路径。</summary>
    public string VirtualPath { get; }

    /// <summary>行/列映射表。</summary>
    public RenderedLineMap LineMap { get; }

    /// <summary>本单元的阶段信息。</summary>
    public ShaderStageInfo Stage { get; }

    /// <summary>渲染文本（可直接 pin）。<see cref="Dispose"/> 之后返回空。</summary>
    public ReadOnlySpan<char> Span => _buffer is { } buffer ? buffer.Written : default;

    /// <summary>渲染文本的字符长度。</summary>
    public int Length => _buffer?.Length ?? 0;

    /// <summary>取某个渲染行的文本（不含换行符）。</summary>
    public ReadOnlySpan<char> GetLine(int renderedLine)
    {
        if (_buffer is not { } buffer || renderedLine < 1 || renderedLine > LineMap.LineCount)
        {
            return default;
        }

        return buffer.Written.Slice(LineMap.LineStartOffset(renderedLine), LineMap.LineLength(renderedLine));
    }

    /// <summary>把整篇渲染文本复制成字符串（"仅"自检/排障用）。</summary>
    public string GetText() => Span.ToString();

    public void Dispose()
    {
        _buffer?.Dispose();
        _buffer = null;
    }
}

/// <summary>一次派发的阶段信息（阶段宏 + DXC 目标档位）。</summary>
public readonly struct ShaderStageInfo
{
    public ShaderStageInfo(ShaderStage stage)
    {
        Stage = stage;
        Macro = stage.ToStageMacro();
        TargetProfile = stage.ToTargetProfile();
    }

    /// <summary>阶段枚举。</summary>
    public ShaderStage Stage { get; }

    /// <summary>Unity 阶段宏名（"SHADER_STAGE_*"）；未指定阶段时为空串。</summary>
    public string Macro { get; }

    /// <summary>DXC 目标档位（"ps_6_0" 等）；未指定阶段时为空串。</summary>
    public string TargetProfile { get; }

    public override string ToString() => Macro.Length == 0 ? "<无阶段>" : Macro;
}
