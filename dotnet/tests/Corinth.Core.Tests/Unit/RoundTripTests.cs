using System.Buffers;
using System.Text;
using System.Text.Json;
using Corinth.Core.Tests.Conformance;
using Xunit;

namespace Corinth.Core.Tests;

/// <summary>Random data for each schema → encode → decode → equal (typed and untyped), with and without dictionary mode.</summary>
public class RoundTripTests
{
    private static string Json(object? value) => JsonSerializer.Serialize(value, VectorModels.Json);

    private static string RandomString(Random r)
    {
        switch (r.Next(8))
        {
            case 0: return "";
            case 1: return "héllo ✓ 日本語";
            case 2: return "😀" + r.Next(10);
            case 3: return new string('x', r.Next(0, 400));
            default:
                var sb = new StringBuilder();
                for (var i = r.Next(1, 12); i > 0; i--)
                {
                    sb.Append(r.Next(5) switch
                    {
                        0 => (char)r.Next('A', 'Z' + 1),
                        1 => (char)r.Next('a', 'z' + 1),
                        2 => (char)r.Next(0x4E00, 0x4E80),
                        3 => (char)r.Next(0xC0, 0x180),
                        _ => (char)r.Next('0', '9' + 1),
                    });
                }

                return sb.ToString();
        }
    }

    private static readonly string[] Pool = { "alpha", "beta", "gamma", "delta", "épsilon", "日本" };

    private static string PooledOrRandom(Random r, double pooled) => r.NextDouble() < pooled ? Pool[r.Next(Pool.Length)] : RandomString(r);

    private static long RandomLong(Random r) => r.Next(8) switch
    {
        0 => long.MinValue,
        1 => long.MaxValue,
        2 => 0,
        3 => r.Next(-1000, 1000),
        _ => r.NextInt64(long.MinValue / 2, long.MaxValue / 2),
    };

    private static ulong RandomULong(Random r) => r.Next(6) switch
    {
        0 => ulong.MaxValue,
        1 => 0,
        2 => (ulong)r.Next(0, 300),
        _ => (ulong)r.NextInt64(0, long.MaxValue) * 2 + (ulong)r.Next(2),
    };

    private static decimal RandomDecimal(Random r, int scale, bool unsigned)
    {
        var raw = r.Next(4) == 0 ? r.NextInt64(-1_000_000_000_000_000L, 1_000_000_000_000_000L) : r.NextInt64(-100_000, 100_000);
        if (unsigned)
        {
            raw = Math.Abs(raw);
        }

        return CcfConvert.FromScaled(raw, scale);
    }

    private static List<Product> RandomProducts(Random r, int n)
    {
        var id = r.NextInt64(-5000, 5000);
        var list = new List<Product>(n);
        for (var i = 0; i < n; i++)
        {
            id = r.Next(20) == 0 ? RandomLong(r) / 4 : id + r.Next(-3, 10);
            list.Add(new Product
            {
                Id = id,
                Name = PooledOrRandom(r, 0.7),
                Price = RandomDecimal(r, 2, unsigned: true),
                Status = (ProductStatus)r.Next(2),
                Stock = r.Next(5) == 0 ? uint.MaxValue : (uint)r.Next(0, 5000),
            });
        }

        return list;
    }

    private static List<AllTypes> RandomAllTypes(Random r, int n)
    {
        var list = new List<AllTypes>(n);
        var at = r.NextInt64(-2_000_000_000_000, 4_000_000_000_000);
        for (var i = 0; i < n; i++)
        {
            at += r.Next(-100_000, 1_000_000);
            var d = r.Next(10) == 0 ? double.MaxValue : r.Next(10) == 0 ? -0.0 : (r.NextDouble() - 0.5) * Math.Pow(10, r.Next(-12, 12));
            list.Add(new AllTypes
            {
                I32 = r.Next(4) == 0 ? null : r.Next(6) switch { 0 => int.MinValue, 1 => int.MaxValue, _ => r.Next() - r.Next() },
                I64 = RandomLong(r),
                U32 = r.Next(6) switch { 0 => uint.MaxValue, 1 => 0, _ => (uint)r.Next() },
                U64 = RandomULong(r),
                Dec = r.Next(4) == 0 ? null : RandomDecimal(r, 4, unsigned: false),
                F64 = d,
                Flag = r.Next(3) == 0 ? null : r.Next(2) == 0,
                Color = (Color)r.Next(3),
                Text = r.Next(4) == 0 ? null : PooledOrRandom(r, 0.5),
                At = r.Next(3) == 0 ? null : CcfConvert.FromEpochMs(Math.Clamp(at, CcfConvert.MinDateTimeMs, CcfConvert.MaxDateTimeMs)),
                Uid = Guid.NewGuid(),
            });
        }

        return list;
    }

    private static List<Order> RandomOrders(Random r, int n)
    {
        var list = new List<Order>(n);
        for (var i = 0; i < n; i++)
        {
            var o = new Order
            {
                Id = (uint)r.Next(),
                Customer = r.Next(3) == 0 ? null : new Customer { Name = PooledOrRandom(r, 0.5), City = r.Next(3) == 0 ? null : PooledOrRandom(r, 0.5) },
                Tags = r.Next(3) == 0 ? null : Enumerable.Range(0, r.Next(0, 5)).Select(_ => PooledOrRandom(r, 0.5)).ToList(),
            };
            for (var k = r.Next(0, 6); k > 0; k--)
            {
                o.Lines.Add(new OrderLine { Sku = PooledOrRandom(r, 0.8), Qty = (uint)r.Next(0, 100), Price = RandomDecimal(r, 2, unsigned: true) });
            }

            list.Add(o);
        }

        return list;
    }

    private static int RowCount(Random r) => r.Next(10) switch { 0 => 0, 1 => 1, 2 => r.Next(200, 700), _ => r.Next(2, 40) };

    private static void RoundTrip<T>(List<T> data, CcfFeatures features)
    {
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(List<T>), null, out var schema, out var reason), reason);
        var buffer = new ArrayBufferWriter<byte>();
        var flags = new CcfEncoder().Encode(data, schema!, features, buffer);
        var bytes = buffer.WrittenSpan.ToArray();

        if (features == CcfFeatures.None)
        {
            Assert.Equal(CcfPayloadFlags.None, flags);
        }

        var typed = CcfDecoder.Decode<List<T>>(bytes, schema!);
        Assert.Equal(Json(data), Json(typed));

        // Untyped: row count and a re-encode of the typed result must be byte-identical (idempotence).
        var untyped = (object?[])CcfDecoder.DecodeValue(bytes, schema!);
        Assert.Equal(data.Count, untyped.Length);
        var again = new ArrayBufferWriter<byte>();
        new CcfEncoder().Encode(typed, schema!, features, again);
        Assert.Equal(bytes, again.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(1, CcfFeatures.None)]
    [InlineData(2, CcfFeatures.Dictionary)]
    [InlineData(3, CcfFeatures.None)]
    [InlineData(4, CcfFeatures.Dictionary)]
    public void Random_products_round_trip(int seed, CcfFeatures features)
    {
        var r = new Random(seed);
        for (var i = 0; i < 60; i++)
        {
            RoundTrip(RandomProducts(r, RowCount(r)), features);
        }
    }

    [Theory]
    [InlineData(1, CcfFeatures.None)]
    [InlineData(2, CcfFeatures.Dictionary)]
    [InlineData(3, CcfFeatures.None)]
    [InlineData(4, CcfFeatures.Dictionary)]
    public void Random_all_types_round_trip(int seed, CcfFeatures features)
    {
        var r = new Random(seed);
        for (var i = 0; i < 60; i++)
        {
            RoundTrip(RandomAllTypes(r, RowCount(r)), features);
        }
    }

    [Theory]
    [InlineData(1, CcfFeatures.None)]
    [InlineData(2, CcfFeatures.Dictionary)]
    [InlineData(3, CcfFeatures.None)]
    [InlineData(4, CcfFeatures.Dictionary)]
    public void Random_nested_orders_round_trip(int seed, CcfFeatures features)
    {
        var r = new Random(seed);
        for (var i = 0; i < 60; i++)
        {
            RoundTrip(RandomOrders(r, RowCount(r)), features);
        }
    }

    [Fact]
    public void Random_single_objects_round_trip()
    {
        var r = new Random(99);
        for (var i = 0; i < 100; i++)
        {
            var order = RandomOrders(r, 1)[0];
            Assert.True(CcfSchemaBuilder.TryBuild(typeof(Order), null, out var schema, out _));
            var buffer = new ArrayBufferWriter<byte>();
            var flags = new CcfEncoder().Encode(order, schema!, CcfFeatures.Dictionary, buffer);
            Assert.True((flags & CcfPayloadFlags.SingleObject) != 0);
            Assert.Equal(Json(order), Json(CcfDecoder.Decode<Order>(buffer.WrittenSpan, schema!)));
        }
    }

    [Fact]
    public void Dictionary_mode_never_makes_a_payload_larger_than_plain_would_on_repetitive_data()
    {
        var r = new Random(7);
        var data = RandomProducts(r, 500);
        foreach (var p in data)
        {
            p.Name = Pool[Math.Abs(p.Name.GetHashCode()) % Pool.Length] + "-" + Math.Abs(p.Name.GetHashCode()) % 20;
        }

        Assert.True(CcfSchemaBuilder.TryBuild(typeof(List<Product>), null, out var schema, out _));
        var plain = new ArrayBufferWriter<byte>();
        var dict = new ArrayBufferWriter<byte>();
        new CcfEncoder().Encode(data, schema!, CcfFeatures.None, plain);
        new CcfEncoder().Encode(data, schema!, CcfFeatures.Dictionary, dict);
        Assert.True(dict.WrittenCount < plain.WrittenCount);
    }
}
