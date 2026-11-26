namespace Corinth.SampleApi;

/// <summary>Product status; member names are the schema's enum values.</summary>
public enum ProductStatus { active, discontinued }

/// <summary>A catalogue product (schema <c>c33a3ed0</c>).</summary>
public sealed class Product
{
    /// <summary>Ids are mostly consecutive, so deltas are tiny.</summary>
    [CcfDelta]
    public long Id { get; set; }

    /// <summary>Name; repetitive, so dictionary mode pays off.</summary>
    public string Name { get; set; } = "";

    /// <summary>Price with two decimals.</summary>
    [CcfDecimal(2, Unsigned = true)]
    public decimal Price { get; set; }

    /// <summary>Status.</summary>
    public ProductStatus Status { get; set; }

    /// <summary>Units in stock.</summary>
    public uint Stock { get; set; }
}

/// <summary>Customer of an order.</summary>
public sealed class Customer
{
    /// <summary>Name.</summary>
    public string Name { get; set; } = "";

    /// <summary>City, when known.</summary>
    public string? City { get; set; }
}

/// <summary>One line of an order.</summary>
public sealed class OrderLine
{
    /// <summary>Stock keeping unit.</summary>
    public string Sku { get; set; } = "";

    /// <summary>Quantity.</summary>
    public uint Qty { get; set; }

    /// <summary>Unit price.</summary>
    [CcfDecimal(2, Unsigned = true)]
    public decimal Price { get; set; }
}

/// <summary>An order with a nullable customer, lines and optional tags.</summary>
public sealed class Order
{
    /// <summary>Id.</summary>
    public uint Id { get; set; }

    /// <summary>Customer, if known.</summary>
    public Customer? Customer { get; set; }

    /// <summary>Lines.</summary>
    public List<OrderLine> Lines { get; set; } = new();

    /// <summary>Tags, or null.</summary>
    public List<string>? Tags { get; set; }
}

/// <summary>Colors.</summary>
public enum Color { red, green, blue }

/// <summary>A type exercising every scalar CCF type.</summary>
public sealed class AllTypes
{
    /// <summary>int32?</summary>
    public int? I32 { get; set; }

    /// <summary>int64</summary>
    public long I64 { get; set; }

    /// <summary>uint32</summary>
    public uint U32 { get; set; }

    /// <summary>uint64</summary>
    public ulong U64 { get; set; }

    /// <summary>decimal(4)?</summary>
    [CcfDecimal(4)]
    public decimal? Dec { get; set; }

    /// <summary>float64</summary>
    public double F64 { get; set; }

    /// <summary>bool?</summary>
    public bool? Flag { get; set; }

    /// <summary>enum</summary>
    public Color Color { get; set; }

    /// <summary>string?</summary>
    public string? Text { get; set; }

    /// <summary>datetime?, delta-coded</summary>
    [CcfDelta]
    public DateTime? At { get; set; }

    /// <summary>guid</summary>
    public Guid Uid { get; set; }
}

/// <summary>A type CCF cannot express (it has an <see cref="object"/> member): always served as JSON.</summary>
public sealed class UnsupportedReport
{
    /// <summary>Title.</summary>
    public string Title { get; set; } = "";

    /// <summary>Free-form details: not expressible in a CCF schema.</summary>
    public object? Details { get; set; }
}

/// <summary>Data that cannot be encoded: more decimals than the scale allows, so CCF falls back to JSON.</summary>
public sealed class PricedItem
{
    /// <summary>Name.</summary>
    public string Name { get; set; } = "";

    /// <summary>Price with scale 2, but the data has three decimals.</summary>
    [CcfDecimal(2)]
    public decimal Price { get; set; }
}
