Shader "MicroShader/Fixtures/Markers"
{
    Properties
    {
        _Marker ("Pass HLSLPROGRAM ENDHLSL", 2D) = "white" {}
        _Tag ("LightMode", Float) = 0
    }

    SubShader
    {
        // Pass
        /* HLSLPROGRAM
           ENDHLSL
           Pass
        */
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        Pass
        {
            Name "Real"
            UsePass "Universal Render Pipeline/Lit/ForwardLit"

            HLSLPROGRAM
            // ENDHLSL inside a line comment
            /* CGPROGRAM ENDCG */
            float4 _Fake; // Pass
            ENDHLSL
        }
    }
}
