namespace Corinth;

/// <summary>
/// Bounds-checked reader over a payload. Reads are scoped to the payload or to the current column block body;
/// running past the scope raises <see cref="CcfErrorCode.Truncated"/> at payload level and
/// <see cref="CcfErrorCode.LengthOverrun"/> inside a block (SPEC §14.1).
/// </summary>
internal ref struct CcfReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;
    private int _end;
    private bool _inBlock;

    public CcfReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
        _end = data.Length;
        _inBlock = false;
    }

    public readonly int Position => _pos;

    /// <summary>Bytes left in the current scope.</summary>
    public readonly int Remaining => _end - _pos;

    /// <summary>Bytes left in the whole payload, ignoring block scopes.</summary>
    public readonly int PayloadRemaining => _data.Length - _pos;

    public readonly CcfFormatException Overrun() =>
        new(_inBlock ? CcfErrorCode.LengthOverrun : CcfErrorCode.Truncated,
            _inBlock ? "read past the end of the column block" : "read past the end of the payload");

    /// <summary>Fails with the overrun error unless <paramref name="count"/> bytes remain in the current scope.</summary>
    public readonly void Require(long count)
    {
        if (count > _end - _pos)
        {
            throw Overrun();
        }
    }

    public byte ReadByte()
    {
        if (_pos >= _end)
        {
            throw Overrun();
        }

        return _data[_pos++];
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        Require(count);
        var s = _data.Slice(_pos, count);
        _pos += count;
        return s;
    }

    public ReadOnlySpan<byte> ReadBytes(long count)
    {
        Require(count);
        return ReadBytes((int)count);
    }

    public ulong ReadUVarint()
    {
        ulong result = 0;
        for (var i = 0; i < CcfPrimitives.MaxUVarintBytes; i++)
        {
            var b = ReadByte();
            if (i == 9 && b > 1)
            {
                // 10th byte may only carry bit 63 (value 0 or 1, no continuation).
                throw new CcfFormatException(CcfErrorCode.VarintOverflow, "uvarint does not fit in 64 bits");
            }

            result |= (ulong)(b & 0x7f) << (7 * i);
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw new CcfFormatException(CcfErrorCode.VarintOverflow, "uvarint longer than 10 bytes");
    }

    public readonly struct Block
    {
        internal Block(int savedEnd, bool savedInBlock, int end)
        {
            SavedEnd = savedEnd;
            SavedInBlock = savedInBlock;
            End = end;
        }

        internal int SavedEnd { get; }

        internal bool SavedInBlock { get; }

        internal int End { get; }
    }

    /// <summary>
    /// Reads a column block's mode byte and length and narrows the scope to the body (SPEC §4.3).
    /// </summary>
    public Block EnterBlock(bool dictionaryAllowed, out byte mode)
    {
        mode = ReadByte();
        if (mode > 1 || (mode == 1 && !dictionaryAllowed))
        {
            throw new CcfFormatException(CcfErrorCode.InvalidMode, $"mode byte {mode:x2}");
        }

        var length = ReadUVarint();
        if (length > (ulong)(_end - _pos))
        {
            throw Overrun();
        }

        var block = new Block(_end, _inBlock, _pos + (int)length);
        _end = block.End;
        _inBlock = true;
        return block;
    }

    /// <summary>Verifies the body was consumed exactly and restores the enclosing scope.</summary>
    public void ExitBlock(in Block block)
    {
        if (_pos != block.End)
        {
            throw new CcfFormatException(CcfErrorCode.BlockLengthMismatch,
                $"{block.End - _pos} bytes left unconsumed in the block body");
        }

        _end = block.SavedEnd;
        _inBlock = block.SavedInBlock;
    }
}
