namespace Ccf.Client;

/// <summary>
/// The schema for a CCF response could not be obtained or verified (request failed, unknown id, content does not
/// hash to the id, invalid schema). A client-side condition, not a <see cref="CcfFormatException"/>; like a format
/// error it makes the client repeat a safe request as JSON.
/// </summary>
public sealed class CcfSchemaFetchException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CcfSchemaFetchException(string schemaId, string message, Exception? inner = null)
        : base(message, inner)
    {
        SchemaId = schemaId;
    }

    /// <summary>The 8-character hex id of the schema that was needed.</summary>
    public string SchemaId { get; }
}
