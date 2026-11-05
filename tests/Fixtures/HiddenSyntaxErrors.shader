Shader "Custom/HiddenSyntaxErrors_URP"
{
    Properties
    {
        // 错误1：ShaderLab 属性行末尾多了分号
        _BaseMap ("Base Map", 2D) = "white" {};
        _BaseColor ("Base Color", Color) = (1,1,1,1)
    }

    SubShader
    {
        Tags
        {
            // 错误2：Tags 键值对之间多了逗号
            "RenderPipeline" = "UniversalPipeline", "RenderType" = "Opaque"
        }

        Pass
        {
            Name "Unlit"
            // 错误3：字符串缺少右引号
            Tags { "LightMode" = "UniversalForward }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            // 错误4：#include 引号不匹配，缺少右引号
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl

            // 错误5：CBUFFER_START 缺少右括号
            CBUFFER_START(UnityPerMaterial
                float4 _BaseMap_ST;
                half4 _BaseColor;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                // 错误6：结构体成员缺少分号
                float4 positionOS : POSITION
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                // 错误7：结构体成员末尾用了逗号而不是分号
                float2 uv : TEXCOORD0,
                float3 normalWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(v.positionOS.xyz);
                o.uv = TRANSFORM_TEX(v.uv, _BaseMap);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // 错误8：函数调用多了一个右括号
                half4 col = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv)) * _BaseColor;
                return col;
            }
            // 错误9：ENDHLSL 拼写错误，少了一个 L
            ENDHLS
        }
    }
}
