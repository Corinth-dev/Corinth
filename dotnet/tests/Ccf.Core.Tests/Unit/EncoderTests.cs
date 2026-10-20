using System.Buffers;
using System.Text.Json.Serialization;
using Xunit;

namespace Ccf.Core.Tests;

public class EncoderTests
{
    public sealed class Str { public string? S { get; set; } }

    public sealed class StrNotNull { public string S { get; set; } = ""; }

    private static CcfSchema SchemaOf<T>(CcfSchemaOptions? options = null)
    {
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(T), options, out var schema, out var reason), reason);
        return schema!;
    }

    private static (byte[] Bytes, CcfPayloadFlags Flags) Encode(
        object value, CcfSchema schema, CcfFeatures features = CcfFeatures.None, CcfLimits? limits = null, CcfEncoder? encoder = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var flags = (encoder ?? new CcfEncoder()).Encode(value, schema, features, buffer, limits);
        return (buffer.WrittenSpan.ToArray(), flags);
    }

    private static List<Str> Strings(params string?[] values) => values.Select(v => new Str { S = v }).ToList();

    /// <summary>Mode byte of the single root column (header is 8 bytes + a 1-byte row count).</summary>
    private static byte Mode(byte[] payload) => payload[9];

    // ---- dictionary rule (SPEC §8.1) -------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "a", "a", "b", "b" }, false)]                    // p=4 k=2: 2k = 4, not < 4
    [InlineData(new[] { "a", "a", "a", "b" }, false)]                    // p=4 k=2
    [InlineData(new[] { "a", "a", "a", "a" }, true)]                     // p=4 k=1: 2 < 4
    [InlineData(new[] { "a", "a", "a", "b", "b" }, true)]                // p=5 k=2: 4 < 5
    [InlineData(new[] { "a", "b", "c" }, false)]                         // all distinct
    [InlineData(new[] { "a" }, false)]                                   // p=1 k=1: 2 < 1 false
    [InlineData(new[] { "a", "a" }, false)]                              // p=2 k=1: 2 < 2 false
    [InlineData(new[] { "a", "a", "a" }, true)]                          // p=3 k=1: 2 < 3
    public void Dictionary_is_used_if_and_only_if_2k_is_less_than_p(string[] values, bool expectDictionary)
    {
        var (bytes, flags) = Encode(Strings(values), SchemaOf<List<Str>>(), CcfFeatures.Dictionary);
        Assert.Equal(expectDictionary ? (byte)1 : (byte)0, Mode(bytes));
        Assert.Equal(expectDictionary, (flags & CcfPayloadFlags.Dictionary) != 0);
        Assert.Equal(expectDictionary ? 1 : 0, bytes[3]);
    }

    [Fact]
    public void Dictionary_is_never_used_unless_the_feature_was_requested()
    {
        var (bytes, flags) = Encode(Strings("a", "a", "a", "a", "a", "a"), SchemaOf<List<Str>>(), CcfFeatures.None);
        Assert.Equal(0, Mode(bytes));
        Assert.Equal(CcfPayloadFlags.None, flags);
    }

    [Fact]
    public void Dictionary_entries_are_in_order_of_first_appearance_and_indexes_follow()
    {
        var (bytes, _) = Encode(Strings("z", "a", "z", "z", "a", "m", "z", "z"), SchemaOf<List<Str>>(), CcfFeatures.Dictionary);
        // header(8) rows(1) mode(1) len(1) | presence bitmap (column is nullable, 8 rows → 0xFF),
        // k=3, "z","a","m", indexes 0 1 0 0 1 2 0 0
        var body = bytes[11..];
        Assert.Equal(
            new byte[] { 0xFF, 3, 1, (byte)'z', 1, (byte)'a', 1, (byte)'m', 0, 1, 0, 0, 1, 2, 0, 0 },
            body);
    }

    [Fact]
    public void Nulls_do_not_count_toward_p_or_k()
    {
        // present values: a, a, a → p=3, k=1 → dictionary, even though 4 rows
        var schema = SchemaOf<List<Str>>();
        var (bytes, flags) = Encode(Strings("a", null, "a", "a"), schema, CcfFeatures.Dictionary);
        Assert.True((flags & CcfPayloadFlags.Dictionary) != 0);
        // bitmap 0b1101, k=1, "a", indexes 0 0 0
        Assert.Equal(new byte[] { 0b1101, 1, 1, (byte)'a', 0, 0, 0 }, bytes[11..]);
        var rows = (object?[])CcfDecoder.DecodeValue(bytes, schema);
        Assert.Equal(new object?[] { "a", null, "a", "a" }, rows.Select(r => ((CcfObject)r!)["s"]).ToArray());
    }

    [Fact]
    public void All_null_and_empty_columns_are_plain_even_with_the_feature()
    {
        var schema = SchemaOf<List<Str>>();
        Assert.Equal(0, Mode(Encode(Strings(null, null, null), schema, CcfFeatures.Dictionary).Bytes));
        Assert.Equal(0, Mode(Encode(new List<Str>(), schema, CcfFeatures.Dictionary).Bytes));
    }

    [Fact]
    public void More_than_256_entries_use_two_byte_little_endian_indexes_and_round_trip()
    {
        var values = Enumerable.Range(0, 1000).Select(i => "v" + (i % 300)).ToArray();
        var schema = SchemaOf<List<Str>>();
        var (bytes, flags) = Encode(Strings(values), schema, CcfFeatures.Dictionary);
        Assert.True((flags & CcfPayloadFlags.Dictionary) != 0);
        var rows = (object?[])CcfDecoder.DecodeValue(bytes, schema);
        Assert.Equal(values, rows.Select(r => (string)((CcfObject)r!)["s"]!).ToArray());
        // 300 entries → indexes are 2 bytes each: the tail of the payload is 1000 × 2 bytes of indexes.
        var indexes = bytes[^2000..];
        Assert.Equal(299 % 256, indexes[2 * 299]);     // index 299 = 0x012B, little-endian: low byte first
        Assert.Equal(299 / 256, indexes[2 * 299 + 1]);
    }

    [Fact]
    public void Exactly_65536_entries_still_use_a_dictionary_and_65537_fall_back_to_plain()
    {
        var limits = new CcfLimits { MaxRows = 200_000 };
        var schema = SchemaOf<List<Str>>();

        var atCap = Enumerable.Range(0, 140_000).Select(i => "k" + (i % 65_536)).ToArray();
        var (bytes, flags) = Encode(Strings(atCap), schema, CcfFeatures.Dictionary, limits);
        Assert.Equal(1, RootModeAt(bytes, 3));
        Assert.True((flags & CcfPayloadFlags.Dictionary) != 0);
        var rows = (object?[])CcfDecoder.DecodeValue(bytes, schema, limits);
        Assert.Equal(atCap[^1], ((CcfObject)rows[^1]!)["s"]);

        var overCap = Enumerable.Range(0, 140_000).Select(i => "k" + (i % 65_537)).ToArray(); // 2k = 131,074 < 140,000 would qualify
        (bytes, flags) = Encode(Strings(overCap), schema, CcfFeatures.Dictionary, limits);
        Assert.Equal(CcfPayloadFlags.None, flags);
        Assert.Equal(0, RootModeAt(bytes, 3));
    }

    /// <summary>Mode byte of the root column when the row count uvarint has <paramref name="rowCountBytes"/> bytes.</summary>
    private static byte RootModeAt(byte[] payload, int rowCountBytes) => payload[8 + rowCountBytes];

    [Fact]
    public void The_threshold_is_configurable_but_defaults_to_one_half()
    {
        var schema = SchemaOf<List<Str>>();
        var data = Strings("a", "a", "b", "b"); // k/p = 0.5 exactly
        Assert.Equal(0, Mode(Encode(data, schema, CcfFeatures.Dictionary).Bytes));
        Assert.Equal(1, Mode(Encode(data, schema, CcfFeatures.Dictionary, encoder: new CcfEncoder(dictionaryThreshold: 0.75)).Bytes));
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        var schema = SchemaOf<List<Str>>();
        var data = Strings("x", "y", "x", null, "x", "y");
        Assert.Equal(Encode(data, schema, CcfFeatures.Dictionary).Bytes, Encode(data, schema, CcfFeatures.Dictionary).Bytes);
        Assert.Equal(Encode(data, schema).Bytes, Encode(data, schema).Bytes);
    }

    // ---- single object and root shapes -----------------------------------------------------------------

    [Fact]
    public void Single_object_sets_flag_S_and_one_row()
    {
        var schema = SchemaOf<Str>();
        var (bytes, flags) = Encode(new Str { S = "hi" }, schema);
        Assert.Equal(CcfPayloadFlags.SingleObject, flags);
        Assert.Equal(2, bytes[3]);
        Assert.Equal(1, bytes[8]);
        Assert.IsType<CcfObject>(CcfDecoder.DecodeValue(bytes, schema));
    }

    [Fact]
    public void Roots_may_be_arrays_lists_lazy_enumerables_and_sets()
    {
        var schema = SchemaOf<List<StrNotNull>>();
        var items = new[] { new StrNotNull { S = "a" }, new StrNotNull { S = "b" } };
        var expected = Encode(items, schema).Bytes;
        Assert.Equal(expected, Encode(items.ToList(), schema).Bytes);
        Assert.Equal(expected, Encode(items.Select(x => x), schema).Bytes);
        Assert.Equal(expected, Encode(Lazy(items), schema).Bytes);
        Assert.Equal(expected, Encode(new LinkedList<StrNotNull>(items), schema).Bytes);
        Assert.Equal(expected, Encode((IReadOnlyList<StrNotNull>)items, schema).Bytes);

        static IEnumerable<StrNotNull> Lazy(IEnumerable<StrNotNull> source)
        {
            foreach (var x in source)
            {
                yield return x;
            }
        }
    }

    public struct StructRow { public int A { get; set; } public string? B { get; set; } }

    [Fact]
    public void Struct_rows_encode_and_decode()
    {
        var schema = SchemaOf<StructRow[]>();
        var rows = new[] { new StructRow { A = 1, B = "x" }, new StructRow { A = -2, B = null } };
        var bytes = Encode(rows, schema).Bytes;
        var back = CcfDecoder.Decode<StructRow[]>(bytes, schema);
        Assert.Equal(rows, back);
    }

    // ---- failures (SPEC §10.2) -------------------------------------------------------------------------

    private static CcfEncodeException Fails(object? value, CcfSchema schema, CcfLimits? limits = null, CcfFeatures features = CcfFeatures.None)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var ex = Assert.Throws<CcfEncodeException>(() => new CcfEncoder().Encode(value!, schema, features, buffer, limits));
        Assert.Equal(0, buffer.WrittenCount); // nothing is written when encoding fails
        return ex;
    }

    public sealed class Dec2 { [CcfDecimal(2)] public decimal D { get; set; } }

    public sealed class Dec2U { [CcfDecimal(2, Unsigned = true)] public decimal D { get; set; } }

    public sealed class Delta64 { [CcfDelta] public long V { get; set; } }

    public sealed class Delta64U { [CcfDelta] public ulong V { get; set; } }

    public sealed class Child { public int X { get; set; } }

    public sealed class Parent
    {
        public Child Required { get; set; } = new();
        public List<int> Numbers { get; set; } = new();
        public List<Child> Children { get; set; } = new();
    }

    public sealed class Dbl { public double D { get; set; } }

    [Fact]
    public void Null_value_and_unencodable_roots_fail()
    {
        var schema = SchemaOf<List<Str>>();
        Fails(null, schema);
        Fails(42, schema);
        Fails("text", schema);
    }

    [Fact]
    public void Null_in_a_non_nullable_field_fails()
    {
        var schema = SchemaOf<List<StrNotNull>>();
        var ex = Fails(new List<StrNotNull> { new() { S = null! } }, schema);
        Assert.Contains("'s'", ex.Message);

        var parentSchema = SchemaOf<List<Parent>>();
        Fails(new List<Parent> { new() { Required = null! } }, parentSchema);
        Fails(new List<Parent> { new() { Numbers = null! } }, parentSchema);
    }

    [Fact]
    public void Null_element_inside_a_list_of_objects_fails_with_an_encode_error_not_a_null_reference()
    {
        var schema = SchemaOf<List<Parent>>();
        Fails(new List<Parent> { new() { Children = new List<Child> { new(), null! } } }, schema);
        Fails(new List<Parent?> { new(), null }, schema);
    }

    [Fact]
    public void Decimal_with_more_digits_than_the_scale_fails_but_trailing_zeros_are_fine()
    {
        var schema = SchemaOf<List<Dec2>>();
        Fails(new List<Dec2> { new() { D = 1.234m } }, schema);
        Fails(new List<Dec2> { new() { D = 0.001m } }, schema);
        var bytes = Encode(new List<Dec2> { new() { D = 1.5000m } }, schema).Bytes;
        Assert.Equal(1.50m, ((CcfObject)((object?[])CcfDecoder.DecodeValue(bytes, schema))[0]!)["d"]);
    }

    [Fact]
    public void Decimal_overflowing_int64_after_scaling_fails()
    {
        var schema = SchemaOf<List<Dec2>>();
        Fails(new List<Dec2> { new() { D = 92_233_720_368_547_759m } }, schema);   // × 100 > int64
        Fails(new List<Dec2> { new() { D = decimal.MaxValue } }, schema);
        var ok = new List<Dec2> { new() { D = 92_233_720_368_547_758.07m }, new() { D = -92_233_720_368_547_758.08m } };
        var rows = (object?[])CcfDecoder.DecodeValue(Encode(ok, schema).Bytes, schema);
        Assert.Equal(ok[1].D, ((CcfObject)rows[1]!)["d"]);
    }

    [Fact]
    public void Unsigned_decimal_must_not_be_negative()
    {
        var schema = SchemaOf<List<Dec2U>>();
        Fails(new List<Dec2U> { new() { D = -0.01m } }, schema);
        Assert.NotEmpty(Encode(new List<Dec2U> { new() { D = 0m } }, schema).Bytes);
    }

    [Fact]
    public void Delta_difference_overflowing_int64_fails()
    {
        Fails(new List<Delta64> { new() { V = long.MaxValue }, new() { V = long.MinValue } }, SchemaOf<List<Delta64>>());
        Fails(new List<Delta64> { new() { V = long.MinValue }, new() { V = 1 } }, SchemaOf<List<Delta64>>());
        // a difference of exactly long.MinValue fits
        Assert.NotEmpty(Encode(new List<Delta64> { new() { V = long.MaxValue }, new() { V = -1 } }, SchemaOf<List<Delta64>>()).Bytes);
        Fails(new List<Delta64U> { new() { V = 0 }, new() { V = ulong.MaxValue } }, SchemaOf<List<Delta64U>>());
        Fails(new List<Delta64U> { new() { V = ulong.MaxValue }, new() { V = 0 } }, SchemaOf<List<Delta64U>>());
        // The first difference is taken from 0, so a first value of 2^63 does not fit; 2^62 and back does.
        Fails(new List<Delta64U> { new() { V = 1UL << 63 } }, SchemaOf<List<Delta64U>>());
        Assert.NotEmpty(Encode(new List<Delta64U> { new() { V = 1UL << 62 }, new() { V = 0 } }, SchemaOf<List<Delta64U>>()).Bytes);
    }

    [Fact]
    public void Float_nan_and_infinities_fail_and_negative_zero_is_preserved()
    {
        var schema = SchemaOf<List<Dbl>>();
        Fails(new List<Dbl> { new() { D = double.NaN } }, schema);
        Fails(new List<Dbl> { new() { D = double.PositiveInfinity } }, schema);
        Fails(new List<Dbl> { new() { D = double.NegativeInfinity } }, schema);

        var bytes = Encode(new List<Dbl> { new() { D = -0.0 }, new() { D = 0.0 } }, schema).Bytes;
        Assert.Equal(BitConverter.GetBytes(BitConverter.DoubleToInt64Bits(-0.0)), bytes[^16..^8]);
        var rows = (object?[])CcfDecoder.DecodeValue(bytes, schema);
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits((double)((CcfObject)rows[0]!)["d"]!));
        Assert.Equal(BitConverter.DoubleToInt64Bits(0.0), BitConverter.DoubleToInt64Bits((double)((CcfObject)rows[1]!)["d"]!));
    }

    [Fact]
    public void Float_values_from_single_precision_properties_are_widened_exactly()
    {
        var schema = SchemaOf<List<FloatRow>>();
        var bytes = Encode(new List<FloatRow> { new() { F = 0.1f } }, schema).Bytes;
        Assert.Equal((double)0.1f, (double)((CcfObject)((object?[])CcfDecoder.DecodeValue(bytes, schema))[0]!)["f"]!);
        Fails(new List<FloatRow> { new() { F = float.NaN } }, schema);
    }

    public sealed class FloatRow { public float F { get; set; } }

    [Fact]
    public void Lone_surrogates_fail_to_encode()
    {
        Fails(Strings("ok", "bad\uD800"), SchemaOf<List<Str>>());
        Fails(Strings("bad\uDC00"), SchemaOf<List<Str>>(), features: CcfFeatures.Dictionary);
        Assert.NotEmpty(Encode(Strings("😀 😀"), SchemaOf<List<Str>>()).Bytes);
    }

    // ---- limits (SPEC §9, §14.6) -----------------------------------------------------------------------

    [Fact]
    public void Root_rows_above_the_limit_fail()
    {
        var schema = SchemaOf<List<Child>>();
        var limits = new CcfLimits { MaxRows = 3 };
        Assert.NotEmpty(Encode(Enumerable.Range(0, 3).Select(i => new Child { X = i }).ToList(), schema, limits: limits).Bytes);
        Fails(Enumerable.Range(0, 4).Select(i => new Child { X = i }).ToList(), schema, limits);
        Fails(Enumerable.Range(0, 4).Select(i => new Child { X = i }), schema, limits); // lazy source too
    }

    [Fact]
    public void Row_limit_applies_to_list_child_tables_separately()
    {
        var schema = SchemaOf<List<Parent>>();
        var limits = new CcfLimits { MaxRows = 3 };
        // 2 root rows, each with 2 numbers → child table of 4 rows
        var data = new List<Parent> { new() { Numbers = { 1, 2 } }, new() { Numbers = { 3, 4 } } };
        Fails(data, schema, limits);
        data[1].Numbers.RemoveAt(1); // 3 child rows
        Assert.NotEmpty(Encode(data, schema, limits: limits).Bytes);
    }

    [Fact]
    public void String_longer_than_the_string_limit_fails()
    {
        var limits = new CcfLimits { MaxStringBytes = 4 };
        Assert.NotEmpty(Encode(Strings("abcd"), SchemaOf<List<Str>>(), limits: limits).Bytes);
        Fails(Strings("abcde"), SchemaOf<List<Str>>(), limits);
        Fails(Strings("é€€"), SchemaOf<List<Str>>(), limits); // 7 bytes
        Fails(Strings("aaaaa", "aaaaa", "aaaaa"), SchemaOf<List<Str>>(), limits, CcfFeatures.Dictionary); // dictionary entry too
    }

    [Fact]
    public void Payload_larger_than_the_payload_limit_fails_without_writing()
    {
        var schema = SchemaOf<List<Str>>();
        var data = Strings(Enumerable.Range(0, 100).Select(i => "value-" + i).ToArray());
        var size = Encode(data, schema).Bytes.Length;
        Assert.NotEmpty(Encode(data, schema, limits: new CcfLimits { MaxPayloadBytes = size }).Bytes);
        Fails(data, schema, new CcfLimits { MaxPayloadBytes = size - 1 });
        Fails(Strings(new string('x', 100_000)), schema, new CcfLimits { MaxPayloadBytes = 1000 });
    }

    [Fact]
    public void Schema_deeper_than_8_fails_to_encode()
    {
        var deep = CcfSchema.Parse(DecoderTests.NestObjects(9));
        Fails(new Child(), deep);
    }

    // ---- binding mismatches ----------------------------------------------------------------------------

    public sealed class Ints { public long Big { get; set; } public int Signed { get; set; } public ulong Huge { get; set; } }

    [Fact]
    public void Values_outside_the_range_of_the_schema_type_fail()
    {
        var narrow = CcfSchema.Parse("""{"name":"Ints","fields":[{"name":"big","type":"int32"},{"name":"signed","type":"uint32"},{"name":"huge","type":"int64"}]}""");
        Fails(new Ints { Big = int.MaxValue + 1L }, narrow);
        Fails(new Ints { Signed = -1 }, narrow);
        Fails(new Ints { Huge = (ulong)long.MaxValue + 1 }, narrow);
        Assert.NotEmpty(Encode(new Ints { Big = int.MinValue, Signed = 5, Huge = long.MaxValue }, narrow).Bytes);

        var unsigned = CcfSchema.Parse("""{"name":"Ints","fields":[{"name":"big","type":"uint64"},{"name":"signed","type":"uint64"},{"name":"huge","type":"uint64"}]}""");
        Fails(new Ints { Big = -1 }, unsigned);
        Fails(new Ints { Signed = -1 }, unsigned);
        Assert.NotEmpty(Encode(new Ints { Huge = ulong.MaxValue }, unsigned).Bytes);
    }

    [Fact]
    public void Schema_that_does_not_fit_the_type_fails_with_an_encode_error()
    {
        Fails(new Child(), CcfSchema.Parse("""{"name":"Child","fields":[{"name":"missing","type":"int32"}]}"""));
        Fails(new Child(), CcfSchema.Parse("""{"name":"Child","fields":[{"name":"x","type":"string"}]}"""));
        Fails(new Child(), CcfSchema.Parse("""{"name":"Child","fields":[{"name":"x","type":"list","fields":[{"name":"$value","type":"int32"}]}]}"""));
    }

    [JsonConverter(typeof(JsonStringEnumConverter))] public enum Mood { Happy, Sad, Unknown }

    public sealed class MoodRow { public Mood M { get; set; } }

    [Fact]
    public void Enum_member_missing_from_the_schema_fails_only_when_such_a_value_occurs()
    {
        var schema = CcfSchema.Parse("""{"name":"MoodRow","fields":[{"name":"m","type":"enum","values":["Happy","Sad"]}]}""");
        Assert.NotEmpty(Encode(new List<MoodRow> { new() { M = Mood.Sad } }, schema).Bytes);
        Fails(new List<MoodRow> { new() { M = Mood.Unknown } }, schema);
        Fails(new List<MoodRow> { new() { M = (Mood)99 } }, schema);
    }

    [Fact]
    public void Enum_indexes_follow_the_schema_order_not_the_clr_order()
    {
        var schema = CcfSchema.Parse("""{"name":"MoodRow","fields":[{"name":"m","type":"enum","values":["Sad","Happy"]}]}""");
        var bytes = Encode(new List<MoodRow> { new() { M = Mood.Happy }, new() { M = Mood.Sad } }, schema).Bytes;
        Assert.Equal(0b01, bytes[^1]); // Happy → index 1, Sad → index 0, LSB first
    }

    // ---- datetime --------------------------------------------------------------------------------------

    public sealed class Dates { public DateTime A { get; set; } public DateTimeOffset B { get; set; } }

    [Fact]
    public void Datetimes_are_converted_to_utc_milliseconds()
    {
        var schema = SchemaOf<List<Dates>>();
        var local = new DateTime(2026, 1, 2, 3, 4, 5, 678, DateTimeKind.Local);
        var row = new Dates
        {
            A = local,
            B = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.FromHours(5.5)),
        };
        var rows = (object?[])CcfDecoder.DecodeValue(Encode(new List<Dates> { row }, schema).Bytes, schema);
        var o = (CcfObject)rows[0]!;
        Assert.Equal(local.ToUniversalTime(), (DateTime)o["a"]!);
        Assert.Equal(row.B.UtcDateTime, (DateTime)o["b"]!);
    }

    [Fact]
    public void Unspecified_kind_is_treated_as_utc_and_sub_millisecond_ticks_are_truncated()
    {
        var schema = SchemaOf<List<Dates>>();
        var unspecified = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Unspecified).AddTicks(9_999); // + 0.9999 ms
        var bytes = Encode(new List<Dates> { new() { A = unspecified, B = DateTimeOffset.UnixEpoch } }, schema).Bytes;
        var a = (DateTime)((CcfObject)((object?[])CcfDecoder.DecodeValue(bytes, schema))[0]!)["a"]!;
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), a);
    }

    // ---- enumeration ------------------------------------------------------------------------------------

    private sealed class Counting<T> : IEnumerable<T>
    {
        private readonly IEnumerable<T> _source;

        public Counting(IEnumerable<T> source)
        {
            _source = source;
        }

        public int Enumerations { get; private set; }

        public IEnumerator<T> GetEnumerator()
        {
            Enumerations++;
            return _source.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public sealed class WithLazyList
    {
        public int Id { get; set; }

        public IEnumerable<string> Items { get; set; } = Array.Empty<string>();
    }

    [Fact]
    public void A_lazy_root_and_lazy_list_properties_are_each_enumerated_exactly_once_however_many_columns_there_are()
    {
        var schema = SchemaOf<List<WithLazyList>>();
        var inner = new[] { new Counting<string>(new[] { "a", "b" }), new Counting<string>(new[] { "c" }) };
        var rows = new Counting<WithLazyList>(new[]
        {
            new WithLazyList { Id = 1, Items = inner[0] },
            new WithLazyList { Id = 2, Items = inner[1] },
        });

        var bytes = Encode(rows, schema, CcfFeatures.Dictionary).Bytes;

        Assert.Equal(1, rows.Enumerations);
        Assert.All(inner, c => Assert.Equal(1, c.Enumerations));
        Assert.Equal(2, ((object?[])CcfDecoder.DecodeValue(bytes, schema)).Length);
    }

    [Fact]
    public void A_failed_encode_still_enumerates_each_source_only_once()
    {
        var schema = SchemaOf<List<Dec2>>();
        var rows = new Counting<Dec2>(new[] { new Dec2 { D = 1.234m } });

        Fails(rows, schema);

        Assert.Equal(1, rows.Enumerations);
    }

    // ---- output ----------------------------------------------------------------------------------------

    [Fact]
    public void Content_type_follows_the_flags_actually_produced()
    {
        Assert.Equal("application/vnd.ccf", CcfFeatureParser.ContentTypeFor(CcfPayloadFlags.None));
        Assert.Equal("application/vnd.ccf", CcfFeatureParser.ContentTypeFor(CcfPayloadFlags.SingleObject));
        Assert.Equal("application/vnd.ccf; features=d", CcfFeatureParser.ContentTypeFor(CcfPayloadFlags.Dictionary));
        Assert.Equal("application/vnd.ccf; features=d", CcfFeatureParser.ContentTypeFor(CcfPayloadFlags.Dictionary | CcfPayloadFlags.SingleObject));
    }

    [Theory]
    [InlineData(null, CcfFeatures.None)]
    [InlineData("", CcfFeatures.None)]
    [InlineData("d", CcfFeatures.Dictionary)]
    [InlineData("\"d\"", CcfFeatures.Dictionary)]
    [InlineData("\"d,n\"", CcfFeatures.Dictionary)]
    [InlineData("n", CcfFeatures.None)]
    [InlineData("xyz", CcfFeatures.None)]
    public void Features_parameter_parsing_ignores_unknown_letters(string? value, CcfFeatures expected) =>
        Assert.Equal(expected, CcfFeatureParser.Parse(value));

    [Fact]
    public void Output_is_appended_to_the_buffer_writer_in_one_piece()
    {
        var schema = SchemaOf<List<Str>>();
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write(new byte[] { 9, 9 });
        new CcfEncoder().Encode(Strings("a"), schema, CcfFeatures.None, buffer);
        Assert.Equal(new byte[] { 9, 9, 0x43, 0x43 }, buffer.WrittenSpan[..4].ToArray());
    }
}
