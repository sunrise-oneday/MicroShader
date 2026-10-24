using MicroShader.ContextEngine;
using MicroShader.ObservabilityEngine;

namespace MicroShader.DiagnosticEngine;

/// <summary>宏取值的证据等级 —— 必须一路带到注入段注释里，供排障时区分「实测」与「推定」。</summary>
public enum MacroProvenance : byte
{
    /// <summary>从本机真实 Unity 日志里直接读出来的。</summary>
    Measured = 0,

    /// <summary>从工程设置（ProjectSettings）按已确认的字段语义推导出来的。</summary>
    Derived = 1,

    /// <summary>按「Unity 版本 × 平台」表查出来的，"未经本机验证"。</summary>
    Postulated = 2,
}

/// <summary>平台宏集合的来源。</summary>
public enum PlatformMacroSourceKind : byte
{
    /// <summary>没有任何来源（空集）。</summary>
    None = 0,

    /// <summary>工程自身 "Logs/Editor.log" 的 "Platform defines:" 行。</summary>
    EditorLog = 1,

    /// <summary>只有工程设置的推导项。</summary>
    ProjectSettings = 2,

    /// <summary>只有「版本 × 平台」表的推定项。</summary>
    VersionPlatformTable = 3,

    /// <summary>推导项 + 推定项混合（本机的常态）。</summary>
    Mixed = 4,
}

/// <summary>平台事实的来源。</summary>
public enum PlatformFactsSource : byte
{
    /// <summary>没有任何来源，解算器按本工具第一阶段的目标平台（Windows / D3D11）推定。</summary>
    Assumed = 0,

    /// <summary>Unity 桥接上报的构建目标与图形 API（发现文件的 activeBuildTarget / graphicsApi 字段）。</summary>
    Bridge = 1,
}

/// <summary>
/// 一次解算用的平台描述：构建目标 + 图形 API + 来源。
/// 只承载字符串与枚举，刻意不引桥接类型 —— 解算器保持纯函数，取事实的责任在调用方。
/// </summary>
/// <remarks>
/// 构建目标现在只用来写注释与留痕；真正决定注入哪个 SHADER_API_* 宏的是图形 API
/// （Unity 按构建目标的图形 API 编译，编辑器自己跑在哪个后端不是权威口径）。
/// </remarks>
public readonly record struct ShaderPlatformFacts(string BuildTarget, string GraphicsApi, PlatformFactsSource Source)
{
    /// <summary>没有平台事实时的取值：来源为推定，字符串全空。</summary>
    public static ShaderPlatformFacts Assumed { get; } = new(string.Empty, string.Empty, PlatformFactsSource.Assumed);
}

/// <summary>一条平台宏。</summary>
public readonly struct PlatformMacro
{
    /// <summary>宏名。</summary>
    public string Name { get; init; }

    /// <summary>宏值（"#define NAME value" 里的 value；无值时为空串）。</summary>
    public string Value { get; init; }

    /// <summary>证据等级。</summary>
    public MacroProvenance Provenance { get; init; }

    /// <summary>来源说明（写进注入段注释，便于用户理解工具假设了什么）。</summary>
    public string Source { get; init; }

    public override string ToString() => Value.Length == 0 ? Name : Name + "=" + Value;
}

/// <summary>一次平台宏解算的结果。</summary>
public sealed class PlatformMacroSet
{
    private readonly List<PlatformMacro> _macros = new();
    private readonly List<string> _notes = new();

    /// <summary>宏列表（保持添加顺序）。</summary>
    public IReadOnlyList<PlatformMacro> Macros => _macros;

    /// <summary>解算过程的人读说明（写进注入段注释，也是自检的断言对象）。</summary>
    public IReadOnlyList<string> Notes => _notes;

    /// <summary>来源类别。</summary>
    public PlatformMacroSourceKind SourceKind { get; set; } = PlatformMacroSourceKind.None;

    /// <summary>是否存在推定项。</summary>
    public bool HasPostulated
    {
        get
        {
            foreach (var macro in _macros)
            {
                if (macro.Provenance == MacroProvenance.Postulated)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public void Add(string name, string value, MacroProvenance provenance, string source)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        foreach (var existing in _macros)
        {
            if (string.Equals(existing.Name, name, StringComparison.Ordinal))
            {
                return;
            }
        }

        _macros.Add(new PlatformMacro
        {
            Name = name,
            Value = value,
            Provenance = provenance,
            Source = source,
        });
    }

    public void AddNote(string note)
    {
        if (!string.IsNullOrWhiteSpace(note))
        {
            _notes.Add(note);
        }
    }

    /// <summary>渲染成一段可写进注入段的注释文本（单行）。</summary>
    public string Describe()
    {
        if (_macros.Count == 0)
        {
            return "平台宏：无（未采集到任何可用来源）";
        }

        var postulated = new List<string>();
        foreach (var macro in _macros)
        {
            if (macro.Provenance == MacroProvenance.Postulated)
            {
                postulated.Add(macro.Name);
            }
        }

        var text = "平台宏 " + _macros.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                   " 项，来源=" + SourceKind;
        if (postulated.Count > 0)
        {
            text += "，推定项=[" + string.Join(", ", postulated) + "]（未经本机验证）";
        }

        return text;
    }
}

/// <summary>
/// 平台宏解算器 —— v2.0 清单第 15 条要求的三层来源，按优先级依次尝试。
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>"① 工程 Editor.log" 的 "Platform defines:" 行（"&lt;Project&gt;/Logs/Editor.log"
/// 与 "%LOCALAPPDATA%/Unity/Editor/Editor.log" "两处都探"）。工程级日志只有用 "-logFile"
/// 指定时才出现，Unity 6.x 才默认写工程级。</item>
/// <item>"② ProjectSettings 推导"：目前只实现色彩空间（字段与语义都已在本机确证）。</item>
/// <item>"③ 版本 × 平台表"：只覆盖「平台能力类宏」，且一律标记 <see cref="MacroProvenance.Postulated"/>。</item>
/// </list>
/// "Editor.log 是稀疏采样"：实测本机 "%LOCALAPPDATA%/Unity/Editor/Editor.log" 有 310063 字节，
/// 但 "Platform defines" 与 "SHADER_API" 都是 0 命中 —— 所以它只能当「契约快照 + 黄金期望」，
/// 不能当常驻校准源。本机常态是走 ② + ③ 并如实标注推定。
/// </remarks>
public static class PlatformMacroResolver
{
    /// <summary>默认目标平台（本工具第一阶段的唯一目标平台）。</summary>
    public const string DefaultPlatform = "Windows";

    /// <summary>解算一篇文档所属工程的平台宏集合。</summary>
    /// <remarks>
    /// platformFacts 是「当前构建目标 / 图形 API」的载体，由调用方从注册表的动态上下文取。
    /// 取不到就传默认值（来源 = 推定），解算结果与引入平台事实之前逐项相同。
    /// </remarks>
    public static PlatformMacroSet Resolve(
        UnityProjectLayout layout,
        UnityInstallation? installation,
        string? unityVersion = null,
        string platform = DefaultPlatform,
        ShaderPlatformFacts platformFacts = default)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (EditorLogPlatformProbe.TryRead(layout, out var measured) && measured.Macros.Count > 0)
        {
            measured.SourceKind = PlatformMacroSourceKind.EditorLog;
            measured.AddNote("平台宏来自本工程/本机的 Editor.log Platform defines 行（实测）");
            return measured;
        }

        var set = new PlatformMacroSet();
        set.AddNote("Editor.log 未提供 Platform defines 行（本机实测 0 命中）→ 退化为 ProjectSettings 推导 + 版本×平台表");

        var derived = ProjectSettingsMacroDeriver.Derive(layout, set);
        AddVersionPlatformTable(set, platform, unityVersion, platformFacts);

        set.SourceKind = derived && set.HasPostulated
            ? PlatformMacroSourceKind.Mixed
            : derived ? PlatformMacroSourceKind.ProjectSettings
            : set.HasPostulated ? PlatformMacroSourceKind.VersionPlatformTable
            : PlatformMacroSourceKind.None;

        _ = installation;
        return set;
    }

    /// <summary>
    /// 第三层：「Unity 版本 × 平台」表。只放"平台能力类"宏，且一律标推定。
    /// </summary>
    /// <remarks>
    /// 选词纪律：只注入在真实 URP 代码里确实承载语义的宏。实测 URP 14.0.12 + core 14.0.12
    /// 的 296 个 shader 文件里，"SHADER_API_D3D11" 出现 12 次、
    /// "UNITY_COMPILER_DXC" 出现在 "UnityInput.hlsl:6"；
    /// 而 "SHADER_API_DESKTOP" "0 次"出现 —— 因此不注入它。
    /// 注入一个真实 Unity 不会定义、代码也不看的宏是没意义的；注入一个真实 Unity 会定义的宏则必须标推定。
    /// 本层覆盖的是 SM6.0 校验路径（DXC 1.8.2502 + dxcompiler.dll），不是 Unity 的完整平台宏矩阵。
    /// </remarks>
    private static void AddVersionPlatformTable(
        PlatformMacroSet set,
        string platform,
        string? unityVersion,
        ShaderPlatformFacts platformFacts)
    {
        // 有桥接上报的图形 API 时按它查宏名；D3D11 与「认不出的 API」都落到下面那张旧的
        // Windows/D3D11 表，区别只是有没有留痕说明「这是推定值」。
        var factsDriven = platformFacts.Source == PlatformFactsSource.Bridge
            && !string.IsNullOrEmpty(platformFacts.GraphicsApi);

        if (factsDriven)
        {
            var apiMacro = MapShaderApiMacro(platformFacts.GraphicsApi);

            if (apiMacro is null)
            {
                set.AddNote(
                    "桥接上报的图形 API '" + platformFacts.GraphicsApi + "' 不在已知映射里 → 平台能力宏退回 Windows/D3D11 推定");
                DiagTrace.Mark(
                    "平台宏：图形 API '" + platformFacts.GraphicsApi + "'（构建目标 '" + platformFacts.BuildTarget
                    + "'）无可信映射，按 Windows/D3D11 推定值注入");
            }
            else if (!string.Equals(apiMacro, D3D11ShaderApiMacro, StringComparison.Ordinal))
            {
                var apiSource = "版本×平台表 (图形 API=" + platformFacts.GraphicsApi
                    + "，桥接上报的构建目标 "
                    + (string.IsNullOrEmpty(platformFacts.BuildTarget) ? "<未知>" : platformFacts.BuildTarget) + ")";
                set.Add(apiMacro, "1", MacroProvenance.Postulated, apiSource);
                // UNITY_COMPILER_DXC 表示「正在用 DXC 编译」，与图形 API 不是一回事：
                // D3D11/D3D12 走 DXC，而 Vulkan/Metal 由 Unity 自己转到各自的中间语言。
                // 本工具无从确认非 D3D 平台的编译器身份，因此不推定这条宏 —— 这是按编译器语义的取舍，
                // 不是「少定义一个宏试试看」。
                set.AddNote(
                    "图形 API '" + platformFacts.GraphicsApi + "' → " + apiMacro
                    + "；不推定 UNITY_COMPILER_DXC（该宏表示编译器身份，非 D3D 平台无从确认）");
                DiagTrace.Mark(
                    "平台宏：图形 API '" + platformFacts.GraphicsApi + "'（构建目标 '" + platformFacts.BuildTarget
                    + "'）→ " + apiMacro);
                return;
            }
            else
            {
                set.AddNote(
                    "平台事实来自桥接（构建目标 '" + platformFacts.BuildTarget + "'，图形 API '"
                    + platformFacts.GraphicsApi + "'），与此表的 D3D11 行一致");
            }
        }
        else if (!string.Equals(platform, DefaultPlatform, StringComparison.OrdinalIgnoreCase))
        {
            set.AddNote($"版本×平台表未覆盖平台 '{platform}'，不注入任何平台能力宏");
            return;
        }
        else
        {
            // 取不到构建目标/图形 API（桥接未在线、发现文件还没读到）：沿用老的推定值，但要留痕。
            DiagTrace.Mark("平台宏：未取到构建目标/图形 API，按 Windows/D3D11 推定值注入");
        }

        const string tableSource = "版本×平台表 (2022.3 / Windows / D3D11)";

        if (!string.IsNullOrEmpty(unityVersion) &&
            !unityVersion.StartsWith("2022.3", StringComparison.OrdinalIgnoreCase))
        {
            set.AddNote(
                $"版本×平台表未覆盖 Unity {unityVersion}，按 2022.3 兜底（推定项需重新验证）");
        }

        set.Add(D3D11ShaderApiMacro, "1", MacroProvenance.Postulated, tableSource);
        set.Add("UNITY_COMPILER_DXC", "1", MacroProvenance.Postulated, tableSource);
    }

    /// <summary>D3D11 的图形 API 宏名 —— 旧表的唯一一行，也是「行为不变」的基准。</summary>
    private const string D3D11ShaderApiMacro = "SHADER_API_D3D11";

    /// <summary>
    /// 图形 API 名 → Unity 的 SHADER_API_* 宏；认不出来返回 null。
    /// </summary>
    /// <remarks>
    /// 依据来源是仓库里自己那份桥接脚本：client/unity/MicroShaderBridge/BridgeEnvironment.cs 的
    /// MapShaderApiMacro，它拿 PlayerSettings.GetGraphicsAPIs(activeBuildTarget)[0] 得到
    /// GraphicsDeviceType，再把枚举名写进发现文件的 graphicsApi 字段。所以这里的键必须与
    /// Unity 的 GraphicsDeviceType 枚举名逐字相同，值与那份映射逐项一致。
    /// 只做一对一、有确证的关系：认不出来就返回 null，宁可不注入也不猜 ——
    /// 猜错的宏会静默改变代码分支，比缺一个宏危险得多。
    /// </remarks>
    private static string? MapShaderApiMacro(string graphicsApi) => graphicsApi switch
    {
        "Direct3D11" => D3D11ShaderApiMacro,
        "Direct3D12" => "SHADER_API_D3D12",
        "Vulkan" => "SHADER_API_VULKAN",
        "Metal" => "SHADER_API_METAL",
        "OpenGLCore" => "SHADER_API_GLCORE",
        "OpenGLES2" => "SHADER_API_GLES",
        "OpenGLES3" => "SHADER_API_GLES3",
        _ => null,
    };
}
