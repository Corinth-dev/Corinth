using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ccf.Core.Tests.Conformance;

// C# types that model the three schemas of test-vectors/. Enum members are lower-case because the schemas'
// `values` are the member names.

public enum ProductStatus { active, discontinued }

public sealed class Product
{
    [CcfDelta]
    public long Id { get; set; }

    public string Name { get; set; } = "";

    [CcfDecimal(2, Unsigned = true)]
    public decimal Price { get; set; }

    public ProductStatus Status { get; set; }

    public uint Stock { get; set; }
}

public enum Color { red, green, blue }

public sealed class AllTypes
{
    public int? I32 { get; set; }

    public long I64 { get; set; }

    public uint U32 { get; set; }

    public ulong U64 { get; set; }

    [CcfDecimal(4)]
    public decimal? Dec { get; set; }

    public double F64 { get; set; }

    public bool? Flag { get; set; }

    public Color Color { get; set; }

    public string? Text { get; set; }

    [CcfDelta]
    public DateTime? At { get; set; }

    public Guid Uid { get; set; }
}

public sealed class Customer
{
    public string Name { get; set; } = "";

    public string? City { get; set; }
}

public sealed class OrderLine
{
    public string Sku { get; set; } = "";

    public uint Qty { get; set; }

    [CcfDecimal(2, Unsigned = true)]
    public decimal Price { get; set; }
}

public sealed class Order
{
    public uint Id { get; set; }

    public Customer? Customer { get; set; }

    public List<OrderLine> Lines { get; set; } = new();

    public List<string>? Tags { get; set; }
}

internal static class VectorModels
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Type ModelFor(string schemaName) => schemaName switch
    {
        "Product" => typeof(Product),
        "AllTypes" => typeof(AllTypes),
        "Order" => typeof(Order),
        _ => throw new ArgumentException("No C# model for schema " + schemaName),
    };

    /// <summary>Loads an expected file into the model: a <c>List&lt;T&gt;</c> for arrays, a <c>T</c> for objects.</summary>
    public static object Load(string expectedJson, string schemaName)
    {
        var model = ModelFor(schemaName);
        var isArray = expectedJson.TrimStart().StartsWith('[');
        var target = isArray ? typeof(List<>).MakeGenericType(model) : model;
        return JsonSerializer.Deserialize(expectedJson, target, Json)!;
    }
}
