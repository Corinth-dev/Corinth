using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Corinth.Core.Tests.Conformance;
using Xunit;

namespace Corinth.Core.Tests;

public class SchemaBuilderTests
{
    private static CcfSchema Build<T>(CcfSchemaOptions? options = null)
    {
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(T), options, out var schema, out var reason), reason);
        return schema!;
    }

    private static string Reason(Type type, CcfSchemaOptions? options = null)
    {
        Assert.False(CcfSchemaBuilder.TryBuild(type, options, out var schema, out var reason));
        Assert.Null(schema);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        return reason!;
    }

    private static string Fields(CcfSchema s) => s.CanonicalJson[(s.CanonicalJson.IndexOf("\"fields\":", StringComparison.Ordinal) + 9)..^1];

    // ---- the three vector schemas ----------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(Product), "schema-Product.json")]
    [InlineData(typeof(AllTypes), "schema-AllTypes.json")]
    [InlineData(typeof(Order), "schema-Order.json")]
    public void Vector_models_build_exactly_the_canonical_schema_files(Type model, string file)
    {
        Assert.True(CcfSchemaBuilder.TryBuild(model, null, out var schema, out var reason), reason);
        Assert.Equal(File.ReadAllText(VectorManifest.PathOf(file)), schema!.CanonicalJson);
    }

    [Theory]
    [InlineData(typeof(List<Product>))]
    [InlineData(typeof(Product[]))]
    [InlineData(typeof(IEnumerable<Product>))]
    [InlineData(typeof(IReadOnlyList<Product>))]
    [InlineData(typeof(IList<Product>))]
    [InlineData(typeof(ICollection<Product>))]
    [InlineData(typeof(Product))]
    public void Root_collection_or_single_object_gives_the_same_schema(Type root)
    {
        Assert.True(CcfSchemaBuilder.TryBuild(root, null, out var schema, out _));
        Assert.Equal("c33a3ed0", schema!.IdHex);
    }

    // ---- type mapping ----------------------------------------------------------------------------------

    private sealed class Scalars
    {
        public bool A { get; set; }
        public sbyte B { get; set; }
        public short C { get; set; }
        public int D { get; set; }
        public byte E { get; set; }
        public ushort F { get; set; }
        public uint G { get; set; }
        public long H { get; set; }
        public ulong I { get; set; }
        public float J { get; set; }
        public double K { get; set; }
        public decimal L { get; set; }
        public string M { get; set; } = "";
        public DateTime N { get; set; }
        public DateTimeOffset O { get; set; }
        public Guid P { get; set; }
    }

    [Fact]
    public void Scalar_mapping_follows_the_architecture_table()
    {
        var f = Fields(Build<Scalars>());
        Assert.Equal(
            """[{"name":"a","type":"bool"},{"name":"b","type":"int32"},{"name":"c","type":"int32"},{"name":"d","type":"int32"},{"name":"e","type":"uint32"},{"name":"f","type":"uint32"},{"name":"g","type":"uint32"},{"name":"h","type":"int64"},{"name":"i","type":"uint64"},{"name":"j","type":"float64"},{"name":"k","type":"float64"},{"name":"l","type":"decimal","scale":2},{"name":"m","type":"string","nullable":false},{"name":"n","type":"datetime"},{"name":"o","type":"datetime"},{"name":"p","type":"guid"}]""".Replace(",\"nullable\":false", ""),
            f);
    }

    private sealed class NullableScalars
    {
        public int? A { get; set; }
        public DateTime? B { get; set; }
        public Guid? C { get; set; }
        public Color? D { get; set; }
        public decimal? E { get; set; }
    }

    [Fact]
    public void Nullable_value_types_are_nullable_fields_of_the_underlying_type() =>
        Assert.Equal(
            """[{"name":"a","type":"int32","nullable":true},{"name":"b","type":"datetime","nullable":true},{"name":"c","type":"guid","nullable":true},{"name":"d","type":"enum","nullable":true,"values":["red","green","blue"]},{"name":"e","type":"decimal","nullable":true,"scale":2}]""",
            Fields(Build<NullableScalars>()));

    private sealed class Nullability
    {
        public string Required { get; set; } = "";
        public string? Optional { get; set; }
        public Customer Inner { get; set; } = new();
        public Customer? MaybeInner { get; set; }
        public List<string> Names { get; set; } = new();
        public List<string>? MaybeNames { get; set; }
        public List<string?> NullableElements { get; set; } = new();
    }

    [Fact]
    public void Reference_nullability_comes_from_nullable_annotations() =>
        Assert.Equal(
            """[{"name":"required","type":"string"},{"name":"optional","type":"string","nullable":true},{"name":"inner","type":"object","fields":[{"name":"name","type":"string"},{"name":"city","type":"string","nullable":true}]},{"name":"maybeInner","type":"object","nullable":true,"fields":[{"name":"name","type":"string"},{"name":"city","type":"string","nullable":true}]},{"name":"names","type":"list","fields":[{"name":"$value","type":"string"}]},{"name":"maybeNames","type":"list","nullable":true,"fields":[{"name":"$value","type":"string"}]},{"name":"nullableElements","type":"list","fields":[{"name":"$value","type":"string","nullable":true}]}]""",
            Fields(Build<Nullability>()));

#nullable disable
    private sealed class Oblivious
    {
        public string Text { get; set; }
        public Customer Child { get; set; }
        public int Number { get; set; }
    }
#nullable enable

    [Fact]
    public void Unannotated_reference_types_are_nullable() =>
        Assert.Equal(
            """[{"name":"text","type":"string","nullable":true},{"name":"child","type":"object","nullable":true,"fields":[{"name":"name","type":"string"},{"name":"city","type":"string","nullable":true}]},{"name":"number","type":"int32"}]""",
            Fields(Build<Oblivious>()));

    private sealed class Collections
    {
        public int[] Array { get; set; } = Array_();
        public List<int> List { get; set; } = new();
        public IList<long> IList { get; set; } = new List<long>();
        public IReadOnlyList<string> ReadOnly { get; set; } = new List<string>();
        public IEnumerable<Guid> Enumerable { get; set; } = new List<Guid>();
        public ICollection<bool> Collection { get; set; } = new List<bool>();
        public List<List<int>> Nested { get; set; } = new();
        public OrderLine[] Objects { get; set; } = System.Array.Empty<OrderLine>();

        private static int[] Array_() => System.Array.Empty<int>();
    }

    [Fact]
    public void Collections_map_to_lists_with_value_fields_or_object_fields() =>
        Assert.Equal(
            """[{"name":"array","type":"list","fields":[{"name":"$value","type":"int32"}]},{"name":"list","type":"list","fields":[{"name":"$value","type":"int32"}]},{"name":"iList","type":"list","fields":[{"name":"$value","type":"int64"}]},{"name":"readOnly","type":"list","fields":[{"name":"$value","type":"string"}]},{"name":"enumerable","type":"list","fields":[{"name":"$value","type":"guid"}]},{"name":"collection","type":"list","fields":[{"name":"$value","type":"bool"}]},{"name":"nested","type":"list","fields":[{"name":"$value","type":"list","fields":[{"name":"$value","type":"int32"}]}]},{"name":"objects","type":"list","fields":[{"name":"sku","type":"string"},{"name":"qty","type":"uint32"},{"name":"price","type":"decimal","unsigned":true,"scale":2}]}]""",
            Fields(Build<Collections>()));

    private sealed class Attributed
    {
        [CcfDelta] public int Counter { get; set; }
        [CcfDelta] public DateTime Stamp { get; set; }
        [CcfDelta] [CcfDecimal(3)] public decimal Amount { get; set; }
        [CcfDecimal(0, Unsigned = true)] public decimal Whole { get; set; }
        public decimal Default { get; set; }
    }

    [Fact]
    public void Delta_and_decimal_attributes_set_the_schema_keys() =>
        Assert.Equal(
            """[{"name":"counter","type":"int32","delta":true},{"name":"stamp","type":"datetime","delta":true},{"name":"amount","type":"decimal","delta":true,"scale":3},{"name":"whole","type":"decimal","unsigned":true,"scale":0},{"name":"default","type":"decimal","scale":5}]""",
            Fields(Build<Attributed>(new CcfSchemaOptions { DefaultDecimalScale = 5 })));

    [Flags] private enum Perm { None = 0, Read = 1, Write = 2 }

    [JsonConverter(typeof(JsonStringEnumConverter))] private enum Plain { Zebra = 5, Apple = 1, Mango = 3 }

    private sealed class WithEnum { public Plain P { get; set; } }

    [Fact]
    public void Enum_values_are_member_names_in_declaration_order_not_sorted_or_by_value() =>
        Assert.Equal("""[{"name":"p","type":"enum","values":["Zebra","Apple","Mango"]}]""", Fields(Build<WithEnum>()));

    [JsonConverter(typeof(JsonStringEnumConverter))]
    private enum Renamed
    {
        [JsonStringEnumMemberName("first_one")] A,
        B,
    }

    private sealed class WithRenamedEnum { public Renamed R { get; set; } }

    [Fact]
    public void JsonStringEnumMemberName_is_respected_when_present() =>
        Assert.Equal("""[{"name":"r","type":"enum","values":["first_one","B"]}]""", Fields(Build<WithRenamedEnum>()));

    // ---- names, order, ignore --------------------------------------------------------------------------

    private sealed class Naming
    {
        public int FirstName { get; set; }
        [JsonPropertyName("custom_Name")] public int Second { get; set; }
        [JsonIgnore] public int Hidden { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Conditional { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] public int Zero { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int Explicit { get; set; }
        [JsonPropertyOrder(-5)] public int Early { get; set; }
        [JsonPropertyOrder(5)] public int Late { get; set; }
        public int Plain { get; set; }
        public int this[int i] => i;
        public static int Static { get; set; }
        public int ReadOnly => 1;
        public int WriteOnly { set { _ = value; } }
        private int Private { get; set; }
    }

    [Fact]
    public void Field_names_order_and_ignore_follow_system_text_json()
    {
        var schema = Build<Naming>();
        var names = JsonDocument.Parse(schema.CanonicalJson).RootElement.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).ToArray();
        Assert.Equal(new[] { "early", "firstName", "custom_Name", "conditional", "zero", "explicit", "plain", "readOnly", "late" }, names);

        // Conditional ignore keeps the field but makes it nullable.
        Assert.Contains("""{"name":"conditional","type":"string","nullable":true}""", schema.CanonicalJson);
        Assert.Contains("""{"name":"zero","type":"int32","nullable":true}""", schema.CanonicalJson);
        Assert.Contains("""{"name":"explicit","type":"int32"}""", schema.CanonicalJson);
    }

    [Fact]
    public void Property_naming_policy_can_be_changed_and_disabled()
    {
        Assert.Contains("\"firstName\"", Build<Naming>().CanonicalJson);
        Assert.Contains("\"first_name\"", Build<Naming>(new CcfSchemaOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }).CanonicalJson);
        Assert.Contains("\"FirstName\"", Build<Naming>(new CcfSchemaOptions { PropertyNamingPolicy = null }).CanonicalJson);
    }

    private class BaseModel { public int BaseOne { get; set; } public int BaseTwo { get; set; } }

    private sealed class DerivedModel : BaseModel { public int DerivedOne { get; set; } public int DerivedTwo { get; set; } }

    [Fact]
    public void Property_order_matches_what_system_text_json_actually_emits_for_derived_types()
    {
        var json = JsonSerializer.Serialize(new DerivedModel(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var stj = JsonNode.Parse(json)!.AsObject().Select(p => p.Key).ToArray();
        var ours = JsonDocument.Parse(Build<DerivedModel>().CanonicalJson).RootElement.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()).ToArray();
        Assert.Equal(stj, ours);
    }

    [Theory]
    [InlineData(typeof(Product))]
    [InlineData(typeof(AllTypes))]
    [InlineData(typeof(Order))]
    [InlineData(typeof(Naming))]
    [InlineData(typeof(Collections))]
    [InlineData(typeof(Nullability))]
    public void Field_order_equals_system_text_json_property_order_for_the_test_models(Type model)
    {
        var instance = Activator.CreateInstance(model)!;
        var json = JsonSerializer.Serialize(instance, model, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // Conditionally ignored properties would be missing from the JSON; this model set has none that fire for defaults except Naming.
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Converters = { new JsonStringEnumConverter() },
        });
        var stj = JsonNode.Parse(json)!.AsObject().Select(p => p.Key).ToList();
        Assert.True(CcfSchemaBuilder.TryBuild(model, null, out var schema, out var reason), reason);
        var ours = JsonDocument.Parse(schema!.CanonicalJson).RootElement.GetProperty("fields").EnumerateArray()
            .Select(f => f.GetProperty("name").GetString()!).ToList();
        // STJ omits write-only and (per condition) some ignored members; ours must be the same sequence for what STJ emits.
        Assert.Equal(stj, ours.Where(stj.Contains).ToList());
    }

    // ---- unsupported -----------------------------------------------------------------------------------

    private sealed class HasObject { public object? Value { get; set; } }
    private sealed class HasJsonElement { public JsonElement Value { get; set; } }
    private sealed class HasJsonNode { public JsonNode? Value { get; set; } }
    private sealed class HasDictionary { public Dictionary<string, int> Value { get; set; } = new(); }
    private interface IThing { int X { get; } }
    private sealed class HasInterface { public IThing? Value { get; set; } }
    private abstract class AbstractThing { public int X { get; set; } }
    private sealed class HasAbstract { public AbstractThing? Value { get; set; } }

    [JsonDerivedType(typeof(Circle), "circle")]
    private class Shape { public int X { get; set; } }
    private sealed class Circle : Shape { public int R { get; set; } }
    private sealed class HasPolymorphic { public Shape? Value { get; set; } }

    [JsonConverter(typeof(JsonStringEnumConverter))] [Flags] private enum StrPerm { None = 0, Read = 1, Write = 2 }

    private sealed class HasFlags { public StrPerm Value { get; set; } }
    private sealed class HasChar { public char Value { get; set; } }
    private sealed class HasBytes { public byte[] Value { get; set; } = Array.Empty<byte>(); }
    private sealed class HasTimeSpan { public TimeSpan Value { get; set; } }
    private sealed class HasDateOnly { public DateOnly Value { get; set; } }
    private sealed class HasTimeOnly { public TimeOnly Value { get; set; } }
    private sealed class HasHalf { public Half Value { get; set; } }
    private sealed class HasInt128 { public Int128 Value { get; set; } }
    private sealed class HasBigInteger { public BigInteger Value { get; set; } }
    private sealed class HasUri { public Uri? Value { get; set; } }
    private sealed class HasTuple { public (int, int) Value { get; set; } }
    private sealed class Recursive { public Recursive? Next { get; set; } public int X { get; set; } }
    private sealed class RecursiveViaList { public List<RecursiveViaList> Children { get; set; } = new(); public int X { get; set; } }
    private sealed class BadName { [JsonPropertyName("a-b")] public int X { get; set; } }
    private sealed class DuplicateName { public int A { get; set; } [JsonPropertyName("a")] public int B { get; set; } }
    private sealed class Empty { }
    private sealed class OnlyIgnored { [JsonIgnore] public int X { get; set; } }
    private sealed class DeltaOnString { [CcfDelta] public string S { get; set; } = ""; }
    private sealed class DecimalOnInt { [CcfDecimal(2)] public int S { get; set; } }
    private sealed class BigScale { [CcfDecimal(19)] public decimal S { get; set; } }
    private sealed class NegativeScale { [CcfDecimal(-1)] public decimal S { get; set; } }

    private sealed class UpperConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToUpperInvariant());
    }

    private sealed class HasConverter { [JsonConverter(typeof(UpperConverter))] public string Value { get; set; } = ""; }

    [JsonConverter(typeof(UpperConverter))]
    private sealed class ConvertedType { public int X { get; set; } }
    private sealed class HasConvertedType { public ConvertedType? Value { get; set; } }

    // eight nested object levels below the root → depth 9
    private sealed class L8 { public int X { get; set; } }
    private sealed class L7 { public L8 Next { get; set; } = new(); }
    private sealed class L6 { public L7 Next { get; set; } = new(); }
    private sealed class L5 { public L6 Next { get; set; } = new(); }
    private sealed class L4 { public L5 Next { get; set; } = new(); }
    private sealed class L3 { public L4 Next { get; set; } = new(); }
    private sealed class L2 { public L3 Next { get; set; } = new(); }
    private sealed class L1 { public L2 Next { get; set; } = new(); }
    private sealed class L0 { public L1 Next { get; set; } = new(); }

    [Theory]
    [InlineData(typeof(HasObject))]
    [InlineData(typeof(HasJsonElement))]
    [InlineData(typeof(HasJsonNode))]
    [InlineData(typeof(HasDictionary))]
    [InlineData(typeof(HasInterface))]
    [InlineData(typeof(HasAbstract))]
    [InlineData(typeof(HasPolymorphic))]
    [InlineData(typeof(HasFlags))]
    [InlineData(typeof(HasChar))]
    [InlineData(typeof(HasBytes))]
    [InlineData(typeof(HasTimeSpan))]
    [InlineData(typeof(HasDateOnly))]
    [InlineData(typeof(HasTimeOnly))]
    [InlineData(typeof(HasHalf))]
    [InlineData(typeof(HasInt128))]
    [InlineData(typeof(HasBigInteger))]
    [InlineData(typeof(HasUri))]
    [InlineData(typeof(HasTuple))]
    [InlineData(typeof(Recursive))]
    [InlineData(typeof(RecursiveViaList))]
    [InlineData(typeof(BadName))]
    [InlineData(typeof(DuplicateName))]
    [InlineData(typeof(Empty))]
    [InlineData(typeof(OnlyIgnored))]
    [InlineData(typeof(DeltaOnString))]
    [InlineData(typeof(DecimalOnInt))]
    [InlineData(typeof(BigScale))]
    [InlineData(typeof(NegativeScale))]
    [InlineData(typeof(HasConverter))]
    [InlineData(typeof(HasConvertedType))]
    [InlineData(typeof(L0))]
    public void Unsupported_members_make_the_whole_type_unsupported_with_a_reason(Type type) => Reason(type);

    [Theory]
    [InlineData(typeof(int))]
    [InlineData(typeof(string))]
    [InlineData(typeof(List<int>))]
    [InlineData(typeof(List<string>))]
    [InlineData(typeof(List<List<Product>>))]
    [InlineData(typeof(object))]
    [InlineData(typeof(Dictionary<string, Product>))]
    [InlineData(typeof(IThing))]
    [InlineData(typeof(byte[]))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(Plain))]
    [InlineData(typeof(JsonElement))]
    public void Scalar_and_non_record_roots_are_unsupported(Type type) => Reason(type);

    [Fact]
    public void Seven_levels_of_nesting_below_the_root_is_the_deepest_supported()
    {
        // L1 → … → L8 is root + 7 nested objects = depth 8.
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(L1), null, out var schema, out var reason), reason);
        Assert.Equal(8, schema!.Depth);
        Assert.Contains("nesting deeper", Reason(typeof(L0)));
    }

    [Fact]
    public void Reasons_name_the_offending_member()
    {
        Assert.Contains("HasObject.value", Reason(typeof(HasObject)));
        Assert.Contains("recursive", Reason(typeof(Recursive)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a-b", Reason(typeof(BadName)));
        Assert.Contains("Flags", Reason(typeof(HasFlags)), StringComparison.OrdinalIgnoreCase); // a flags enum written as strings
    }

    [Fact]
    public void Generic_root_types_get_their_name_without_arity()
    {
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(Page<Product>), null, out var schema, out _));
        Assert.Equal("Page", schema!.Name);
    }

    private sealed class Page<T> { public List<T> Items { get; set; } = new(); public int Total { get; set; } }

    [Fact]
    public void Structs_and_records_are_object_like()
    {
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(PointStruct), null, out var s1, out _));
        Assert.Equal("""[{"name":"x","type":"int32"},{"name":"y","type":"int32"}]""", Fields(s1!));
        Assert.True(CcfSchemaBuilder.TryBuild(typeof(PointRecord), null, out var s2, out _));
        Assert.Equal(Fields(s1!), Fields(s2!));
    }

    private struct PointStruct { public int X { get; set; } public int Y { get; set; } }

    private sealed record PointRecord(int X, int Y);
}
