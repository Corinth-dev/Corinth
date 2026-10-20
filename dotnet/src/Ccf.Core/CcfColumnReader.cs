using System.Text;
using System.Text.Unicode;

namespace Ccf;

/// <summary>Reads column blocks into <see cref="ColumnData"/> (SPEC §4–§8, §14).</summary>
internal static class CcfColumnReader
{
    private static readonly UTF8Encoding Utf8Strict = new(false, true);

    /// <summary>Validates the header and reads the whole payload into columns.</summary>
    public static ColumnData[] ReadPayload(
        ReadOnlySpan<byte> payload, CcfSchema schema, CcfLimits limits, out CcfPayloadFlags flags, out int rows)
    {
        if (payload.Length > limits.MaxPayloadBytes)
        {
            throw new CcfFormatException(CcfErrorCode.LimitExceeded, "payload larger than the payload limit");
        }

        var r = new CcfReader(payload);
        ReadHeader(ref r, schema.IdBytes, out flags, out var rowCount, limits);
        rows = rowCount;

        if (schema.Depth > CcfLimits.MaxDepth)
        {
            throw new CcfFormatException(CcfErrorCode.LimitExceeded, "schema nesting deeper than 8");
        }

        var table = ReadTable(ref r, schema.Fields, rows, limits);
        if (r.Remaining != 0)
        {
            throw new CcfFormatException(CcfErrorCode.TrailingData, $"{r.Remaining} bytes after the root table");
        }

        return table;
    }

    /// <summary>
    /// Validates magic, version and flags and reads the schema id. When <paramref name="expectedId"/> is
    /// empty the schema id check is skipped.
    /// </summary>
    public static void ReadHeader(
        ref CcfReader r, ReadOnlySpan<byte> expectedId, out CcfPayloadFlags flags, out int rows, CcfLimits limits)
    {
        var magic = r.ReadBytes(2);
        if (magic[0] != 0x43 || magic[1] != 0x43)
        {
            throw new CcfFormatException(CcfErrorCode.BadMagic);
        }

        var version = r.ReadByte();
        if (version != 1)
        {
            throw new CcfFormatException(CcfErrorCode.UnsupportedVersion, $"version {version}");
        }

        var f = r.ReadByte();
        if ((f & 0xFC) != 0)
        {
            throw new CcfFormatException(CcfErrorCode.ReservedFlags, $"flags {f:x2}");
        }

        flags = (CcfPayloadFlags)f;
        var id = r.ReadBytes(4);
        if (!expectedId.IsEmpty && !id.SequenceEqual(expectedId))
        {
            throw new CcfFormatException(CcfErrorCode.SchemaMismatch);
        }

        var n = r.ReadUVarint();
        if ((flags & CcfPayloadFlags.SingleObject) != 0 && n != 1)
        {
            throw new CcfFormatException(CcfErrorCode.SingleObjectRowCount, $"row count {n}");
        }

        if (n > (ulong)limits.MaxRows)
        {
            throw new CcfFormatException(CcfErrorCode.LimitExceeded, $"row count {n}");
        }

        rows = (int)n;
    }

    public static ColumnData[] ReadTable(ref CcfReader r, IReadOnlyList<CcfField> fields, int rows, CcfLimits limits)
    {
        if (rows > limits.MaxRows)
        {
            throw new CcfFormatException(CcfErrorCode.LimitExceeded, $"{rows} rows in one table");
        }

        var columns = new ColumnData[fields.Count];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = ReadColumn(ref r, fields[i], rows, limits);
        }

        return columns;
    }

    private static ColumnData ReadColumn(ref CcfReader r, CcfField field, int rows, CcfLimits limits)
    {
        var block = r.EnterBlock(field.Type == CcfType.String, out var mode);
        var col = new ColumnData(field, rows);
        var p = rows;

        if (field.Nullable)
        {
            var bitmap = r.ReadBytes(((long)rows + 7) / 8);
            var present = new bool[rows];
            p = 0;
            for (var i = 0; i < rows; i++)
            {
                if (((bitmap[i >> 3] >> (i & 7)) & 1) != 0)
                {
                    present[i] = true;
                    p++;
                }
            }

            col.Present = present;
        }

        col.PresentCount = p;

        switch (field.Type)
        {
            case CcfType.Bool:
                ReadBools(ref r, col, p);
                break;
            case CcfType.Int32:
            case CcfType.Int64:
            case CcfType.UInt32:
            case CcfType.Decimal:
            case CcfType.DateTime:
                ReadSignedLike(ref r, col, p);
                break;
            case CcfType.UInt64:
                ReadUInt64(ref r, col, p);
                break;
            case CcfType.Float64:
                r.Require(8L * p);
                var f64 = new double[rows];
                for (var i = 0; i < rows; i++)
                {
                    if (col.IsPresent(i))
                    {
                        f64[i] = CcfPrimitives.ReadDouble(r.ReadBytes(8));
                    }
                }

                col.F64 = f64;
                break;
            case CcfType.Guid:
                r.Require(16L * p);
                var guids = new Guid[rows];
                for (var i = 0; i < rows; i++)
                {
                    if (col.IsPresent(i))
                    {
                        guids[i] = new Guid(r.ReadBytes(16), bigEndian: true);
                    }
                }

                col.Guids = guids;
                break;
            case CcfType.Enum:
                ReadEnum(ref r, col, p);
                break;
            case CcfType.String:
                if (mode == 1)
                {
                    ReadDictionary(ref r, col, p, limits);
                }
                else
                {
                    ReadPlainStrings(ref r, col, p, limits);
                }

                break;
            case CcfType.Object:
                ReadObject(ref r, col, p, limits);
                break;
            case CcfType.List:
                ReadList(ref r, col, p, limits);
                break;
        }

        r.ExitBlock(block);
        return col;
    }

    private static void ReadBools(ref CcfReader r, ColumnData col, int p)
    {
        var packed = r.ReadBytes(CcfPrimitives.PackedByteCount(p, 1));
        var bools = new bool[col.Rows];
        var k = 0;
        for (var i = 0; i < col.Rows; i++)
        {
            if (col.IsPresent(i))
            {
                bools[i] = CcfPrimitives.ReadPacked(packed, k++, 1) != 0;
            }
        }

        col.Bools = bools;
    }

    private static CcfFormatException OutOfRange(string what) => new(CcfErrorCode.ValueOutOfRange, what);

    private static long AddChecked(long current, long diff)
    {
        if ((diff > 0 && current > long.MaxValue - diff) || (diff < 0 && current < long.MinValue - diff))
        {
            throw OutOfRange("delta overflows int64");
        }

        return current + diff;
    }

    /// <summary>int32, int64, uint32, decimal and datetime columns: everything stored as a 64-bit signed integer.</summary>
    private static void ReadSignedLike(ref CcfReader r, ColumnData col, int p)
    {
        r.Require(p); // every value takes at least one byte
        var field = col.Field;
        var values = new long[col.Rows];
        var delta = field.Delta;
        long current = 0;

        for (var i = 0; i < values.Length; i++)
        {
            if (!col.IsPresent(i))
            {
                continue;
            }

            var raw = r.ReadUVarint();
            long v;
            switch (field.Type)
            {
                case CcfType.UInt32:
                    if (delta)
                    {
                        current = AddChecked(current, CcfPrimitives.UnZigZag(raw));
                        v = current;
                    }
                    else
                    {
                        v = raw > uint.MaxValue ? -1 : (long)raw;
                    }

                    if (v < 0 || v > uint.MaxValue)
                    {
                        throw OutOfRange("uint32 value out of range");
                    }

                    break;
                case CcfType.Int32:
                    v = delta ? current = AddChecked(current, CcfPrimitives.UnZigZag(raw)) : CcfPrimitives.UnZigZag(raw);
                    if (v < int.MinValue || v > int.MaxValue)
                    {
                        throw OutOfRange("int32 value out of range");
                    }

                    break;
                case CcfType.Decimal when field.Unsigned && !delta:
                    if (raw > long.MaxValue)
                    {
                        throw OutOfRange("decimal value out of range");
                    }

                    v = (long)raw;
                    break;
                default:
                    v = delta ? current = AddChecked(current, CcfPrimitives.UnZigZag(raw)) : CcfPrimitives.UnZigZag(raw);
                    if (field.Type == CcfType.Decimal && field.Unsigned && v < 0)
                    {
                        throw OutOfRange("unsigned decimal went negative");
                    }

                    if (field.Type == CcfType.DateTime && (v < CcfConvert.MinDateTimeMs || v > CcfConvert.MaxDateTimeMs))
                    {
                        throw OutOfRange("datetime outside the supported range");
                    }

                    break;
            }

            values[i] = v;
        }

        col.I64 = values;
    }

    private static void ReadUInt64(ref CcfReader r, ColumnData col, int p)
    {
        r.Require(p);
        var values = new ulong[col.Rows];
        var delta = col.Field.Delta;
        ulong current = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (!col.IsPresent(i))
            {
                continue;
            }

            var raw = r.ReadUVarint();
            if (!delta)
            {
                values[i] = raw;
                continue;
            }

            var diff = CcfPrimitives.UnZigZag(raw);
            if (diff >= 0)
            {
                var sum = current + (ulong)diff;
                if (sum < current)
                {
                    throw OutOfRange("uint64 delta overflows");
                }

                current = sum;
            }
            else
            {
                var magnitude = unchecked((ulong)(-(diff + 1))) + 1;
                if (magnitude > current)
                {
                    throw OutOfRange("uint64 delta goes below zero");
                }

                current -= magnitude;
            }

            values[i] = current;
        }

        col.U64 = values;
    }

    private static void ReadEnum(ref CcfReader r, ColumnData col, int p)
    {
        var count = col.Field.Values!.Count;
        var width = CcfPrimitives.EnumBitWidth(count);
        var packed = r.ReadBytes(CcfPrimitives.PackedByteCount(p, width));
        var values = new long[col.Rows];
        var k = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (!col.IsPresent(i))
            {
                continue;
            }

            var index = CcfPrimitives.ReadPacked(packed, k++, width);
            if (index >= count)
            {
                throw new CcfFormatException(CcfErrorCode.IndexOutOfRange, $"enum index {index} of {count}");
            }

            values[i] = index;
        }

        col.I64 = values;
    }

    private static string ReadUtf8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (!Utf8.IsValid(bytes))
        {
            throw new CcfFormatException(CcfErrorCode.InvalidUtf8);
        }

        return Utf8Strict.GetString(bytes);
    }

    private static void ReadPlainStrings(ref CcfReader r, ColumnData col, int p, CcfLimits limits)
    {
        r.Require(p); // every length takes at least one byte
        var lengths = new int[p];
        long total = 0;
        for (var k = 0; k < p; k++)
        {
            var len = r.ReadUVarint();
            if (len > (ulong)limits.MaxStringBytes)
            {
                throw new CcfFormatException(CcfErrorCode.LimitExceeded, $"string of {len} bytes");
            }

            lengths[k] = (int)len;
            total += (long)len;
        }

        r.Require(total);
        var strings = new string?[col.Rows];
        var next = 0;
        for (var i = 0; i < strings.Length; i++)
        {
            if (col.IsPresent(i))
            {
                strings[i] = ReadUtf8(r.ReadBytes(lengths[next++]));
            }
        }

        col.Strings = strings;
    }

    private static void ReadDictionary(ref CcfReader r, ColumnData col, int p, CcfLimits limits)
    {
        var k = r.ReadUVarint();
        if (k > CcfLimits.MaxDictionaryEntries)
        {
            throw new CcfFormatException(CcfErrorCode.LimitExceeded, $"dictionary of {k} entries");
        }

        r.Require((long)k); // every entry takes at least one length byte
        var entries = new string[(int)k];
        for (var e = 0; e < entries.Length; e++)
        {
            var len = r.ReadUVarint();
            if (len > (ulong)limits.MaxStringBytes)
            {
                throw new CcfFormatException(CcfErrorCode.LimitExceeded, $"dictionary entry of {len} bytes");
            }

            entries[e] = ReadUtf8(r.ReadBytes((int)len));
        }

        var strings = new string?[col.Rows];
        if (p > 0 && k == 0)
        {
            r.ReadByte(); // the first index; with an empty dictionary it cannot be valid (SPEC §14.5)
            throw new CcfFormatException(CcfErrorCode.IndexOutOfRange, "index into an empty dictionary");
        }

        var width = k <= 256 ? 1 : 2;
        r.Require((long)p * width);
        for (var i = 0; i < strings.Length; i++)
        {
            if (!col.IsPresent(i))
            {
                continue;
            }

            var index = width == 1 ? r.ReadByte() : (int)r.ReadByte() | ((int)r.ReadByte() << 8);
            if ((uint)index >= (uint)entries.Length)
            {
                throw new CcfFormatException(CcfErrorCode.IndexOutOfRange, $"dictionary index {index} of {entries.Length}");
            }

            strings[i] = entries[index];
        }

        col.Strings = strings;
    }

    private static void ReadObject(ref CcfReader r, ColumnData col, int p, CcfLimits limits)
    {
        col.Children = ReadTable(ref r, col.Field.Fields!, p, limits);
        col.ChildRows = p;
        var start = new int[col.Rows];
        var next = 0;
        for (var i = 0; i < start.Length; i++)
        {
            start[i] = col.IsPresent(i) ? next++ : -1;
        }

        col.ChildStart = start;
    }

    private static void ReadList(ref CcfReader r, ColumnData col, int p, CcfLimits limits)
    {
        r.Require(p); // every count takes at least one byte
        var counts = new int[p];
        ulong sum = 0;
        var sumOverflow = false;
        for (var k = 0; k < p; k++)
        {
            var c = r.ReadUVarint();
            var s = sum + c;
            sumOverflow |= s < sum;
            sum = s;
            counts[k] = c > int.MaxValue ? int.MaxValue : (int)c;
        }

        var total = r.ReadUVarint();
        if (sumOverflow || sum != total)
        {
            throw new CcfFormatException(CcfErrorCode.CountMismatch, $"counts sum to {sum}, total is {total}");
        }

        if (total > (ulong)limits.MaxRows)
        {
            throw new CcfFormatException(CcfErrorCode.LimitExceeded, $"{total} rows in one table");
        }

        col.Children = ReadTable(ref r, col.Field.Fields!, (int)total, limits);
        col.ChildRows = (int)total;

        var start = new int[col.Rows];
        var count = new int[col.Rows];
        var next = 0;
        var running = 0;
        for (var i = 0; i < start.Length; i++)
        {
            start[i] = running;
            if (col.IsPresent(i))
            {
                count[i] = counts[next];
                running += counts[next++];
            }
        }

        col.ChildStart = start;
        col.ChildCount = count;
    }
}
