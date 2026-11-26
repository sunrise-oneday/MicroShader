namespace MicroShader.IntelliSenseEngine;

/// ShaderGraph Custom Function 的隐式上下文。
/// 这类文件是裸 HLSL 片段，自己不写任何 include：ShaderGraph 生成 shader 时才把
/// URP 的渲染管线头文件 include 到它外层。所以在编辑器里单独打开时，
/// UnityTexture2D、UnitySamplerState、SAMPLE_TEXTURE2D_GRAD 这些名字在磁盘上找不到定义，
/// 补全也就一个都不给。
/// 这里的职责只有两件：判断一份文档是不是这种片段，以及给出它需要的隐式头文件清单。
/// 判定与清单都必须是纯函数：预热器在后台线程调用它，不碰磁盘、不解析工程。
/// 真正的路径解析交给 IncludeSymbolWarmer 已有的虚拟路径解析（按文件所属工程走 VfsIndex），
/// 这样多工程窗口下每个文件各拿自己那份包，不会串。
public static class ShaderGraphPrelude
{
    /// 用来判断「这是 ShaderGraph 片段」的特征词。
    /// 只收 ShaderGraph 槽位类型与它实际会用到的采样宏：这些名字在普通 HLSL 里不存在，
    /// 出现即说明这份代码的编译上下文是 ShaderGraph 注入的那一层。
    /// 用固定几个词而不是扫描 #include，是因为这类文件本来就没有 include；
    /// 反过来，普通 URP 头文件即使 include 了 Core.hlsl 也不会出现这些词。
    private static readonly string[] Markers =
    [
        "UnityTexture2D",
        "UnityTexture2DArray",
        "UnityTextureCube",
        "UnityTexture3D",
        "UnitySamplerState",
        "SAMPLE_TEXTURE2D_GRAD",
    ];

    /// 预置上下文的虚拟路径。两个文件都不 include 别的文件，所以预热只需扫一层，
    /// 不会像 Common.hlsl 那样把七个平台的 API 文件全都拉进来。
    /// Texture.hlsl 提供五个 Unity 纹理槽位结构与 UnityBuild* 系列宏；
    /// API/D3D11.hlsl 提供 TEXTURE2D、SAMPLER 这类声明宏与整族 SAMPLE_TEXTURE2D_* 采样宏。
    /// 路径里不写版本号：包目录的版本由工程的包解析负责，跨 URP 14 与 17 都能解析到。
    public static readonly string[] DefaultVirtualPaths =
    [
        "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl",
        "Packages/com.unity.render-pipelines.core/ShaderLibrary/API/D3D11.hlsl",
    ];

    /// 判定一份文档要不要补上隐式上下文。
    /// 只看扩展名与几个特征词：不读工程、不查包、不扫 .shadergraph 资产。
    /// 这样判定在每次按键时都能跑（一次子串查找，微秒级），也不会因为工程拓扑还没建好而漏判。
    /// 误判的代价是「多给一批 URP 符号」，不是「给错符号」：这些文件本来就在 URP 工程的
    /// Assets 目录里，补出来的名字与它将来真正被编译时的上下文一致。
    /// physicalPath 可能为空（未保存的新文件），这时只按内容判定。
    public static bool ShouldApply(string? physicalPath, string text)
    {
        if (!string.IsNullOrEmpty(physicalPath)
            && !physicalPath.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var marker in Markers)
        {
            if (text.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// 缓存里出现这个类型，就说明预置上下文已经预热生效。
    /// 补全侧用它来决定要不要跳过与预置头重名的语言内建条目，避免同一个名字出现两条候选。
    public const string ProbeTypeName = "UnityTexture2D";
}
