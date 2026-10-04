using System.Diagnostics;
using MicroShader.ContextEngine;
using MicroShader.ObservabilityEngine;
using MicroShader.ShaderLab;

namespace MicroShader.Server;

/// <summary>
/// 冷启动期从现场采集标签合法值，产出模块 9 的校验快照。
/// </summary>
/// <remarks>
/// "采集口径必须与规划文档一致"：
/// <list type="bullet">
/// <item>标签"名与值"只从"已安装的渲染管线包"采集 —— 不扫用户工程自己的 ".shader"，
/// 否则用户自己的拼写错误会把自己"洗白"进合法集合，SL0103/SL0105 就永远不会触发。</item>
/// <item>"LightMode" 的"自定义值"只从用户工程 C# 的 "new ShaderTagId("…")" 采集
/// （规划文档第 1.5 层）—— 那是「用户自定义 LightMode」唯一有权威依据的来源。</item>
/// </list>
/// 只扫渲染管线相关包（"com.unity.render-pipelines.*"）与内置包目录，
/// 不遍历全部 80 多个包：标签值只可能出现在渲染管线自己的 shader 里。
/// </remarks>
internal static class TagSchemaHarvester
{
    public static ShaderTagSchema Harvest(VfsIndex index, string projectRoot)
    {
        var shaderRoots = new List<string>();
        var hasScriptablePipeline = false;

        if (!ReferenceEquals(index, VfsIndex.Empty))
        {
            foreach (var package in index.Packages.Values)
            {
                if (package.PhysicalRoot is not { Length: > 0 } root || !Directory.Exists(root))
                {
                    continue;
                }

                if (package.Name.Contains("render-pipelines", StringComparison.OrdinalIgnoreCase))
                {
                    hasScriptablePipeline = true;
                    shaderRoots.Add(root);
                }
                else if (package.Source == PackageSourceKind.BuiltIn)
                {
                    // 内置包（含 2D/核心库）里也有 LightMode/RenderPipeline 的合法取值。
                    shaderRoots.Add(root);
                }
            }
        }

        var userCodeRoots = new List<string>();
        if (!string.IsNullOrEmpty(projectRoot))
        {
            var assets = Path.Combine(projectRoot, "Assets");
            if (Directory.Exists(assets))
            {
                userCodeRoots.Add(assets);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var schema = ShaderTagSchemaBuilder.Build(shaderRoots, userCodeRoots, hasScriptablePipeline);
        stopwatch.Stop();

        Log.Info(
            LogCategories.Runtime,
            $"标签校验表：{schema.SourceFileCount} 个管线 shader、已知标签名 {schema.AllKnownTagNames.Count} 个、"
            + $"LightMode 合法值 {schema.LightModeValues.Count} 个、来源 {schema.Provenance}，耗时 {stopwatch.ElapsedMilliseconds} ms");

        return schema;
    }
}
