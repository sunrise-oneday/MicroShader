namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 模块 3（DiagnosticEngine / LineCalibrator）内部稳定错误码。
/// 与模块 1 的 "SL000x"、模块 2 的 "VFS000x" 同一命名体系，不面向用户，只用于去重、黄金测试与回归比对。
/// </summary>
public static class DiagnosticEngineCodes
{
    /// <summary>依赖缺失：某条 "#include" 在依赖扫描层解析不出来，该程序块不做原生编译（v2.0 清单第 10 条）。</summary>
    public const string MissingInclude = "MS0001";

    /// <summary>程序块未闭合（EOF 熔断）：编译被抑制，错误落在开启标记行（ADR-023 / 模块 1 SL0001 的消费侧）。</summary>
    public const string UnterminatedBlock = "MS0002";

    /// <summary>遗留 CG 块且未引用现代核心头文件，抑制原生派发（<see cref="MicroShader.Domain.ShaderPassSnippet.SuppressNativeDispatch"/>）。</summary>
    public const string NativeDispatchSuppressed = "MS0003";

    /// <summary>列号换算降级：DXC 报告的 UTF-8 字节列落在一个多字节序列中间，只能给出「行 + 近似列」。</summary>
    public const string ColumnDegraded = "MS0004";

    /// <summary>组装产物不变量（I1/I2/I3/I5）校验失败 —— 属工具自身缺陷，必须当作硬错误暴露。</summary>
    public const string InvariantViolation = "MS0005";

    /// <summary>没有可编译的程序块（例如整篇 ShaderLab 语法都不成立）。</summary>
    public const string NothingToCompile = "MS0006";

    /// <summary>
    /// 诊断落在装配注入段（宏矩阵 / SHADER_STAGE_* 段）里 —— 该段在第一条 "#line" 之前，
    /// 说明是工具自己注入的内容有缺陷，而不是用户的 shader 有问题。
    /// </summary>
    public const string AssemblyPrologue = "MS0007";

    /// <summary>
    /// 单次分析的单元数超过上限：被截断的那些 Pass 这次根本没编译，既不是干净也不是有错。
    /// 报告里必须带上它，宿主据此跳过这些槽位的发布（保留上一次的红线），用户侧也能看见提示。
    /// </summary>
    public const string AnalysisBudgetExceeded = "MS0008";

    /// <summary>计算着色器（.compute）缺少 #pragma kernel 声明。</summary>
    public const string ComputeMissingKernel = "CS0101";
}
