using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MicroShader.ContextEngine;

/// <summary>发现文件的读取结论。</summary>
public enum UnityBridgeEndpointStatus : byte
{
    /// <summary>文件不存在：Unity 没跑、或桥接脚本未安装/被禁用。**这是正常态，不是错误**。</summary>
    Missing = 0,

    /// <summary>读到了半截/非法 JSON。桥接是原子写，出现即说明有第三方工具在动这个文件；容忍，下轮重试。</summary>
    Unreadable = 1,

    /// <summary>schema 版本不在支持集。拒绝连接（宁可不动，也不猜字段语义）。</summary>
    UnsupportedVersion = 2,

    /// <summary>域重载进行中：端口已被主动释放、进程仍活着。**别连**。</summary>
    Reloading = 3,

    /// <summary>pid 已死：发现文件是崩溃/强杀残留。保留文件（可能是另一个实例的），只报告。</summary>
    ProcessGone = 4,

    /// <summary>判活信息取不到（权限等）。**降级为告警，绝不删除文件、绝不判非法**。</summary>
    ProcessUnknown = 5,

    /// <summary>可以连接。</summary>
    Ready = 6,
}

/// <summary>pid 判活的三态结论。</summary>
public enum UnityBridgeProcessState : byte
{
    /// <summary>进程在。</summary>
    Alive = 0,

    /// <summary>进程不在（或 pid 已被复用）。</summary>
    Gone = 1,

    /// <summary>查不动（权限、异常）。**不得据此判死**。</summary>
    Unknown = 2,
}

/// <summary>
/// pid 判活探针。抽成委托是为了让自检能注入确定性结果（真实 <see cref="Process"/> 在受限环境下会抛权限异常）。
/// </summary>
/// <param name="pid">Unity 编辑器主进程 pid。</param>
/// <param name="startTimeUtc">发现文件记录的进程启动时间（抗 PID 复用的判据）。</param>
/// <param name="processName">发现文件记录的进程名（辅助信号）。</param>
public delegate UnityBridgeProcessState UnityBridgeProcessProbe(int pid, DateTimeOffset? startTimeUtc, string? processName);

/// <summary>
/// 契约 1 的发现文件（<c>&lt;Project&gt;/Library/MicroShader/endpoint.json</c>）在 LSP 侧的只读投影。
/// </summary>
public sealed class UnityBridgeEndpoint
{
    /// <summary>读取结论。</summary>
    public UnityBridgeEndpointStatus Status { get; init; }

    /// <summary>结论的人读原因（写进日志/自检输出）。</summary>
    public string? Detail { get; init; }

    /// <summary>发现文件物理路径（供排障引用）。</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>schema 版本（契约 1 的 <c>version</c>）。</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>桥接脚本版本。</summary>
    public string BridgeVersion { get; init; } = string.Empty;

    /// <summary>传输类型。当前只认 <c>"tcp"</c>。</summary>
    public string Transport { get; init; } = string.Empty;

    /// <summary>监听地址。</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>监听端口（动态分配，必须从发现文件读，不能写死）。</summary>
    public int Port { get; init; }

    /// <summary>线协议标识（<c>ldjson-1</c> = 换行分隔 JSON）。</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>Unity 编辑器主进程 pid。</summary>
    public int UnityPid { get; init; }

    /// <summary>Unity 编辑器进程名（辅助判活）。</summary>
    public string? UnityProcessName { get; init; }

    /// <summary>Unity 编辑器进程启动时间（抗 PID 复用的权威判据）。</summary>
    public DateTimeOffset? UnityStartTimeUtc { get; init; }

    /// <summary>Unity 版本（例如 <c>2022.3.62f3c1</c>）。</summary>
    public string? UnityVersion { get; init; }

    /// <summary>工程根（正斜杠归一化）。</summary>
    public string? ProjectPath { get; init; }

    /// <summary>工程名。</summary>
    public string? ProjectName { get; init; }

    /// <summary>活动色彩空间。</summary>
    public string? ActiveColorSpace { get; init; }

    /// <summary>活动构建目标。</summary>
    public string? ActiveBuildTarget { get; init; }

    /// <summary>构建目标的图形 API。</summary>
    public string? GraphicsApi { get; init; }

    /// <summary>当前渲染管线资产类型名。</summary>
    public string? PipelineAssetType { get; init; }

    /// <summary>桥接是否正处在域重载窗口内。</summary>
    public bool Reloading { get; init; }

    /// <summary>pid 判活结论。</summary>
    public UnityBridgeProcessState ProcessState { get; init; }

    /// <summary>文件指纹（长度 + 最后写入时间），用于「不变就不重解析」。</summary>
    public string Fingerprint { get; init; } = string.Empty;

    /// <summary>是否可连接。</summary>
    public bool IsReady => Status == UnityBridgeEndpointStatus.Ready;

    /// <summary>单行摘要（日志/自检用）。</summary>
    public string Describe()
    {
        var text = "Unity 桥接发现文件 " + Status + "（" + SourcePath + "）"
            + "，transport=" + (Transport.Length == 0 ? "<无>" : Transport)
            + "，addr=" + (Address.Length == 0 ? "<无>" : Address + ":" + Port.ToString(CultureInfo.InvariantCulture))
            + "，unityPid=" + UnityPid.ToString(CultureInfo.InvariantCulture);

        if (UnityVersion is { Length: > 0 })
        {
            text += "，unity=" + UnityVersion;
        }

        if (Detail is { Length: > 0 })
        {
            text += "；“" + Detail + "”";
        }

        return text;
    }
}

/// <summary>
/// 读取并校验发现文件。
/// </summary>
/// <remarks>
/// "读端的四不原则"（全部来自 v2.0 修正清单与 ADR-016）：
/// <list type="number">
/// <item>**不删**发现文件：任何「看起来不合法」都只报告，删了会把正常运行的实例踢下线；</item>
/// <item>**不抛**：文件可能被原子替换、可能被清理工具误伤，一切异常都退化为「未绑定」；</item>
/// <item>**不猜**：schema 版本不认识就拒绝，不按字段位置硬解；</item>
/// <item>**不越界**：只读自己工程根下的路径，不刺探同机其它工程。</item>
/// </list>
/// </remarks>
public static class UnityBridgeEndpointReader
{
    /// <summary>当前支持的 schema 版本集合。</summary>
    public const string SupportedVersion = "1.0.0";

    /// <summary>当前支持的线协议标识。</summary>
    public const string SupportedProtocol = "ldjson-1";

    private const int MaxEndpointBytes = 64 * 1024;

    /// <summary>读一次发现文件。<paramref name="probe"/> 为 null 时使用真实进程判活。</summary>
    public static UnityBridgeEndpoint Read(UnityProjectLayout layout, UnityBridgeProcessProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return ReadPath(layout.EndpointFilePath, probe);
    }

    /// <summary>按路径读一次发现文件（自检直接调用这个入口）。</summary>
    public static UnityBridgeEndpoint ReadPath(string endpointFilePath, UnityBridgeProcessProbe? probe = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointFilePath);

        var native = VfsPath.ToNative(endpointFilePath);
        var fingerprint = CaptureFingerprint(native);

        string text;
        try
        {
            if (!File.Exists(native))
            {
                return new UnityBridgeEndpoint
                {
                    Status = UnityBridgeEndpointStatus.Missing,
                    SourcePath = endpointFilePath,
                    Fingerprint = fingerprint,
                    Detail = "发现文件不存在（Unity 未运行，或桥接脚本未安装/被禁用）",
                };
            }

            // FileShare.ReadWrite | Delete：桥接用 "*.tmp + 替换" 原子写，
            // 读端若不带 Delete 共享位，替换会撞上共享冲突。
            using var stream = new FileStream(native, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxEndpointBytes)
            {
                return new UnityBridgeEndpoint
                {
                    Status = UnityBridgeEndpointStatus.Unreadable,
                    SourcePath = endpointFilePath,
                    Fingerprint = fingerprint,
                    Detail = "发现文件超过 " + MaxEndpointBytes.ToString(CultureInfo.InvariantCulture) + " 字节，拒绝解析",
                };
            }

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            text = reader.ReadToEnd();
        }
        catch (IOException exception)
        {
            return Unreadable(endpointFilePath, fingerprint, "读取失败：" + exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Unreadable(endpointFilePath, fingerprint, "读取被拒：" + exception.Message);
        }

        return Parse(text, endpointFilePath, fingerprint, probe);
    }

    /// <summary>解析发现文件文本（自检与读端共用同一条路径）。</summary>
    public static UnityBridgeEndpoint Parse(
        string text,
        string endpointFilePath,
        string fingerprint = "",
        UnityBridgeProcessProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException exception)
        {
            return Unreadable(endpointFilePath, fingerprint, "JSON 解析失败：" + exception.Message);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Unreadable(endpointFilePath, fingerprint, "根节点不是对象");
            }

            var version = GetString(root, "version") ?? string.Empty;
            if (!string.Equals(version, SupportedVersion, StringComparison.Ordinal))
            {
                return new UnityBridgeEndpoint
                {
                    Status = UnityBridgeEndpointStatus.UnsupportedVersion,
                    SourcePath = endpointFilePath,
                    Fingerprint = fingerprint,
                    Version = version,
                    Detail = "发现文件 schema 版本为 " + (version.Length == 0 ? "<缺失>" : version)
                        + "，本端只支持 " + SupportedVersion,
                };
            }

            var transport = GetString(root, "transport") ?? string.Empty;
            var protocol = GetString(root, "protocol") ?? SupportedProtocol;
            var address = GetString(root, "address") ?? string.Empty;
            var port = GetInt(root, "port");
            var unityPid = GetInt(root, "unityPid");
            var reloading = GetBool(root, "reloading");

            if (!string.Equals(transport, "tcp", StringComparison.OrdinalIgnoreCase))
            {
                return new UnityBridgeEndpoint
                {
                    Status = UnityBridgeEndpointStatus.UnsupportedVersion,
                    SourcePath = endpointFilePath,
                    Fingerprint = fingerprint,
                    Version = version,
                    Transport = transport,
                    Detail = "transport='" + transport + "' 未实现（本版只认 tcp；pipe/ws 见 ADR-016 的后续项）",
                };
            }

            if (address.Length == 0 || port is <= 0 or > 65535 || unityPid <= 0)
            {
                return Unreadable(
                    endpointFilePath,
                    fingerprint,
                    "字段不完整：address='" + address + "'，port=" + port.ToString(CultureInfo.InvariantCulture)
                    + "，unityPid=" + unityPid.ToString(CultureInfo.InvariantCulture));
            }

            var startTime = ParseTimestamp(GetString(root, "unityStartTimeUtc"));
            var processName = GetString(root, "unityProcessName");

            var processState = (probe ?? UnityProcessLiveness.Probe)(unityPid, startTime, processName);
            var status = processState switch
            {
                UnityBridgeProcessState.Alive => UnityBridgeEndpointStatus.Ready,
                UnityBridgeProcessState.Gone => UnityBridgeEndpointStatus.ProcessGone,
                _ => UnityBridgeEndpointStatus.ProcessUnknown,
            };
            var detail = processState switch
            {
                UnityBridgeProcessState.Alive => null,
                UnityBridgeProcessState.Gone => "pid 已不在（发现文件是崩溃/强杀残留，不删，只报告）",
                _ => "pid 判活信息取不到（权限等），降级为告警且不连接",
            };

            // 域重载窗口：端口已被主动释放，但进程还活着。此时连接要么被拒、要么（更糟）
            // 撞上抢先占住该端口的其它进程。判活照做（保留在字段里供排障），但状态明确不连。
            if (reloading)
            {
                status = UnityBridgeEndpointStatus.Reloading;
                detail = "发现文件标记 reloading=true（域重载中，端口已释放）";
            }

            return new UnityBridgeEndpoint
            {
                Status = status,
                Detail = detail,
                ProcessState = processState,
                SourcePath = endpointFilePath,
                Fingerprint = fingerprint,
                Version = version,
                BridgeVersion = GetString(root, "bridgeVersion") ?? string.Empty,
                Transport = transport,
                Protocol = protocol,
                Address = address,
                Port = port,
                UnityPid = unityPid,
                UnityProcessName = processName,
                UnityStartTimeUtc = startTime,
                UnityVersion = GetString(root, "unityVersion"),
                ProjectPath = GetString(root, "projectPath"),
                ProjectName = GetString(root, "projectName"),
                ActiveColorSpace = GetString(root, "activeColorSpace"),
                ActiveBuildTarget = GetString(root, "activeBuildTarget"),
                GraphicsApi = GetString(root, "graphicsApi"),
                PipelineAssetType = GetString(root, "pipelineAssetType"),
                Reloading = reloading,
            };
        }
    }

    /// <summary>轻量指纹：只有变化了才值得重新解析整个文件。</summary>
    public static string CaptureFingerprint(string endpointFilePath)
    {
        try
        {
            var info = new FileInfo(VfsPath.ToNative(endpointFilePath));
            return info.Exists
                ? info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                : "absent";
        }
        catch (IOException)
        {
            return "unreadable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unreadable";
        }
    }

    private static UnityBridgeEndpoint Unreadable(string path, string fingerprint, string detail)
        => new()
        {
            Status = UnityBridgeEndpointStatus.Unreadable,
            SourcePath = path,
            Fingerprint = fingerprint,
            Detail = detail,
        };

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static bool GetBool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }
}

/// <summary>真实进程判活（照抄 Rider 的判据并补三条实测语义）。</summary>
public static class UnityProcessLiveness
{
    /// <summary>启动时间允许的偏差（秒）。超过即判为 PID 复用。</summary>
    private const double StartTimeToleranceSeconds = 2.0;

    /// <summary>
    /// 判活。
    /// </summary>
    /// <remarks>
    /// 三条实测语义（v2.0 清单第 3 条 / 附-补第 3 条）：
    /// <list type="bullet">
    /// <item><c>Process.ProcessName</c> 在进程已退出时抛 <c>InvalidOperationException</c> —— 不 try/catch 就会把 LSP 打挂；</item>
    /// <item>PID 复用判据必须是 <c>(pid, StartTime)</c>，不能只看进程名（同机可能有别的名字含 Unity 的程序）；</item>
    /// <item>取不到信息（管理员进程的 <c>StartTime</c> 会抛拒绝访问）→ 返回 <see cref="UnityBridgeProcessState.Unknown"/>，
    /// **降级为告警，绝不判为非法**。</item>
    /// </list>
    /// </remarks>
    public static UnityBridgeProcessState Probe(int pid, DateTimeOffset? startTimeUtc, string? processName)
    {
        if (pid <= 0)
        {
            return UnityBridgeProcessState.Unknown;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);

            if (process.HasExited)
            {
                return UnityBridgeProcessState.Gone;
            }

            if (startTimeUtc is { } expected)
            {
                try
                {
                    var actual = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    if (Math.Abs((actual - expected).TotalSeconds) > StartTimeToleranceSeconds)
                    {
                        // pid 被复用了：对这个 pid 判活会误导读端去连一个早已死掉（或根本不是 Unity）的实例。
                        return UnityBridgeProcessState.Gone;
                    }
                }
                catch (Exception)
                {
                    // 目标以更高权限运行时会拒绝读 StartTime：**不足以判死**，继续看进程名。
                }
            }

            if (processName is { Length: > 0 })
            {
                try
                {
                    if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                    {
                        return UnityBridgeProcessState.Unknown;
                    }
                }
                catch (Exception)
                {
                    return UnityBridgeProcessState.Unknown;
                }
            }

            return UnityBridgeProcessState.Alive;
        }
        catch (ArgumentException)
        {
            // 进程不存在（官方语义）。
            return UnityBridgeProcessState.Gone;
        }
        catch (InvalidOperationException)
        {
            // 两次调用之间退出了。
            return UnityBridgeProcessState.Gone;
        }
        catch (Exception)
        {
            return UnityBridgeProcessState.Unknown;
        }
    }
}
