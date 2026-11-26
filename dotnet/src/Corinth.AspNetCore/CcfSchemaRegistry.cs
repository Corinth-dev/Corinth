using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HttpJsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;

namespace Corinth.AspNetCore;

/// <summary>Thread-safe default <see cref="ISchemaRegistry"/>.</summary>
public sealed class CcfSchemaRegistry : ISchemaRegistry
{
    private sealed record Entry(CcfSchema? Schema, string? Reason);

    private readonly ConcurrentDictionary<(Type, JsonNamingPolicy?, int, System.Text.Json.JsonSerializerOptions?), Entry> _byKey = new();
    private readonly ConcurrentDictionary<string, CcfSchema> _byId = new(StringComparer.Ordinal);
    private readonly object _registerLock = new();
    private readonly ILogger _logger;
    private readonly Func<Type, CcfSchemaOptions, (CcfSchema? Schema, string? Reason)> _build;

    /// <summary>Creates the registry used by <c>AddCcf</c>.</summary>
    public CcfSchemaRegistry(
        IOptions<CcfOptions> options,
        IOptions<JsonOptions> mvcJson,
        IOptions<HttpJsonOptions> httpJson,
        ILoggerFactory loggerFactory)
        : this(
            // Field names mirror what System.Text.Json emits on that path's own JSON output (ARCHITECTURE §5.2).
            new CcfSchemaOptions
            {
                PropertyNamingPolicy = mvcJson.Value.JsonSerializerOptions.PropertyNamingPolicy,
                JsonSerializerOptions = mvcJson.Value.JsonSerializerOptions,
                DefaultDecimalScale = options.Value.DefaultDecimalScale,
            },
            new CcfSchemaOptions
            {
                PropertyNamingPolicy = httpJson.Value.SerializerOptions.PropertyNamingPolicy,
                JsonSerializerOptions = httpJson.Value.SerializerOptions,
                DefaultDecimalScale = options.Value.DefaultDecimalScale,
            },
            loggerFactory.CreateLogger("Ccf"),
            null)
    {
    }

    /// <summary>Test seam: lets tests substitute how schemas are built (for example to force an id collision).</summary>
    internal CcfSchemaRegistry(
        CcfSchemaOptions mvc,
        CcfSchemaOptions http,
        ILogger logger,
        Func<Type, CcfSchemaOptions, (CcfSchema? Schema, string? Reason)>? build)
    {
        SchemaOptions = mvc;
        HttpSchemaOptions = http;
        _logger = logger;
        _build = build ?? Build;
    }

    /// <inheritdoc />
    public CcfSchemaOptions SchemaOptions { get; }

    /// <inheritdoc />
    public CcfSchemaOptions HttpSchemaOptions { get; }

    /// <inheritdoc />
    public IReadOnlyCollection<CcfSchema> Schemas => _byId.Values.ToArray();

    /// <inheritdoc />
    public bool TryGetSchema(Type type, [NotNullWhen(true)] out CcfSchema? schema) => TryGetSchema(type, SchemaOptions, out schema);

    /// <inheritdoc />
    public bool TryGetSchema(Type type, CcfSchemaOptions options, [NotNullWhen(true)] out CcfSchema? schema)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        var key = KeyOf(type, options);
        var entry = _byKey.TryGetValue(key, out var existing) ? existing : _byKey.GetOrAdd(key, static (_, state) => state.Self.Resolve(state.Type, state.Options), (Self: this, Type: type, Options: options));
        schema = entry.Schema;
        return schema is not null;
    }

    /// <inheritdoc />
    public bool TryGetById(string id, [NotNullWhen(true)] out CcfSchema? schema) => _byId.TryGetValue(id, out schema);

    /// <inheritdoc />
    public CcfSchema Register(Type type) => Register(type, SchemaOptions);

    /// <inheritdoc />
    public CcfSchema Register<T>() => Register(typeof(T), SchemaOptions);

    /// <inheritdoc />
    public CcfSchema Register(Type type, CcfSchemaOptions options)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(options);
        var entry = _byKey.GetOrAdd(KeyOf(type, options), static (_, state) => state.Self.Resolve(state.Type, state.Options), (Self: this, Type: type, Options: options));
        return entry.Schema ?? throw new InvalidOperationException($"{type} cannot be served as CCF: {entry.Reason}");
    }

    private static (Type, JsonNamingPolicy?, int, System.Text.Json.JsonSerializerOptions?) KeyOf(Type type, CcfSchemaOptions options) =>
        (type, options.PropertyNamingPolicy, options.DefaultDecimalScale, options.JsonSerializerOptions);

    private Entry Resolve(Type type, CcfSchemaOptions options)
    {
        var (schema, reason) = _build(type, options);
        if (schema is null && TryGetEnumerableInterface(type, out var asEnumerable))
        {
            // A runtime collection type (LINQ iterator, HashSet<T>, …) is served through IEnumerable<T>.
            (schema, reason) = _build(asEnumerable, options);
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

    private static (CcfSchema? Schema, string? Reason) Build(Type type, CcfSchemaOptions options)
    {
        var ok = CcfSchemaBuilder.TryBuild(type, options, out var schema, out var reason);
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
