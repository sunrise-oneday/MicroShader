namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 日志分类（v2.0 清单第 11 条：category 用静态常量，避免每次调用查表或算字符串哈希）。
/// </summary>
/// <remarks>
/// 分类只影响 dump 的可读性与 LSP 输出前缀，不参与任何逻辑判定，
/// 因此新增分类只需在这里加一个常量（上限 <see cref="Max"/>）。
/// </remarks>
public static class LogCategories
{
    public const int None = 0;
    public const int Runtime = 1;
    public const int Slice = 2;
    public const int Vfs = 3;
    public const int Assembly = 4;
    public const int Compiler = 5;
    public const int Transport = 6;
    public const int Registry = 7;
    public const int Dump = 8;
    public const int Test = 9;
    public const int IntelliSense = 10;
    public const int Navigation = 11;

    /// <summary>合法分类 id 的上界（dump 侧用它做边界检查，避免越界读名字表）。</summary>
    public const int Max = 16;

    /// <summary>分类名（静态常量字符串，dump 时才取，非热路径）。</summary>
    public static string Name(int category) => category switch
    {
        None => "None",
        Runtime => "Runtime",
        Slice => "Slice",
        Vfs => "Vfs",
        Assembly => "Assembly",
        Compiler => "Compiler",
        Transport => "Transport",
        Registry => "Registry",
        Dump => "Dump",
        Test => "Test",
        IntelliSense => "IntelliSense",
        Navigation => "Navigation",
        _ => "Unknown",
    };

    /// <summary>按名字反查分类 id；"零分配"（逐字符比较，不算哈希、不建字符串）。</summary>
    public static int FromName(ReadOnlySpan<char> name) => name switch
    {
        _ when name.Equals("Runtime", StringComparison.Ordinal) => Runtime,
        _ when name.Equals("Slice", StringComparison.Ordinal) => Slice,
        _ when name.Equals("Vfs", StringComparison.Ordinal) => Vfs,
        _ when name.Equals("Assembly", StringComparison.Ordinal) => Assembly,
        _ when name.Equals("Compiler", StringComparison.Ordinal) => Compiler,
        _ when name.Equals("Transport", StringComparison.Ordinal) => Transport,
        _ when name.Equals("Registry", StringComparison.Ordinal) => Registry,
        _ when name.Equals("Dump", StringComparison.Ordinal) => Dump,
        _ when name.Equals("Test", StringComparison.Ordinal) => Test,
        _ when name.Equals("IntelliSense", StringComparison.Ordinal) => IntelliSense,
        _ when name.Equals("Navigation", StringComparison.Ordinal) => Navigation,
        _ => None,
    };
}
