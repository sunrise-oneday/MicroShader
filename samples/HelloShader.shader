Shader "MicroShader/HelloTriangle"
{
    // 最简的 URP 可编译 Shader —— 故意不含任何错误。
    // 用途：给新人一条"诊断应该是 0 Error"的基线，用来区分"我的 shader 写错了"
    // 和"工具没生效"。配套说明见 samples/README.md。
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.2, 0.6, 1.0, 1.0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            // 这个 include 路径是 F12 与 include 链补全能穿透的地方
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                // 补全演示：IN.positionOS 的成员、TransformObjectToHClip 的参数类型
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return _BaseColor;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
