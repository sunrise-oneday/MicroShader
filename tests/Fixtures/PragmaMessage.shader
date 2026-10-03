Shader "MicroShader/Fixtures/PragmaMessage"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma message " Attributor smoke noise "
            void vert() { }
            ENDHLSL
        }
    }
}
