namespace Ccf;

/// <summary>
/// One decoded or to-be-encoded column in row-aligned form: arrays have one slot per row of the owning
/// table, and <see cref="Present"/> (null when the field is not nullable) says which slots hold values.
/// Shared by the decoder, the encoder's wire writer, and both typed front-ends.
/// </summary>
internal sealed class ColumnData
{
    public ColumnData(CcfField field, int rows)
    {
        Field = field;
        Rows = rows;
    }

    public CcfField Field { get; }

    public int Rows { get; }

    /// <summary>Number of present values (equals <see cref="Rows"/> when the field is not nullable).</summary>
    public int PresentCount { get; set; }

    public bool[]? Present { get; set; }

    /// <summary>int32, int64, uint32, scaled decimal, datetime (epoch ms) and enum index.</summary>
    public long[]? I64 { get; set; }

    public ulong[]? U64 { get; set; }

    public double[]? F64 { get; set; }

    public bool[]? Bools { get; set; }

    public string?[]? Strings { get; set; }

    public Guid[]? Guids { get; set; }

    /// <summary>Child table columns (object and list fields).</summary>
    public ColumnData[]? Children { get; set; }

    /// <summary>Rows in the child table.</summary>
    public int ChildRows { get; set; }

    /// <summary>Object: child row of each parent row (−1 when null). List: first child row of each parent row.</summary>
    public int[]? ChildStart { get; set; }

    /// <summary>List: number of elements of each parent row (0 when null).</summary>
    public int[]? ChildCount { get; set; }

    public bool IsPresent(int row) => Present is null || Present[row];
}
