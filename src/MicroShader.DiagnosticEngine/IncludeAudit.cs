using MicroShader.ContextEngine;
using MicroShader.Domain;
using MicroShader.ShaderLab;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 依赖扫描层的 include 审计（v2.0 清单第 10 条的「报告层」）。
/// </summary>
/// <remarks>
/// "与编译路径的分工"：编译热路径对 include 只做纯路径演算（零文件 I/O），
/// 只把 "Packages/…"、"Assets/…" 两种虚拟前缀换成物理路径；裸名/相对路径原样留给 DXC
/// （DXC 以包含方目录 + 我们给的 "-I &lt;CGIncludesRoot&gt;" 解析，语义与 Unity 一致）。
/// 停机不下的「这个文件到底在不在」必须由本层"无条件遍历全篇"、逐条探盘来回答。
/// "为什么是无条件的"：Unity 官方明确「back-end builds import dependencies per shader,
/// not per shader variant」（该 issue 标记为 Won't Fix），也就是说"任何一个变体都不会编译到的分支里的死链"，
/// 在 Unity 里同样是构建错误"。按「当前激活的宏」去过滤 include，会把真实存在的依赖缺失藏起来。
/// "去重"：同一行会被 N 个 Pass 的编译单元各扫一遍（共享块更是 N 倍），
/// 因此必须"按位置去重、只报一次"，否则一条死链会变成满屏红。
/// "措辞纪律"：文案必须点明这是「依赖扫描层」的结论，
/// 且"绝不能"提示用户改用 "#if defined(K)" —— 那是把工具的实现方式强加给用户的代码。
/// </remarks>
public static class IncludeAudit
{
    /// <summary>遍历全篇（含共享块）审计依赖，按位置去重后返回诊断。</summary>
    public static List<ShaderDiagnosticItem> Audit(
        SourceShaderDocument document,
        ShaderLabParseResult parse,
        ShaderContextRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentNullException.ThrowIfNull(registry);

        var diagnostics = new List<ShaderDiagnosticItem>();
        var seen = new HashSet<(int Line, string Path)>();

        Scan(parse.SharedSnippets);
        Scan(parse.Passes);
        return diagnostics;

        void Scan(List<ShaderPassSnippet> snippets)
        {
            foreach (var snippet in snippets)
            {
                foreach (var include in snippet.Includes)
                {
                    if (!seen.Add((include.LineNumber, include.Path)))
                    {
                        continue;
                    }

                    var resolution = registry.ResolveInclude(include.Path, document.FilePath, verifyOnDisk: true);
                    if (resolution.Found)
                    {
                        continue;
                    }

                    diagnostics.Add(new ShaderDiagnosticItem
                    {
                        Severity = DiagnosticSeverity.Error,
                        Line = include.LineNumber,
                        Column = include.Column,
                        Code = DiagnosticEngineCodes.MissingInclude,
                        TargetUri = document.FileUri,
                        Message = resolution.DescribeMissing(include.Path.AsSpan()) +
                                  "（依赖扫描层结论：Unity 的依赖扫描不区分变体，未命中的 include 在任何变体下都是构建错误）",
                    });
                }
            }
        }
    }
}
