// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 契约 1 编码器（发现文件）
//
// 输入是一份「事实」（BridgeEndpointFacts），输出是发现文件文本。
// 只生成文本，**不落盘** —— 落盘属于 BridgeEndpointFile。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 写进发现文件的全部事实（值类型快照）。
    /// </summary>
    /// <remarks>
    /// 之所以把「pid / 启动时间 / 进程名」也做成显式字段而不是让编码器自己去问 <c>Process</c>：
    /// 编码器因此成为**纯函数**，而「哪些字段来自进程、哪些来自编辑器」这件事
    /// 集中在 <see cref="Capture"/> 一处，读起来只有一条路径。
    /// </remarks>
    internal readonly struct BridgeEndpointFacts
    {
        public BridgeEndpointFacts(
            int port,
            bool reloading,
            int unityPid,
            string unityProcessName,
            DateTimeOffset unityStartTimeUtc,
            string unityVersion,
            string projectPath,
            string projectName,
            BridgeEnvironmentSnapshot environment)
        {
            Port = port;
            Reloading = reloading;
            UnityPid = unityPid;
            UnityProcessName = unityProcessName;
            UnityStartTimeUtc = unityStartTimeUtc;
            UnityVersion = unityVersion;
            ProjectPath = projectPath;
            ProjectName = projectName;
            Environment = environment;
        }

        public int Port { get; }

        public bool Reloading { get; }

        public int UnityPid { get; }

        public string UnityProcessName { get; }

        public DateTimeOffset UnityStartTimeUtc { get; }

        public string UnityVersion { get; }

        public string ProjectPath { get; }

        public string ProjectName { get; }

        public BridgeEnvironmentSnapshot Environment { get; }

        /// <summary>
        /// 采集当前进程与编辑器事实，凑成一份可编码的快照。**主线程调用**。
        /// </summary>
        /// <remarks>
        /// 进程启动时间是**抗 PID 复用的权威判据**（pid 会被回收重发；只凭 pid 判活会把
        /// 「刚起来的另一个进程」误认成 Unity）。取不到时留 <see cref="DateTimeOffset.MinValue"/>：
        /// 读端对缺失的启动时间会退化为「只看进程名」，而不是判死。
        /// </remarks>
        public static BridgeEndpointFacts Capture(
            int port,
            bool reloading,
            string projectPath,
            BridgeEnvironmentSnapshot environment)
        {
            var pid = 0;
            var processName = string.Empty;
            var startTimeUtc = DateTimeOffset.MinValue;

            try
            {
                var process = Process.GetCurrentProcess();
                pid = process.Id;
                processName = SafeProcessName(process);

                try
                {
                    startTimeUtc = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                }
                catch (Exception)
                {
                    // 取不到启动时间不是致命问题：pid + 文件内容仍可判活，只是抗 PID 复用弱一些。
                }
            }
            catch (Exception exception)
            {
                BridgeLog.Warn("读取当前进程信息失败：" + exception.Message);
            }

            var normalizedRoot = NormalizePath(projectPath);

            return new BridgeEndpointFacts(
                port,
                reloading,
                pid,
                processName,
                startTimeUtc,
                Application.unityVersion,
                normalizedRoot,
                SafeDirectoryName(normalizedRoot),
                environment);
        }

        private static string SafeProcessName(Process process)
        {
            try
            {
                return process.ProcessName;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static string SafeDirectoryName(string path)
        {
            try
            {
                return new System.IO.DirectoryInfo(path).Name;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        /// <summary>统一用正斜杠：读端与文档都按这个形态比对，避免同一个工程出现两种字面。</summary>
        public static string NormalizePath(string path)
            => string.IsNullOrEmpty(path) ? string.Empty : path.Replace('\\', '/');
    }

    /// <summary>
    /// 契约 1（发现文件）的编码。
    /// </summary>
    /// <remarks>
    /// 字段集必须与 <c>client/unity/endpoint.schema.json</c> **逐一对应**：
    /// 自检套件 `Bridge.Contract` 会拿 schema 与固定载荷比对字段集，
    /// 任何一侧多了或少了字段都会红 —— 这是本条契约唯一会「静默腐烂」的地方。
    /// </remarks>
    internal static class BridgeEndpointDocument
    {
        /// <summary>域重载标记的字面量（读写两侧共用，避免两处各写一半）。</summary>
        private const string ReloadingFalse = "\"reloading\": false";

        private const string ReloadingTrue = "\"reloading\": true";

        /// <summary>编码整份发现文件（多行、带缩进，便于人眼排障）。</summary>
        public static string Encode(BridgeEndpointFacts facts)
        {
            var builder = new StringBuilder(512);
            var environment = facts.Environment;

            builder.Append("{\n");

            AppendField(builder, "version", BridgeContracts.EndpointVersion);
            AppendField(builder, "bridgeVersion", BridgeContracts.BridgeVersion);

            builder.Append("  \"unityPid\": ");
            BridgeJson.AppendNumber(builder, facts.UnityPid);
            builder.Append(",\n");

            AppendField(builder, "unityProcessName", facts.UnityProcessName);
            AppendField(builder, "unityStartTimeUtc", FormatTimestamp(facts.UnityStartTimeUtc));
            AppendField(builder, "unityVersion", facts.UnityVersion);
            AppendField(builder, "projectPath", facts.ProjectPath);
            AppendField(builder, "projectName", facts.ProjectName);
            AppendField(builder, "transport", BridgeContracts.Transport);
            AppendField(builder, "address", BridgeContracts.LoopbackAddress);

            builder.Append("  \"port\": ");
            BridgeJson.AppendNumber(builder, facts.Port);
            builder.Append(",\n");

            AppendField(builder, "protocol", BridgeContracts.WireProtocol);
            AppendField(builder, "activeColorSpace", environment.ColorSpace);
            AppendField(builder, "activeBuildTarget", environment.BuildTarget);
            AppendField(builder, "graphicsApi", environment.GraphicsApi);
            AppendField(builder, "pipelineAssetType", environment.PipelineAssetType);

            builder.Append("  \"reloading\": ").Append(facts.Reloading ? "true" : "false").Append(",\n");

            AppendField(builder, "startedAtUtc", FormatTimestamp(DateTimeOffset.UtcNow), trailingComma: false);

            builder.Append("}\n");
            return builder.ToString();
        }

        /// <summary>
        /// 把已写好的发现文件标成「域重载中」，**不删除**。
        /// </summary>
        /// <remarks>
        /// 为什么标而不删：域重载前端口已被主动释放，若删掉文件读端只能退化为「未绑定」；
        /// 但若原封不动保留旧文件，读端又可能去连一个**已被别的进程抢走**的端口。
        /// 标记把「端口已释放但进程还活着」这个窗口显式表达出来，读端据此不连接、等待重写。
        /// 返回 null 表示文本无需改动（找不到标记）。
        /// </remarks>
        public static string MarkReloading(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf(ReloadingFalse, StringComparison.Ordinal) < 0)
            {
                return null;
            }

            return json.Replace(ReloadingFalse, ReloadingTrue);
        }

        private static void AppendField(StringBuilder builder, string name, string value, bool trailingComma = true)
        {
            builder.Append("  ");
            BridgeJson.AppendString(builder, name);
            builder.Append(": ");
            BridgeJson.AppendString(builder, value ?? string.Empty);
            builder.Append(trailingComma ? ",\n" : "\n");
        }

        private static string FormatTimestamp(DateTimeOffset value)
            => value == DateTimeOffset.MinValue ? string.Empty : value.ToString("o", CultureInfo.InvariantCulture);
    }
}
