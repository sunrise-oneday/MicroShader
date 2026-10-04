Shader "Custom/BrokenURP_Test"
{
    Properties
    {
        _BaseMap ("Base Map", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Cutoff ("Cutoff", Range(0,1)) = 0.5
    }

    SubShader
    {
        Tags
        {
            // 故意拼错：UniversalPipeline -> UniversialPipeline
            "RenderPipeline" = "UniversialPipeline"
            "RenderType" = "Opaque"
        }

        Pass
        {
            Name "ForwardLit"

            // 故意拼错：UniversalForward -> UniversalFoward
            Tags { "LightMode" = "UniversalFoward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            // 故意拼错 URP 包路径：universal -> universial
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
            CBUFFER_END

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings vert(Attributes v)
            {
                Varyings o;

                // 故意函数名错误：TransformObjectToHClip -> TransformObjectToHClip
                // 而且参数类型也不对，应该传 v.positionOS.xyz
                o.positionHCS = TransformObjectToHClip(v.positionOS);

                // 故意使用未声明的 _MainTex
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);

                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                // 故意使用 URP 不推荐的 tex2D，并继续用未声明 _MainTex
                half4 col = tex2D(_MainTex, i.uv) * _BaseColor;

                // 故意返回类型不对：frag 声明 half4，却返回 half3
                return col.rgb;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
