using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Ccf.Core.Tests;

/// <summary>
/// Enums mirror System.Text.Json: an enum that serializes as a string becomes a CCF enum column (names as STJ
/// writes them); any other enum becomes an integer column with the underlying value.
/// </summary>
public class EnumMirrorTests
{
    private enum Plain { Zebra = 5, Apple = 1, Mango = 3 }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum OnType { Zebra = 5, Apple = 1 }

    private enum Bare { Red, Green }

    private enum Small : byte { A = 1, B = 200 }

    private enum Big : long { A = 1, B = 5_000_000_000 }

    private enum Huge : ulong { A = 1, B = 18_000_000_000_000_000_000 }

    [Flags] private enum Perm { None = 0, Read = 1, Write = 2 }

    private sealed class Numeric { public Plain P { get; set; } public Plain? Q { get; set; } }

    private sealed class WithTypeAttr { public OnType V { get; set; } }

    private sealed class WithPropertyAttr
    {
        [JsonConverter(typeof(JsonStringEnumConverter))] public Plain P { get; set; }
        public Bare B { get; set; }
    }

    private sealed class Widths { public Small S { get; set; } public Big L { get; set; } public Huge U { get; set; } public Perm F { get; set; } }

    private sealed class WithBare { public Bare B { get; set; } public Bare? N { get; set; } }

    private sealed class WithCustom { [JsonConverter(typeof(NumberAsStringEnumConverter))] public Bare B { get; set; } }

    private sealed class NumberAsStringEnumConverter : JsonConverter<Bare>
    {
        public override Bare Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => Bare.Red;

        public override void Write(Utf8JsonWriter writer, Bare value, JsonSerializerOptions options) => writer.WriteStringValue("x" + (int)value);
    }

    private static readonly JsonSerializerOptions Global = new() { Converters = { new JsonStringEnumConverter() } };

    private static CcfSchema Build<T>(JsonSerializerOptions? json = null)
    {
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(List<T>), new CcfSchemaOptions { JsonSerializerOptions = json }, out var schema, out var reason), reason);
        return schema!;
    }

    private static string Fields(CcfSchema s) => s.CanonicalJson[(s.CanonicalJson.IndexOf("\"fields\":", StringComparison.Ordinal) + 9)..^1];

    // ---- numeric: no string converter applies --------------------------------------------------------------

    [Fact]
    public void Enums_without_a_string_converter_are_int32_columns_with_the_underlying_value() =>
        Assert.Equal("""[{"name":"p","type":"int32"},{"name":"q","type":"int32","nullable":true}]""", Fields(Build<Numeric>()));

    [Fact]
    public void Numeric_enums_use_the_integer_type_of_their_underlying_type() =>
        Assert.Equal(
            """[{"name":"s","type":"uint32"},{"name":"l","type":"int64"},{"name":"u","type":"uint64"},{"name":"f","type":"int32"}]""",
            Fields(Build<Widths>()));

    [Fact]
    public void Numeric_enum_values_match_the_json_numbers_and_round_trip()
    {
        var rows = new List<Numeric> { new() { P = Plain.Zebra, Q = null }, new() { P = Plain.Apple, Q = Plain.Mango }, new() { P = (Plain)42, Q = Plain.Zebra } };
        var schema = Build<Numeric>();

        // The CCF value is the number System.Text.Json writes.
        var json = JsonSerializer.Serialize(rows, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var viaJson = JsonSerializer.Deserialize<List<Dictionary<string, int?>>>(json)!;
        var buffer = new ArrayBufferWriter<byte>();
        new CcfEncoder().Encode(rows, schema, CcfFeatures.None, buffer);
        var untyped = (object?[])CcfDecoder.DecodeValue(buffer.WrittenSpan, schema);
        for (var i = 0; i < rows.Count; i++)
        {
            var row = (CcfObject)untyped[i]!;
            Assert.Equal(viaJson[i]["p"], Convert.ToInt32(row["p"]));
            Assert.Equal(viaJson[i]["q"], row["q"] is null ? null : Convert.ToInt32(row["q"]));
        }

        // And decodes back into the enum, including a value that has no member.
        var back = CcfDecoder.Decode<List<Numeric>>(buffer.WrittenSpan, schema);
        Assert.Equal(new[] { Plain.Zebra, Plain.Apple, (Plain)42 }, back.Select(r => r.P));
        Assert.Equal(new Plain?[] { null, Plain.Mango, Plain.Zebra }, back.Select(r => r.Q));
    }

    [Fact]
    public void Wide_numeric_enums_round_trip()
    {
        var rows = new List<Widths> { new() { S = Small.B, L = Big.B, U = Huge.B, F = Perm.Read | Perm.Write } };
        var schema = Build<Widths>();
        var buffer = new ArrayBufferWriter<byte>();
        new CcfEncoder().Encode(rows, schema, CcfFeatures.None, buffer);
        var back = CcfDecoder.Decode<List<Widths>>(buffer.WrittenSpan, schema).Single();
        Assert.Equal((Small.B, Big.B, Huge.B, Perm.Read | Perm.Write), (back.S, back.L, back.U, back.F));
    }

    [Fact]
    public void A_numeric_value_outside_the_enums_underlying_range_is_a_value_out_of_range_error()
    {
        // An int64 column cannot be decoded into a byte-backed enum when the value does not fit.
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(List<BigRow>), null, out var bigSchema, out _));
        var buffer = new ArrayBufferWriter<byte>();
        new CcfEncoder().Encode(new List<BigRow> { new() { S = 300 } }, bigSchema!, CcfFeatures.None, buffer);
        var e = Assert.Throws<CcfFormatException>(() => CcfDecoder.Decode<List<SmallRow>>(buffer.WrittenSpan, bigSchema!));
        Assert.Equal(CcfErrorCode.ValueOutOfRange, e.Code);
    }

    private sealed class BigRow { public int S { get; set; } }

    private sealed class SmallRow { public Small S { get; set; } }

    // ---- strings: a string-enum converter applies ---------------------------------------------------------------

    [Fact]
    public void A_global_string_enum_converter_makes_enum_columns_with_declaration_order_names() =>
        Assert.Equal("""[{"name":"p","type":"enum","values":["Zebra","Apple","Mango"]},{"name":"q","type":"enum","nullable":true,"values":["Zebra","Apple","Mango"]}]""",
            Fields(Build<Numeric>(Global)));

    [Fact]
    public void A_converter_on_the_enum_type_makes_an_enum_column() =>
        Assert.Equal("""[{"name":"v","type":"enum","values":["Zebra","Apple"]}]""", Fields(Build<WithTypeAttr>()));

    [Fact]
    public void A_converter_on_the_property_applies_to_that_property_only() =>
        Assert.Equal("""[{"name":"p","type":"enum","values":["Zebra","Apple","Mango"]},{"name":"b","type":"int32"}]""", Fields(Build<WithPropertyAttr>()));

    [Fact]
    public void The_names_are_the_ones_the_converter_writes_including_a_naming_policy()
    {
        var camel = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        Assert.Equal("""[{"name":"p","type":"enum","values":["zebra","apple","mango"]},{"name":"q","type":"enum","nullable":true,"values":["zebra","apple","mango"]}]""",
            Fields(Build<Numeric>(camel)));
    }

    [Fact]
    public void String_enum_columns_encode_and_decode_through_the_same_names()
    {
        var camel = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
        var schema = Build<Numeric>(camel);
        var options = new CcfSchemaOptions { JsonSerializerOptions = camel };
        var rows = new List<Numeric> { new() { P = Plain.Mango, Q = Plain.Apple }, new() { P = Plain.Zebra, Q = null } };
        var buffer = new ArrayBufferWriter<byte>();
        new CcfEncoder(options).Encode(rows, schema, CcfFeatures.None, buffer);

        Assert.Equal("mango", ((CcfObject)((object?[])CcfDecoder.DecodeValue(buffer.WrittenSpan, schema))[0]!)["p"]);
        var back = CcfDecoder.Decode<List<Numeric>>(buffer.WrittenSpan, schema, options);
        Assert.Equal(new[] { Plain.Mango, Plain.Zebra }, back.Select(r => r.P));
        Assert.Equal(new Plain?[] { Plain.Apple, null }, back.Select(r => r.Q));
    }

    [Fact]
    public void Encoding_a_string_enum_column_with_options_that_do_not_write_strings_fails_cleanly()
    {
        var schema = Build<Numeric>(Global);
        var e = Assert.Throws<CcfEncodeException>(() => new CcfEncoder().Encode(new List<Numeric> { new() }, schema, CcfFeatures.None, new ArrayBufferWriter<byte>()));
        Assert.Contains("not written as strings", e.Message);
    }

    // ---- unsupported -------------------------------------------------------------------------------------

    [Fact]
    public void A_custom_converter_on_an_enum_is_unsupported()
    {
        Assert.False(CcfSchemaBuilder.TryBuild(typeof(List<WithCustom>), null, out _, out var reason));
        Assert.Contains("custom JsonConverter", reason);
    }

    [Fact]
    public void A_flags_enum_is_a_number_unless_it_is_written_as_strings()
    {
        Assert.Equal("""[{"name":"f","type":"int32"}]""", Fields(Build<FlagsOnly>()));
        Assert.False(CcfSchemaBuilder.TryBuild(typeof(List<FlagsOnly>), new CcfSchemaOptions { JsonSerializerOptions = Global }, out _, out var reason));
        Assert.Contains("Flags", reason);
    }

    private sealed class FlagsOnly { public Perm F { get; set; } }

    [Fact]
    public void Bare_and_nullable_enums_without_options_are_numeric()
    {
        Assert.Equal("""[{"name":"b","type":"int32"},{"name":"n","type":"int32","nullable":true}]""", Fields(Build<WithBare>()));
    }
}
