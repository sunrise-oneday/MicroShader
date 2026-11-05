using System.Buffers;

namespace MicroShader.CoordinationEngine.Transport;

/// <summary>
/// 基于 <see cref="ArrayPool{T}"/> 的 <see cref="IBufferWriter{T}"/>：给
/// <see cref="System.Text.Json.Utf8JsonWriter"/> 提供一块可增长、可归还的连续输出区。
/// </summary>
public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _written;
    private readonly int _initialCapacity;

    public PooledBufferWriter(int initialCapacity = 4096)
    {
        _initialCapacity = Math.Max(64, initialCapacity);
    }

    /// <summary>已写入的字节数。</summary>
    public int WrittenCount => _written;

    /// <summary>已写入内容的只读视图。</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer is null ? default : _buffer.AsSpan(0, _written);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_buffer is null || _written + count > _buffer.Length)
        {
            throw new InvalidOperationException("Advance 超出已确保的缓冲容量。");
        }
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer!.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer!.AsSpan(_written);
    }

    /// <summary>把写入位置归零（缓冲保留，供下一帧复用）。</summary>
    public void Reset() => _written = 0;

    public void Dispose()
    {
        if (_buffer is null) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = null;
        _written = 0;
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;

        if (_buffer is null)
        {
            _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(_initialCapacity, sizeHint));
            return;
        }

        if (_written + sizeHint <= _buffer.Length) return;

        var next = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _written + sizeHint));
        _buffer.AsSpan(0, _written).CopyTo(next);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = next;
    }
}
