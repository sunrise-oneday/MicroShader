Shader "MicroShader/Fixtures/ModernCg"
{
    SubShader
    {
        Pass
        {
            Name "ModernCg"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            ENDCG
        }
    }
}
