using System.Collections.Immutable;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace MicroShader.ContextEngine;

/// <summary>契约 2：<c>onShaderContextChanged</c> 载荷的解析。</summary>
/// <remarks>
/// "为什么手写 <see cref="JsonDocument"/> 而不是 JsonSerializer"：保持零第三方、零反射，
/// 与模块 8 的 JSON-RPC 读取同一条路（AOT 裁剪面可控）。
/// </remarks>
public static class UnityBridgePayload
{
    /// <summary>上下文变更事件名（契约 2 的唯一事件）。</summary>
    public const string ContextChangedEvent = "onShaderContextChanged";

    /// <summary>单帧字节上限。超限即丢弃（对端异常/版本错配的第一道闸）。</summary>
    public const int MaxPayloadBytes = 64 * 1024;

    /// <summary>单个数组的元素上限。</summary>
    public const int MaxEntries = 512;

    /// <summary>
    /// 解析一帧 LDJSON 载荷。
    /// </summary>
    /// <param name="json">不含换行的单行 JSON。</param>
    /// <param name="context">解析成功时的不可变上下文。</param>
    /// <param name="detail">失败/忽略原因。</param>
    /// <returns>是否产出了一个可发布的上下文。</returns>
    public static bool TryParse(string json, out DynamicShaderContext context, out string? detail)
    {
        context = DynamicShaderContext.Empty;
        detail = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            detail = "空帧";
            return false;
        }

        if (json.Length > MaxPayloadBytes)
        {
            detail = "帧超过 " + MaxPayloadBytes.ToString(CultureInfo.InvariantCulture) + " 字节，已丢弃";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            detail = "JSON 非法：" + exception.Message;
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                detail = "根节点不是对象";
                return false;
            }

            var eventName = root.TryGetProperty("event", out var eventElement) && eventElement.ValueKind == JsonValueKind.String
                ? eventElement.GetString()
                : null;

            if (!string.Equals(eventName, ContextChangedEvent, StringComparison.Ordinal))
            {
                // 未知事件一律忽略（**不报错**）：协议是前向兼容的，未来加的事件不能把老读端打挂。
                detail = "未知事件 '" + (eventName ?? "<缺失>") + "'，已忽略";
                return false;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                detail = "缺少 data 节点";
                return false;
            }

            var keywords = ReadStringArray(data, "activeKeywords");
            var defined = ImmutableArray<string>.Empty;
            var undefined = ImmutableArray<string>.Empty;

            if (data.TryGetProperty("globalDefines", out var defines) && defines.ValueKind == JsonValueKind.Object)
            {
                defined = ReadStringArray(defines, "defined");
                undefined = ReadStringArray(defines, "undefined");
            }

            var updatedAt = ReadTimestamp(root);

            context = new DynamicShaderContext
            {
                ActiveKeywords = keywords,
                DefinedMacros = defined,
                UndefinedMacros = undefined,
                IsLive = true,
                UpdatedAtUtc = updatedAt,
            };

            return true;
        }
    }

    /// <summary>内容等价判定：避免「同一份状态又发布一次」推进注册表版本、把诊断缓存整片打掉。</summary>
    public static bool HasSameContent(DynamicShaderContext left, DynamicShaderContext right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return left.IsLive == right.IsLive
            && SameArray(left.ActiveKeywords, right.ActiveKeywords)
            && SameArray(left.DefinedMacros, right.DefinedMacros)
            && SameArray(left.UndefinedMacros, right.UndefinedMacros);
    }

    private static bool SameArray(ImmutableArray<string> left, ImmutableArray<string> right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableArray<string> ReadStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return ImmutableArray<string>.Empty;
        }

        // 先探测数量，再决定是否分配（空数组是常态，不为此付出分配）。
        var length = array.GetArrayLength();
        if (length == 0)
        {
            return ImmutableArray<string>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<string>(Math.Min(length, MaxEntries));
        foreach (var item in array.EnumerateArray())
        {
            if (builder.Count >= MaxEntries)
            {
                break;
            }

            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var value = item.GetString();
            if (!string.IsNullOrEmpty(value))
            {
                builder.Add(value);
            }
        }

        return builder.ToImmutable();
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root)
    {
        if (root.TryGetProperty("timestamp", out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var seconds)
            && seconds > 0
            && seconds < 4102444800)   // 2100-01-01：越界的对端时钟一律不信
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return DateTimeOffset.UtcNow;
    }
}

/// <summary>Unity 桥接的链路状态。</summary>
public enum UnityBridgeLinkState : byte
{
    /// <summary>尚未开始（或已被释放）。</summary>
    Idle = 0,

    /// <summary>发现文件不存在：Unity 没跑或桥接未装。**正常态**。</summary>
    Unbound = 1,

    /// <summary>域重载中：等它重写发现文件。</summary>
    Reloading = 2,

    /// <summary>pid 已死 / 判活未知：发现文件不可信。</summary>
    Stale = 3,

    /// <summary>正在连接。</summary>
    Connecting = 4,

    /// <summary>已连接，正在接收上下文广播。</summary>
    Connected = 5,
}

/// <summary>桥接客户端选项。</summary>
public sealed record UnityBridgeOptions
{
    /// <summary>未绑定时的重试间隔。</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>断线后的重连延时。</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>TCP 连接超时。</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>空闲复检间隔（socket 读超时）：用于发现「域重载后重写了发现文件」。</summary>
    public TimeSpan IdleRecheckInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>链路状态的一份不可变快照（供日志/状态栏读取，无锁）。</summary>
public sealed class UnityBridgeStatus
{
    /// <summary>链路状态。</summary>
    public UnityBridgeLinkState State { get; init; } = UnityBridgeLinkState.Idle;

    /// <summary>当前端口（0 = 未绑定）。</summary>
    public int Port { get; init; }

    /// <summary>Unity 编辑器 pid。</summary>
    public int UnityPid { get; init; }

    /// <summary>Unity 版本。</summary>
    public string? UnityVersion { get; init; }

    /// <summary>原因/细节（人读）。</summary>
    public string? Detail { get; init; }

    /// <summary>最近一次状态变更时间。</summary>
    public DateTimeOffset ChangedAtUtc { get; init; }

    /// <summary>最近一次成功应用载荷的时间。</summary>
    public DateTimeOffset? LastPayloadUtc { get; init; }

    /// <summary>累计应用（真实变化并被发布）的载荷数。</summary>
    public long PayloadsApplied { get; init; }

    /// <summary>累计收到的帧数（含未产生变化的重复帧）。</summary>
    public long FramesReceived { get; init; }

    /// <summary>发现文件路径。</summary>
    public string EndpointPath { get; init; } = string.Empty;

    /// <summary>单行摘要。</summary>
    public string Describe()
    {
        var text = State switch
        {
            UnityBridgeLinkState.Connected => "已连接 Unity（pid " + UnityPid.ToString(CultureInfo.InvariantCulture)
                + "，127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture) + "，已收 " + FramesReceived.ToString(CultureInfo.InvariantCulture)
                + " 帧/应用 " + PayloadsApplied.ToString(CultureInfo.InvariantCulture) + " 次）",
            UnityBridgeLinkState.Connecting => "正在连接 Unity " + Port.ToString(CultureInfo.InvariantCulture),
            UnityBridgeLinkState.Reloading => "Unity 域重载中，等待重写发现文件",
            UnityBridgeLinkState.Stale => "发现文件不可信（" + (Detail ?? "判活失败") + "）",
            UnityBridgeLinkState.Unbound => "未绑定 Unity（" + (Detail ?? "发现文件不存在") + "）",
            _ => "未启动",
        };

        if (UnityVersion is { Length: > 0 })
        {
            text += "，Unity " + UnityVersion;
        }

        return text;
    }
}

/// <summary>
/// Unity 桥接客户端（拓扑：**Unity 当服务端，本端外连**，ADR-016 的默认路线）。
/// </summary>
/// <remarks>
/// "为什么不做成 async Task"：这是一条**低频、与 LSP 主循环完全无关**的旁路。
/// 用一条专用后台线程 + 同步 socket 读写，换来三件事：
/// <list type="number">
/// <item>读超时（<c>ReceiveTimeout</c>）直接映射「复检发现文件」的节奏，不需要每帧重建 CTS；</item>
/// <item>不往线程池塞长期挂起的 await（NFR-3a 要求热路径零分配，这里索性一次都不分配）；</item>
/// <item>释放路径简单：<c>Dispose</c> 关掉活动 socket，线程在 1 秒内自然退出。</item>
/// </list>
/// "四不原则"：发现文件缺失/破损/判活失败一律退化为「未绑定」，**绝不删除、绝不报错**；
/// 只有「真实变化」才推进注册表版本（否则会白白打掉诊断缓存）。
/// </remarks>
public sealed class UnityBridgeClient : IDisposable
{
    private readonly UnityProjectLayout _layout;
    private readonly ShaderContextRegistry _registry;
    private readonly UnityBridgeOptions _options;
    private readonly UnityBridgeProcessProbe? _probe;
    private readonly Action<string>? _log;
    private readonly Lock _lifecycleGate = new();

    private Thread? _thread;
    private TcpClient? _activeClient;
    private volatile bool _disposed;
    private UnityBridgeStatus _status;

    public UnityBridgeClient(
        UnityProjectLayout layout,
        ShaderContextRegistry registry,
        UnityBridgeOptions? options = null,
        UnityBridgeProcessProbe? probe = null,
        Action<string>? log = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? new UnityBridgeOptions();
        _probe = probe;
        _log = log;
        _status = new UnityBridgeStatus
        {
            State = UnityBridgeLinkState.Idle,
            EndpointPath = layout.EndpointFilePath,
            ChangedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>发现文件路径（排障引用）。</summary>
    public string EndpointPath => _layout.EndpointFilePath;

    /// <summary>当前状态快照（一次 volatile 读）。</summary>
    public UnityBridgeStatus Status => Volatile.Read(ref _status);

    /// <summary>启动后台链路。重复调用是幂等的。</summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_disposed || _thread is not null)
            {
                return;
            }

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "MicroShader.UnityBridge",
            };
            _thread.Start();
        }
    }

    /// <summary>停止并回收后台线程（上限 <see cref="UnityBridgeOptions.ConnectTimeout"/> + 1 秒）。</summary>
    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CloseActiveClient();
        }

        var thread = _thread;
        if (thread is not null && !thread.Join(_options.ConnectTimeout + TimeSpan.FromSeconds(1)))
        {
            _log?.Invoke("[MicroShader] Unity 桥接线程未在时限内退出（后台线程，不阻塞退出）");
        }

        _thread = null;

        // 断开即「未绑定」：留着上一次的宏状态会让诊断带着一个已经不存在的 Unity 的假设继续跑。
        PublishUnbound();
    }

    // ── 主循环 ──────────────────────────────────────────────────────────────

    private void Loop()
    {
        while (!_disposed)
        {
            var endpoint = UnityBridgeEndpointReader.Read(_layout, _probe);
            if (!endpoint.IsReady)
            {
                PublishUnboundIfLive();
                SetStatus(MapState(endpoint.Status), endpoint, endpoint.Detail);
                Sleep(_options.PollInterval);
                continue;
            }

            SetStatus(UnityBridgeLinkState.Connecting, endpoint, null);

            TcpClient client;
            try
            {
                client = Connect(endpoint);
            }
            catch (Exception exception)
            {
                PublishUnboundIfLive();
                SetStatus(
                    UnityBridgeLinkState.Unbound,
                    endpoint,
                    "连接 127.0.0.1:" + endpoint.Port.ToString(CultureInfo.InvariantCulture) + " 失败：" + exception.Message);
                Sleep(_options.ReconnectDelay);
                continue;
            }

            _activeClient = client;
            try
            {
                SetStatus(UnityBridgeLinkState.Connected, endpoint, null);
                ReadLoop(client, endpoint);
            }
            catch (Exception exception)
            {
                SetStatus(UnityBridgeLinkState.Unbound, endpoint, "链路异常：" + exception.Message);
            }
            finally
            {
                _activeClient = null;
                try
                {
                    client.Dispose();
                }
                catch (Exception)
                {
                    // 忽略。
                }
            }

            PublishUnboundIfLive();
            Sleep(_options.ReconnectDelay);
        }

        SetStatus(UnityBridgeLinkState.Idle, null, null);
    }

    private TcpClient Connect(UnityBridgeEndpoint endpoint)
    {
        var client = new TcpClient
        {
            NoDelay = true,
            ReceiveTimeout = (int)_options.IdleRecheckInterval.TotalMilliseconds,
            SendTimeout = (int)_options.ConnectTimeout.TotalMilliseconds,
        };

        try
        {
            var connect = client.ConnectAsync(endpoint.Address, endpoint.Port);
            if (!connect.Wait(_options.ConnectTimeout))
            {
                throw new TimeoutException(
                    "连接超时 " + _options.ConnectTimeout.TotalMilliseconds.ToString(CultureInfo.InvariantCulture) + "ms");
            }

            connect.GetAwaiter().GetResult();
            return client;
        }
        catch (Exception)
        {
            client.Dispose();
            throw;
        }
    }

    private void ReadLoop(TcpClient client, UnityBridgeEndpoint endpoint)
    {
        var stream = client.GetStream();
        var chunk = new byte[8 * 1024];
        var line = new byte[UnityBridgePayload.MaxPayloadBytes];
        var lineLength = 0;
        var lastFingerprint = endpoint.Fingerprint;

        while (!_disposed)
        {
            int read;
            try
            {
                read = stream.Read(chunk, 0, chunk.Length);
            }
            catch (IOException)
            {
                // 读超时不是断开：借这个节奏复检发现文件（域重载会重写它）。
                if (EndpointChanged(ref lastFingerprint))
                {
                    return;
                }

                continue;
            }

            if (read <= 0)
            {
                return;   // 对端关闭。
            }

            for (var i = 0; i < read; i++)
            {
                var value = chunk[i];
                if (value == (byte)'\n')
                {
                    if (lineLength > 0)
                    {
                        ApplyFrame(line, lineLength);
                        lineLength = 0;
                    }

                    continue;
                }

                if (value == (byte)'\r')
                {
                    continue;
                }

                if (lineLength >= line.Length)
                {
                    // 超长帧：丢弃整行，不让它无限增长（协议是长度受限的单行 JSON）。
                    lineLength = 0;
                    _log?.Invoke("[MicroShader] Unity 桥接帧超过 "
                        + UnityBridgePayload.MaxPayloadBytes.ToString(CultureInfo.InvariantCulture) + " 字节，已丢弃");
                    continue;
                }

                line[lineLength++] = value;
            }
        }
    }

    private bool EndpointChanged(ref string lastFingerprint)
    {
        var current = UnityBridgeEndpointReader.CaptureFingerprint(_layout.EndpointFilePath);
        if (string.Equals(current, lastFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        // 文件被重写过（启动/域重载/环境变化）→ 断开重来：重连后必然拿到一份新的快照，
        // 比在原地猜测「新端口是不是还有效」更简单也更安全。
        lastFingerprint = current;
        return true;
    }

    private void ApplyFrame(byte[] buffer, int length)
    {
        var text = Encoding.UTF8.GetString(buffer, 0, length);
        if (!UnityBridgePayload.TryParse(text, out var context, out var detail))
        {
            if (detail is { Length: > 0 })
            {
                _log?.Invoke("[MicroShader] Unity 桥接帧被忽略：" + detail);
            }

            return;
        }

        var frames = Status.FramesReceived + 1;
        var current = _registry.Current.Dynamic;

        if (UnityBridgePayload.HasSameContent(current, context))
        {
            // 内容一致：只更新统计，**不推进注册表版本**（否则每次心跳都会打掉诊断缓存）。
            SetStatus(UnityBridgeLinkState.Connected, null, null, frames, null);
            return;
        }

        _registry.PublishDynamic(context);
        SetStatus(UnityBridgeLinkState.Connected, null, null, frames, DateTimeOffset.UtcNow);
    }

    private void PublishUnboundIfLive()
    {
        if (!_registry.Current.Dynamic.IsLive)
        {
            return;
        }

        PublishUnbound();
    }

    private void PublishUnbound()
    {
        if (!_registry.Current.Dynamic.IsLive)
        {
            return;
        }

        _registry.PublishDynamic(DynamicShaderContext.Empty);
    }

    private void SetStatus(
        UnityBridgeLinkState state,
        UnityBridgeEndpoint? endpoint,
        string? detail,
        long? framesReceived = null,
        DateTimeOffset? lastPayload = null)
    {
        while (true)
        {
            var current = Volatile.Read(ref _status);
            var sameState = current.State == state
                && string.Equals(current.Detail, detail, StringComparison.Ordinal);

            if (sameState && framesReceived is null && lastPayload is null)
            {
                return;   // 状态没变：不制造日志噪音（ADR-027 的纪律：只在跃迁时记）。
            }

            var next = new UnityBridgeStatus
            {
                State = state,
                Detail = detail,
                Port = endpoint?.Port ?? current.Port,
                UnityPid = endpoint?.UnityPid ?? current.UnityPid,
                UnityVersion = endpoint?.UnityVersion ?? current.UnityVersion,
                EndpointPath = _layout.EndpointFilePath,
                ChangedAtUtc = sameState ? current.ChangedAtUtc : DateTimeOffset.UtcNow,
                FramesReceived = framesReceived ?? current.FramesReceived,
                LastPayloadUtc = lastPayload ?? current.LastPayloadUtc,
                PayloadsApplied = lastPayload is null ? current.PayloadsApplied : current.PayloadsApplied + 1,
            };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _status, next, current), current))
            {
                if (!sameState)
                {
                    _log?.Invoke("[MicroShader] Unity 桥接：" + next.Describe()
                        + (detail is { Length: > 0 } ? "（" + detail + "）" : string.Empty));
                }

                return;
            }
        }
    }

    private void CloseActiveClient()
    {
        var client = _activeClient;
        if (client is null)
        {
            return;
        }

        try
        {
            // 关掉 socket 会让阻塞中的 Read/Connect 立刻抛出，线程随即退出。
            client.Close();
        }
        catch (Exception)
        {
            // 忽略。
        }
    }

    private void Sleep(TimeSpan duration)
    {
        var remaining = duration;
        while (!_disposed && remaining > TimeSpan.Zero)
        {
            var slice = remaining > TimeSpan.FromMilliseconds(200) ? TimeSpan.FromMilliseconds(200) : remaining;
            Thread.Sleep(slice);
            remaining -= slice;
        }
    }

    private static UnityBridgeLinkState MapState(UnityBridgeEndpointStatus status) => status switch
    {
        UnityBridgeEndpointStatus.Reloading => UnityBridgeLinkState.Reloading,
        UnityBridgeEndpointStatus.ProcessGone => UnityBridgeLinkState.Stale,
        UnityBridgeEndpointStatus.ProcessUnknown => UnityBridgeLinkState.Stale,
        _ => UnityBridgeLinkState.Unbound,
    };
}
