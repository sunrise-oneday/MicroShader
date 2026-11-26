Shader "MicroShader/Fixtures/DeadIncludeShared"
{
    HLSLINCLUDE
    #include "Packages/com.example.missing/Missing.hlsl"
    ENDHLSL

    SubShader
    {
        Pass
        {
            Name "First"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
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
