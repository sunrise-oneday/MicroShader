using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.DiagnosticEngine;

/// <summary>编译单元未能装配出来的原因。</summary>
public enum AssemblyFailureKind : byte
{
    /// <summary>成功。</summary>
    None = 0,

    /// <summary>程序块未闭合（EOF 熔断）：编译被抑制，诊断由模块 1（SL0001）给出。</summary>
    UnterminatedBlock = 1,

    /// <summary>遗留 CG 块且未引用现代核心头文件：按设计抑制原生派发。</summary>
    SuppressedNativeDispatch = 2,

    /// <summary>依赖缺失导致中止。</summary>
    MissingInclude = 3,

    /// <summary>块内容为空，没有可编译的东西。</summary>
    NoContent = 4,
}

/// <summary>
/// 单个编译单元的装配结果。
/// </summary>
/// <remarks>
/// "所有权"：<see cref="Text"/> 非空时由调用方负责 "Dispose"（它持有池化字符缓冲）。
/// </remarks>
public sealed class AssemblyResult
{
    private AssemblyResult()
    {
    }

    /// <summary>装配出的渲染文本；失败时为 null。</summary>
    public AssembledShaderText? Text { get; private init; }

    /// <summary>本次装配产生的诊断（依赖缺失等）。</summary>
    public IReadOnlyList<ShaderDiagnosticItem> Diagnostics { get; private init; } = Array.Empty<ShaderDiagnosticItem>();

    /// <summary>失败原因。</summary>
    public AssemblyFailureKind Failure { get; private init; }

    /// <summary>失败说明（面向排障）。</summary>
    public string? FailureDetail { get; private init; }

    /// <summary>交给 DXC 的 "-I" 搜索路径（包含方目录 + CGIncludes 根）。</summary>
    public IReadOnlyList<string> IncludeSearchPaths { get; private init; } = Array.Empty<string>();

    /// <summary>本次派发使用的关键字矩阵（黄金测试断言对象）。</summary>
    public ShaderKeywordPlan? KeywordPlan { get; private init; }

    /// <summary>本次派发的阶段。</summary>
    public ShaderStage Stage { get; private init; }

    /// <summary>是否装配成功。</summary>
    public bool Succeeded => Text is not null;

    internal static AssemblyResult Success(
        AssembledShaderText text,
        List<ShaderDiagnosticItem> diagnostics,
        List<string> searchPaths,
        ShaderKeywordPlan plan,
        ShaderStage stage) => new()
        {
            Text = text,
            Diagnostics = diagnostics.ToArray(),
            Failure = AssemblyFailureKind.None,
            IncludeSearchPaths = searchPaths.ToArray(),
            KeywordPlan = plan,
            Stage = stage,
        };

    internal static AssemblyResult Fail(
        AssemblyFailureKind kind,
        string detail,
        ShaderStage stage,
        IReadOnlyList<ShaderDiagnosticItem>? diagnostics = null) => new()
        {
            Failure = kind,
            FailureDetail = detail,
            Diagnostics = diagnostics ?? Array.Empty<ShaderDiagnosticItem>(),
            Stage = stage,
        };
}
