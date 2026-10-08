using MicroShader.ContextEngine;

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
    public static PlatformMacroSet Resolve(
        UnityProjectLayout layout,
        UnityInstallation? installation,
        string? unityVersion = null,
        string platform = DefaultPlatform)
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
        AddVersionPlatformTable(set, platform, unityVersion);

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
    private static void AddVersionPlatformTable(PlatformMacroSet set, string platform, string? unityVersion)
    {
        if (!string.Equals(platform, DefaultPlatform, StringComparison.OrdinalIgnoreCase))
        {
            set.AddNote($"版本×平台表未覆盖平台 '{platform}'，不注入任何平台能力宏");
            return;
        }

        const string tableSource = "版本×平台表 (2022.3 / Windows / D3D11)";

        if (!string.IsNullOrEmpty(unityVersion) &&
            !unityVersion.StartsWith("2022.3", StringComparison.OrdinalIgnoreCase))
        {
            set.AddNote(
                $"版本×平台表未覆盖 Unity {unityVersion}，按 2022.3 兜底（推定项需重新验证）");
        }

        set.Add("SHADER_API_D3D11", "1", MacroProvenance.Postulated, tableSource);
        set.Add("UNITY_COMPILER_DXC", "1", MacroProvenance.Postulated, tableSource);
    }
}
