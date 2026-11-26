using System.Collections.Concurrent;

namespace Corinth.Client;

/// <summary>
/// Stores verified schemas by id. Schemas are immutable (the id is a hash of the content), so entries never
/// expire. Implement this to persist schemas, for example on disk.
/// </summary>
public interface ICcfSchemaCache
{
    /// <summary>Gets the schema with the given 8-character lowercase hex id, or <c>null</c>.</summary>
    ValueTask<CcfSchema?> GetAsync(string schemaId, CancellationToken cancellationToken);

    /// <summary>Stores a schema that was fetched and verified against its id.</summary>
    ValueTask SetAsync(CcfSchema schema, CancellationToken cancellationToken);
}

/// <summary>The default cache: schemas live for the lifetime of the process.</summary>
public sealed class InMemoryCcfSchemaCache : ICcfSchemaCache
{
    private readonly ConcurrentDictionary<string, CcfSchema> _schemas = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public ValueTask<CcfSchema?> GetAsync(string schemaId, CancellationToken cancellationToken) =>
        new(_schemas.TryGetValue(schemaId, out var schema) ? schema : null);

    /// <inheritdoc />
    public ValueTask SetAsync(CcfSchema schema, CancellationToken cancellationToken)
    {
        _schemas[schema.IdHex] = schema;
        return ValueTask.CompletedTask;
    }
}
