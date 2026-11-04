// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 跨类契约
//
// 本文件只放「多个类都要知道的约定」：协议常量与唯一一个跨类接口。
// 任何具体实现（编码、传输、采样）都不属于这里 —— 它是依赖图的叶子。
// ─────────────────────────────────────────────────────────────────────────────

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 契约 1（发现文件）与契约 2（广播载荷）里的全部常量。
    /// </summary>
    /// <remarks>
    /// 集中在一处是为了让「协议」只有一个真相源：读端（`MicroShader.ContextEngine.UnityBridgeEndpoint`）
    /// 与自检 fixture 都按这些字面量对齐，散布到各文件里就再也对不齐了。
    /// </remarks>
    internal static class BridgeContracts
    {
        /// <summary>契约 1 的 schema 版本（读端不认识即拒绝连接）。</summary>
        public const string EndpointVersion = "1.0.0";

        /// <summary>本脚本版本（与语言服务端版本独立演进）。</summary>
        public const string BridgeVersion = "0.2.0";

        /// <summary>契约 2 的唯一事件名。</summary>
        public const string ContextChangedEvent = "onShaderContextChanged";

        /// <summary>传输类型。当前实现只有 tcp（ADR-016）。</summary>
        public const string Transport = "tcp";

        /// <summary>监听地址。只绑回环 —— 无端口暴露面、不需要 URL ACL / 管理员权限。</summary>
        public const string LoopbackAddress = "127.0.0.1";

        /// <summary>线协议：换行分隔的单行 JSON（LDJSON）。</summary>
        public const string WireProtocol = "ldjson-1";

        /// <summary>可选客户端指令：请求立即重算并重播一次上下文。</summary>
        public const string RefreshCommand = "refresh";

        /// <summary>发现文件所在目录名（位于 <c>&lt;Project&gt;/Library/</c> 下）。</summary>
        public const string DiscoveryDirectoryName = "MicroShader";

        /// <summary>发现文件名。</summary>
        public const string DiscoveryFileName = "endpoint.json";

        /// <summary>
        /// 允许同时在线的客户端上限。
        /// </summary>
        /// <remarks>
        /// 超出即断开新客户端（明确的「先到者胜」），而不是踢掉正在工作的读端：
        /// 被踢的那一方可能正在写诊断，静默失联比拒绝接入更难排。
        /// </remarks>
        public const int MaxClients = 4;

        /// <summary>出站队列容量。满时丢最旧一帧（对端只需最新状态，历史无意义）。</summary>
        public const int OutboxCapacity = 16;

        /// <summary>客户端指令行的字节上限（超长整行丢弃，避免无界累积）。</summary>
        public const int MaxClientCommandBytes = 4096;

        /// <summary>载荷中单个数组的元素上限（防御异常对端，同时限制注入段规模）。</summary>
        public const int MaxPayloadEntries = 512;

        /// <summary>发现文件读写的字节上限。</summary>
        public const int MaxEndpointBytes = 64 * 1024;
    }

    /// <summary>
    /// 上下文来源：可被周期性采样，并如实回答「内容变了吗」。
    /// </summary>
    /// <remarks>
    /// 抽出这个接口是为了让「采样」与「发布/传输」两件事彼此不可见：
    /// 桥接主体只知道「问它要一次采样结果」，不关心它怎么读关键字、怎么序列化；
    /// 采样器也不知道自己产出的内容会被写进文件还是发上网络。
    /// </remarks>
    internal interface IBridgeContextSource
    {
        /// <summary>最近一次生成的载荷；从未生成过时为 null。</summary>
        string Payload { get; }

        /// <summary>发现文件需要的环境字段（每次采样后刷新）。</summary>
        BridgeEnvironmentSnapshot Environment { get; }

        /// <summary>节流判定；返回 false 表示本次 tick 跳过采样。</summary>
        /// <param name="now">当前时刻（秒），由调用方注入。</param>
        /// <param name="hasConsumers">当前是否有读端在线；无人在线时允许降频。</param>
        bool ShouldSample(double now, bool hasConsumers);

        /// <summary>
        /// 采样一次。
        /// </summary>
        /// <param name="now">当前时刻（秒），由调用方注入。</param>
        /// <param name="hasConsumers">当前是否有读端在线。</param>
        /// <param name="environmentChanged">
        /// 只影响发现文件的环境字段（色彩空间 / 构建目标 / 图形 API / 管线）是否变化。
        /// </param>
        /// <returns>影响广播载荷的内容是否真实变化。</returns>
        bool Sample(double now, bool hasConsumers, out bool environmentChanged);
    }
}
