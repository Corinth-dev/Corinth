using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ccf.SampleApi;

/// <summary>Loads the sample data (the golden-vector expected files, copied next to the binaries).</summary>
public sealed class SampleData
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Creates the data set.</summary>
    public SampleData()
    {
        Products = Load<List<Product>>("products.json");
        Orders = Load<List<Order>>("orders.json");
        AllTypes = Load<List<AllTypes>>("all-types.json");
    }

    /// <summary>The 500 sample products.</summary>
    public IReadOnlyList<Product> Products { get; }

    /// <summary>Three sample orders.</summary>
    public IReadOnlyList<Order> Orders { get; }

    /// <summary>Six rows of every scalar type.</summary>
    public IReadOnlyList<AllTypes> AllTypes { get; }

    private static T Load<T>(string file) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", file)), Json)!;
}
