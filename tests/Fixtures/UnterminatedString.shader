Shader "MicroShader/Fixtures/UnterminatedString"
{
    SubShader
    {
        Pass
        {
            Name "Broken
            HLSLPROGRAM
            #pragma vertex vert
            ENDHLSL
        }
    }
}
