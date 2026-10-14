// Pure HLSL（无 ShaderLab 外壳）—— 本文件索引应包含本文件声明的符号
struct LightData
{
    float3 directionWS;
    float3 color;
};

float3 ComputeShadowCoord(float3 worldPos, float4x4 shadowMatrix)
{
    float3 shadowCoord = mul(shadowMatrix, float4(worldPos, 1.0)).xyz;
    return shadowCoord;
}

float4 FragmentMain(float3 worldPos) : SV_Target
{
    float atten = 1.0;
    float biasLocal = 0.01;
    LightData ld;
    float a = bia
    float b = ld.dir
    return float4(1,1,1,1);
}
