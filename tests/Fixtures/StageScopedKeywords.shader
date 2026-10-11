Shader "MicroShader/Fixtures/StageScoped"
{
    SubShader
    {
        Pass
        {
            Name "Scoped"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_vertex _ _VERTEX_SCOPE_ON
            #pragma shader_feature_local _LOCAL_FEATURE
            #pragma shader_feature_local_fragment _FRAGMENT_ONLY_FEATURE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
}
