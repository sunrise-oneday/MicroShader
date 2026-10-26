Shader "T/NoBlock"
{
    Properties
    {
        _MainTex ("Main", 2D) = "white" {}
        _LeakProbe ("Leak", Float) = 1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            Name "PASSNAME"
        }
    }
}
