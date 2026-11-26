using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Corinth;

/// <summary>Writes <see cref="ColumnData"/> as column blocks (SPEC §4–§8).</summary>
internal sealed class CcfColumnWriter
{
    private static readonly UTF8Encoding Utf8Strict = new(false, true);

    private readonly CcfByteWriter _w;
    private readonly CcfFeatures _features;
    private readonly CcfLimits _limits;
    private readonly double _dictionaryThreshold;

    public CcfColumnWriter(CcfByteWriter writer, CcfFeatures features, CcfLimits limits, double dictionaryThreshold)
    {
        _w = writer;
        _features = features;
        _limits = limits;
        _dictionaryThreshold = dictionaryThreshold;
    }

    /// <summary>True once any column at any depth was written in dictionary mode.</summary>
    public bool UsedDictionary { get; private set; }

    public void WriteTable(ColumnData[] columns)
    {
        foreach (var c in columns)
        {
            WriteColumn(c);
        }
    }

    private void WriteColumn(ColumnData col)
    {
        var field = col.Field;
        var start = _w.BeginBlock();
        byte mode = 0;

        if (field.Nullable)
        {
            WritePresence(col);
        }

        switch (field.Type)
        {
            case CcfType.Bool:
                WriteBools(col);
                break;
            case CcfType.Int32:
            case CcfType.Int64:
            case CcfType.UInt32:
            case CcfType.Decimal:
            case CcfType.DateTime:
                WriteSigned(col);
                break;
            case CcfType.UInt64:
                WriteUInt64(col);
                break;
            case CcfType.Float64:
                for (var i = 0; i < col.Rows; i++)
                {
                    if (col.IsPresent(i))
                    {
                        BinaryPrimitives.WriteInt64LittleEndian(_w.GetSpan(8), BitConverter.DoubleToInt64Bits(col.F64![i]));
                        _w.Advance(8);
                    }
                }

                break;
            case CcfType.Guid:
                for (var i = 0; i < col.Rows; i++)
                {
                    if (col.IsPresent(i))
                    {
                        col.Guids![i].TryWriteBytes(_w.GetSpan(16), bigEndian: true, out _);
                        _w.Advance(16);
                    }
                }

                break;
            case CcfType.Enum:
                WriteEnum(col);
                break;
            case CcfType.String:
                mode = WriteStrings(col);
                break;
            case CcfType.Object:
                WriteTable(col.Children!);
                break;
            case CcfType.List:
                WriteList(col);
                break;
        }

        _w.EndBlock(start, mode);
    }

    private void WritePresence(ColumnData col)
    {
        var bytes = (int)(((long)col.Rows + 7) / 8);
        var span = _w.GetSpan(bytes);
        span.Clear();
        for (var i = 0; i < col.Rows; i++)
        {
            if (col.Present![i])
            {
                span[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        _w.Advance(bytes);
    }

    private void WriteBools(ColumnData col)
    {
        var bytes = CcfPrimitives.PackedByteCount(col.PresentCount, 1);
        var span = _w.GetSpan(bytes);
        span.Clear();
        var k = 0;
        for (var i = 0; i < col.Rows; i++)
        {
            if (col.IsPresent(i))
            {
                if (col.Bools![i])
                {
                    span[k >> 3] |= (byte)(1 << (k & 7));
                }

                k++;
            }
        }

        _w.Advance(bytes);
    }

    private void WriteEnum(ColumnData col)
    {
        var width = CcfPrimitives.EnumBitWidth(col.Field.Values!.Count);
        var bytes = CcfPrimitives.PackedByteCount(col.PresentCount, width);
        var span = _w.GetSpan(bytes);
        span.Clear();
        var k = 0;
        for (var i = 0; i < col.Rows; i++)
        {
            if (col.IsPresent(i))
            {
                CcfPrimitives.WritePacked(span, k++, width, (int)col.I64![i]);
            }
        }

        _w.Advance(bytes);
    }

    private void WriteSigned(ColumnData col)
    {
        var field = col.Field;
        var plainUnsigned = field.Type == CcfType.UInt32 || (field.Type == CcfType.Decimal && field.Unsigned);
        long previous = 0;
        for (var i = 0; i < col.Rows; i++)
        {
            if (!col.IsPresent(i))
            {
                continue;
            }

            var v = col.I64![i];
            if (field.Delta)
            {
                var diff = (Int128)v - previous;
                if (diff < long.MinValue || diff > long.MaxValue)
                {
                    throw new CcfEncodeException($"Delta difference of field '{field.Name}' overflows int64.");
                }

                _w.WriteUVarint(CcfPrimitives.ZigZag((long)diff));
                previous = v;
            }
            else
            {
                _w.WriteUVarint(plainUnsigned ? (ulong)v : CcfPrimitives.ZigZag(v));
            }
        }
    }

    private void WriteUInt64(ColumnData col)
    {
        ulong previous = 0;
        for (var i = 0; i < col.Rows; i++)
        {
            if (!col.IsPresent(i))
            {
                continue;
            }

            var v = col.U64![i];
            if (col.Field.Delta)
            {
                var diff = (Int128)v - previous;
                if (diff < long.MinValue || diff > long.MaxValue)
                {
                    throw new CcfEncodeException($"Delta difference of field '{col.Field.Name}' overflows int64.");
                }

                _w.WriteUVarint(CcfPrimitives.ZigZag((long)diff));
                previous = v;
            }
            else
            {
                _w.WriteUVarint(v);
            }
        }
    }

    private void WriteList(ColumnData col)
    {
        for (var i = 0; i < col.Rows; i++)
        {
            if (col.IsPresent(i))
            {
                _w.WriteUVarint((ulong)col.ChildCount![i]);
            }
        }

        _w.WriteUVarint((ulong)col.ChildRows);
        WriteTable(col.Children!);
    }

    /// <summary>Writes a string column; returns the mode byte (0 plain, 1 dictionary).</summary>
    private byte WriteStrings(ColumnData col)
    {
        var strings = col.Strings!;
        var p = col.PresentCount;

        if ((_features & CcfFeatures.Dictionary) != 0 && p > 0 && TryBuildDictionary(col, out var entries, out var indexes))
        {
            _w.WriteUVarint((ulong)entries.Count);
            foreach (var e in entries)
            {
                WriteString(e);
            }

            var wide = entries.Count > 256;
            var span = _w.GetSpan(p * (wide ? 2 : 1));
            var pos = 0;
            for (var k = 0; k < p; k++)
            {
                if (wide)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(pos, 2), (ushort)indexes[k]);
                    pos += 2;
                }
                else
                {
                    span[pos++] = (byte)indexes[k];
                }
            }

            _w.Advance(pos);
            ArrayPool<int>.Shared.Return(indexes);
            UsedDictionary = true;
            return 1;
        }

        // Plain: all lengths first, then all bytes.
        for (var i = 0; i < col.Rows; i++)
        {
            if (col.IsPresent(i))
            {
                _w.WriteUVarint((ulong)ByteCount(strings[i]!));
            }
        }

        for (var i = 0; i < col.Rows; i++)
        {
            if (col.IsPresent(i))
            {
                WriteStringBytes(strings[i]!);
            }
        }

        return 0;
    }

    private int ByteCount(string s)
    {
        int n;
        try
        {
            n = Utf8Strict.GetByteCount(s);
        }
        catch (EncoderFallbackException e)
        {
            throw new CcfEncodeException("String is not valid Unicode (unpaired surrogate).", e);
        }

        if (n > _limits.MaxStringBytes)
        {
            throw new CcfEncodeException($"String of {n} bytes exceeds the string limit.");
        }

        return n;
    }

    private void WriteString(string s)
    {
        _w.WriteUVarint((ulong)ByteCount(s));
        WriteStringBytes(s);
    }

    private void WriteStringBytes(string s)
    {
        if (s.Length == 0)
        {
            return;
        }

        var n = Utf8Strict.GetByteCount(s);
        Utf8Strict.GetBytes(s, _w.GetSpan(n));
        _w.Advance(n);
    }

    /// <summary>The dictionary rule of SPEC §8.1; entries in order of first appearance.</summary>
    private bool TryBuildDictionary(ColumnData col, out List<string> entries, out int[] indexes)
    {
        var p = col.PresentCount;
        var strings = col.Strings!;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        entries = new List<string>();
        indexes = Array.Empty<int>();
        var rented = ArrayPool<int>.Shared.Rent(p);
        var k = 0;
        for (var i = 0; i < col.Rows; i++)
        {
            if (!col.IsPresent(i))
            {
                continue;
            }

            var s = strings[i]!;
            if (!map.TryGetValue(s, out var index))
            {
                index = entries.Count;
                map.Add(s, index);
                entries.Add(s);
                // Distinct count only grows, so stop as soon as the rule can no longer hold.
                if (!(entries.Count <= CcfLimits.MaxDictionaryEntries && entries.Count < _dictionaryThreshold * p))
                {
                    ArrayPool<int>.Shared.Return(rented);
                    return false;
                }
            }

            rented[k++] = index;
        }

        indexes = rented;
        return true;
    }
}
