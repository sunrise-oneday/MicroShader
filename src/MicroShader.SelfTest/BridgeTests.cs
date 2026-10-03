using System.Net;
using System.Net.Sockets;
using System.Text;
using MicroShader.ContextEngine;
using MicroShader.ShaderLab;

namespace MicroShader.SelfTest;

/// <summary>
/// 模块 11B（Unity 桥接）自检套件。
/// </summary>
/// <remarks>
/// 三条防线：
/// <list type="number">
/// <item><b>契约解析与判活</b>（<c>Bridge.Endpoint</c> / <c>Bridge.Liveness</c>）：
/// 覆盖「缺文件 / 半截 JSON / 版本不认识 / reloading / pid 死 / 判活取不到」六种读端形态 ——
/// 每一条都要求读端**退化**而不是抛异常或删文件。</item>
/// <item><b>真回环</b>（<c>Bridge.Loopback</c>）：起一个真的 <see cref="TcpListener"/>，
/// 用真实字节跑通「发现文件 → 连接 → 广播 → 注册表快照」整条链路，
/// 并验证「重复帧不推进版本」与「断开即回退为未绑定」。</item>
/// <item><b>离线零漂移</b>（<c>Bridge.Assembly</c>）：桥接不在线时，
/// 装配出来的渲染文本必须与引入本模块前**逐字节相同**（黄金判据的保护网）。</item>
/// </list>
/// </remarks>
internal static class BridgeTests
{
    private const string EndpointJson = """
        {
          "version": "1.0.0",
          "bridgeVersion": "0.1.0",
          "unityPid": 4242,
          "unityProcessName": "Unity",
          "unityStartTimeUtc": "2026-09-30T14:28:32.6577109+00:00",
          "unityVersion": "2022.3.62f3c1",
          "projectPath": "D:/Projects/Demo",
          "projectName": "Demo",
          "transport": "tcp",
          "address": "127.0.0.1",
          "port": 51234,
          "protocol": "ldjson-1",
          "activeColorSpace": "Linear",
          "activeBuildTarget": "StandaloneWindows64",
          "graphicsApi": "Direct3D11",
          "pipelineAssetType": "UniversalRenderPipelineAsset",
          "reloading": false,
          "startedAtUtc": "2026-09-30T14:42:11.5855941+00:00"
        }
        """;

    public static void Register()
    {
        TestSuite.Add("Bridge.Endpoint", "发现文件完整解析出传输描述符与工程身份", EndpointParses);
        TestSuite.Add("Bridge.Endpoint", "发现文件缺失时退化为未绑定而不是报错", EndpointMissing);
        TestSuite.Add("Bridge.Endpoint", "半截 JSON 只报不可读、绝不抛异常也不删文件", EndpointTruncated);
        TestSuite.Add("Bridge.Endpoint", "schema 版本不认识时拒绝连接而不是猜字段", EndpointUnsupportedVersion);
        TestSuite.Add("Bridge.Endpoint", "reloading=true 时明确不连接（域重载窗口）", EndpointReloading);
        TestSuite.Add("Bridge.Endpoint", "transport 非 tcp 时拒绝（本版只认 tcp）", EndpointUnsupportedTransport);
        TestSuite.Add("Bridge.Endpoint", "字段缺失/越界（port=0）判为不可读", EndpointIncompleteFields);

        TestSuite.Add("Bridge.Liveness", "pid 已死时判为残留、文件保留不删", LivenessProcessGone);
        TestSuite.Add("Bridge.Liveness", "判活取不到信息时降级为告警而不是判非法", LivenessUnknownDegrades);
        TestSuite.Add("Bridge.Liveness", "pid 复用时按启动时间判死（进程名不足为凭）", LivenessPidReuse);

        TestSuite.Add("Bridge.Payload", "契约 2 载荷解析出关键字与宏三态", PayloadParses);
        TestSuite.Add("Bridge.Payload", "未知事件被忽略而不是报错（前向兼容）", PayloadUnknownEventIgnored);
        TestSuite.Add("Bridge.Payload", "非法 JSON 只记原因、不发布任何上下文", PayloadMalformed);
        TestSuite.Add("Bridge.Payload", "内容等价判定忽略时间戳", PayloadContentEquivalence);

        TestSuite.Add("Bridge.Contract", "契约 1 的 schema 与读端字段集不漂移", EndpointSchemaMatchesReader);

        TestSuite.Add("Bridge.Loopback", "真实 TCP 回环：发现文件 → 连接 → 注册表快照", LoopbackAppliesContext);
        TestSuite.Add("Bridge.Loopback", "重复帧不推进注册表版本（不打掉诊断缓存）", LoopbackDuplicateFrameKeepsVersion);
        TestSuite.Add("Bridge.Loopback", "对端断开后回退为未绑定（不残留上一个编辑器的假设）", LoopbackDisconnectResetsContext);

        TestSuite.Add("Bridge.Assembly", "桥接离线时装配文本逐字节不变（黄金判据保护网）", OfflineAssemblyUnchanged);
        TestSuite.Add("Bridge.Assembly", "桥接在线时把实测宏写进注入段且先 undef 再 define", OnlineInjectsMacros);
        TestSuite.Add("Bridge.Assembly", "上报的非法宏名/宏值被拒（结构不变量不被外部数据打破）", OnlineRejectsUnsafeMacros);
        TestSuite.Add("Bridge.Assembly", "注册表的活动关键字能改写变体选择（ADR-006 → 实测）", OnlineSelectsActiveKeyword);
    }

    // ── 契约 1：发现文件 ──────────────────────────────────────────────────

    private static void EndpointParses()
    {
        var endpoint = UnityBridgeEndpointReader.Parse(EndpointJson, "endpoint.json", "fp", Alive);

        Check.Equal(UnityBridgeEndpointStatus.Ready, endpoint.Status, "完整且 pid 存活时应可连接");
        Check.Equal("tcp", endpoint.Transport, "transport");
        Check.Equal("127.0.0.1", endpoint.Address, "address");
        Check.Equal(51234, endpoint.Port, "端口必须从发现文件读，不能写死");
        Check.Equal("ldjson-1", endpoint.Protocol, "线协议标识");
        Check.Equal(4242, endpoint.UnityPid, "unityPid");
        Check.Equal("2022.3.62f3c1", endpoint.UnityVersion, "unityVersion");
        Check.Equal("D:/Projects/Demo", endpoint.ProjectPath, "projectPath");
        Check.Equal("Linear", endpoint.ActiveColorSpace, "activeColorSpace");
        Check.Equal("StandaloneWindows64", endpoint.ActiveBuildTarget, "activeBuildTarget");
        Check.Equal("Direct3D11", endpoint.GraphicsApi, "graphicsApi");
        Check.Equal("UniversalRenderPipelineAsset", endpoint.PipelineAssetType, "pipelineAssetType");
        Check.True(!endpoint.Reloading, "reloading 应为 false");
        TestCaseHelpers.Report(endpoint.Describe());
    }

    private static void EndpointMissing()
    {
        using var sandbox = BridgeSandbox.Create();
        var endpoint = UnityBridgeEndpointReader.ReadPath(sandbox.EndpointPath, Alive);

        Check.Equal(UnityBridgeEndpointStatus.Missing, endpoint.Status, "文件不存在应判为 Missing");
        Check.True(!endpoint.IsReady, "Missing 不可连接");
        Check.True(!File.Exists(sandbox.EndpointPath), "读端不得创建任何文件");
        TestCaseHelpers.Report(endpoint.Describe());
    }

    private static void EndpointTruncated()
    {
        using var sandbox = BridgeSandbox.Create();
        // 半截 JSON：模拟原子替换被打断 / 被清理工具截断。
        File.WriteAllText(sandbox.EndpointPath, EndpointJson[..(EndpointJson.Length / 2)], new UTF8Encoding(false));

        var endpoint = UnityBridgeEndpointReader.ReadPath(sandbox.EndpointPath, Alive);

        Check.Equal(UnityBridgeEndpointStatus.Unreadable, endpoint.Status, "半截 JSON 应判为 Unreadable");
        Check.True(File.Exists(sandbox.EndpointPath), "**无论多不合法都不得删除发现文件**（删了会把正常实例踢下线）");
        TestCaseHelpers.Report(endpoint.Describe());
    }

    private static void EndpointUnsupportedVersion()
    {
        var json = EndpointJson.Replace("\"version\": \"1.0.0\"", "\"version\": \"2.0.0\"", StringComparison.Ordinal);
        var endpoint = UnityBridgeEndpointReader.Parse(json, "endpoint.json", "fp", Alive);

        Check.Equal(UnityBridgeEndpointStatus.UnsupportedVersion, endpoint.Status, "版本不认识必须拒绝");
        Check.True(endpoint.Detail is { Length: > 0 }, "应给出人读原因");
        TestCaseHelpers.Report(endpoint.Describe());
    }

    private static void EndpointReloading()
    {
        var json = EndpointJson.Replace("\"reloading\": false", "\"reloading\": true", StringComparison.Ordinal);
        var endpoint = UnityBridgeEndpointReader.Parse(json, "endpoint.json", "fp", Alive);

        Check.Equal(UnityBridgeEndpointStatus.Reloading, endpoint.Status, "域重载窗口必须明确不连接");
        Check.True(endpoint.Reloading, "reloading 字段应被保留");
        TestCaseHelpers.Report(endpoint.Describe());
    }

    private static void EndpointUnsupportedTransport()
    {
        var json = EndpointJson.Replace("\"transport\": \"tcp\"", "\"transport\": \"pipe\"", StringComparison.Ordinal);
        var endpoint = UnityBridgeEndpointReader.Parse(json, "endpoint.json", "fp", Alive);

        Check.Equal(UnityBridgeEndpointStatus.UnsupportedVersion, endpoint.Status, "未实现的传输必须拒绝而不是猜");
        CheckContains(endpoint.Detail ?? string.Empty, "pipe", "原因里应点明 transport");
        TestCaseHelpers.Report(endpoint.Describe());
    }

    private static void EndpointIncompleteFields()
    {
        var json = EndpointJson.Replace("\"port\": 51234", "\"port\": 0", StringComparison.Ordinal);
        var endpoint = UnityBridgeEndpointReader.Parse(json, "endpoint.json", "fp", Alive);

        Check.Equal(UnityBridgeEndpointStatus.Unreadable, endpoint.Status, "port=0 判为不可读");

        var noPid = EndpointJson.Replace("\"unityPid\": 4242", "\"unityPid\": 0", StringComparison.Ordinal);
        Check.Equal(
            UnityBridgeEndpointStatus.Unreadable,
            UnityBridgeEndpointReader.Parse(noPid, "endpoint.json", "fp", Alive).Status,
            "unityPid=0 判为不可读");
    }

    // ── 判活 ─────────────────────────────────────────────────────────────

    private static void LivenessProcessGone()
    {
        using var sandbox = BridgeSandbox.Create();
        sandbox.WriteEndpoint(EndpointJson);

        var endpoint = UnityBridgeEndpointReader.ReadPath(sandbox.EndpointPath, Gone);

        Check.Equal(UnityBridgeEndpointStatus.ProcessGone, endpoint.Status, "pid 死 → 残留");
        Check.Equal(UnityBridgeProcessState.Gone, endpoint.ProcessState, "判活结论应为 Gone");
        Check.True(File.Exists(sandbox.EndpointPath), "残留文件不得被读端删除");
        TestCaseHelpers.Report(endpoint.Describe());
    }

    private static void LivenessUnknownDegrades()
    {
        using var sandbox = BridgeSandbox.Create();
        sandbox.WriteEndpoint(EndpointJson);

        var endpoint = UnityBridgeEndpointReader.ReadPath(sandbox.EndpointPath, (_, _, _) => UnityBridgeProcessState.Unknown);

        Check.Equal(UnityBridgeEndpointStatus.ProcessUnknown, endpoint.Status, "取不到信息 → ProcessUnknown");
        Check.True(File.Exists(sandbox.EndpointPath), "**降级为告警，绝不判为非法并删除**");
        TestCaseHelpers.Report(endpoint.Describe());
    }

    private static void LivenessPidReuse()
    {
        // pid 复用：启动时间对不上 → 判死；不匹配的进程名只能判 Unknown（不足以定死）。
        var reused = UnityProcessLiveness.Probe(
            Environment.ProcessId,
            DateTimeOffset.UtcNow.AddDays(-30),
            null);

        Check.Equal(UnityBridgeProcessState.Gone, reused, "启动时间差 30 天应判 pid 复用 → Gone");

        var current = UnityProcessLiveness.Probe(Environment.ProcessId, null, null);
        Check.Equal(UnityBridgeProcessState.Alive, current, "当前进程 pid 应判 Alive");

        var nameMismatch = UnityProcessLiveness.Probe(Environment.ProcessId, null, "DefinitelyNotThisProcess");
        Check.Equal(UnityBridgeProcessState.Unknown, nameMismatch, "进程名不符 → Unknown（不得判死）");
        TestCaseHelpers.Report("pid 复用判据 = (pid, StartTime)；进程名只作辅助");
    }

    // ── 契约 2：载荷 ─────────────────────────────────────────────────────

    private static void PayloadParses()
    {
        const string frame = """{"event":"onShaderContextChanged","timestamp":1790779331,"data":{"activeKeywords":["_MAIN_LIGHT_SHADOWS","_ADDITIONAL_LIGHTS"],"globalDefines":{"defined":["SHADER_API_D3D11"],"undefined":["UNITY_COLORSPACE_GAMMA"]}}}""";

        Check.True(UnityBridgePayload.TryParse(frame, out var context, out var detail), "载荷应解析成功：" + detail);
        Check.SequenceEqual(["_MAIN_LIGHT_SHADOWS", "_ADDITIONAL_LIGHTS"], context.ActiveKeywords, "activeKeywords");
        Check.SequenceEqual(["SHADER_API_D3D11"], context.DefinedMacros, "globalDefines.defined");
        Check.SequenceEqual(["UNITY_COLORSPACE_GAMMA"], context.UndefinedMacros, "globalDefines.undefined");
        Check.True(context.IsLive, "解析成功即视为在线");
        Check.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1790779331),
            context.UpdatedAtUtc,
            "时间戳按契约取自载荷；钳制失败才回落本地时钟");
        TestCaseHelpers.Report("契约 2 载荷：关键字 " + context.ActiveKeywords.Length + " 项 / 定义 "
            + context.DefinedMacros.Length + " 项 / 未定义 " + context.UndefinedMacros.Length + " 项");
    }

    private static void PayloadUnknownEventIgnored()
    {
        const string frame = """{"event":"onSomethingElse","timestamp":1,"data":{"activeKeywords":["X"]}}""";

        Check.True(!UnityBridgePayload.TryParse(frame, out _, out var detail), "未知事件应被忽略");
        CheckContains(detail ?? string.Empty, "onSomethingElse", "原因里应点名未知事件");
    }

    private static void PayloadMalformed()
    {
        Check.True(!UnityBridgePayload.TryParse("{not json", out var context, out var detail), "非法 JSON 应失败");
        Check.True(context is not null && !context.IsLive, "失败时不得产出在线上下文");
        CheckContains(detail ?? string.Empty, "JSON", "原因应指明是 JSON 层");

        Check.True(!UnityBridgePayload.TryParse(string.Empty, out _, out _), "空帧应失败");
    }

    private static void PayloadContentEquivalence()
    {
        const string a = """{"event":"onShaderContextChanged","timestamp":100,"data":{"activeKeywords":["A"],"globalDefines":{"defined":[],"undefined":[]}}}""";
        const string b = """{"event":"onShaderContextChanged","timestamp":999,"data":{"activeKeywords":["A"],"globalDefines":{"defined":[],"undefined":[]}}}""";
        const string c = """{"event":"onShaderContextChanged","timestamp":999,"data":{"activeKeywords":["A","B"],"globalDefines":{"defined":[],"undefined":[]}}}""";

        Check.True(UnityBridgePayload.TryParse(a, out var left, out _), "a");
        Check.True(UnityBridgePayload.TryParse(b, out var right, out _), "b");
        Check.True(UnityBridgePayload.TryParse(c, out var third, out _), "c");

        Check.True(
            UnityBridgePayload.HasSameContent(left, right),
            "时间戳不同但内容一致 → 必须判等价（否则每次心跳都会推进版本、打掉诊断缓存）");
        Check.True(!UnityBridgePayload.HasSameContent(left, third), "内容不同 → 不等价");
    }

    /// <summary>
    /// 契约漂移门禁：Unity 侧写出的字段集（<c>client/unity/endpoint.schema.json</c>）
    /// 必须与读端真正消费的字段集一致。
    /// </summary>
    /// <remarks>
    /// 这是模块 11B 唯一可能「静默腐烂」的地方：Unity 脚本、schema、文档三处
    /// 各改各的，而读端会把未知字段当不存在 —— 结果是一个字段永远读不到、谁都不报错。
    /// </remarks>
    private static void EndpointSchemaMatchesReader()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "UnityBridge", "endpoint.schema.json");
        if (!File.Exists(schemaPath))
        {
            throw new SkipException("schema 未拷入输出目录：" + schemaPath);
        }

        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(schemaPath, Encoding.UTF8));
        var root = document.RootElement;

        Check.True(root.TryGetProperty("required", out var required) && required.ValueKind == System.Text.Json.JsonValueKind.Array, "schema 必须有 required");

        var requiredNames = required.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        Check.True(requiredNames.Length > 0, "required 不得为空");

        var properties = root.GetProperty("properties");
        var declared = properties.EnumerateObject().Select(p => p.Name).ToArray();

        // ① schema 声明的字段集必须与固定载荷的字段集完全一致（既不多也不少）。
        using var fixture = System.Text.Json.JsonDocument.Parse(EndpointJson);
        var actual = fixture.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        var missingInSchema = actual.Except(declared, StringComparer.Ordinal).ToArray();
        var staleInSchema = declared.Except(actual, StringComparer.Ordinal).ToArray();

        Check.True(
            missingInSchema.Length == 0,
            "发现文件里出现了 schema 未声明的字段（schema 落后于就绪脚本）：" + string.Join(", ", missingInSchema));
        Check.True(
            staleInSchema.Length == 0,
            "schema 声明了发现文件里没有的字段（schema 领先于就绪脚本）：" + string.Join(", ", staleInSchema));

        // ② required 必须是读端真正的硬前提：缺任何一个都必须判为不可读。
        foreach (var name in requiredNames)
        {
            var stripped = RemoveField(EndpointJson, name);
            var status = UnityBridgeEndpointReader.Parse(stripped, "endpoint.json", "fp", Alive).Status;
            Check.True(
                status != UnityBridgeEndpointStatus.Ready,
                "schema 把 '" + name + "' 标为 required，但读端在它缺失时仍判为 Ready");
        }

        TestCaseHelpers.Report("契约 1 字段集一致（" + declared.Length + " 个），required 硬前提 "
            + requiredNames.Length + " 项逐条验证");
    }

    /// <summary>从发现文件 JSON 文本里删掉一个顶层字段（用于验证 required 的硬前提语义）。</summary>
    private static string RemoveField(string json, string field)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(field, out var value))
        {
            return json;
        }

        // 定长定位：字段名（含引号 + 冒号）到值结尾。值都是标量，不存在嵌套。
        var marker = "\"" + field + "\":";
        var start = json.IndexOf(marker, StringComparison.Ordinal);
        Check.True(start >= 0, "未在 JSON 文本里定位字段 " + field);

        var valueStart = start + marker.Length;
        var end = valueStart + value.GetRawText().Length;

        // 顺带吃掉后面的逗号（或前面的，如果它是最后一个字段）。
        if (end < json.Length && json[end] == ',')
        {
            end++;
            while (end < json.Length && char.IsWhiteSpace(json[end]) && json[end] != '\n')
            {
                end++;
            }
        }
        else
        {
            var before = start - 1;
            while (before >= 0 && char.IsWhiteSpace(json[before]))
            {
                before--;
            }

            if (before >= 0 && json[before] == ',')
            {
                start = before;
            }
        }

        return json[..start] + json[end..];
    }

    // ── 真回环 ────────────────────────────────────────────────────────────

    private static void LoopbackAppliesContext()
    {
        using var sandbox = BridgeSandbox.Create();
        using var server = new FakeBridgeServer(sandbox, Alive);
        server.Start();

        var registry = new ShaderContextRegistry();
        using var client = new UnityBridgeClient(
            sandbox.Layout,
            registry,
            new UnityBridgeOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(100),
                ReconnectDelay = TimeSpan.FromMilliseconds(100),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                IdleRecheckInterval = TimeSpan.FromMilliseconds(200),
            },
            Alive);

        client.Start();
        server.WaitForClient(TimeSpan.FromSeconds(5));
        server.Broadcast("""{"event":"onShaderContextChanged","timestamp":1790779331,"data":{"activeKeywords":["_MAIN_LIGHT_SHADOWS"],"globalDefines":{"defined":["SHADER_API_D3D11"],"undefined":["UNITY_COLORSPACE_GAMMA"]}}}""");

        var applied = WaitUntil(
            () => registry.Current.Dynamic.IsLive && registry.Current.Dynamic.ActiveKeywords.Length == 1,
            TimeSpan.FromSeconds(5));

        Check.True(applied, "应在时限内把载荷推进注册表快照：" + client.Status.Describe());
        Check.SequenceEqual(["_MAIN_LIGHT_SHADOWS"], registry.Current.Dynamic.ActiveKeywords, "活动关键字应进入快照");
        Check.SequenceEqual(["SHADER_API_D3D11"], registry.Current.Dynamic.DefinedMacros, "动态宏应进入快照");
        Check.Contains(registry.Current.GetActiveDefines(), "SHADER_API_D3D11", "GetActiveDefines 应暴露动态宏");
        Check.True(registry.IsUnityLiveConnected, "注册表应报告桥接在线");
        TestCaseHelpers.Report("回环：" + client.Status.Describe());
    }

    private static void LoopbackDuplicateFrameKeepsVersion()
    {
        using var sandbox = BridgeSandbox.Create();
        using var server = new FakeBridgeServer(sandbox, Alive);
        server.Start();

        var registry = new ShaderContextRegistry();
        using var client = new UnityBridgeClient(
            sandbox.Layout,
            registry,
            new UnityBridgeOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(100),
                ReconnectDelay = TimeSpan.FromMilliseconds(100),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                IdleRecheckInterval = TimeSpan.FromMilliseconds(200),
            },
            Alive);

        client.Start();
        server.WaitForClient(TimeSpan.FromSeconds(5));

        const string frame = """{"event":"onShaderContextChanged","timestamp":1,"data":{"activeKeywords":["_A"],"globalDefines":{"defined":[],"undefined":[]}}}""";
        server.Broadcast(frame);
        Check.True(
            WaitUntil(() => registry.Current.Dynamic.ActiveKeywords.Length == 1, TimeSpan.FromSeconds(5)),
            "首帧应被应用");

        var versionAfterFirst = registry.Current.Version;

        // 连发 5 帧内容完全一致的载荷（只有时间戳不同）。
        for (var i = 0; i < 5; i++)
        {
            server.Broadcast(frame.Replace("\"timestamp\":1", "\"timestamp\":" + (100 + i), StringComparison.Ordinal));
        }

        Thread.Sleep(600);

        Check.Equal(versionAfterFirst, registry.Current.Version, "内容不变时绝不能推进快照版本（否则诊断缓存会被整片打掉）");
        TestCaseHelpers.Report("重复帧后版本仍为 " + versionAfterFirst.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void LoopbackDisconnectResetsContext()
    {
        using var sandbox = BridgeSandbox.Create();
        using var server = new FakeBridgeServer(sandbox, Alive);
        server.Start();

        var registry = new ShaderContextRegistry();
        using var client = new UnityBridgeClient(
            sandbox.Layout,
            registry,
            new UnityBridgeOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(100),
                ReconnectDelay = TimeSpan.FromMilliseconds(150),
                ConnectTimeout = TimeSpan.FromSeconds(2),
                IdleRecheckInterval = TimeSpan.FromMilliseconds(150),
            },
            Alive);

        client.Start();
        server.WaitForClient(TimeSpan.FromSeconds(5));
        server.Broadcast("""{"event":"onShaderContextChanged","timestamp":1,"data":{"activeKeywords":["_A"],"globalDefines":{"defined":[],"undefined":[]}}}""");
        Check.True(WaitUntil(() => registry.Current.Dynamic.IsLive, TimeSpan.FromSeconds(5)), "应先上线");

        // Unity 退出：发现文件消失 + 服务端断开。
        server.Stop();
        sandbox.DeleteEndpoint();

        var reset = WaitUntil(() => !registry.Current.Dynamic.IsLive, TimeSpan.FromSeconds(6));

        Check.True(reset, "对端消失后必须回退为未绑定，不能留着一个已经不存在的 Unity 的假设继续诊断");
        Check.True(!registry.IsUnityLiveConnected, "注册表应报告离线");
        TestCaseHelpers.Report("断开后已重置为：" + client.Status.Describe());
    }

    // ── 装配侧（离线零漂移 / 在线注入） ───────────────────────────────────

    private static void OfflineAssemblyUnchanged()
    {
        var (document, parse) = BridgeFixtureShaders.Open();

        // ① 显式关闭动态宏：这是「引入本模块前」的行为基线。
        var baseline = AssembleFirst(document, parse, includeDynamicMacros: false, DynamicShaderContext.Empty);

        // ② 打开开关但桥接离线（Empty 上下文）：必须与基线逐字节相同。
        var withSwitch = AssembleFirst(document, parse, includeDynamicMacros: true, DynamicShaderContext.Empty);

        Check.Equal(baseline, withSwitch, "桥接离线时装配结果必须逐字节不变（黄金判据的保护网）");
        CheckDoesNotContain(withSwitch, "Unity 桥接动态宏", "离线时不得出现动态宏注入段");
        TestCaseHelpers.Report("离线装配文本 " + baseline.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " 字符，逐字节一致");
    }

    private static void OnlineInjectsMacros()
    {
        var (document, parse) = BridgeFixtureShaders.Open();

        var context = new DynamicShaderContext
        {
            ActiveKeywords = ["_MAIN_LIGHT_SHADOWS"],
            DefinedMacros = ["SHADER_API_D3D11=1", "CUSTOM_ON"],
            UndefinedMacros = ["UNITY_COLORSPACE_GAMMA"],
            IsLive = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var text = AssembleFirst(document, parse, includeDynamicMacros: true, context);

        CheckContains(text, "Unity 桥接动态宏", "在线时必须出现动态宏注入段");
        CheckContains(text, "#undef UNITY_COLORSPACE_GAMMA", "未定义项应产出 #undef");
        CheckContains(text, "#define SHADER_API_D3D11 1", "带值项应原样注入");
        CheckContains(text, "#define CUSTOM_ON 1", "裸名应按 ADR-018 补值 1（裸 define 会让 #if K 报 expected value）");
        CheckContains(text, "#ifdef SHADER_API_D3D11", "重定义前必须先摘掉旧定义（否则注入段彼此打架）");

        // 「先 undef 再 define」的顺序：undef 必须出现在**动态段内部**的第一个 define 之前。
        // 注意不能拿全文本里的 "#define SHADER_API_D3D11 1" 定位：平台宏段（版本×平台表）
        // 已经先写过同一个宏名，那一条在动态段之前。
        var headerIndex = text.IndexOf("Unity 桥接动态宏", StringComparison.Ordinal);
        var anchorIndex = text.IndexOf("#line ", headerIndex, StringComparison.Ordinal);
        var dynamicBlock = text[headerIndex..anchorIndex];
        var undefIndex = dynamicBlock.IndexOf("#undef UNITY_COLORSPACE_GAMMA", StringComparison.Ordinal);
        var defineIndex = dynamicBlock.IndexOf("#define SHADER_API_D3D11 1", StringComparison.Ordinal);
        Check.True(undefIndex >= 0 && defineIndex > undefIndex, "动态段内应先 undef 再 define");

        // 动态段必须排在平台宏之后（实测权威盖掉推定项）。
        var platformIndex = text.IndexOf("平台宏", StringComparison.Ordinal);
        Check.True(platformIndex >= 0 && platformIndex < headerIndex, "动态段必须排在平台宏之后才能覆盖推定项");

        TestCaseHelpers.Report("注入段含动态宏：" + CountOccurrences(text, "#undef") + " 条 #undef / "
            + CountOccurrences(text, "#define") + " 条 #define");
    }

    private static void OnlineRejectsUnsafeMacros()
    {
        var (document, parse) = BridgeFixtureShaders.Open();

        var context = new DynamicShaderContext
        {
            ActiveKeywords = [],
            DefinedMacros =
            [
                "GOOD_ONE=1",
                "EVIL\n#line 1 \"other.hlsl\"",     // 企图把 #line 注入进渲染文本
                "BAD NAME=1",                       // 宏名含空格
                "BAD_VALUE=1\\",                    // 反斜杠可以续行
                "9STARTS_WITH_DIGIT=1",
            ],
            UndefinedMacros = ["OK_UNDEF", "BAD\nUNDEF"],
            IsLive = true,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
        };

        var text = AssembleFirst(document, parse, includeDynamicMacros: true, context);

        CheckContains(text, "#define GOOD_ONE 1", "合法项应注入");
        CheckContains(text, "#undef OK_UNDEF", "合法的 undef 项应注入");
        CheckDoesNotContain(text, "other.hlsl", "注入字符串里绝不允许出现结构字节（#line 是 ADR-023 的结构不变量）");
        CheckDoesNotContain(text, "BAD NAME", "非法宏名应被拒");
        CheckDoesNotContain(text, "9STARTS_WITH_DIGIT", "数字开头的宏名应被拒");
        CheckDoesNotContain(text, "#undef BAD", "非法 undef 名应被拒");

        TestCaseHelpers.Report("非法项被拒：跨进程数据不得打破 Segment 结构不变量");
    }

    private static void OnlineSelectsActiveKeyword()
    {
        // 用**真实切片器**产出的声明，而不是手工构造的假声明：ADR-006 的「变体首项」由
        // ShaderPragmaScanner 计算（跳过 "_"/"__" 占位符后取第一个真实关键字），
        // 手工构造会测出一个现实中不存在的状态。
        var parse = new ShaderLabStateMachine().Parse(
            "/MicroShaderFixture/ActiveKeywordChoice.shader",
            BridgeFixtureShaders.ActiveKeywordChoiceText);
        var declaration = parse.Passes[0].Variants.Single(v => v.Keywords.Count == 2);

        Check.Equal("_ALPHA_TEST_FAKE", declaration.SelectedKeyword, "ADR-006 离线口径 = 跳过占位符后的第一个真实关键字");
        Check.SequenceEqual(["_ALPHA_TEST_FAKE", "_OUTPUT_DEPTH"], declaration.Keywords, "两关键字集合");

        // 离线：走 ADR-006 的变体首项。
        var offline = new ShaderKeywordPlan();
        ShaderKeywordMatrixBuilder.Build([declaration], MicroShader.Domain.ShaderStage.Fragment, offline);
        Check.SequenceEqual(["_ALPHA_TEST_FAKE"], offline.EnabledKeywords, "离线启用集合 = 变体首项");

        // 在线：Unity 上报 _OUTPUT_DEPTH 才是真正启用态 → 改选它（这正是模块 11B 的增量价值）。
        var online = new ShaderKeywordPlan();
        ShaderKeywordMatrixBuilder.Build([declaration], MicroShader.Domain.ShaderStage.Fragment, online, ["_OUTPUT_DEPTH"]);
        Check.SequenceEqual(["_OUTPUT_DEPTH"], online.EnabledKeywords, "桥接上报的活动关键字应改写变体选择");
        Check.Contains(online.Lines, "#define _OUTPUT_DEPTH 1", "应产出带值的 define");
        Check.Contains(online.Lines, "#define _OUTPUT_DEPTH_KEYWORD_DECLARED 1", "声明项仍应补齐（整组语义不变）");

        // 桥接上报的关键字与本 set 无关时应回落，绝不放行「一个 set 启用两个关键字」。
        var unrelated = new ShaderKeywordPlan();
        ShaderKeywordMatrixBuilder.Build([declaration], MicroShader.Domain.ShaderStage.Fragment, unrelated, ["_SOMETHING_ELSE"]);
        Check.SequenceEqual(["_ALPHA_TEST_FAKE"], unrelated.EnabledKeywords, "无关的活动关键字必须回落到变体首项");
        Check.True(unrelated.EnabledKeywords.Count == 1, "同一 set 只能启用一个关键字");

        // 一个 set 里同时命中两个（理论上不该发生）也只启用第一个：整组语义是硬约束。
        var both = new ShaderKeywordPlan();
        ShaderKeywordMatrixBuilder.Build([declaration], MicroShader.Domain.ShaderStage.Fragment, both, ["_OUTPUT_DEPTH", "_ALPHA_TEST_FAKE"]);
        Check.True(both.EnabledKeywords.Count == 1, "同一 set 绝不允许启用两个关键字，实际 " + both.EnabledKeywords.Count);

        TestCaseHelpers.Report("变体选择：离线=_ALPHA_TEST_FAKE（首项） / 在线=_OUTPUT_DEPTH（Unity 权威）");
    }

    // ── 工具 ─────────────────────────────────────────────────────────────

    private static UnityBridgeProcessState Alive(int pid, DateTimeOffset? startTime, string? name)
        => UnityBridgeProcessState.Alive;

    private static UnityBridgeProcessState Gone(int pid, DateTimeOffset? startTime, string? name)
        => UnityBridgeProcessState.Gone;

    private static string AssembleFirst(
        MicroShader.Domain.SourceShaderDocument document,
        ShaderLabParseResult parse,
        bool includeDynamicMacros,
        DynamicShaderContext context)
    {
        var registry = new ShaderContextRegistry();
        if (context.IsLive)
        {
            registry.PublishDynamic(context);
        }

        var assembler = new MicroShader.DiagnosticEngine.VirtualTextAssembler(
            registry,
            new MicroShader.DiagnosticEngine.AssemblerOptions
            {
                IncludeDynamicMacros = includeDynamicMacros,
                AbortOnUnresolvedInclude = false,
            });

        var snippet = parse.Passes[0];
        var result = assembler.Assemble(document, parse, snippet, MicroShader.Domain.ShaderStage.Fragment);
        Check.True(result.Succeeded, "装配应成功：" + (result.FailureDetail ?? "<无>"));
        var text = result.Text!.GetText();
        result.Text.Dispose();
        return text;
    }

    private static void CheckContains(string haystack, string needle, string message)
    {
        if (!haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionException(
                message + "\n  期望包含: " + needle + "\n  实际: " + haystack);
        }
    }

    private static void CheckDoesNotContain(string haystack, string needle, string message)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            throw new AssertionException(message + "\n  期望不含: " + needle);
        }
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return condition();
    }

    /// <summary>临时工程沙盒（Library/MicroShader/endpoint.json）。</summary>
    private sealed class BridgeSandbox : IDisposable
    {
        private BridgeSandbox(string root, UnityProjectLayout layout)
        {
            Root = root;
            Layout = layout;
        }

        public string Root { get; }

        public UnityProjectLayout Layout { get; }

        public string EndpointPath => Layout.EndpointFilePath;

        public static BridgeSandbox Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "microshader-bridge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(root, "Library", "MicroShader"));
            File.WriteAllText(
                Path.Combine(root, "ProjectSettings", "ProjectVersion.txt"),
                "m_EditorVersion: 2022.3.62f3c1\n",
                new UTF8Encoding(false));

            return new BridgeSandbox(VfsPath.Normalize(root), UnityProjectLayout.FromRoot(VfsPath.Normalize(root)));
        }

        public void WriteEndpoint(string json)
        {
            var directory = Path.GetDirectoryName(EndpointPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(EndpointPath, json, new UTF8Encoding(false));
        }

        public void DeleteEndpoint()
        {
            if (File.Exists(EndpointPath))
            {
                File.Delete(EndpointPath);
            }
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响结论。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    /// <summary>
    /// 假 Unity 桥接服务端：真 <see cref="TcpListener"/> + 真字节，只把「Unity」换成确定性判活。
    /// </summary>
    /// <remarks>
    /// 用真实 socket 而不是内存管道：要验证的恰恰是**跨进程那一段** ——
    /// 发现文件的原子替换、连接建立、LDJSON 分行、断开语义。
    /// </remarks>
    private sealed class FakeBridgeServer : IDisposable
    {
        private readonly BridgeSandbox _sandbox;
        private readonly UnityBridgeProcessProbe _probe;
        private readonly List<TcpClient> _clients = [];
        private readonly Lock _gate = new();
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private volatile bool _stopping;
        private int _port;

        public FakeBridgeServer(BridgeSandbox sandbox, UnityBridgeProcessProbe probe)
        {
            _sandbox = sandbox;
            _probe = probe;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            // 先起监听、再写发现文件：读端绝不可能读到一个连不上的端口。
            _sandbox.WriteEndpoint(BuildEndpointJson(_port));

            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "FakeBridge.Accept" };
            _acceptThread.Start();
        }

        public bool WaitForClient(TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_gate)
                {
                    if (_clients.Count > 0)
                    {
                        return true;
                    }
                }

                Thread.Sleep(25);
            }

            return false;
        }

        /// <summary>广播一帧（自动补换行，与 Unity 侧一致）。</summary>
        public void Broadcast(string json)
        {
            var frame = Encoding.UTF8.GetBytes(json + "\n");
            TcpClient[] targets;
            lock (_gate)
            {
                targets = [.. _clients];
            }

            foreach (var target in targets)
            {
                try
                {
                    target.GetStream().Write(frame, 0, frame.Length);
                    target.GetStream().Flush();
                }
                catch (IOException)
                {
                    // 客户端已断开。
                }
            }
        }

        public void Stop()
        {
            _stopping = true;

            lock (_gate)
            {
                foreach (var client in _clients)
                {
                    client.Dispose();
                }

                _clients.Clear();
            }

            try
            {
                _listener?.Stop();
            }
            catch (SocketException)
            {
                // 忽略。
            }
        }

        public void Dispose() => Stop();

        private void AcceptLoop()
        {
            while (!_stopping)
            {
                TcpClient client;
                try
                {
                    client = _listener!.AcceptTcpClient();
                }
                catch (Exception)
                {
                    return;
                }

                client.NoDelay = true;
                lock (_gate)
                {
                    _clients.Add(client);
                }
            }
        }

        private string BuildEndpointJson(int port)
        {
            var pid = Environment.ProcessId;
            return "{\n"
                + "  \"version\": \"1.0.0\",\n"
                + "  \"bridgeVersion\": \"0.1.0-test\",\n"
                + "  \"unityPid\": " + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                + "  \"unityProcessName\": \"FakeUnity\",\n"
                + "  \"unityVersion\": \"2022.3.62f3c1\",\n"
                + "  \"projectPath\": \"" + _sandbox.Root + "\",\n"
                + "  \"projectName\": \"Sandbox\",\n"
                + "  \"transport\": \"tcp\",\n"
                + "  \"address\": \"127.0.0.1\",\n"
                + "  \"port\": " + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\n"
                + "  \"protocol\": \"ldjson-1\",\n"
                + "  \"reloading\": false\n"
                + "}\n";
        }
    }
}

/// <summary>桥接自检用的最小 shader 语料（真实结构：HLSLINCLUDE + multi_compile + 一个 Pass）。</summary>
internal static class BridgeFixtureShaders
{
    public const string Text = """
        Shader "MicroShader/BridgeFixture"
        {
            HLSLINCLUDE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma shader_feature_local _NORMALMAP
            ENDHLSL

            SubShader
            {
                Tags { "RenderType" = "Opaque" }

                Pass
                {
                    Name "Forward"
                    HLSLPROGRAM
                    #pragma vertex vert
                    #pragma fragment frag

                    struct Attributes { float4 positionOS : POSITION; };
                    struct Varyings { float4 positionCS : SV_POSITION; };

                    Varyings vert(Attributes input)
                    {
                        Varyings output;
                        output.positionCS = UnityObjectToClipPos(input.positionOS);
                        return output;
                    }

                    half4 frag(Varyings input) : SV_Target
                    {
                        return half4(1, 1, 1, 1);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    /// <summary>变体选择专用语料：两个真实关键字，首项不是 Unity 实际启用的那一个。</summary>
    public const string ActiveKeywordChoiceText = """
        Shader "MicroShader/ActiveKeywordChoice"
        {
            SubShader
            {
                Pass
                {
                    HLSLPROGRAM
                    #pragma multi_compile _ALPHA_TEST_FAKE _OUTPUT_DEPTH

                    float4 vert(float4 positionOS : POSITION) : SV_POSITION
                    {
                        return float4(positionOS.xyz, 1.0);
                    }

                    half4 frag() : SV_Target
                    {
                        return half4(1, 1, 1, 1);
                    }
                    ENDHLSL
                }
            }
        }
        """;

    /// <summary>把固定语料切成「文档 + 切片结果」对（与真实链路的顺序一致：先切片、后装配）。</summary>
    public static (MicroShader.Domain.SourceShaderDocument Document, ShaderLabParseResult Parse) Open()
    {
        const string path = "/MicroShaderFixture/BridgeFixture.shader";
        var parse = new ShaderLabStateMachine().Parse(path, Text);
        var document = new MicroShader.Domain.SourceShaderDocument
        {
            FileUri = parse.TargetUri,
            FilePath = path,
            FullText = Text,
            Version = 1,
        };

        return (document, parse);
    }
}
