using System.Text.Json;
using Ccf.Core.Tests.Conformance;
using System.Text.Json.Serialization;
using Xunit;

namespace Ccf.Core.Tests;

public class TypedDecodeTests
{
    private static CcfSchema S(string fieldsJson) => CcfSchema.Parse($$"""{"name":"T","fields":[{{fieldsJson}}]}""");

    private static string Json(object? value) => JsonSerializer.Serialize(value, VectorModels.Json);

    private static T Decode<T>(byte[] payload, CcfSchema schema) => CcfDecoder.Decode<T>(payload, schema);

    /// <summary>Calls <c>Decode&lt;target&gt;</c> by reflection (a span parameter cannot be passed to MethodInfo.Invoke).</summary>
    private static object DecodeAs(Type target, byte[] payload, CcfSchema schema)
    {
        try
        {
            return typeof(TypedDecodeTests).GetMethod(nameof(Decode), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .MakeGenericMethod(target).Invoke(null, new object[] { payload, schema })!;
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    // ---- vectors decode into the C# models -------------------------------------------------------------

    [Theory]
    [MemberData(nameof(VectorManifest.ValidNames), MemberType = typeof(VectorManifest))]
    public void Valid_vectors_decode_into_the_models_and_re_encode_to_the_same_bytes(string name)
    {
        var v = VectorManifest.Get(name);
        var schema = CcfSchema.Parse(v.ReadSchemaText());
        var model = VectorModels.ModelFor(schema.Name);
        var payload = v.ReadPayload();

        var isSingle = (payload[3] & 2) != 0;
        var target = isSingle ? model : typeof(List<>).MakeGenericType(model);
        var decoded = DecodeAs(target, payload, schema);

        var expected = VectorModels.Load(File.ReadAllText(VectorManifest.PathOf(v.Expected!)), schema.Name);
        Assert.Equal(Json(expected), Json(decoded));

        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        new CcfEncoder().Encode(decoded, schema, CcfFeatureParser.Parse(v.RequestedFeatures), buffer);
        Assert.Equal(payload, buffer.WrittenSpan.ToArray());
    }

    [Theory]
    [MemberData(nameof(VectorManifest.InvalidNames), MemberType = typeof(VectorManifest))]
    public void Invalid_vectors_fail_the_typed_decoder_with_the_same_codes(string name)
    {
        var v = VectorManifest.Get(name);
        var schema = CcfSchema.Parse(v.ReadSchemaText());
        var model = VectorModels.ModelFor(schema.Name);
        var target = typeof(List<>).MakeGenericType(model);
        var ex = Assert.Throws<CcfFormatException>(() => DecodeAs(target, v.ReadPayload(), schema));
        Assert.Equal(v.ExpectedError, ex.Code.ToString());
    }

    [Fact]
    public void Decimals_decode_exactly_with_their_scale()
    {
        var v = VectorManifest.Get("v01-products-plain");
        var products = CcfDecoder.Decode<List<Product>>(v.ReadPayload(), CcfSchema.Parse(v.ReadSchemaText()));
        Assert.Equal(361.48m, products[0].Price);
        Assert.Equal(2, (decimal.GetBits(products[0].Price)[3] >> 16) & 0xFF);
        Assert.Equal(500, products.Count);
        Assert.Equal(1001, products[0].Id);

        var all = VectorManifest.Get("v03-all-types");
        var rows = CcfDecoder.Decode<AllTypes[]>(all.ReadPayload(), CcfSchema.Parse(all.ReadSchemaText()));
        Assert.Equal(99999999.9999m, rows[3].Dec);
        Assert.Equal("0.0000", rows[1].Dec!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(DateTimeKind.Utc, rows[0].At!.Value.Kind);
        Assert.Equal(long.MinValue, rows[0].I64);
        Assert.Equal(ulong.MaxValue, rows[0].U64);
    }

    // ---- target shapes ---------------------------------------------------------------------------------

    private static (CcfSchema Schema, byte[] Payload) TwoStrings()
    {
        var schema = S("""{"name":"s","type":"string"}""");
        var payload = new PayloadBuilder(schema, 2).Block(0, PayloadBuilder.Cat(new byte[] { 1, 1 }, "ab"u8.ToArray())).ToArray();
        return (schema, payload);
    }

    public sealed class SRow { public string S { get; set; } = ""; }

    [Fact]
    public void Collections_decode_into_arrays_lists_and_read_only_interfaces()
    {
        var (schema, payload) = TwoStrings();
        Assert.Equal(new[] { "a", "b" }, CcfDecoder.Decode<SRow[]>(payload, schema).Select(r => r.S));
        Assert.Equal(new[] { "a", "b" }, CcfDecoder.Decode<List<SRow>>(payload, schema).Select(r => r.S));
        Assert.Equal(new[] { "a", "b" }, CcfDecoder.Decode<IReadOnlyList<SRow>>(payload, schema).Select(r => r.S));
        Assert.Equal(new[] { "a", "b" }, CcfDecoder.Decode<IEnumerable<SRow>>(payload, schema).Select(r => r.S));
        Assert.Equal(new[] { "a", "b" }, CcfDecoder.Decode<ICollection<SRow>>(payload, schema).Select(r => r.S));
        Assert.Equal(new[] { "a", "b" }, CcfDecoder.Decode<IList<SRow>>(payload, schema).Select(r => r.S));
        Assert.Equal(new[] { "a", "b" }, CcfDecoder.Decode<HashSet<SRow>>(payload, schema).Select(r => r.S).OrderBy(x => x));
    }

    [Fact]
    public void Single_object_and_array_roots_convert_into_each_other_when_the_shape_allows()
    {
        var schema = S("""{"name":"s","type":"string"}""");
        var single = new PayloadBuilder(schema, 1, 2).Block(0, 1, (byte)'a').ToArray();
        Assert.Equal("a", CcfDecoder.Decode<SRow>(single, schema).S);
        Assert.Equal("a", CcfDecoder.Decode<List<SRow>>(single, schema).Single().S);

        var one = new PayloadBuilder(schema, 1).Block(0, 1, (byte)'a').ToArray();
        Assert.Equal("a", CcfDecoder.Decode<SRow>(one, schema).S);

        var (_, two) = TwoStrings();
        Assert.Throws<InvalidOperationException>(() => CcfDecoder.Decode<SRow>(two, schema));
    }

    // ---- property matching (ARCHITECTURE §6.2) ----------------------------------------------------------

    public sealed class Subset
    {
        public string S { get; set; } = "";
        public string Extra { get; set; } = "kept";
        public int WithInitializer { get; set; } = 7;
    }

    [Fact]
    public void Fields_without_a_property_are_skipped_and_properties_without_a_field_keep_their_default()
    {
        var schema = S("""{"name":"s","type":"string"},{"name":"unknown","type":"int32"},{"name":"also","type":"list","fields":[{"name":"$value","type":"string"}]}""");
        var payload = new PayloadBuilder(schema, 1)
            .Block(0, 1, (byte)'x')
            .Block(0, 0x04)
            .Block(0, PayloadBuilder.Cat(PayloadBuilder.UV(2), PayloadBuilder.UV(2), new byte[] { 0, 4, 1, 1, (byte)'p', (byte)'q' }))
            .ToArray();
        var row = CcfDecoder.Decode<List<Subset>>(payload, schema).Single();
        Assert.Equal("x", row.S);
        Assert.Equal("kept", row.Extra);
        Assert.Equal(7, row.WithInitializer);
    }

    public sealed class Cased { public string Name { get; set; } = ""; }

    [Fact]
    public void Names_match_exactly_then_case_insensitively()
    {
        var schema = S("""{"name":"NAME","type":"string"}""");
        var payload = new PayloadBuilder(schema, 1).Block(0, 1, (byte)'x').ToArray();
        Assert.Equal("x", CcfDecoder.Decode<List<Cased>>(payload, schema).Single().Name);
    }

    public sealed class Restricted
    {
        public string Settable { get; set; } = "";
        public string GetOnly { get; } = "fixed";
        public string PrivateSet { get; private set; } = "private";
        public string InitOnly { get; init; } = "";
    }

    [Fact]
    public void Only_public_setters_including_init_are_assigned()
    {
        var schema = S("""{"name":"settable","type":"string"},{"name":"getOnly","type":"string"},{"name":"privateSet","type":"string"},{"name":"initOnly","type":"string"}""");
        var payload = new PayloadBuilder(schema, 1)
            .Block(0, 1, (byte)'a').Block(0, 1, (byte)'b').Block(0, 1, (byte)'c').Block(0, 1, (byte)'d').ToArray();
        var row = CcfDecoder.Decode<List<Restricted>>(payload, schema).Single();
        Assert.Equal("a", row.Settable);
        Assert.Equal("fixed", row.GetOnly);
        Assert.Equal("private", row.PrivateSet);
        Assert.Equal("d", row.InitOnly);
    }

    public sealed record Positional(string S, int N);

    [Fact]
    public void Records_with_positional_constructors_decode()
    {
        var schema = S("""{"name":"s","type":"string"},{"name":"n","type":"int32"}""");
        var payload = new PayloadBuilder(schema, 1).Block(0, 1, (byte)'a').Block(0, 0x06).ToArray();
        Assert.Equal(new Positional("a", 3), CcfDecoder.Decode<List<Positional>>(payload, schema).Single());
    }

    public struct StructRow { public string S { get; set; } public int N { get; set; } }

    [Fact]
    public void Struct_rows_decode()
    {
        var schema = S("""{"name":"s","type":"string"},{"name":"n","type":"int32"}""");
        var payload = new PayloadBuilder(schema, 1).Block(0, 1, (byte)'a').Block(0, 0x06).ToArray();
        var row = CcfDecoder.Decode<StructRow[]>(payload, schema).Single();
        Assert.Equal("a", row.S);
        Assert.Equal(3, row.N);
    }

    // ---- value conversion ------------------------------------------------------------------------------

    public sealed class Narrow { public short Small { get; set; } public byte Tiny { get; set; } public long Wide { get; set; } public int? Maybe { get; set; } }

    [Fact]
    public void Integers_narrow_with_a_range_check_that_reports_ValueOutOfRange()
    {
        var schema = S("""{"name":"small","type":"int32"},{"name":"tiny","type":"uint32"},{"name":"wide","type":"uint32"},{"name":"maybe","type":"uint32","nullable":true}""");

        byte[] Payload(long small, ulong tiny) => new PayloadBuilder(schema, 1)
            .Block(0, PayloadBuilder.UV(CcfPrimitives.ZigZag(small)))
            .Block(0, PayloadBuilder.UV(tiny))
            .Block(0, PayloadBuilder.UV(uint.MaxValue))
            .Block(0, PayloadBuilder.Cat(new byte[] { 0 }))
            .ToArray();

        var ok = CcfDecoder.Decode<List<Narrow>>(Payload(short.MinValue, 255), schema).Single();
        Assert.Equal(short.MinValue, ok.Small);
        Assert.Equal(255, ok.Tiny);
        Assert.Equal(uint.MaxValue, ok.Wide);
        Assert.Null(ok.Maybe);

        Assert.Equal(CcfErrorCode.ValueOutOfRange, Assert.Throws<CcfFormatException>(() => CcfDecoder.Decode<List<Narrow>>(Payload(short.MaxValue + 1, 0), schema)).Code);
        Assert.Equal(CcfErrorCode.ValueOutOfRange, Assert.Throws<CcfFormatException>(() => CcfDecoder.Decode<List<Narrow>>(Payload(0, 256), schema)).Code);
    }

    public sealed class Dates { public DateTimeOffset At { get; set; } public DateTime? Later { get; set; } }

    [Fact]
    public void Datetimes_decode_to_DateTimeOffset_at_offset_zero_and_to_utc_DateTime()
    {
        var schema = S("""{"name":"at","type":"datetime"},{"name":"later","type":"datetime","nullable":true}""");
        var payload = new PayloadBuilder(schema, 1)
            .Block(0, PayloadBuilder.UV(CcfPrimitives.ZigZag(1_700_000_000_123)))
            .Block(0, PayloadBuilder.Cat(new byte[] { 1 }, PayloadBuilder.UV(CcfPrimitives.ZigZag(-1))))
            .ToArray();
        var row = CcfDecoder.Decode<List<Dates>>(payload, schema).Single();
        Assert.Equal(TimeSpan.Zero, row.At.Offset);
        Assert.Equal(1_700_000_000_123, row.At.ToUnixTimeMilliseconds());
        Assert.Equal(DateTimeKind.Utc, row.Later!.Value.Kind);
        Assert.Equal(new DateTime(1969, 12, 31, 23, 59, 59, 999, DateTimeKind.Utc), row.Later);
    }

    [JsonConverter(typeof(JsonStringEnumConverter))] public enum Known { A, B }

    public sealed class WithEnum { public Known K { get; set; } }

    public sealed class WithEnumName { public string K { get; set; } = ""; }

    [Fact]
    public void Enums_decode_to_clr_enums_or_to_their_names()
    {
        var schema = S("""{"name":"k","type":"enum","values":["B","A"]}""");
        var payload = new PayloadBuilder(schema, 2).Block(0, 0b10).ToArray(); // index 0 then 1
        Assert.Equal(new[] { Known.B, Known.A }, CcfDecoder.Decode<List<WithEnum>>(payload, schema).Select(x => x.K));
        Assert.Equal(new[] { "B", "A" }, CcfDecoder.Decode<List<WithEnumName>>(payload, schema).Select(x => x.K));
    }

    [Fact]
    public void Enum_member_unknown_to_the_clr_type_is_a_usage_error_not_a_format_error()
    {
        var schema = S("""{"name":"k","type":"enum","values":["A","Newer"]}""");
        var payload = new PayloadBuilder(schema, 1).Block(0, 0b1).ToArray();
        Assert.Throws<InvalidOperationException>(() => CcfDecoder.Decode<List<WithEnum>>(payload, schema));
        var fine = new PayloadBuilder(schema, 1).Block(0, 0b0).ToArray();
        Assert.Equal(Known.A, CcfDecoder.Decode<List<WithEnum>>(fine, schema).Single().K);
    }

    public sealed class Lists
    {
        public int[] Array { get; set; } = System.Array.Empty<int>();
        public List<string?> Names { get; set; } = new();
        public HashSet<int>? Set { get; set; }
        public IReadOnlyList<List<int>> Nested { get; set; } = new List<List<int>>();
        public List<Product> Products { get; set; } = new();
        public IEnumerable<int>? Absent { get; set; } = new[] { 1 };
    }

    [Fact]
    public void Lists_decode_into_arrays_sets_nested_lists_and_object_lists()
    {
        var schema = S("""
            {"name":"array","type":"list","fields":[{"name":"$value","type":"int32"}]},
            {"name":"names","type":"list","fields":[{"name":"$value","type":"string","nullable":true}]},
            {"name":"set","type":"list","fields":[{"name":"$value","type":"int32"}]},
            {"name":"nested","type":"list","fields":[{"name":"$value","type":"list","fields":[{"name":"$value","type":"int32"}]}]},
            {"name":"products","type":"list","fields":[{"name":"id","type":"int64"}]},
            {"name":"absent","type":"list","nullable":true,"fields":[{"name":"$value","type":"int32"}]}
            """);

        byte[] ListBlock(ulong[] counts, byte[] child)
        {
            var total = (ulong)counts.Sum(c => (long)c);
            return PayloadBuilder.Cat(PayloadBuilder.Cat(counts.Select(PayloadBuilder.UV).ToArray()), PayloadBuilder.UV(total), child);
        }

        byte[] Block(byte[] body) => PayloadBuilder.Cat(new byte[] { 0 }, PayloadBuilder.UV((ulong)body.Length), body);
        byte[] Ints(params long[] v) => Block(PayloadBuilder.Cat(v.Select(x => PayloadBuilder.UV(CcfPrimitives.ZigZag(x))).ToArray()));

        var payload = new PayloadBuilder(schema, 1)
            .Block(0, ListBlock(new ulong[] { 3 }, Ints(1, 2, 3)))
            // names: two elements, second is null → bitmap 0b01, one string "n"
            .Block(0, ListBlock(new ulong[] { 2 }, Block(PayloadBuilder.Cat(new byte[] { 0b01 }, new byte[] { 1 }, "n"u8.ToArray()))))
            .Block(0, ListBlock(new ulong[] { 3 }, Ints(5, 5, 6)))
            // nested: [[7],[8,9]]: outer count 2, inner counts 1,2 then values
            .Block(0, ListBlock(new ulong[] { 2 }, Block(ListBlock(new ulong[] { 1, 2 }, Ints(7, 8, 9)))))
            .Block(0, ListBlock(new ulong[] { 2 }, Ints(100, 1)))
            // absent: nullable list that is null in the only row → bitmap 0, total 0, child column block (mode 0, length 0)
            .Block(0, 0, 0, 0, 0)
            .ToArray();

        var row = CcfDecoder.Decode<List<Lists>>(payload, schema).Single();
        Assert.Equal(new[] { 1, 2, 3 }, row.Array);
        Assert.Equal(new string?[] { "n", null }, row.Names);
        Assert.Equal(new[] { 5, 6 }, row.Set!.OrderBy(x => x));
        Assert.Equal(new[] { 7 }, row.Nested[0]);
        Assert.Equal(new[] { 8, 9 }, row.Nested[1]);
        Assert.Equal(new long[] { 100, 1 }, row.Products.Select(p => p.Id));
        Assert.Null(row.Absent);
    }

    // ---- usage errors ----------------------------------------------------------------------------------

    public sealed class WrongType { public int S { get; set; } }

    public abstract class AbstractRow { public string S { get; set; } = ""; }

    [Fact]
    public void Incompatible_or_abstract_targets_are_usage_errors_not_format_errors()
    {
        var (schema, payload) = TwoStrings();
        Assert.Throws<InvalidOperationException>(() => CcfDecoder.Decode<List<WrongType>>(payload, schema));
        Assert.Throws<InvalidOperationException>(() => CcfDecoder.Decode<List<AbstractRow>>(payload, schema));
        Assert.Throws<InvalidOperationException>(() => CcfDecoder.Decode<List<IDisposable>>(payload, schema));
    }

    [Fact]
    public void Typed_decoding_is_thread_safe_and_reuses_compiled_accessors()
    {
        var v = VectorManifest.Get("v02-products-dict");
        var schema = CcfSchema.Parse(v.ReadSchemaText());
        var payload = v.ReadPayload();
        var results = new string[16];
        Parallel.For(0, results.Length, i => results[i] = Json(CcfDecoder.Decode<List<Product>>(payload, schema)));
        Assert.Single(results.Distinct());
    }
}
