namespace Ccf;

/// <summary>
/// Decodes CCF payloads. Every failure on malformed input is a <see cref="CcfFormatException"/>; nothing else
/// escapes for any byte sequence.
/// </summary>
public static class CcfDecoder
{
    /// <summary>
    /// Decodes <paramref name="payload"/> into the untyped value tree: an <c>object?[]</c> of
    /// <see cref="CcfObject"/> for a table root, or a single <see cref="CcfObject"/> when header flag
    /// <c>S</c> is set.
    /// </summary>
    /// <exception cref="CcfFormatException">The payload is malformed (SPEC §11).</exception>
    public static object DecodeValue(ReadOnlySpan<byte> payload, CcfSchema schema, CcfLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var table = CcfColumnReader.ReadPayload(payload, schema, limits ?? CcfLimits.Default, out var flags, out var rows);
        var objects = CcfValueTree.BuildRows(table, rows, schema.Fields);
        return (flags & CcfPayloadFlags.SingleObject) != 0 ? objects[0]! : objects;
    }

    /// <summary>
    /// Decodes <paramref name="payload"/> into <typeparamref name="T"/>: a collection of objects (array,
    /// <c>List&lt;U&gt;</c>, <c>IReadOnlyList&lt;U&gt;</c>, …) or a single object, matching schema fields to
    /// properties by JSON name (ARCHITECTURE §6.2). Fields without a property are skipped; properties without a
    /// field keep their default. Accessors are compiled once per (schema, type).
    /// </summary>
    /// <exception cref="CcfFormatException">
    /// The payload is malformed (SPEC §11), including a value that does not fit its target CLR type
    /// (<see cref="CcfErrorCode.ValueOutOfRange"/>).
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="T"/> cannot receive this schema (incompatible property type, abstract type, several
    /// rows into a single object); this is a usage error, not a format error.
    /// </exception>
    public static T Decode<T>(
        ReadOnlySpan<byte> payload, CcfSchema schema, CcfSchemaOptions? options = null, CcfLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var root = TypedRoot.Get(schema, typeof(T), options ?? CcfSchemaOptions.Default);
        var table = CcfColumnReader.ReadPayload(payload, schema, limits ?? CcfLimits.Default, out _, out var rows);
        try
        {
            return (T)root.Convert(table, rows);
        }
        catch (OverflowException e)
        {
            throw new CcfFormatException(CcfErrorCode.ValueOutOfRange, "a value does not fit its target CLR type: " + e.Message);
        }
    }

    /// <summary>
    /// Reads and validates the header only (magic, version, flags) and returns the schema id as 8 lowercase hex
    /// characters, so a client can look the schema up before decoding.
    /// </summary>
    /// <exception cref="CcfFormatException">The header is malformed.</exception>
    public static string PeekSchemaId(ReadOnlySpan<byte> payload)
    {
        var r = new CcfReader(payload);
        CcfColumnReader.ReadHeader(ref r, ReadOnlySpan<byte>.Empty, out _, out _, new CcfLimits { MaxRows = int.MaxValue });
        return Convert.ToHexString(payload.Slice(4, 4)).ToLowerInvariant();
    }
}
