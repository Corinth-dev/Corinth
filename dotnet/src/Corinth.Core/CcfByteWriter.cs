using System.Buffers;

namespace Corinth;

/// <summary>Growable pooled byte buffer used to build a whole payload before anything is sent (SPEC §10.2).</summary>
internal sealed class CcfByteWriter : IDisposable
{
    /// <summary>Space reserved for a block header: mode byte plus a maximal uvarint.</summary>
    private const int BlockHeaderReserve = 1 + CcfPrimitives.MaxUVarintBytes;

    private readonly long _maxBytes;
    private byte[] _buffer;
    private int _length;

    public CcfByteWriter(long maxBytes, int initialCapacity = 4096)
    {
        _maxBytes = maxBytes;
        _buffer = ArrayPool<byte>.Shared.Rent(initialCapacity);
    }

    public int Length => _length;

    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _length);

    public void Dispose()
    {
        var b = _buffer;
        _buffer = Array.Empty<byte>();
        if (b.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(b);
        }
    }

    private void Ensure(int extra)
    {
        var needed = (long)_length + extra;
        if (needed <= _buffer.Length)
        {
            return;
        }

        // Nested blocks reserve a few bytes each, so allow a small slack over the limit; the exact limit is
        // checked on the finished payload.
        if (needed > _maxBytes + 4096)
        {
            throw new CcfEncodeException("Payload exceeds the payload size limit.");
        }

        var bigger = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max(needed, (long)_buffer.Length * 2), int.MaxValue - 64));
        _buffer.AsSpan(0, _length).CopyTo(bigger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = bigger;
    }

    public Span<byte> GetSpan(int size)
    {
        Ensure(size);
        return _buffer.AsSpan(_length, size);
    }

    public void Advance(int count) => _length += count;

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        Ensure(bytes.Length);
        bytes.CopyTo(_buffer.AsSpan(_length));
        _length += bytes.Length;
    }

    public void WriteUVarint(ulong value)
    {
        Ensure(CcfPrimitives.MaxUVarintBytes);
        _length += CcfPrimitives.WriteUVarint(_buffer.AsSpan(_length), value);
    }

    /// <summary>Patches an already written byte (used for the header flags).</summary>
    public void SetByte(int position, byte value) => _buffer[position] = value;

    /// <summary>Starts a column block; its body is written after this call and closed with <see cref="EndBlock"/>.</summary>
    public int BeginBlock()
    {
        Ensure(BlockHeaderReserve);
        var start = _length;
        _length += BlockHeaderReserve;
        return start;
    }

    /// <summary>
    /// Writes the mode byte and the (shortest) length in front of the body and closes the gap left by the
    /// reserved header space.
    /// </summary>
    public void EndBlock(int start, byte mode)
    {
        var bodyStart = start + BlockHeaderReserve;
        var bodyLength = _length - bodyStart;
        _buffer[start] = mode;
        var headerLength = 1 + CcfPrimitives.WriteUVarint(_buffer.AsSpan(start + 1), (ulong)bodyLength);
        var shift = BlockHeaderReserve - headerLength;
        if (shift > 0)
        {
            _buffer.AsSpan(bodyStart, bodyLength).CopyTo(_buffer.AsSpan(bodyStart - shift));
            _length -= shift;
        }
    }
}
