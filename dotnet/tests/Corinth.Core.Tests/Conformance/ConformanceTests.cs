using System.Text.Json;
using Xunit;

namespace Corinth.Core.Tests.Conformance;

/// <summary>
/// Golden-vector tests, one theory case per manifest entry. Tagged <c>Category=Conformance</c>: CI runs them
/// as a non-blocking job until phase 1 lands, after which the tag stops being excluded by default.
/// </summary>
[Trait("Category", "Conformance")]
public class ConformanceTests
{
    [Theory]
    [MemberData(nameof(VectorManifest.ValidNames), MemberType = typeof(VectorManifest))]
    public void Valid_schema_canonical_form_and_id(string name)
    {
        var v = VectorManifest.Get(name);
        var schemaText = v.ReadSchemaText();

        Assert.Equal(schemaText, ConformanceSubject.Canonicalize(schemaText));
        Assert.Equal(v.SchemaId, ConformanceSubject.SchemaIdHex(schemaText));
    }

    [Theory]
    [MemberData(nameof(VectorManifest.ValidNames), MemberType = typeof(VectorManifest))]
    public void Valid_decodes_to_expected_value(string name)
    {
        var v = VectorManifest.Get(name);
        var actual = ConformanceSubject.DecodeToCanonicalJson(v.ReadPayload(), v.ReadSchemaText());

        using var expectedDoc = v.ReadExpected();
        using var actualDoc = JsonDocument.Parse(actual);
        CanonicalJson.AssertEqual(expectedDoc.RootElement, actualDoc.RootElement);
    }

    [Theory]
    [MemberData(nameof(VectorManifest.ValidNames), MemberType = typeof(VectorManifest))]
    public void Valid_encodes_to_identical_bytes(string name)
    {
        var v = VectorManifest.Get(name);
        var expectedJson = File.ReadAllText(VectorManifest.PathOf(v.Expected!));

        var (bytes, flags) = ConformanceSubject.EncodeFromCanonicalJson(
            name, expectedJson, v.ReadSchemaText(), v.RequestedFeatures ?? "");

        Assert.Equal(v.ReadPayload(), bytes);
        Assert.Equal(v.ExpectedFlags, flags);
        Assert.Equal(v.ExpectedContentType, ContentTypeFor(flags));
    }

    [Theory]
    [MemberData(nameof(VectorManifest.InvalidNames), MemberType = typeof(VectorManifest))]
    public void Invalid_fails_with_expected_error(string name)
    {
        var v = VectorManifest.Get(name);
        var payload = v.ReadPayload();
        var schemaText = v.ReadSchemaText();

        var ex = Record.Exception(() => ConformanceSubject.DecodeToCanonicalJson(payload, schemaText));

        Assert.NotNull(ex);
        Assert.Equal(v.ExpectedError, ConformanceSubject.ErrorCodeOf(ex!));
    }

    /// <summary>Content-Type per SPEC §2.3, derived from the header flags actually produced.</summary>
    private static string ContentTypeFor(int flags) =>
        (flags & 1) != 0 ? "application/vnd.ccf; features=d" : "application/vnd.ccf";
}
