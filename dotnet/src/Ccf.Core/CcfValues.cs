namespace Ccf;

/// <summary>
/// A decoded record in the untyped value tree: field names in schema order and their values. Values are
/// <c>bool</c>, <c>int</c>, <c>long</c>, <c>uint</c>, <c>ulong</c>, <c>double</c>, <c>decimal</c>,
/// <c>string</c> (also enum member names), <see cref="DateTime"/> (UTC), <see cref="Guid"/>,
/// <see cref="CcfObject"/>, <c>object?[]</c> (lists), or <c>null</c>.
/// </summary>
public sealed class CcfObject
{
    internal CcfObject(string[] names, object?[] values)
    {
        Names = names;
        Values = values;
    }

    /// <summary>Field names in schema order.</summary>
    public IReadOnlyList<string> Names { get; }

    /// <summary>Field values, parallel to <see cref="Names"/>.</summary>
    public IReadOnlyList<object?> Values { get; }

    /// <summary>Gets a field value by name.</summary>
    /// <exception cref="KeyNotFoundException">No such field.</exception>
    public object? this[string name]
    {
        get
        {
            var i = Array.IndexOf((string[])Names, name);
            return i >= 0 ? Values[i] : throw new KeyNotFoundException(name);
        }
    }
}

/// <summary>Builds the untyped value tree from decoded columns.</summary>
internal static class CcfValueTree
{
    public static object?[] BuildRows(ColumnData[] columns, int rows, IReadOnlyList<CcfField> fields)
    {
        var names = new string[fields.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = fields[i].Name;
        }

        var result = new object?[rows];
        for (var row = 0; row < rows; row++)
        {
            result[row] = BuildRow(columns, row, names);
        }

        return result;
    }

    private static CcfObject BuildRow(ColumnData[] columns, int row, string[] names)
    {
        var values = new object?[columns.Length];
        for (var c = 0; c < columns.Length; c++)
        {
            values[c] = Box(columns[c], row);
        }

        return new CcfObject(names, values);
    }

    private static object? Box(ColumnData col, int row)
    {
        if (!col.IsPresent(row))
        {
            return null;
        }

        var f = col.Field;
        switch (f.Type)
        {
            case CcfType.Bool: return col.Bools![row];
            case CcfType.Int32: return (int)col.I64![row];
            case CcfType.Int64: return col.I64![row];
            case CcfType.UInt32: return (uint)col.I64![row];
            case CcfType.UInt64: return col.U64![row];
            case CcfType.Float64: return col.F64![row];
            case CcfType.Decimal: return CcfConvert.FromScaled(col.I64![row], f.Scale!.Value);
            case CcfType.Enum: return f.Values![(int)col.I64![row]];
            case CcfType.String: return col.Strings![row];
            case CcfType.DateTime: return CcfConvert.FromEpochMs(col.I64![row]);
            case CcfType.Guid: return col.Guids![row];
            case CcfType.Object:
                return BuildRow(col.Children!, col.ChildStart![row], Names(f.Fields!));
            default:
                var start = col.ChildStart![row];
                var count = col.ChildCount![row];
                var items = new object?[count];
                var children = col.Children!;
                if (f.Fields!.Count == 1 && f.Fields[0].Name == "$value")
                {
                    for (var k = 0; k < count; k++)
                    {
                        items[k] = Box(children[0], start + k);
                    }
                }
                else
                {
                    var names = Names(f.Fields);
                    for (var k = 0; k < count; k++)
                    {
                        items[k] = BuildRow(children, start + k, names);
                    }
                }

                return items;
        }
    }

    private static string[] Names(IReadOnlyList<CcfField> fields)
    {
        var names = new string[fields.Count];
        for (var i = 0; i < names.Length; i++)
        {
            names[i] = fields[i].Name;
        }

        return names;
    }
}
