Shader "MicroShader/Fixtures/Mixed"
{
    CGINCLUDE
    float4 _CgShared;
    ENDCG

    HLSLINCLUDE
    float4 _HlslShared;
    ENDHLSL

    SubShader
    {
        Pass
        {
            Name "TwoBlocks"

            HLSLPROGRAM
            #pragma vertex vert
            ENDHLSL

            HLSLPROGRAM
            #pragma fragment frag
            ENDHLSL
        }

        Pass
        {
            Name "Legacy"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            ENDCG
        }
    }
}
