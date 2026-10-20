using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ccf.AspNetCore;

/// <summary>Thread-safe default <see cref="ISchemaRegistry"/>.</summary>
public sealed class CcfSchemaRegistry : ISchemaRegistry
{
    private sealed record Entry(CcfSchema? Schema, string? Reason);

    private readonly ConcurrentDictionary<Type, Entry> _byType = new();
    private readonly ConcurrentDictionary<string, CcfSchema> _byId = new(StringComparer.Ordinal);
    private readonly object _registerLock = new();
    private readonly ILogger _logger;
    private readonly Func<Type, (CcfSchema? Schema, string? Reason)> _build;

    /// <summary>Creates the registry used by <c>AddCcf</c>.</summary>
    public CcfSchemaRegistry(IOptions<CcfOptions> options, IOptions<JsonOptions> mvcJson, ILoggerFactory loggerFactory)
        : this(
            new CcfSchemaOptions
            {
                // Field names mirror what System.Text.Json emits for MVC responses (ARCHITECTURE §5.2).
                PropertyNamingPolicy = mvcJson.Value.JsonSerializerOptions.PropertyNamingPolicy,
                DefaultDecimalScale = options.Value.DefaultDecimalScale,
            },
            loggerFactory.CreateLogger("Ccf"),
            null)
    {
    }

    /// <summary>Test seam: lets tests substitute how schemas are built (for example to force an id collision).</summary>
    internal CcfSchemaRegistry(CcfSchemaOptions schemaOptions, ILogger logger, Func<Type, (CcfSchema? Schema, string? Reason)>? build)
    {
        SchemaOptions = schemaOptions;
        _logger = logger;
        _build = build ?? Build;
    }

    /// <inheritdoc />
    public CcfSchemaOptions SchemaOptions { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<CcfSchema> Schemas => _byId.Values.ToArray();

    /// <inheritdoc />
    public bool TryGetSchema(Type type, [NotNullWhen(true)] out CcfSchema? schema)
    {
        ArgumentNullException.ThrowIfNull(type);
        var entry = _byType.TryGetValue(type, out var existing) ? existing : _byType.GetOrAdd(type, Resolve);
        schema = entry.Schema;
        return schema is not null;
    }

    /// <inheritdoc />
    public bool TryGetById(string id, [NotNullWhen(true)] out CcfSchema? schema) => _byId.TryGetValue(id, out schema);

    /// <inheritdoc />
    public CcfSchema Register(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var entry = _byType.AddOrUpdate(type, Resolve, static (_, old) => old);
        return entry.Schema ?? throw new InvalidOperationException($"{type} cannot be served as CCF: {entry.Reason}");
    }

    /// <inheritdoc />
    public CcfSchema Register<T>() => Register(typeof(T));

    private Entry Resolve(Type type)
    {
        var (schema, reason) = _build(type);
        if (schema is null && TryGetEnumerableInterface(type, out var asEnumerable))
        {
            // A runtime collection type (LINQ iterator, HashSet<T>, …) is served through IEnumerable<T>.
            (schema, reason) = _build(asEnumerable);
        }

        if (schema is null)
        {
            _logger.LogDebug("Type {Type} is not served as CCF (JSON fallback): {Reason}", type, reason);
            return new Entry(null, reason);
        }

        lock (_registerLock)
        {
            if (_byId.TryGetValue(schema.IdHex, out var other))
            {
                if (other.CanonicalJson != schema.CanonicalJson)
                {
                    var message = $"Schema id {schema.IdHex} of {type} collides with a different registered schema ({other.Name}); {type} will be served as JSON.";
                    _logger.LogError("{Message}", message);
                    return new Entry(null, message);
                }

                return new Entry(other, null);
            }

            _byId[schema.IdHex] = schema;
        }

        _logger.LogDebug("Registered CCF schema {SchemaId} for {Type}", schema.IdHex, type);
        return new Entry(schema, null);
    }

    private (CcfSchema? Schema, string? Reason) Build(Type type)
    {
        var ok = CcfSchemaBuilder.TryBuild(type, SchemaOptions, out var schema, out var reason);
        return ok ? (schema, null) : (null, reason);
    }

    private static bool TryGetEnumerableInterface(Type type, [NotNullWhen(true)] out Type? enumerable)
    {
        enumerable = null;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            return false; // already IEnumerable<T> itself
        }

        foreach (var i in type.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                if (enumerable is not null)
                {
                    enumerable = null;
                    return false;
                }

                enumerable = i;
            }
        }

        return enumerable is not null && type != typeof(string);
    }
}
