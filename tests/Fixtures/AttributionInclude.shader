Shader "MicroShader/Fixtures/AttributionInclude"
{
    SubShader
    {
        Pass
        {
            HLSLPROGRAM
            #include "Attribution/Core.hlsl"
            void vert() { }
            ENDHLSL
        }
    }
}
