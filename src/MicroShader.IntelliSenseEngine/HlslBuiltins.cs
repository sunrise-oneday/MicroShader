namespace MicroShader.IntelliSenseEngine;

/// <summary>一条内建符号（HLSL 语言内建函数 / ShaderLab 关键字）。</summary>
public readonly record struct BuiltinSymbol(string Name, int LspKind, string Signature, string Description);

/// <summary>
/// 内置知识库："只含语言层符号"。
/// </summary>
/// <remarks>
/// "法律边界（详设 v2.0 第 1 条，必须遵守）"：URP 源码是 Unity Companion License，
/// 不允许抽取后随「非 Unity 依赖」的工具分发。因此本表"只"包含：
/// <list type="number">
/// <item>HLSL 语言内建函数与类型 —— 属语言规范，不是任何厂商的源码符号；</item>
/// <item>ShaderLab 关键字与 pragma —— 取公开文档语义，不做源码复制。</item>
/// </list>
/// "URP / 包内符号一律不内置"：运行时从用户工程的 "Packages/" 或
/// "Library/PackageCache/" 现场解析（合法，且天然随版本自适应）。
/// LSP CompletionItemKind 取值：2=Method 3=Function 6=Variable 14=Keyword 22=Struct。
/// </remarks>
public static class HlslBuiltins
{
    /// <summary>HLSL 语言内建函数（子集：URP shader 里实际高频出现的那批）。</summary>
    public static readonly BuiltinSymbol[] Functions =
    [
        new("abs", 3, "T abs(T x)", "绝对值"),
        new("acos", 3, "T acos(T x)", "反余弦"),
        new("all", 3, "bool all(T x)", "所有分量为真"),
        new("any", 3, "bool any(T x)", "任一分量为真"),
        new("asin", 3, "T asin(T x)", "反正弦"),
        new("atan", 3, "T atan(T x)", "反正切"),
        new("atan2", 3, "T atan2(T y, T x)", "两参数反正切"),
        new("ceil", 3, "T ceil(T x)", "向上取整"),
        new("clamp", 3, "T clamp(T x, T lo, T hi)", "把 x 夹到 [lo, hi]"),
        new("clip", 3, "void clip(T x)", "任一分量小于 0 时丢弃当前像素"),
        new("cos", 3, "T cos(T x)", "余弦"),
        new("cross", 3, "float3 cross(float3 a, float3 b)", "叉积"),
        new("ddx", 3, "T ddx(T x)", "x 方向的屏幕空间偏导"),
        new("ddx_coarse", 3, "T ddx_coarse(T x)", "粗粒度 ddx"),
        new("ddx_fine", 3, "T ddx_fine(T x)", "细粒度 ddx"),
        new("ddy", 3, "T ddy(T x)", "y 方向的屏幕空间偏导"),
        new("distance", 3, "float distance(T a, T b)", "欧氏距离"),
        new("dot", 3, "float dot(T a, T b)", "点积"),
        new("exp", 3, "T exp(T x)", "自然指数"),
        new("exp2", 3, "T exp2(T x)", "2 的 x 次幂"),
        new("floor", 3, "T floor(T x)", "向下取整"),
        new("frac", 3, "T frac(T x)", "取小数部分"),
        new("fwidth", 3, "T fwidth(T x)", "abs(ddx)+abs(ddy)"),
        new("isnan", 3, "bool isnan(T x)", "是否 NaN"),
        new("length", 3, "float length(T x)", "向量长度"),
        new("lerp", 3, "T lerp(T a, T b, T t)", "线性插值"),
        new("log", 3, "T log(T x)", "自然对数"),
        new("log2", 3, "T log2(T x)", "以 2 为底对数"),
        new("mad", 3, "T mad(T a, T b, T c)", "a * b + c（一次 FMA）"),
        new("max", 3, "T max(T a, T b)", "较大者"),
        new("min", 3, "T min(T a, T b)", "较小者"),
        new("modf", 3, "T modf(T x, out T ip)", "拆出整数与小数部分"),
        new("mul", 3, "T mul(A a, B b)", "矩阵/向量乘法（注意参数顺序）"),
        new("normalize", 3, "T normalize(T x)", "单位化"),
        new("pow", 3, "T pow(T x, T y)", "x 的 y 次幂"),
        new("reflect", 3, "T reflect(T i, T n)", "反射向量"),
        new("refract", 3, "T refract(T i, T n, T eta)", "折射向量"),
        new("round", 3, "T round(T x)", "四舍五入到最近整数"),
        new("rsqrt", 3, "T rsqrt(T x)", "1/sqrt(x)"),
        new("saturate", 3, "T saturate(T x)", "夹到 [0, 1]"),
        new("sign", 3, "T sign(T x)", "符号"),
        new("sin", 3, "T sin(T x)", "正弦"),
        new("sincos", 3, "void sincos(T x, out T s, out T c)", "同时求正弦与余弦"),
        new("smoothstep", 3, "T smoothstep(T lo, T hi, T x)", "平滑阶跃插值"),
        new("sqrt", 3, "T sqrt(T x)", "平方根"),
        new("step", 3, "T step(T edge, T x)", "阶跃函数"),
        new("tan", 3, "T tan(T x)", "正切"),
        new("transpose", 3, "T transpose(T m)", "矩阵转置"),
        new("tex2D", 3, "float4 tex2D(sampler2D s, float2 uv)", "（旧式）采样 2D 纹理；URP 请用 SAMPLE_TEXTURE2D"),
        new("tex2Dlod", 3, "float4 tex2Dlod(sampler2D s, float4 uv)", "（旧式）指定 mip 采样"),
        new("tex2Dbias", 3, "float4 tex2Dbias(sampler2D s, float4 uv)", "（旧式）带偏置采样"),
        new("SAMPLE_TEXTURE2D", 3, "float4 SAMPLE_TEXTURE2D(TEXTURE2D_PARAM(tex, samp), float2 uv)", "URP 宏：采样 2D 纹理"),
        new("SAMPLE_TEXTURE2D_LOD", 3, "float4 SAMPLE_TEXTURE2D_LOD(TEXTURE2D_PARAM(tex, samp), float2 uv, float lod)", "URP 宏：指定 mip 采样"),
        new("TRANSFORM_TEX", 3, "float2 TRANSFORM_TEX(float2 uv, TEXTURE2D_ARGS(tex, samp))", "URP 宏：套用纹理的 _ST 缩放偏移"),
        new("TEXTURE2D", 22, "TEXTURE2D(name)", "URP 宏：声明纹理对象"),
        new("SAMPLER", 22, "SAMPLER(name)", "URP 宏：声明采样器"),
        new("CBUFFER_START", 14, "CBUFFER_START(name)", "开始常量缓冲区（SRP Batcher 要求同名同布局）"),
        new("CBUFFER_END", 14, "CBUFFER_END", "结束常量缓冲区"),
    ];

    /// <summary>ShaderLab / HLSL 关键字与 pragma 词干。</summary>
    public static readonly BuiltinSymbol[] Keywords =
    [
        new("Shader", 14, "Shader \"Name\" { ... }", "着色器声明"),
        new("SubShader", 14, "SubShader { ... }", "子着色器块"),
        new("Pass", 14, "Pass { ... }", "渲染通道"),
        new("Tags", 14, "Tags { \"Key\" = \"Value\" }", "渲染管线元数据标签"),
        new("Properties", 14, "Properties { ... }", "暴露给材质面板的属性"),
        new("HLSLPROGRAM", 14, "HLSLPROGRAM ... ENDHLSL", "HLSL 程序块"),
        new("ENDHLSL", 14, "ENDHLSL", "结束 HLSL 程序块"),
        new("CGPROGRAM", 14, "CGPROGRAM ... ENDCG", "遗留 CG 程序块"),
        new("ENDCG", 14, "ENDCG", "结束遗留 CG 程序块"),
        new("HLSLINCLUDE", 14, "HLSLINCLUDE ... ENDHLSL", "文件级共享 HLSL 块"),
        new("CGINCLUDE", 14, "CGINCLUDE ... ENDCG", "文件级共享 CG 块"),
        new("FallBack", 14, "FallBack \"Shader/Name\"", "回退着色器"),
        new("Name", 14, "Name \"PassName\"", "Pass 名"),
        new("Blend", 14, "Blend SrcAlpha OneMinusSrcAlpha", "混合模式"),
        new("ZWrite", 14, "ZWrite On", "深度写入"),
        new("ZTest", 14, "ZTest LEqual", "深度测试"),
        new("Cull", 14, "Cull Back", "背面剔除"),
        new("ColorMask", 14, "ColorMask RGB", "颜色通道掩码"),
        new("Stencil", 14, "Stencil { ... }", "模板测试"),
        new("LOD", 14, "LOD 200", "SubShader 细节层级"),
        new("Queue", 14, "Queue \"Geometry\"", "渲染队列"),
        new("vertex", 14, "#pragma vertex <fn>", "顶点入口"),
        new("fragment", 14, "#pragma fragment <fn>", "片元入口"),
        new("kernel", 14, "#pragma kernel <fn>", "计算着色器入口"),
        new("target", 14, "#pragma target 3.0", "着色器模型目标"),
        new("multi_compile", 14, "#pragma multi_compile A B", "多关键字变体"),
        new("shader_feature", 14, "#pragma shader_feature A B", "特性关键字变体"),
        new("multi_compile_local", 14, "#pragma multi_compile_local A B", "局部多关键字变体"),
        new("shader_feature_local", 14, "#pragma shader_feature_local A B", "局部特性关键字变体"),
        new("include", 14, "#include \"Path/File.hlsl\"", "包含头文件"),
        new("include_with_pragmas", 14, "#include_with_pragmas \"File.hlsl\"", "包含头文件并采纳其 pragma"),
        new("pragma", 14, "#pragma ...", "编译指示"),
    ];

    /// <summary>全部内建符号（函数 + 关键字）。</summary>
    public static IEnumerable<BuiltinSymbol> All()
    {
        foreach (var f in Functions)
        {
            yield return f;
        }

        foreach (var k in Keywords)
        {
            yield return k;
        }
    }
}
