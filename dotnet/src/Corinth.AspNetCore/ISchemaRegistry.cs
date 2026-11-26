using System.Diagnostics.CodeAnalysis;

namespace Corinth.AspNetCore;

/// <summary>
/// Maps CLR types to CCF schemas and schema ids to schemas. Types are registered lazily the first time they
/// are encoded; call <see cref="Register{T}"/> at startup to make a schema available to the schema endpoint
/// before the first request (important behind a load balancer).
/// </summary>
/// <remarks>
/// Schemas are keyed by (type, naming policy): the MVC path names fields like MVC's JSON output
/// (<see cref="SchemaOptions"/>), the minimal-API path like <c>ConfigureHttpJsonOptions</c>
/// (<see cref="HttpSchemaOptions"/>). Different naming gives a different schema and a different id.
/// </remarks>
public interface ISchemaRegistry
{
    /// <summary>Naming rules and defaults of the MVC path (MVC <c>JsonOptions</c>); the default for the overloads without options.</summary>
    CcfSchemaOptions SchemaOptions { get; }

    /// <summary>Naming rules and defaults of the minimal-API path (<c>Microsoft.AspNetCore.Http.Json.JsonOptions</c>).</summary>
    CcfSchemaOptions HttpSchemaOptions { get; }

    /// <summary>All schemas registered so far.</summary>
    IReadOnlyCollection<CcfSchema> Schemas { get; }

    /// <summary>
    /// Gets the schema for <paramref name="type"/> under <paramref name="options"/>, building and registering it on
    /// first use. Returns <c>false</c> when the type cannot be expressed in CCF (the response is then served as
    /// JSON) or when its schema id collides with a different registered schema.
    /// </summary>
    bool TryGetSchema(Type type, CcfSchemaOptions options, [NotNullWhen(true)] out CcfSchema? schema);

    /// <summary>Same as <see cref="TryGetSchema(Type, CcfSchemaOptions, out CcfSchema)"/> with the MVC naming rules.</summary>
    bool TryGetSchema(Type type, [NotNullWhen(true)] out CcfSchema? schema) => TryGetSchema(type, SchemaOptions, out schema);

    /// <summary>Gets a registered schema by its 8-character lowercase hex id.</summary>
    bool TryGetById(string id, [NotNullWhen(true)] out CcfSchema? schema);

    /// <summary>Registers <paramref name="type"/> now under <paramref name="options"/>.</summary>
    /// <exception cref="InvalidOperationException">The type is unsupported, or its id collides with another schema.</exception>
    CcfSchema Register(Type type, CcfSchemaOptions options);

    /// <summary>Registers <paramref name="type"/> now with the MVC naming rules.</summary>
    /// <exception cref="InvalidOperationException">The type is unsupported, or its id collides with another schema.</exception>
    CcfSchema Register(Type type) => Register(type, SchemaOptions);

    /// <summary>Registers <typeparamref name="T"/> now with the MVC naming rules.</summary>
    /// <exception cref="InvalidOperationException">The type is unsupported, or its id collides with another schema.</exception>
    CcfSchema Register<T>() => Register(typeof(T), SchemaOptions);
}
