using MicroShader.ContextEngine;
using MicroShader.ObservabilityEngine;

namespace MicroShader.Server;

/// <summary>
/// 定位 Unity 工程根。
/// </summary>
/// <remarks>
/// 判定口径与模块 2 一致：目录下存在 "ProjectSettings/ProjectVersion.txt"。
/// "找不到不是错误" —— 打开游离的 ".shader" 时仍应给出切片与编译诊断，
/// 只是 include 解析会退化为「按物理同目录」这一条规则（模块 2 的规则 3）。
/// </remarks>
internal static class UnityProjectLocator
{
    public static bool TryFind(string? fromCli, out string projectRoot, out string detail)
    {
        if (!string.IsNullOrWhiteSpace(fromCli))
        {
            if (IsProjectRoot(fromCli))
            {
                projectRoot = Path.GetFullPath(fromCli);
                detail = "--project";
                return true;
            }

            detail = "--project 指向的目录不是 Unity 工程（缺 ProjectSettings/ProjectVersion.txt）：" + fromCli;
            projectRoot = string.Empty;
            return false;
        }

        var env = Environment.GetEnvironmentVariable("MICROSHADER_UNITY_PROJECT");
        if (!string.IsNullOrWhiteSpace(env) && IsProjectRoot(env))
        {
            projectRoot = Path.GetFullPath(env);
            detail = "MICROSHADER_UNITY_PROJECT";
            return true;
        }

        // 从当前目录向上找（编辑器把 cwd 设为工作区根是常见约定）。
        var dir = new DirectoryInfo(Environment.CurrentDirectory);
        while (dir is not null)
        {
            if (IsProjectRoot(dir.FullName))
            {
                projectRoot = dir.FullName;
                detail = "cwd 向上查找";
                return true;
            }

            dir = dir.Parent;
        }

        projectRoot = string.Empty;
        detail = "未找到 Unity 工程（降级为空 VFS 索引）";
        return false;
    }

    private static bool IsProjectRoot(string directory) =>
        !string.IsNullOrWhiteSpace(directory)
        && File.Exists(Path.Combine(directory, "ProjectSettings", "ProjectVersion.txt"));

    /// <summary>
    /// 构建 VFS 索引；任何异常都降级为 <see cref="VfsIndex.Empty"/> ——
    /// 索引坏了不应该让整个语言服务器起不来。
    /// </summary>
    public static VfsIndex BuildIndexOrDefault(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot))
        {
            return VfsIndex.Empty;
        }

        try
        {
            return VfsIndexBuilder.Build(projectRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            Log.Warn(LogCategories.Vfs, $"VFS 索引构建失败，降级为空索引：{ex.Message}");
            return VfsIndex.Empty;
        }
    }
}
