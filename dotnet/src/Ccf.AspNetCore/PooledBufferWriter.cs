using System.Buffers;

namespace Ccf.AspNetCore;

/// <summary>An <see cref="IBufferWriter{T}"/> over a pooled array, so a response is encoded before anything is sent.</summary>
internal sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[] _buffer;
    private int _count;

    public PooledBufferWriter(int initialCapacity = 16 * 1024)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public int WrittenCount => _count;

    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _count);

    public void Advance(int count) => _count += count;

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_count);
    }

    private void Ensure(int sizeHint)
    {
        var needed = _count + Math.Max(sizeHint, 1);
        if (needed <= _buffer.Length)
        {
            return;
        }

        var bigger = ArrayPool<byte>.Shared.Rent(Math.Max(needed, _buffer.Length * 2));
        _buffer.AsSpan(0, _count).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }

    public void Dispose()
    {
        var b = _buffer;
        _buffer = Array.Empty<byte>();
        if (b.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }
}
