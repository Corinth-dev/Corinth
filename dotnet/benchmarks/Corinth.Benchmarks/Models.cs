using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corinth.Benchmarks;

[JsonConverter(typeof(JsonStringEnumConverter<ProductStatus>))]
public enum ProductStatus { active, discontinued }

/// <summary>Same shape as the sample API's product (schema c33a3ed0).</summary>
public sealed class Product
{
    [CcfDelta] public long Id { get; set; }
    public string Name { get; set; } = "";
    [CcfDecimal(2, Unsigned = true)] public decimal Price { get; set; }
    public ProductStatus Status { get; set; }
    public uint Stock { get; set; }
}

/// <summary>Mostly unique strings: dictionary mode cannot help here.</summary>
public sealed class Contact
{
    [CcfDelta] public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Note { get; set; } = "";
    public string Company { get; set; } = "";
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
    [CcfDecimal(2, Unsigned = true)] public decimal Price { get; set; }
}

/// <summary>Same shape as the sample API's order (schema 63a83963): nullable object, list of objects, list of strings.</summary>
public sealed class Order
{
    public uint Id { get; set; }
    public Customer? Customer { get; set; }
    public List<OrderLine> Lines { get; set; } = new();
    public List<string>? Tags { get; set; }
}

/// <summary>The JSON options both the reflection baseline and the CCF naming rules agree on.</summary>
public static class BenchJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<Product>))]
[JsonSerializable(typeof(List<Contact>))]
[JsonSerializable(typeof(List<Order>))]
public partial class BenchJsonContext : JsonSerializerContext
{
}
