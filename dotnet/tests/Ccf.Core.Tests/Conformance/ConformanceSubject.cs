namespace Ccf.Core.Tests.Conformance;

/// <summary>
/// The single seam between the conformance harness and the implementation under test. Every member throws
/// <see cref="NotImplementedException"/> until phase 1 wires it to <c>Ccf.Core</c>; the conformance tests
/// therefore fail with that exception rather than being skipped.
/// </summary>
internal static class ConformanceSubject
{
    /// <summary>Parses schema JSON and returns its canonical text (SPEC §3.2).</summary>
    public static string Canonicalize(string schemaJson) =>
        throw new NotImplementedException("Schema canonicalizer (phase 1)");

    /// <summary>Returns the schema id as 8 lowercase hex characters (SPEC §3.3).</summary>
    public static string SchemaIdHex(string schemaJson) =>
        throw new NotImplementedException("Schema id hashing (phase 1)");

    /// <summary>
    /// Decodes <paramref name="payload"/> with the given schema and returns the value in the canonical JSON
    /// representation of test-vectors/README.md. Format failures must surface as <c>CcfFormatException</c>.
    /// </summary>
    public static string DecodeToCanonicalJson(byte[] payload, string schemaJson) =>
        throw new NotImplementedException("Decoder (phase 1)");

    /// <summary>
    /// Loads the canonical-JSON value into the matching C# type, encodes it with the schema and requested
    /// features, and returns the bytes and the header flags produced.
    /// </summary>
    public static (byte[] Bytes, int Flags) EncodeFromCanonicalJson(
        string vectorName, string expectedJson, string schemaJson, string requestedFeatures) =>
        throw new NotImplementedException("Encoder (phase 1)");

    /// <summary>The error code name (SPEC §11) carried by a decoder failure, e.g. <c>"BadMagic"</c>.</summary>
    public static string? ErrorCodeOf(Exception exception) =>
        throw new NotImplementedException("CcfFormatException / CcfErrorCode (phase 1)");
}
