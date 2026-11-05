Shader "MicroShader/Fixtures/UnterminatedBlock"
{
    SubShader
    {
        Pass
        {
            Name "Broken"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            half4 frag(Varyings input) : SV_Target
            {
                return half4(1, 1, 1, 1);
            }
        }
    }
}
