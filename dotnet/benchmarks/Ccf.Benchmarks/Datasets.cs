using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Ccf.Benchmarks;

/// <summary>A dataset with everything every benchmark needs, built once.</summary>
public sealed class Dataset<T>
{
    public required string Name { get; init; }
    public required List<T> Rows { get; init; }
    public required CcfSchema Schema { get; init; }
    public required JsonTypeInfo<List<T>> SourceGen { get; init; }
    public required byte[] Json { get; init; }
    public required byte[] JsonGzip { get; init; }
    public required byte[] JsonBrotli { get; init; }
    public required byte[] CcfPlain { get; init; }
    public required byte[] CcfDict { get; init; }
}

public static class Datasets
{
    public static readonly string[] Names = { "products-500", "products-5k", "unique-strings-5k", "orders-5k" };

    private static readonly CcfEncoder Encoder = new();

    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CLAUDE.md")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Cannot find the repository root (CLAUDE.md).");
    }

    /// <summary>The 500 products of the golden vectors v01/v02.</summary>
    public static List<Product> Products500()
    {
        var path = Path.Combine(RepoRoot(), "test-vectors", "v01-products-plain.expected.json");
        var options = new JsonSerializerOptions(BenchJson.Options) { NumberHandling = JsonNumberHandling.AllowReadingFromString };
        return JsonSerializer.Deserialize<List<Product>>(File.ReadAllText(path), options)!;
    }

    /// <summary>5,000 products: ids mostly consecutive, ~300 distinct names, a few statuses.</summary>
    public static List<Product> Products5k()
    {
        var rnd = new Random(5000);
        string[] adjectives = { "Steel", "Deluxe", "Compact", "Silent", "Rapid", "Eco", "Smart", "Classic", "Ultra", "Pocket" };
        string[] nouns = { "Monitor", "Keyboard", "Mouse", "Dock", "Router", "Camera", "Speaker", "Charger", "Cable", "Lamp",
            "Chair", "Desk", "Drive", "Hub", "Tablet", "Printer", "Scanner", "Headset", "Webcam", "Stand",
            "Case", "Adapter", "Sensor", "Switch", "Remote", "Light", "Fan", "Heater", "Kettle", "Scale" };
        var list = new List<Product>(5000);
        long id = 1000;
        for (var i = 0; i < 5000; i++)
        {
            id += rnd.Next(100) < 90 ? 1 : rnd.Next(2, 20);
            list.Add(new Product
            {
                Id = id,
                Name = adjectives[rnd.Next(adjectives.Length)] + " " + nouns[rnd.Next(nouns.Length)],
                Price = rnd.Next(199, 250000) / 100m,
                Status = rnd.Next(100) < 93 ? ProductStatus.active : ProductStatus.discontinued,
                Stock = (uint)rnd.Next(0, 2000),
            });
        }

        return list;
    }

    /// <summary>5,000 contacts whose name, email and note are almost always unique.</summary>
    public static List<Contact> UniqueStrings5k()
    {
        var rnd = new Random(7);
        string[] words = { "alpha", "bravo", "cedar", "delta", "ember", "fjord", "grove", "harbor", "iris", "jade", "kelp", "lumen",
            "maple", "nova", "onyx", "pearl", "quartz", "raven", "sable", "tundra", "umber", "vivid", "willow", "xenon", "yarrow", "zephyr" };
        var companies = Enumerable.Range(0, 200).Select(i => "Company " + words[i % words.Length] + " " + (i / words.Length)).ToArray();
        string Word() => words[rnd.Next(words.Length)];
        var list = new List<Contact>(5000);
        long id = 1;
        for (var i = 0; i < 5000; i++)
        {
            var first = Word();
            var last = Word();
            var tag = rnd.Next(1_000_000).ToString("x");
            var note = string.Join(' ', Enumerable.Range(0, rnd.Next(4, 14)).Select(_ => Word())) + " " + tag;
            id += 1;
            list.Add(new Contact
            {
                Id = id,
                Name = char.ToUpperInvariant(first[0]) + first[1..] + " " + char.ToUpperInvariant(last[0]) + last[1..] + " " + tag,
                Email = first + "." + last + tag + "@example.com",
                Note = note,
                Company = companies[rnd.Next(companies.Length)],
            });
        }

        return list;
    }

    /// <summary>5,000 orders with a nullable customer (300 distinct), 0 to 8 lines (120 SKUs) and optional tags.</summary>
    public static List<Order> Orders5k()
    {
        var rnd = new Random(11);
        var names = Enumerable.Range(0, 300).Select(i => "Customer " + i).ToArray();
        string[] cities = { "Pune", "Oslo", "Lima", "Kyiv", "Cairo", "Seoul", "Quito", "Porto", "Hanoi", "Accra", "Perth", "Malmo" };
        var skus = Enumerable.Range(0, 120).Select(i => (char)('A' + i % 26) + "-" + (100 + i * 7)).ToArray();
        var prices = skus.Select(_ => rnd.Next(99, 30000) / 100m).ToArray();
        string[] tags = { "gift", "express", "fragile", "b2b", "returning", "promo", "backorder", "wholesale", "pickup", "signature", "export", "priority" };
        var list = new List<Order>(5000);
        for (var i = 1; i <= 5000; i++)
        {
            var order = new Order { Id = (uint)i };
            if (rnd.Next(100) >= 5)
            {
                order.Customer = new Customer { Name = names[rnd.Next(names.Length)], City = rnd.Next(100) < 20 ? null : cities[rnd.Next(cities.Length)] };
            }

            for (var l = rnd.Next(0, 9); l > 0; l--)
            {
                var s = rnd.Next(skus.Length);
                order.Lines.Add(new OrderLine { Sku = skus[s], Qty = (uint)rnd.Next(1, 10), Price = prices[s] });
            }

            if (rnd.Next(100) >= 30)
            {
                for (var t = rnd.Next(0, 4); t > 0; t--)
                {
                    (order.Tags ??= new()).Add(tags[rnd.Next(tags.Length)]);
                }

                order.Tags ??= new();
            }

            list.Add(order);
        }

        return list;
    }

    public static Dataset<Product> Products500Set() => Build("products-500", Products500(), BenchJsonContext.Default.ListProduct);
    public static Dataset<Product> Products5kSet() => Build("products-5k", Products5k(), BenchJsonContext.Default.ListProduct);
    public static Dataset<Contact> UniqueStringsSet() => Build("unique-strings-5k", UniqueStrings5k(), BenchJsonContext.Default.ListContact);
    public static Dataset<Order> OrdersSet() => Build("orders-5k", Orders5k(), BenchJsonContext.Default.ListOrder);

    public static Dataset<T> Build<T>(string name, List<T> rows, JsonTypeInfo<List<T>> sourceGen)
    {
        if (!CcfSchemaBuilder.TryBuild(typeof(List<T>), null, out var schema, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(rows, sourceGen);
        return new Dataset<T>
        {
            Name = name,
            Rows = rows,
            Schema = schema,
            SourceGen = sourceGen,
            Json = json,
            JsonGzip = Compress.Gzip(json, System.IO.Compression.CompressionLevel.Optimal),
            JsonBrotli = Compress.Brotli(json, System.IO.Compression.CompressionLevel.Optimal),
            CcfPlain = EncodeCcf(rows, schema, CcfFeatures.None),
            CcfDict = EncodeCcf(rows, schema, CcfFeatures.Dictionary),
        };
    }

    public static byte[] EncodeCcf<T>(List<T> rows, CcfSchema schema, CcfFeatures features)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        Encoder.Encode(rows, schema, features, buffer);
        return buffer.WrittenSpan.ToArray();
    }
}

public static class Compress
{
    public static byte[] Gzip(byte[] data, System.IO.Compression.CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.GZipStream(ms, level, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }

    public static byte[] Brotli(byte[] data, System.IO.Compression.CompressionLevel level)
    {
        using var ms = new MemoryStream();
        using (var z = new System.IO.Compression.BrotliStream(ms, level, leaveOpen: true))
        {
            z.Write(data);
        }

        return ms.ToArray();
    }
}
