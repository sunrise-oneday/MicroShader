using System.Collections.Frozen;
using MicroShader.Domain;

namespace MicroShader.ShaderLab;

/// <summary>
/// ShaderLab 标签语义校验器：在切片之后、装配之前执行，纯字符串比较 + 有界编辑距离，不触碰 DXC。
/// </summary>
/// <remarks>
/// "沉默原则（本类最重要的单条规则）"：凡是没有权威依据判定为「非法」的标签名或标签值，
/// 一律不产生任何诊断。误报会训练用户忽略诊断，比漏报更有害。只有两种情况才报错：
/// ① 值/名"近乎命中"一个已知合法项（编辑距离 &lt;= 2）却不等 —— 典型拼写错误；
/// ② 标签值的集合是"封闭且由 Unity 内核硬编码"的（如 "RenderPipeline"），且值不在其中。
/// "三层值来源"：① 现场包扫描（最高权威，见 <see cref="ShaderTagSchemaBuilder"/>）；
/// ② Unity 文档确证的封闭集合；③ 仅当工程不含 SRP 包时才启用的内置管线 LightMode。
/// "零分配"：无诊断路径上不产生任何托管堆分配。依赖两点：候选集合用
/// <see cref="FrozenSet{T}"/>（枚举器是结构体，不装箱）、编辑距离全程 "stackalloc"。
/// "线程安全"：<see cref="Validate"/> 是无状态方法（只读 schema + 局部变量），可并发调用；
/// 但 <paramref name="into"/> 由调用方独占。
/// </remarks>
public sealed class ShaderTagValidator
{
    /// <summary>「近似命中」的编辑距离阈值。取 2 而非 3：覆盖单字符替换/漏字/多字，又不至于把合法值误判。</summary>
    public const int MaxSuggestionDistance = 2;

    /// <summary>
    /// 校验一篇 ShaderLab 文档的全部标签条目，把诊断追加到 <paramref name="into"/>。
    /// </summary>
    /// <returns>本次新增的诊断条数。</returns>
    public int Validate(ShaderLabParseResult parseResult, ShaderTagSchema schema, List<ShaderDiagnosticItem> into)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(into);

        // 三层全空 → 整体沉默，绝不猜测。
        if (schema.IsEmpty || parseResult.TagEntries.Count == 0)
        {
            return 0;
        }

        var before = into.Count;

        foreach (var entry in parseResult.TagEntries)
        {
            // ① 标签名拼写校验（所有标签名都过一遍）
            if (!schema.AllKnownTagNames.Contains(entry.Name))
            {
                if (TryFindClosest(entry.Name, schema.AllKnownTagNames, out var nameCandidate, out var nameDistance))
                {
                    Add(into, parseResult, entry.NameLine, entry.NameColumn, ShaderLabDiagnosticCodes.TagNameTypo, DiagnosticSeverity.Error,
                        $"标签名 \"{entry.Name}\" 近似 \"{nameCandidate}\"（编辑距离 {nameDistance}），是否为拼写错误？"
                        + "标签名拼错时 Unity 会静默忽略整条标签。");
                }

                // 名字都不认识 → 不再对它的值下任何结论（自定义标签名合法且常见）。
                continue;
            }

            // ② 封闭集合标签：值必须落在集合内
            if (schema.ClosedSets.TryGetValue(entry.Name, out var validValues))
            {
                if (validValues.Contains(entry.Value))
                {
                    continue;
                }

                if (TryFindClosest(entry.Value, validValues, out var candidate, out var distance))
                {
                    if (entry.Name == ShaderTagNames.RenderPipeline)
                    {
                        Add(into, parseResult, entry.ValueLine, entry.ValueColumn, ShaderLabDiagnosticCodes.TagPipelineTypo, DiagnosticSeverity.Error,
                            $"\"{entry.Name}\" 的值 \"{entry.Value}\" 近似 \"{candidate}\"（编辑距离 {distance}），"
                            + "是否为拼写错误？管线标记不匹配时该 SubShader 会被整段忽略。");
                    }
                    else
                    {
                        Add(into, parseResult, entry.ValueLine, entry.ValueColumn, ShaderLabDiagnosticCodes.TagClosedSetInvalid, DiagnosticSeverity.Warning,
                            $"\"{entry.Name}\" 的值 \"{entry.Value}\" 非法（近似合法值 \"{candidate}\"，编辑距离 {distance}）。");
                    }
                }
                else if (entry.Name == ShaderTagNames.RenderPipeline)
                {
                    // 无近似候选：可能是本工程自定义的渲染管线标记，只给 Warning 不下 Error。
                    Add(into, parseResult, entry.ValueLine, entry.ValueColumn, ShaderLabDiagnosticCodes.TagPipelineUnknown, DiagnosticSeverity.Warning,
                        $"\"{entry.Name}\" 的值 \"{entry.Value}\" 未知：本工程已安装管线中不存在该标记。");
                }
                else
                {
                    // 封闭集合且无近似候选 —— 仍然非法。
                    Add(into, parseResult, entry.ValueLine, entry.ValueColumn, ShaderLabDiagnosticCodes.TagClosedSetInvalid, DiagnosticSeverity.Warning,
                        $"\"{entry.Name}\" 的值 \"{entry.Value}\" 不在合法集合中。");
                }

                continue;
            }

            // ③ LightMode：开放集合（用户可自定义），只有近似命中才报
            if (entry.Name == ShaderTagNames.LightMode)
            {
                if (schema.LightModeValues.Contains(entry.Value))
                {
                    continue;
                }

                if (TryFindClosest(entry.Value, schema.LightModeValues, out var candidate, out var distance))
                {
                    Add(into, parseResult, entry.ValueLine, entry.ValueColumn, ShaderLabDiagnosticCodes.TagLightModeTypo, DiagnosticSeverity.Error,
                        $"\"LightMode\" 的值 \"{entry.Value}\" 近似 \"{candidate}\"（编辑距离 {distance}），"
                        + "是否为拼写错误？拼错时该 Pass 永远不会被渲染管线调度。");
                }

                // 找不到近似 → 沉默（用户自定义 LightMode 合法且常见）。
            }

            // ④ 其余标签名：不校验值（自由文本）。
        }

        return into.Count - before;
    }

    /// <summary>
    /// 在候选集合里找编辑距离最近的合法项。距离为 0 的候选被跳过（那意味着精确命中，调用方本不该走到这里）。
    /// </summary>
    private static bool TryFindClosest(ReadOnlySpan<char> value, FrozenSet<string> candidates, out string? best, out int bestDistance)
    {
        best = null;
        bestDistance = MaxSuggestionDistance + 1;

        foreach (var candidate in candidates)
        {
            // 长度分桶预过滤：长度差已超阈值时距离必然超阈值，省掉一次矩阵计算。
            if (Math.Abs(candidate.Length - value.Length) > MaxSuggestionDistance)
            {
                continue;
            }

            if (!ShaderTagDistance.TryWithin(value, candidate, MaxSuggestionDistance, out var distance))
            {
                continue;
            }

            if (distance == 0 || distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            best = candidate;

            if (distance == 1)
            {
                break; // 距离 1 已是最优（不可能更小），无需继续扫描
            }
        }

        return best is not null;
    }

    private static void Add(
        List<ShaderDiagnosticItem> into,
        ShaderLabParseResult parseResult,
        int line,
        int column,
        string code,
        DiagnosticSeverity severity,
        string message)
    {
        into.Add(new ShaderDiagnosticItem
        {
            Severity = severity,
            Code = code,
            Line = line,
            Column = column,
            Message = message,
            TargetUri = parseResult.TargetUri,
        });
    }
}

/// <summary>校验器与构建器共用的标签名常量（避免多处硬编码字符串漂移）。</summary>
public static class ShaderTagNames
{
    public const string LightMode = "LightMode";
    public const string RenderPipeline = "RenderPipeline";
    public const string RenderType = "RenderType";
    public const string Queue = "Queue";
    public const string DisableBatching = "DisableBatching";
    public const string PreviewType = "PreviewType";
    public const string IgnoreProjector = "IgnoreProjector";
    public const string ForceNoShadowCasting = "ForceNoShadowCasting";
    public const string ShaderGraphTargetId = "ShaderGraphTargetId";
}
