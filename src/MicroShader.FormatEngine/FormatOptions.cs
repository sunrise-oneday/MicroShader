namespace MicroShader.FormatEngine;

/// <summary>
/// 块开括号的摆放风格。
/// </summary>
/// <remarks>
/// 只区分块开括号。void f() { } 里的左花括号是块括号，
/// float a[] = { 1, 2 }; 里的左花括号是初始化器，不参与风格转换。
/// 判据在 FormatScanner：前一个代码字符不是等号、逗号、左圆括号、
/// 左方括号、左花括号的，才算块括号。
/// </remarks>
public enum BraceStyle
{
    /// <summary>
    /// 保留文件自身的风格：只重排缩进，一个括号都不动。
    /// </summary>
    /// <remarks>
    /// 这是默认值，也是对用户那句「我原本的格式也是对的」的直接回应：
    /// 没有明确要求换风格时，格式化器不改写作者的括号位置。
    /// </remarks>
    Auto = 0,

    /// <summary>Allman：块开括号独占一行。</summary>
    Allman = 1,

    /// <summary>K&amp;R：块开括号跟在头部行末。</summary>
    Kr = 2,
}

/// <summary>
/// 一次格式化的参数。
/// </summary>
/// <remarks>
/// TabSize、InsertSpaces、TrimTrailingWhitespace 对应 LSP FormattingOptions 里
/// 本服务真正用到的三项，由客户端按编辑器设置逐请求下发；
/// BraceStyle 是本服务自己的旋钮，来自启动参数 --brace-style，进程启动时固定。
/// </remarks>
public sealed class FormatOptions
{
    /// <summary>括号风格。</summary>
    public BraceStyle BraceStyle { get; init; } = BraceStyle.Auto;

    /// <summary>一个缩进层级的宽度（LSP 的 options.tabSize）。</summary>
    public int TabSize { get; init; } = 4;

    /// <summary>用空格还是制表符（LSP 的 options.insertSpaces）。</summary>
    public bool InsertSpaces { get; init; } = true;

    /// <summary>是否去掉行尾空白（LSP 的 options.trimTrailingWhitespace）。</summary>
    public bool TrimTrailingWhitespace { get; init; } = true;

    /// <summary>默认参数。</summary>
    public static FormatOptions Default { get; } = new();
}

/// <summary>
/// 一条最小化文本替换（LSP TextEdit 的值对象形态）。
/// </summary>
/// <param name="StartLine">起始行，0 起算。</param>
/// <param name="StartCharacter">起始列，0 起算，单位是 UTF-16 码元。</param>
/// <param name="EndLine">结束行，0 起算。</param>
/// <param name="EndCharacter">结束列，0 起算，单位是 UTF-16 码元。</param>
/// <param name="NewText">替换文本；空串表示删除。</param>
public readonly record struct TextEdit(
    int StartLine,
    int StartCharacter,
    int EndLine,
    int EndCharacter,
    string NewText);
