Shader "MicroShader/Fixtures/UnterminatedBlockComment"
{
    SubShader
    {
        /* 这个块注释永远不会闭合
        Pass
        {
            HLSLPROGRAM
            ENDHLSL
        }
    }
}
