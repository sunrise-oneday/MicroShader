// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 上下文采样器
//
// 「决定向语言服务端上报什么」的唯一位置：
//   · 活动全局关键字（Shader.globalKeywords ∩ IsKeywordEnabled）
//   · 编辑器派生的全局宏（SHADER_API_* / UNITY_COLORSPACE_GAMMA 的定义与未定义）
//
// 性能契约：**稳态零分配**。逐项比较用的是预分配 List + 长度/逐项比对，
// 只有「内容真的变了」才允许调用一次编码器（那是唯一的分配点）。
// 采样节流到 10Hz —— 实测 EditorApplication.update 约 295 次/秒（不是 60）。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 按固定节拍采样「编辑器权威的宏状态」，只在真实变化时产出新载荷。
    /// </summary>
    /// <remarks>
    /// <b>为什么判定「真的变了」比采样本身更重要</b>：读端（模块 2 的
    /// <c>ShaderContextRegistry</c>）每次收到不同内容都会推进快照版本，而版本一变整片
    /// 诊断缓存就失效。若这里每次心跳都发一帧，正常打字会平白触发全量重编译。
    /// 因此本类必须做到「内容一致就一个字都不发」。
    /// <b>关于 HashSet 的取舍</b>：比较用 List + 排序 + 逐项比对，而不是 HashSet ——
    /// HashSet 在扩容时仍会分配，达不到稳态零分配的要求。
    /// </remarks>
    internal sealed class BridgeContextSnoop : IBridgeContextSource
    {
        /// <summary>有人在线时的采样间隔（10Hz）。</summary>
        private const double ActiveSampleIntervalSeconds = 0.1;

        /// <summary>
        /// **无人在线**时的采样间隔（0.5Hz）。
        /// </summary>
        /// <remarks>
        /// 没有读端时，采样结果只能用来刷发现文件；而发现文件里会变的只有
        /// 「色彩空间 / 构建目标 / 图形 API / 管线」四个字段 —— 人手改它们以分钟计。
        /// 因此无人时降到 0.5Hz，把稳态的每 tick 开销再降 20×。
        /// 切回有人在线的那一瞬间不必担心错过：新客户端接入时服务端会推最后一帧快照。
        /// </remarks>
        private const double IdleSampleIntervalSeconds = 2.0;

        /// <summary>关键字候选空间（Shader.globalKeywords）的刷新周期。该属性每次调用都会新建数组，只能低频取。</summary>
        private const double CandidateRefreshSeconds = 5.0;

        /// <summary>上报的定义宏上限（防御异常编辑器状态，同时限制注入段规模）。</summary>
        private const int MaxDefinedMacros = 32;

        private readonly List<string> _enabledBuffer = new List<string>(64);
        private readonly List<string> _definedBuffer = new List<string>(MaxDefinedMacros);
        private readonly List<string> _undefinedBuffer = new List<string>(2);

        private GlobalKeyword[] _candidates = Array.Empty<GlobalKeyword>();

        private string[] _lastEnabled = Array.Empty<string>();
        private string[] _lastDefined = Array.Empty<string>();
        private string[] _lastUndefined = Array.Empty<string>();
        private string _lastEnvironmentFingerprint;

        private double _nextSampleTime;
        private double _nextCandidateRefresh;

        public BridgeContextSnoop()
        {
            Environment = BridgeEnvironment.Capture();
            _lastEnvironmentFingerprint = Environment.Fingerprint;
        }

        /// <summary>最近一次生成的载荷（未变化时为上一次的文本）。</summary>
        public string Payload { get; private set; }

        /// <summary>最近一次采样得到的编辑器环境。</summary>
        public BridgeEnvironmentSnapshot Environment { get; private set; }

        /// <summary>节流判定。</summary>
        /// <param name="now">当前时刻（秒）。由调用方注入，本类因此不依赖任何 UnityEditor API。</param>
        /// <param name="hasConsumers">当前是否有读端在线；无人在线时用低频节奏。</param>
        public bool ShouldSample(double now, bool hasConsumers)
        {
            if (now < _nextSampleTime)
            {
                return false;
            }

            _nextSampleTime = now + (hasConsumers ? ActiveSampleIntervalSeconds : IdleSampleIntervalSeconds);
            return true;
        }

        /// <summary>
        /// 采样一次并更新 <see cref="Payload"/> / <see cref="Environment"/>。
        /// </summary>
        /// <param name="now">当前时刻（秒）。由调用方注入（见 <see cref="ShouldSample"/>）。</param>
        /// <param name="hasConsumers">当前是否有读端在线（决定关键字候选空间的刷新节奏）。</param>
        /// <param name="environmentChanged">只影响发现文件的环境是否变化。</param>
        /// <returns>影响载荷的内容是否真实变化。</returns>
        public bool Sample(double now, bool hasConsumers, out bool environmentChanged)
        {
            BridgeMainThread.AssertMainThread(nameof(BridgeContextSnoop) + ".Sample");

            RefreshCandidatesIfDue(now, hasConsumers);
            CollectEnabledKeywords();
            CollectDerivedMacros();

            var changed = false;

            if (!SameSequence(_enabledBuffer, _lastEnabled))
            {
                changed = true;
            }

            if (!SameSequence(_definedBuffer, _lastDefined) || !SameSequence(_undefinedBuffer, _lastUndefined))
            {
                changed = true;
            }

            environmentChanged = !string.Equals(Environment.Fingerprint, _lastEnvironmentFingerprint, StringComparison.Ordinal);
            _lastEnvironmentFingerprint = Environment.Fingerprint;

            if (!changed)
            {
                return false;
            }

            // 唯一允许分配的位置：确实变了才快照 + 编码一次。
            _lastEnabled = _enabledBuffer.ToArray();
            _lastDefined = _definedBuffer.ToArray();
            _lastUndefined = _undefinedBuffer.ToArray();

            Payload = BridgePayloadCodec.Encode(_lastEnabled, _lastDefined, _lastUndefined, BridgePayloadCodec.NowUnixSeconds());
            return true;
        }

        /// <summary>
        /// 刷新关键字候选空间。
        /// </summary>
        /// <remarks>
        /// <c>Shader.globalKeywords</c> 每次访问都会新建数组，因此只能低频调用（5 秒）；
        /// 而 <c>Shader.IsKeywordEnabled(GlobalKeyword)</c> 走的是结构体重载，
        /// **不做字符串封送、不产生分配** —— 这才是能放心按 10Hz 跑的那一半。
        /// </remarks>
        private void RefreshCandidatesIfDue(double now, bool hasConsumers)
        {
            // 无人在线时关键字根本不必要（没人收），因此把候选空间刷新也拉长到与采样同节拍。
            var interval = hasConsumers ? CandidateRefreshSeconds : IdleSampleIntervalSeconds;

            if (now < _nextCandidateRefresh)
            {
                return;
            }

            _nextCandidateRefresh = now + interval;

            try
            {
                _candidates = Shader.globalKeywords ?? Array.Empty<GlobalKeyword>();
            }
            catch (Exception exception)
            {
                BridgeLog.Warn("读取 Shader.globalKeywords 失败：" + exception.Message);
            }
        }

        private void CollectEnabledKeywords()
        {
            _enabledBuffer.Clear();

            var candidates = _candidates;
            for (var i = 0; i < candidates.Length; i++)
            {
                var keyword = candidates[i];
                if (!Shader.IsKeywordEnabled(keyword))
                {
                    continue;
                }

                var name = keyword.name;
                if (!string.IsNullOrEmpty(name))
                {
                    _enabledBuffer.Add(name);
                }
            }

            // 排序：让「内容等价」判定与顺序无关，避免候选空间重排被误判成变化。
            _enabledBuffer.Sort(StringComparer.Ordinal);
        }

        private void CollectDerivedMacros()
        {
            _definedBuffer.Clear();
            _undefinedBuffer.Clear();

            // 顺带刷新环境（10Hz 一次，都是廉价属性读取）。
            Environment = BridgeEnvironment.Capture();

            // ① 图形 API → SHADER_API_*：把模块 3「版本×平台表」的**推定项**换成编辑器实测。
            var shaderApiMacro = Environment.ShaderApiMacro;
            if (!string.IsNullOrEmpty(shaderApiMacro) && _definedBuffer.Count < MaxDefinedMacros)
            {
                _definedBuffer.Add(shaderApiMacro);
            }

            // ② 色彩空间 → UNITY_COLORSPACE_GAMMA。
            //    Linear 时**必须显式上报为「未定义」**：URP 里 #if UNITY_COLORSPACE_GAMMA 有 5 处，
            //    「不定义」本身是需要被如实表达的结论，不能默不做声。
            if (Environment.IsGammaColorSpace)
            {
                if (_definedBuffer.Count < MaxDefinedMacros)
                {
                    _definedBuffer.Add("UNITY_COLORSPACE_GAMMA");
                }
            }
            else
            {
                _undefinedBuffer.Add("UNITY_COLORSPACE_GAMMA");
            }
        }

        /// <summary>长度快速判定 + 逐项比对（顺序敏感）。零分配。</summary>
        private static bool SameSequence(List<string> current, string[] previous)
        {
            if (current.Count != previous.Length)
            {
                return false;
            }

            for (var i = 0; i < previous.Length; i++)
            {
                if (!string.Equals(current[i], previous[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
