Shader "MicroShader/Fixtures/Orphan"
{
    SubShader
    {
        HLSLPROGRAM
        #pragma vertex vert
        #pragma fragment frag
        ENDHLSL

        Pass
        {
            Name "Real"
            HLSLPROGRAM
            #pragma vertex vert
            ENDHLSL
        }
    }
}
