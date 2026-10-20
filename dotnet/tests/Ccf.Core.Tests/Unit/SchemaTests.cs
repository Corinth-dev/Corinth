using Ccf.Core.Tests.Conformance;
using Xunit;

namespace Ccf.Core.Tests;

public class SchemaTests
{
    [Fact]
    public void Product_schema_has_the_id_from_the_spec()
    {
        var schema = CcfSchema.Parse(File.ReadAllText(VectorManifest.PathOf("schema-Product.json")));
        Assert.Equal("c33a3ed0", schema.IdHex);
        Assert.Equal("c33a3ed0", Hex.Of(schema.IdBytes));
        Assert.Equal("Product", schema.Name);
        Assert.Equal(1, schema.Depth);
    }

    [Theory]
    [MemberData(nameof(VectorManifest.AllNames), MemberType = typeof(VectorManifest))]
    public void Every_vector_schema_canonicalizes_byte_for_byte_and_hashes_to_the_manifest_id(string name)
    {
        var v = VectorManifest.Get(name);
        var text = v.ReadSchemaText();
        var schema = CcfSchema.Parse(text);
        Assert.Equal(text, schema.CanonicalJson);
        Assert.Equal(v.SchemaId, schema.IdHex);
    }

    [Fact]
    public void Whitespace_and_key_order_do_not_change_the_canonical_form_or_id()
    {
        var canonical = File.ReadAllText(VectorManifest.PathOf("schema-Product.json"));
        var messy = """
            { "fields" : [
              { "type":"int64", "delta":true, "name":"id" },
              { "name":"name", "type":"string", "nullable": false },
              { "scale":2, "unsigned":true, "type":"decimal", "name":"price" },
              { "name":"status","type":"enum","values":["active","discontinued"] },
              { "type":"uint32", "name":"stock" } ],
              "name":"Product" }
            """;
        var schema = CcfSchema.Parse(messy);
        Assert.Equal(canonical, schema.CanonicalJson);
    }

    [Fact]
    public void Nested_schema_depth_is_computed()
    {
        var order = CcfSchema.Parse(File.ReadAllText(VectorManifest.PathOf("schema-Order.json")));
        Assert.Equal(2, order.Depth);
        var deep = CcfSchema.Parse(
            """{"name":"A","fields":[{"name":"b","type":"object","fields":[{"name":"c","type":"list","fields":[{"name":"$value","type":"list","fields":[{"name":"$value","type":"int32"}]}]}]}]}""");
        Assert.Equal(4, deep.Depth);
    }

    // SPEC §14.3: invalid schemas raise CcfSchemaException (not a format error).
    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"fields":[{"name":"a","type":"int32"}]}""")]
    [InlineData("""{"name":"X"}""")]
    [InlineData("""{"name":"X","fields":[]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"1a","type":"int32"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a-b","type":"int32"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"","type":"int32"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32"},{"name":"a","type":"string"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int128"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"decimal"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"decimal","scale":19}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"decimal","scale":-1}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"decimal","scale":1.5}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"string","delta":true}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"string","delta":false}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32","unsigned":true}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32","scale":2}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32","values":["x"]}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32","fields":[]}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"float64","delta":true}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"enum"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"enum","values":[]}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"enum","values":["x","x"]}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"enum","values":["not ok"]}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"object"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"object","fields":[]}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32","bogus":1}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32"}],"extra":1}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32","type":"string"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"a","type":"int32","nullable":"yes"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"$value","type":"int32"}]}""")]
    [InlineData("""{"name":"X","fields":[{"name":"l","type":"list","fields":[{"name":"$value","type":"int32"},{"name":"b","type":"int32"}]}]}""")]
    public void Invalid_schemas_raise_CcfSchemaException(string json) =>
        Assert.Throws<CcfSchemaException>(() => CcfSchema.Parse(json));

    [Fact]
    public void Enum_with_65536_values_is_accepted_and_65537_is_not()
    {
        string Json(int n) =>
            "{\"name\":\"X\",\"fields\":[{\"name\":\"e\",\"type\":\"enum\",\"values\":["
            + string.Join(",", Enumerable.Range(0, n).Select(i => $"\"v{i}\"")) + "]}]}";
        Assert.NotNull(CcfSchema.Parse(Json(65_536)));
        Assert.Throws<CcfSchemaException>(() => CcfSchema.Parse(Json(65_537)));
    }

    [Fact]
    public void Schema_created_in_code_is_validated_and_canonical()
    {
        var schema = CcfSchema.Create("T", new[]
        {
            new CcfField("a", CcfType.Int32) { Nullable = true },
            new CcfField("b", CcfType.Decimal) { Scale = 3, Unsigned = true, Delta = true },
        });
        Assert.Equal(
            """{"name":"T","fields":[{"name":"a","type":"int32","nullable":true},{"name":"b","type":"decimal","delta":true,"unsigned":true,"scale":3}]}""",
            schema.CanonicalJson);
        Assert.Throws<CcfSchemaException>(() => CcfSchema.Create("T", new[] { new CcfField("a", CcfType.Decimal) }));
        Assert.Throws<CcfSchemaException>(() => CcfSchema.Create("T", new[] { new CcfField("a", CcfType.String) { Delta = true } }));
    }

    [Fact]
    public void Any_change_to_a_schema_changes_its_id()
    {
        var baseline = CcfSchema.Parse("""{"name":"X","fields":[{"name":"a","type":"int32"},{"name":"b","type":"string"}]}""");
        string[] variants =
        {
            """{"name":"X","fields":[{"name":"b","type":"string"},{"name":"a","type":"int32"}]}""",
            """{"name":"X","fields":[{"name":"a","type":"int64"},{"name":"b","type":"string"}]}""",
            """{"name":"X","fields":[{"name":"a","type":"int32","nullable":true},{"name":"b","type":"string"}]}""",
            """{"name":"X","fields":[{"name":"a","type":"int32"},{"name":"c","type":"string"}]}""",
            """{"name":"X","fields":[{"name":"a","type":"int32"}]}""",
        };
        foreach (var v in variants)
        {
            Assert.NotEqual(baseline.IdHex, CcfSchema.Parse(v).IdHex);
        }
    }

    [Fact]
    public void Names_with_json_special_characters_in_the_root_name_are_escaped_only_where_required()
    {
        var schema = CcfSchema.Create("a\"b\\c\n", new[] { new CcfField("x", CcfType.Bool) });
        Assert.StartsWith("{\"name\":\"a\\\"b\\\\c\\n\",", schema.CanonicalJson);
        Assert.Equal(schema.CanonicalJson, CcfSchema.Parse(schema.CanonicalJson).CanonicalJson);
    }
}
