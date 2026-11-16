Shader "MicroShader/Fixtures/PragmaRobustness"
{
    SubShader
    {
        Pass
        {
            Name "Whitespace"
            HLSLPROGRAM
            #pragma	vertex	vert
            #pragma   fragment     frag

            #pragma  multi_compile_local_fragment   _FOO _BAR
            #pragma multi_compile __ _SECOND_SET
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #pragma target 4.5
            #pragma only_renderers d3d11
            ENDHLSL
        }
    }
}
