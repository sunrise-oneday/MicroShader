namespace MicroShader.DiagnosticEngine;

/// <summary>
/// 一条被重写的渲染行（v2.0 清单第 5 条：「同一行内重写会破坏列映射 → 重写行打 Rewritten 标记并记录偏移」）。
/// </summary>
/// <remarks>
/// 目前唯一的行内重写是 "#include" 系列：
/// <list type="bullet">
/// <item>"#include "Packages/…/A.hlsl"" → "#include "D:/…/PackageCache/…/A.hlsl""（虚拟前缀 → 物理路径）</item>
/// <item>"#include_with_pragmas "…"" → "#include "…""（指令名降级，指令前缀长度从 23 变 10）</item>
/// </list>
/// 两段前缀长度不同 ⇒ 列映射不是简单平移，必须按「前缀区 / 路径区 / 尾区」三段分别映射。
/// 路径区内部的映射是"最好努力"：重写后的路径文字与原文不可能逐字符对应，
/// 落在路径区内的列一律投影到源路径的起点（并按长度夹紧），不假装能给出精确列。
/// </remarks>
public readonly struct LineRewrite
{
    /// <summary>渲染行号（1-based）。</summary>
    public int RenderedLine { get; init; }

    /// <summary>源行号（1-based）。</summary>
    public int SourceLine { get; init; }

    /// <summary>重写类型（面向排障/黄金测试）。</summary>
    public LineRewriteKind Kind { get; init; }

    /// <summary>源文本中「路径文字之前的字符数」（含开头引号）："#include "…" 为 10，"#include_with_pragmas "…" 为 23。</summary>
    public int SourcePrefixLength { get; init; }

    /// <summary>渲染文本中「路径文字之前的字符数」：恒为 10（"#include "…"）。</summary>
    public int RenderedPrefixLength { get; init; }

    /// <summary>
    /// 源前缀与渲染前缀的"最长公共前缀长度"（缩进 + "#include" 这 8 个字符）。
    /// </summary>
    /// <remarks>
    /// "#include "…"" 与 "#include_with_pragmas "…"" 共享前 8 个字符，缩进也算公共部分。
    /// 只有这一段以内的列可以做恒等映射，之后的列必须夹紧到源里的开头引号 —— 否则在
    /// "_with_pragmas" 被削掉的那 13 个字符宽度里会映射出根本不存在的列。
    /// </remarks>
    public int CommonPrefixLength { get; init; }

    /// <summary>源路径文字长度（不含引号）。</summary>
    public int SourcePathLength { get; init; }

    /// <summary>渲染路径文字长度（不含引号）。</summary>
    public int RenderedPathLength { get; init; }

    /// <summary>源路径文字（原样书写）。</summary>
    public string SourcePath { get; init; }

    /// <summary>渲染路径文字（物理路径，正斜杠）。</summary>
    public string RenderedPath { get; init; }

    /// <summary>源路径文字中的第一个字符所在列（1-based）。</summary>
    public int SourcePathStartColumn => SourcePrefixLength + 1;

    /// <summary>渲染路径文字中的第一个字符所在列（1-based）。</summary>
    public int RenderedPathStartColumn => RenderedPrefixLength + 1;

    /// <summary>把渲染列（1-based UTF-16）映射回源列（1-based UTF-16）。</summary>
    public int MapRenderedColumnToSource(int renderedColumn)
    {
        if (renderedColumn <= 0)
        {
            return 0;
        }

        // ① 公共前缀区（缩进 + "#include"）：逐字符相同，恒等映射。
        if (renderedColumn <= CommonPrefixLength)
        {
            return renderedColumn;
        }

        // ② 指令名差异区（"include_with_pragmas" 被削成 "include"）：夹紧到源里的开头引号。
        if (renderedColumn < RenderedPathStartColumn)
        {
            return SourcePrefixLength;
        }

        // ③ 路径区。
        var pathOffset = renderedColumn - RenderedPathStartColumn;
        if (pathOffset < RenderedPathLength)
        {
            if (pathOffset >= SourcePathLength)
            {
                // 重写后的路径更长（虚拟前缀 → 物理绝对路径几乎总是更长）：夹紧到源路径末字符。
                return SourcePrefixLength + Math.Max(1, SourcePathLength);
            }

            return SourcePathStartColumn + pathOffset;
        }

        // ④ 尾区（闭合引号及其后的注释等）：按两段长度差平移。
        var renderedTail = RenderedPathStartColumn + RenderedPathLength;
        var sourceTail = SourcePathStartColumn + SourcePathLength;
        return sourceTail + (renderedColumn - renderedTail);
    }

    public override string ToString() =>
        $"行 {RenderedLine}(源 {SourceLine}) {Kind}: '{SourcePath}' → '{RenderedPath}'";
}

/// <summary>行内重写类型。</summary>
public enum LineRewriteKind : byte
{
    /// <summary>无重写（占位，不会出现在重写表里）。</summary>
    None = 0,

    /// <summary>"#include_with_pragmas" → "#include"（仅指令名降级）。</summary>
    IncludeWithPragmasDowngrade = 1,

    /// <summary>虚拟 include 路径 → 物理路径。</summary>
    IncludePathToPhysical = 2,

    /// <summary>两者同时发生。</summary>
    IncludeWithPragmasAndPath = 3,
}
