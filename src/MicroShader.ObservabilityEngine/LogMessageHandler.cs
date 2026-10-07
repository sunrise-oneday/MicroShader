using System.Runtime.CompilerServices;

namespace MicroShader.ObservabilityEngine;

/// <summary>
/// 零分配插值字符串处理器：把 "$"..."" 直接格式化进一块"线程级复用"的字符缓冲，
/// 不经过 "string.Format"、不产生中间字符串、不装箱。
/// </summary>
/// <remarks>
/// 
/// v2.0 清单第 9 条明确「该模块不要引入 ZString」（第三方格式化库在 NativeAOT 下的兼容性
/// 在本环境无法实测），因此这里直接用 BCL 的 <see cref="ISpanFormattable"/> + 线程静态缓冲。
/// 
/// "为什么泛型重载必须带 "where T : ISpanFormattable" 约束"：
/// 无约束的 "AppendFormatted&lt;T&gt;(T)" 里写 "value is ISpanFormattable" 会要求把
/// 值类型装箱（实测 20000 次插值写入分配 960,000 字节 = 每次 48B，正好是 "int" 与
/// "double" 各一次装箱）。带上接口约束后，"value.TryFormat(...)" 会被编译成
/// 约束调用（constrained callvirt），完全不装箱。代价是：插值非 <see cref="ISpanFormattable"/>
/// 的类型时会落到 <see cref="AppendFormatted(object?, int)"/> 兜底重载（允许装箱），
/// 且带格式说明符的非 ISpanFormattable 类型无法编译 —— 这是刻意的，它把「意外分配」
/// 从运行时问题降级成编译期问题。
/// 
/// 
/// 为什么用 "[ThreadStatic]" 数组而不是 "stackalloc"：处理器结构体是在"构造函数"里
/// 取得缓冲的，若在构造函数中 "stackalloc" 再存进字段，栈内存会随构造函数帧一起失效
/// （Roslyn 的 ref-safety 规则也会拒绝这种逃逸）。线程级复用的数组既满足零分配，又天然安全。
/// 代价是"不可重入"（格式化过程中再写日志会踩同一块缓冲），因此 <see cref="Log"/> 门面
/// 在写入前就完成全部格式化，不存在嵌套机会。
/// 
/// </remarks>
[InterpolatedStringHandler]
public ref struct LogMessageHandler
{
    [ThreadStatic]
    private static char[]? t_scratch;

    private Span<char> _buffer;
    private int _written;

    public LogMessageHandler(int literalLength, int formattedCount)
    {
        _ = literalLength;
        _ = formattedCount;
        _buffer = t_scratch ??= new char[LogRingBuffer.MaxMessageChars];
        _written = 0;
    }

    /// <summary>已格式化的文本（截断到缓冲容量）。</summary>
    public readonly ReadOnlySpan<char> Text => _buffer[.._written];

    public readonly int Length => _written;

    public void AppendLiteral(string value) => Append(value.AsSpan());

    public void AppendFormatted(ReadOnlySpan<char> value) => Append(value);

    public void AppendFormatted(ReadOnlySpan<char> value, int alignment) => AppendAligned(value, alignment);

    public void AppendFormatted(string? value) => Append(value.AsSpan());

    public void AppendFormatted(string? value, int alignment) => AppendAligned(value.AsSpan(), alignment);

    /// <summary>
    /// 兜底重载：仅在泛型重载因接口约束不适用时才被选中（例如枚举）。装箱是这个分支的已知代价。
    /// </summary>
    public void AppendFormatted(object? value, int alignment = 0)
    {
        if (value is null) return;
        AppendAligned(value.ToString().AsSpan(), alignment);
    }

    public void AppendFormatted<T>(T value) where T : ISpanFormattable
        => AppendFormattable(value, default, alignment: 0);

    public void AppendFormatted<T>(T value, string? format) where T : ISpanFormattable
        => AppendFormattable(value, format, alignment: 0);

    public void AppendFormatted<T>(T value, int alignment) where T : ISpanFormattable
        => AppendFormattable(value, default, alignment);

    public void AppendFormatted<T>(T value, int alignment, string? format) where T : ISpanFormattable
        => AppendFormattable(value, format, alignment);

    /// <summary>约束调用："value.TryFormat" 不会装箱。</summary>
    private void AppendFormattable<T>(T value, string? format, int alignment) where T : ISpanFormattable
    {
        if (value is null) return;

        int start = _written;
        if (!value.TryFormat(_buffer[start..], out int written, format, provider: null))
        {
            // 缓冲不够或格式串不被支持：退回 ToString（此处允许一次分配）。
            Append(value.ToString().AsSpan());
            return;
        }

        _written += written;
        ApplyAlignment(start, written, alignment);
    }

    private void Append(ReadOnlySpan<char> value)
    {
        int room = _buffer.Length - _written;
        if (room <= 0 || value.Length == 0) return;
        int n = Math.Min(value.Length, room);
        value[..n].CopyTo(_buffer[_written..]);
        _written += n;
    }

    /// <summary>
    /// 先追加正文、再按对齐要求搬运。
    /// </summary>
    /// <remarks>
    /// 顺序不能反：早期实现在右对齐时「先补空格再拷正文」，而正文当时就落在同一个缓冲位置上，
    /// 结果补的空格把刚格式化好的数字整段覆盖掉（实测 "$"[{42,6}]"" 输出 "[      ]"）。
    /// </remarks>
    private void AppendAligned(ReadOnlySpan<char> value, int alignment)
    {
        int start = _written;
        Append(value);
        ApplyAlignment(start, _written - start, alignment);
    }

    private void ApplyAlignment(int start, int length, int alignment)
    {
        if (alignment == 0 || length == 0) return;

        int width = Math.Abs(alignment);
        if (width <= length) return;

        int padding = width - length;

        // 缓冲余量不够时少补几个空格，绝不越界、也绝不抛异常。
        int room = _buffer.Length - _written;
        if (padding > room) padding = room;
        if (padding <= 0) return;

        if (alignment > 0)
        {
            // 右对齐：把已写好的 length 个字符整体右移 padding。
            // Span.CopyTo 具备 memmove 语义，源与目标重叠也是安全的。
            _buffer.Slice(start, length).CopyTo(_buffer[(start + padding)..]);
            _buffer.Slice(start, padding).Fill(' ');
        }
        else
        {
            // 左对齐：直接在尾部补空格。
            _buffer.Slice(_written, padding).Fill(' ');
        }

        _written += padding;
    }
}
