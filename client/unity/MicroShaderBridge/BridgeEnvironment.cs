// ─────────────────────────────────────────────────────────────────────────────
// 模块 11B · Unity 桥接 · 环境采集
//
// 「这个编辑器现在是什么样」的**只读快照**：色彩空间、构建目标、图形 API、渲染管线。
// 只采集事实，不做任何「该上报什么宏」的判断 —— 那是 BridgeContextSnoop 的策略。
// ─────────────────────────────────────────────────────────────────────────────

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace MicroShader.UnityBridge
{
    /// <summary>
    /// 编辑器环境快照（值类型：复制即快照，不存在被后台线程改坏的风险）。
    /// </summary>
    /// <remarks>
    /// 用「构造函数 + 只读属性」而不是 C# 9 的 <c>init</c> 访问器：
    /// Unity 2022.3 的 .NET Standard 2.1 剖面没有 <c>System.Runtime.CompilerServices.IsExternalInit</c>，
    /// 用 <c>init</c> 会直接编译失败（本机实测 CS0518）。
    /// </remarks>
    internal readonly struct BridgeEnvironmentSnapshot
    {
        public BridgeEnvironmentSnapshot(
            string colorSpace,
            bool isGammaColorSpace,
            string buildTarget,
            string graphicsApi,
            string shaderApiMacro,
            string pipelineAssetType)
        {
            ColorSpace = colorSpace;
            IsGammaColorSpace = isGammaColorSpace;
            BuildTarget = buildTarget;
            GraphicsApi = graphicsApi;
            ShaderApiMacro = shaderApiMacro;
            PipelineAssetType = pipelineAssetType;
            Fingerprint = colorSpace + "|" + buildTarget + "|" + graphicsApi + "|" + shaderApiMacro + "|" + pipelineAssetType;
        }

        public string ColorSpace { get; }

        public bool IsGammaColorSpace { get; }

        public string BuildTarget { get; }

        public string GraphicsApi { get; }

        /// <summary>对应 Unity 的 <c>SHADER_API_*</c> 宏名；映射不出时为空串。</summary>
        public string ShaderApiMacro { get; }

        public string PipelineAssetType { get; }

        /// <summary>只由「会影响诊断」的字段构成，用于判断发现文件是否需要重写。</summary>
        public string Fingerprint { get; }
    }

    /// <summary>采集编辑器里会影响 shader 预处理的环境事实。</summary>
    /// <remarks>
    /// 本类**只能在主线程调用**（读 <c>PlayerSettings</c> / <c>GraphicsSettings</c>）。
    /// 每一个字段都单独 try/catch：某个 API 在特定 Unity 版本上不可用时，
    /// 我们宁可少一个字段，也不能让 10Hz 的采样 tick 整条崩掉。
    /// </remarks>
    internal static class BridgeEnvironment
    {
        public static BridgeEnvironmentSnapshot Capture()
        {
            var colorSpace = "Linear";
            var isGamma = false;
            try
            {
                isGamma = PlayerSettings.colorSpace == ColorSpace.Gamma;
                colorSpace = isGamma ? "Gamma" : "Linear";
            }
            catch (Exception exception)
            {
                BridgeLog.Warn("读取 PlayerSettings.colorSpace 失败：" + exception.Message);
            }

            var buildTarget = string.Empty;
            var graphicsApi = string.Empty;
            var shaderApiMacro = string.Empty;
            try
            {
                buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();

                // 用**构建目标**的图形 API，而不是编辑器自己跑在哪个后端：
                // 着色器编译按目标平台走，编辑器当前后端不是权威口径。
                var apis = PlayerSettings.GetGraphicsAPIs(EditorUserBuildSettings.activeBuildTarget);
                if (apis != null && apis.Length > 0)
                {
                    graphicsApi = apis[0].ToString();
                    shaderApiMacro = MapShaderApiMacro(apis[0]);
                }
            }
            catch (Exception exception)
            {
                BridgeLog.Warn("读取图形 API 失败：" + exception.Message);
            }

            var pipelineAssetType = string.Empty;
            try
            {
                var pipeline = GraphicsSettings.renderPipelineAsset;
                pipelineAssetType = pipeline == null ? "BuiltIn" : pipeline.GetType().Name;
            }
            catch (Exception exception)
            {
                BridgeLog.Warn("读取 RenderPipelineAsset 失败：" + exception.Message);
            }

            return new BridgeEnvironmentSnapshot(
                colorSpace,
                isGamma,
                buildTarget,
                graphicsApi,
                shaderApiMacro,
                pipelineAssetType);
        }

        /// <summary>
        /// 图形 API → Unity 的 <c>SHADER_API_*</c> 宏。
        /// </summary>
        /// <remarks>
        /// 只映射能确证的一对一关系。映射不出来就返回空串 ——
        /// **宁可不注入，也不注入一个推测的宏名**（猜错的宏比缺少的宏危险得多：它会静默改变代码分支）。
        /// </remarks>
        private static string MapShaderApiMacro(GraphicsDeviceType device)
        {
            switch (device)
            {
                case GraphicsDeviceType.Direct3D11: return "SHADER_API_D3D11";
                case GraphicsDeviceType.Direct3D12: return "SHADER_API_D3D12";
                case GraphicsDeviceType.Vulkan: return "SHADER_API_VULKAN";
                case GraphicsDeviceType.OpenGLCore: return "SHADER_API_GLCORE";
                case GraphicsDeviceType.OpenGLES2: return "SHADER_API_GLES";
                case GraphicsDeviceType.OpenGLES3: return "SHADER_API_GLES3";
                case GraphicsDeviceType.Metal: return "SHADER_API_METAL";
                default: return string.Empty;
            }
        }
    }
}
