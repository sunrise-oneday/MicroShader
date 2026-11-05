Shader "MicroShader/Fixtures/IncludeMatrix"
{
    HLSLINCLUDE
    #pragma multi_compile _ SHARED_TOGGLE
    #include "SharedCommon.hlsl"
    float4 _SharedTint;
    ENDHLSL

    SubShader
    {
        Pass
        {
            Name "First"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma shader_feature_local_fragment _ALPHATEST_ON

            #include_with_pragmas "Pragmas.hlsl"
            #include "Local.hlsl"

            float4 _LocalTint;
            ENDHLSL
        }
    }

    SubShader
    {
        Pass
        {
            Name "Second"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
