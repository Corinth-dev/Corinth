using System.Diagnostics.CodeAnalysis;

namespace Ccf.AspNetCore;

/// <summary>
/// Maps CLR types to CCF schemas and schema ids to schemas. Types are registered lazily the first time they
/// are encoded; call <see cref="Register{T}"/> at startup to make a schema available to the schema endpoint
/// before the first request (important behind a load balancer).
/// </summary>
public interface ISchemaRegistry
{
    /// <summary>The naming rules and defaults schemas are built with; the encoder must use the same.</summary>
    CcfSchemaOptions SchemaOptions { get; }

    /// <summary>All schemas registered so far.</summary>
    IReadOnlyCollection<CcfSchema> Schemas { get; }

    /// <summary>
    /// Gets the schema for <paramref name="type"/>, building and registering it on first use. Returns
    /// <c>false</c> when the type cannot be expressed in CCF (the response is then served as JSON) or when its
    /// schema id collides with a different registered schema.
    /// </summary>
    bool TryGetSchema(Type type, [NotNullWhen(true)] out CcfSchema? schema);

    /// <summary>Gets a registered schema by its 8-character lowercase hex id.</summary>
    bool TryGetById(string id, [NotNullWhen(true)] out CcfSchema? schema);

    /// <summary>Registers <paramref name="type"/> now.</summary>
    /// <exception cref="InvalidOperationException">The type is unsupported, or its id collides with another schema.</exception>
    CcfSchema Register(Type type);

    /// <summary>Registers <typeparamref name="T"/> now.</summary>
    /// <exception cref="InvalidOperationException">The type is unsupported, or its id collides with another schema.</exception>
    CcfSchema Register<T>() => Register(typeof(T));
}
