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

        // 兜底：VfsIndex 尚未解析到渲染管线包时（包索引按需构建、冷启动早期可能还没就绪），
        // 直接扫 <projectRoot>/Library/PackageCache 下的 render-pipelines.* 目录。
        // 与 prof-one 探针同口径；缺它会导致补全/诊断只拿到用户 C# 自定义值、漏掉全部 URP 内置值。
        if (!hasScriptablePipeline && !string.IsNullOrEmpty(projectRoot))
        {
            var pkgCache = Path.Combine(projectRoot, "Library", "PackageCache");
            if (Directory.Exists(pkgCache))
            {
                foreach (var dir in Directory.EnumerateDirectories(pkgCache))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Contains("render-pipelines", StringComparison.OrdinalIgnoreCase))
                    {
                        hasScriptablePipeline = true;
                        shaderRoots.Add(dir);
                    }
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

        // 同时进 trace：采集发生在 Log.Sink 汇装之前，只记 Log 会被静默丢掉，
        // 排障时「标签表到底采到了什么、花了多久」必须留痕。
        DiagTrace.Mark($"标签校验表：{schema.SourceFileCount} 个管线 shader、LightMode 值 {schema.LightModeValues.Count} 个、耗时 {stopwatch.ElapsedMilliseconds} ms、根={projectRoot}");

        Log.Info(
            LogCategories.Runtime,
            $"标签校验表：{schema.SourceFileCount} 个管线 shader、已知标签名 {schema.AllKnownTagNames.Count} 个、"
            + $"LightMode 合法值 {schema.LightModeValues.Count} 个、来源 {schema.Provenance}，耗时 {stopwatch.ElapsedMilliseconds} ms");

        return schema;
    }
}
