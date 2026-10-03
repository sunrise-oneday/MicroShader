namespace MicroShader.NavigationEngine;

/// <summary>
/// 把 "#include" 的目标字符串解析成可点击的物理 URI。
/// </summary>
/// <remarks>
/// 
/// "分两段"（详设 v2.0 第 4 条）：
/// <see cref="CandidatePath"/> 是"纯字符串"计算（首屏用，零 I/O）；
/// <see cref="Resolve"/> 才做存活性校验与「是否正被编辑器打开」查询（resolve 期用）。
/// 
/// "坏路径的处理"：解析不到、或目标文件不存在 → <see cref="Resolve"/> 返回 "null"
/// （规范允许），客户端表现为该链接不可点击；"绝不"给出一个点了会弹
/// "File not found" 的目标（详设 §六.4 的原始意图，落点从「不生成链接」改为「resolve 返回 null」）。
/// </remarks>
internal static class DocumentLinkResolver
{
    /// <summary>算出候选物理路径；算不出返回空串（不是错误，只是该条链接最终不可点击）。</summary>
    public static string CandidatePath(INavigationHost host, string documentUri, in IncludeLink link)
    {
        // ① 工程虚拟路径（Packages/… / Assets/…）：交给宿主的 VFS 路由表
        if (link.Virtual && host.TryResolve(link.Target, out var resolved))
        {
            return resolved;
        }

        // ② 相对/裸名：以"包含方所在目录"为基准（URP 头里大量使用）
        if (host.TryGetPhysicalPath(documentUri, out var documentPath))
        {
            if (host.TryResolveRelative(documentPath, link.Target, out var relative))
            {
                return relative;
            }

            var slash = documentPath.LastIndexOfAny(['/', '\\']);
            if (slash > 0)
            {
                var candidate = documentPath[..(slash + 1)] + link.Target.Replace('\\', '/');
                if (host.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // ③ 裸名兜底：仍走一次 VFS（有的工程把裸名映射到包根下）
        if (!link.Virtual && host.TryResolve(link.Target, out var bare))
        {
            return bare;
        }

        return string.Empty;
    }

    /// <summary>
    /// resolve 期：把候选路径变成最终 URI。
    /// </summary>
    /// <remarks>
    /// "脏文件优先"（详设 v2.0 第 10 条）：目标若正被编辑器打开，必须原样回传客户端
    /// didOpen 时用的 URI —— VS Code 里未存盘的新文件是 "untitled:" 方案，
    /// 自己拼 "file:///" 会让它去打开一个磁盘上并不存在的文件。
    /// </remarks>
    public static string? Resolve(INavigationHost host, string candidatePath)
    {
        if (string.IsNullOrEmpty(candidatePath))
        {
            return null;
        }

        if (host.TryGetOpenDocument(candidatePath, out var openUri, out _))
        {
            return openUri;
        }

        // resolve 是允许 I/O 的路径（详设第 4 条），但 I/O 一律走宿主：引擎只做纯逻辑。
        return host.Exists(candidatePath) ? host.ToFileUri(candidatePath) : null;
    }
}
