using System.Text.Json;
using Xunit;

namespace Ccf.Core.Tests.Conformance;

/// <summary>Tests of the harness itself. These are blocking: they pass in phase 0.</summary>
public class HarnessTests
{
    [Fact]
    public void Manifest_lists_all_15_vectors_and_their_files_exist()
    {
        Assert.Equal(15, VectorManifest.Vectors.Count);
        Assert.Equal(6, VectorManifest.Vectors.Count(v => v.IsValid));
        Assert.Equal(9, VectorManifest.Vectors.Count(v => !v.IsValid));
        Assert.Equal(VectorManifest.Vectors.Count, VectorManifest.Vectors.Select(v => v.Name).Distinct().Count());

        foreach (var v in VectorManifest.Vectors)
        {
            Assert.True(File.Exists(VectorManifest.PathOf(v.File)), v.File);
            Assert.True(File.Exists(VectorManifest.PathOf(v.Schema)), v.Schema);
            Assert.Equal(v.IsValid, v.Expected is not null);
            Assert.Equal(!v.IsValid, v.ExpectedError is not null);
            if (v.Expected is not null)
            {
                Assert.True(File.Exists(VectorManifest.PathOf(v.Expected)), v.Expected);
            }
        }
    }

    [Fact]
    public void Payload_sizes_match_the_manifest()
    {
        foreach (var v in VectorManifest.Vectors)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(VectorManifest.PathOf("manifest.json")));
            var bytes = doc.RootElement.GetProperty("vectors").EnumerateArray()
                .Single(e => e.GetProperty("name").GetString() == v.Name).GetProperty("bytes").GetInt32();
            Assert.Equal(bytes, v.ReadPayload().Length);
        }
    }

    [Fact]
    public void Every_expected_file_compares_equal_to_itself()
    {
        foreach (var v in VectorManifest.Vectors.Where(v => v.IsValid))
        {
            using var a = v.ReadExpected();
            using var b = v.ReadExpected();
            Assert.Null(CanonicalJson.Diff(a.RootElement, b.RootElement));
        }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Object_key_order_is_ignored() =>
        Assert.True(CanonicalJson.Equal(J("""{"a":1,"b":[1,2]}"""), J("""{"b":[1,2],"a":1}""")));

    [Fact]
    public void Array_order_matters() =>
        Assert.False(CanonicalJson.Equal(J("[1,2]"), J("[2,1]")));

    [Fact]
    public void Numbers_compare_by_parsed_value_not_text() =>
        Assert.True(CanonicalJson.Equal(J("[100.0,0.0,1.7976931348623157e+308]"), J("[100,0,1.7976931348623157E308]")));

    [Fact]
    public void Negative_zero_is_distinct_from_zero()
    {
        Assert.False(CanonicalJson.Equal(J("[0.0]"), J("[-0.0]")));
        Assert.False(CanonicalJson.Equal(J("[-0.0]"), J("[0]")));
        Assert.True(CanonicalJson.Equal(J("[-0.0]"), J("[-0]")));
    }

    [Fact]
    public void Floats_are_compared_exactly_not_approximately() =>
        Assert.False(CanonicalJson.Equal(J("[3.141592653589793]"), J("[3.1415926535897936]")));

    [Fact]
    public void Strings_and_numbers_are_not_interchangeable()
    {
        Assert.False(CanonicalJson.Equal(J("""{"id":"1001"}"""), J("""{"id":1001}""")));
        Assert.False(CanonicalJson.Equal(J("""["361.48"]"""), J("[361.48]")));
    }

    [Fact]
    public void Null_missing_and_extra_properties_are_differences()
    {
        Assert.False(CanonicalJson.Equal(J("""{"a":null}"""), J("""{"a":0}""")));
        Assert.False(CanonicalJson.Equal(J("""{"a":1}"""), J("""{}""")));
        Assert.False(CanonicalJson.Equal(J("""{}"""), J("""{"a":1}""")));
        Assert.False(CanonicalJson.Equal(J("[]"), J("""{}""")));
    }

    [Fact]
    public void Diff_reports_the_path_of_the_first_difference() =>
        Assert.Equal("$[1].lines[0].qty: expected 2, got 3",
            CanonicalJson.Diff(J("""[{"lines":[{"qty":1}]},{"lines":[{"qty":2}]}]"""),
                               J("""[{"lines":[{"qty":1}]},{"lines":[{"qty":3}]}]""")));
}
