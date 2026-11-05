using System.Buffers;

namespace MicroShader.DiagnosticEngine;

/// <summary>
/// <see cref="ArrayPool{T}"/> 租借的可增长字符缓冲。
/// </summary>
/// <remarks>
/// 规格书的内存所有权链是：文档切片"借用" "ReadOnlySpan&lt;char&gt;" → 装配器"租借"
/// "ArrayPool&lt;char&gt;" → "fixed" pin → "DxcBuffer" → 调用返回后"立即归还池"。
/// 本类型负责中间的「租借 + 增长 + 归还」这一段；上层（模块 4）只拿 <see cref="Written"/> 去 pin。
/// </remarks>
internal sealed class RentedCharBuffer : IDisposable
{
    private char[] _buffer;
    private int _length;

    public RentedCharBuffer(int initialCapacity = 8192)
    {
        _buffer = ArrayPool<char>.Shared.Rent(Math.Max(256, initialCapacity));
    }

    /// <summary>已写入的字符数。</summary>
    public int Length => _length;

    /// <summary>已写入区域（可直接 pin）。</summary>
    public ReadOnlySpan<char> Written => _buffer.AsSpan(0, _length);

    /// <summary>当前底层数组容量（排障用）。</summary>
    public int Capacity => _buffer.Length;

    public void Append(ReadOnlySpan<char> value)
    {
        if (value.Length == 0)
        {
            return;
        }

        EnsureCapacity(_length + value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    public void Append(char value)
    {
        EnsureCapacity(_length + 1);
        _buffer[_length++] = value;
    }

    public void AppendNewLine()
    {
        EnsureCapacity(_length + 1);
        _buffer[_length++] = '\n';
    }

    public void Clear() => _length = 0;

    private void EnsureCapacity(int required)
    {
        if (required <= _buffer.Length)
        {
            return;
        }

        var next = ArrayPool<char>.Shared.Rent(Math.Max(required, _buffer.Length * 2));
        _buffer.AsSpan(0, _length).CopyTo(next);
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = next;
    }

    public void Dispose()
    {
        if (_buffer.Length == 0)
        {
            return;
        }

        // 不清零：ArrayPool 契约允许携带旧数据，而清零会让热路径多做一遍全量写。
        // 本缓冲的内容全部来自用户自己的 shader 文本，不存在跨租户泄漏面。
        ArrayPool<char>.Shared.Return(_buffer);
        _buffer = Array.Empty<char>();
        _length = 0;
    }
}
