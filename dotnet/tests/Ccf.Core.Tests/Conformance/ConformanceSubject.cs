using System.Buffers;

namespace Ccf.Core.Tests.Conformance;

/// <summary>
/// The single seam between the conformance harness and the implementation under test. Every member throws
/// <see cref="NotImplementedException"/> until phase 1 wires it to <c>Ccf.Core</c>; the conformance tests
/// therefore fail with that exception rather than being skipped.
/// </summary>
internal static class ConformanceSubject
{
    /// <summary>Parses schema JSON and returns its canonical text (SPEC §3.2).</summary>
    public static string Canonicalize(string schemaJson) => CcfSchema.Parse(schemaJson).CanonicalJson;

    /// <summary>Returns the schema id as 8 lowercase hex characters (SPEC §3.3).</summary>
    public static string SchemaIdHex(string schemaJson) => CcfSchema.Parse(schemaJson).IdHex;

    /// <summary>
    /// Decodes <paramref name="payload"/> with the given schema and returns the value in the canonical JSON
    /// representation of test-vectors/README.md. Format failures must surface as <c>CcfFormatException</c>.
    /// </summary>
    public static string DecodeToCanonicalJson(byte[] payload, string schemaJson) =>
        CanonicalJsonWriter.Write(CcfDecoder.DecodeValue(payload, CcfSchema.Parse(schemaJson)));

    /// <summary>
    /// Loads the canonical-JSON value into the matching C# type, encodes it with the schema and requested
    /// features, and returns the bytes and the header flags produced.
    /// </summary>
    public static (byte[] Bytes, int Flags) EncodeFromCanonicalJson(
        string vectorName, string expectedJson, string schemaJson, string requestedFeatures)
    {
        var schema = CcfSchema.Parse(schemaJson);
        var value = VectorModels.Load(expectedJson, schema.Name);
        var buffer = new ArrayBufferWriter<byte>();
        var flags = new CcfEncoder().Encode(value, schema, CcfFeatureParser.Parse(requestedFeatures), buffer);
        return (buffer.WrittenSpan.ToArray(), (int)flags);
    }

    /// <summary>The error code name (SPEC §11) carried by a decoder failure, e.g. <c>"BadMagic"</c>.</summary>
    public static string? ErrorCodeOf(Exception exception) =>
        exception is CcfFormatException f ? f.Code.ToString() : "Unexpected " + exception.GetType().FullName + ": " + exception.Message;
}
