namespace MicroShader.IntelliSenseEngine;

/// HLSL 方括号属性标注的一条补全候选。
/// Label 是候选显示名，也用来匹配已输入的前缀。InsertBody 不含右方括号，
/// 由补全服务按光标右侧是否已有右方括号决定要不要补上，避免与编辑器自动配对的括号重复。
public readonly record struct HlslAttribute(
    string Label,
    string Detail,
    string Description,
    string InsertBody,
    bool IsSnippet,
    bool RequiresDxc,
    int Rank);

/// HLSL 方括号属性标注的静态候选表。
/// 收录依据是微软 Learn 的 if、for、switch 语句文档、Shader Model 5 Attributes 清单、
/// numthreads 与 earlydepthstencil 的单页文档，以及 DirectX-Specs 的 HLSL Wave Size。
/// 这些都是 HLSL 编译器层的标注，不属于任何 URP 包版本，URP 14 与 URP 17 通用，
/// 因此不需要按 URP 版本裁剪候选。真正的边界是编译器后端：WaveSize 属于 SM 6.6，只有 DXC 才有。
/// Unity 6 官方文档说明 DXC 只在 Windows 的 DX12 上受支持，且由 pragma use_dxc 开启，
/// 所以这一条只在文档里显式启用 DXC 时才给出，不给仍走 FXC 的着色器一条编译不过的候选。
/// URP 不使用几何与曲面细分阶段，SM5 里那一组细分属性不进候选表，避免给出用不上的东西。
public static class HlslAttributes
{
    /// 全部候选，数组顺序即推荐顺序；Rank 决定补全列表里的档位，数字越小越靠前。
    public static readonly HlslAttribute[] All =
    [
        new("branch", "[branch] · if / switch 流控",
            "用在 if 或 switch 前：只求值条件成立的一侧，生成真正的动态分支。Unity 的跨平台写法是 UNITY_BRANCH，在 HLSL 平台上展开为本属性。来源：微软 Learn 的 if 与 switch 语句文档。",
            "branch", false, false, 0),
        new("flatten", "[flatten] · if / switch 流控",
            "用在 if 或 switch 前：两侧都求值，再按条件选结果，不生成分支指令。Unity 的跨平台写法是 UNITY_FLATTEN。来源：微软 Learn 的 if 与 switch 语句文档。",
            "flatten", false, false, 0),
        new("unroll", "[unroll] · for 循环",
            "用在 for 前：把循环展开成逐次迭代的直线代码。与 loop 互斥，同时写会编译报错。Unity 的跨平台写法是 UNITY_UNROLL。来源：微软 Learn 的 for 语句文档。",
            "unroll", false, false, 0),
        new("unroll(n)", "[unroll(n)] · for 循环",
            "用在 for 前：展开循环并指定最大展开次数，写法如 unroll(4)。与 loop 互斥。来源：微软 Learn 的 for 语句文档。",
            "unroll(${1:n})", true, false, 0),
        new("loop", "[loop] · for 循环",
            "用在 for 前：保留真实循环，用流控制执行每次迭代，禁止编译器展开。与 unroll 互斥。Unity 的跨平台写法是 UNITY_LOOP。来源：微软 Learn 的 for 语句文档。",
            "loop", false, false, 0),
        new("fastopt", "[fastopt] · for 循环",
            "用在 for 前：缩短编译时间、降低优化强度，且不会展开循环。SM3 及以上可用，SM4 及以上最能体现作用。来源：微软 Learn 的 for 语句文档。",
            "fastopt", false, false, 0),
        new("allow_uav_condition", "[allow_uav_condition] · compute 的 for 循环",
            "用在 compute shader 的 for 前：允许循环终止条件依赖 UAV 读取，循环体内不得有同步 intrinsic。来源：微软 Learn 的 for 语句文档。",
            "allow_uav_condition", false, false, 0),
        new("forcecase", "[forcecase] · switch",
            "用在 switch 前：强制生成硬件 switch。需要 Feature Level 10_0 及以上硬件。来源：微软 Learn 的 switch 语句文档。",
            "forcecase", false, false, 0),
        new("call", "[call] · switch",
            "用在 switch 前：把各个 case 的函数体移到硬件子程序，switch 变成一组子程序调用。需要 Feature Level 10_0 及以上硬件。来源：微软 Learn 的 switch 语句文档。",
            "call", false, false, 0),
        new("numthreads(x, y, z)", "[numthreads(x, y, z)] · compute 入口",
            "用在 compute shader 的入口函数前：声明一个线程组的三个维度，写法如 numthreads(8, 8, 1)。来源：微软 Learn 的 numthreads 文档。",
            "numthreads(${1:x}, ${2:y}, ${3:z})", true, false, 1),
        new("earlydepthstencil", "[earlydepthstencil] · pixel 入口",
            "用在 pixel shader 的入口函数前：强制在着色器执行前做深度模板测试，被遮挡的片元可以直接跳过。来源：微软 Learn 的 earlydepthstencil 文档。",
            "earlydepthstencil", false, false, 1),
        new("maxvertexcount(n)", "[maxvertexcount(n)] · geometry 入口",
            "用在 geometry shader 前：声明输出顶点数的上限。URP 不使用几何阶段，只有自建管线或非 URP 的 hlsl 头文件里才用得上。来源：微软 Learn 的 Geometry-Shader Object 文档。",
            "maxvertexcount(${1:n})", true, false, 2),
        new("WaveSize(n)", "[WaveSize(N)] · compute 入口 · 仅 DXC / SM 6.6",
            "仅 DXC 与 SM 6.6 可用：用在 compute shader 入口函数前，声明该函数兼容的 wave 宽度，取值必须是 4 到 128 之间的 2 的幂。Unity 中需要 pragma use_dxc，且 DXC 目前只支持 Windows 的 DX12。来源：DirectX-Specs 的 HLSL Wave Size。",
            "WaveSize(${1:32})", true, true, 3),
    ];
}
