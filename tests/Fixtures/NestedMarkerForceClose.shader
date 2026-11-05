Shader "MicroShader/Fixtures/ForceClose"
{
    SubShader
    {
        Pass
        {
            Name "Forced"
            HLSLPROGRAM
            #pragma vertex vert
            HLSLPROGRAM
            #pragma fragment frag
            ENDHLSL
        }
    }
}
