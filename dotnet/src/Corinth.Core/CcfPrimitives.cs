using System.Buffers.Binary;

namespace Corinth;

/// <summary>uvarint, zigzag and bit-packing helpers (SPEC §5).</summary>
internal static class CcfPrimitives
{
    public const int MaxUVarintBytes = 10;

    public static int UVarintSize(ulong value)
    {
        var n = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            n++;
        }

        return n;
    }

    /// <summary>Writes the shortest uvarint; returns the byte count.</summary>
    public static int WriteUVarint(Span<byte> destination, ulong value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            destination[i++] = (byte)(value | 0x80);
            value >>= 7;
        }

        destination[i++] = (byte)value;
        return i;
    }

    public static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    public static long UnZigZag(ulong value) => (long)(value >> 1) ^ -(long)(value & 1);

    /// <summary>Smallest width <c>w ≥ 1</c> with 2^w ≥ <paramref name="count"/> (SPEC §6.1, enum).</summary>
    public static int EnumBitWidth(int count)
    {
        var w = 1;
        while ((1L << w) < count)
        {
            w++;
        }

        return w;
    }

    public static int PackedByteCount(long count, int bitsPerValue) => (int)((count * bitsPerValue + 7) / 8);

    /// <summary>Reads value <paramref name="index"/> of a <paramref name="width"/>-bit LSB-first packed run.</summary>
    public static int ReadPacked(ReadOnlySpan<byte> packed, long index, int width)
    {
        var bit = index * width;
        var byteIndex = (int)(bit >> 3);
        var shift = (int)(bit & 7);
        // width ≤ 16 and shift ≤ 7 → at most 23 bits → 3 bytes.
        uint acc = packed[byteIndex];
        if (shift + width > 8)
        {
            acc |= (uint)packed[byteIndex + 1] << 8;
            if (shift + width > 16)
            {
                acc |= (uint)packed[byteIndex + 2] << 16;
            }
        }

        return (int)((acc >> shift) & ((1u << width) - 1));
    }

    /// <summary>Ors value <paramref name="index"/> into a zero-initialised packed run.</summary>
    public static void WritePacked(Span<byte> packed, long index, int width, int value)
    {
        var bit = index * width;
        var byteIndex = (int)(bit >> 3);
        var shift = (int)(bit & 7);
        var v = (uint)value << shift;
        packed[byteIndex] |= (byte)v;
        if (shift + width > 8)
        {
            packed[byteIndex + 1] |= (byte)(v >> 8);
            if (shift + width > 16)
            {
                packed[byteIndex + 2] |= (byte)(v >> 16);
            }
        }
    }

    public static double ReadDouble(ReadOnlySpan<byte> source) =>
        BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(source));
}
