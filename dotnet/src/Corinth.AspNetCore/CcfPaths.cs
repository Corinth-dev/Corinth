using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Corinth.AspNetCore;

/// <summary>Naming rules plus the encoder bound to them: one per JSON pipeline (MVC, minimal APIs).</summary>
internal sealed class CcfPath
{
    public CcfPath(CcfSchemaOptions schemaOptions, CcfEncoder encoder)
    {
        SchemaOptions = schemaOptions;
        Encoder = encoder;
    }

    public CcfSchemaOptions SchemaOptions { get; }

    public CcfEncoder Encoder { get; }
}

/// <summary>
/// CCF follows the JSON options of the path it serves: MVC output uses MVC's <c>JsonOptions</c>, minimal APIs the
/// <c>ConfigureHttpJsonOptions</c> ones, so a CCF and a JSON response always name fields identically.
/// </summary>
internal sealed class CcfPaths
{
    public CcfPaths(ISchemaRegistry registry, IOptions<CcfOptions> options)
    {
        var threshold = options.Value.DictionaryThreshold;
        Mvc = new CcfPath(registry.SchemaOptions, new CcfEncoder(registry.SchemaOptions, threshold));
        Http = new CcfPath(registry.HttpSchemaOptions, new CcfEncoder(registry.HttpSchemaOptions, threshold));
    }

    public CcfPath Mvc { get; }

    public CcfPath Http { get; }
}

/// <summary>
/// Turns a lazily evaluated <c>IEnumerable&lt;T&gt;</c> response into a list exactly once, so an LINQ or EF query is
/// never executed twice (once for CCF, again for the JSON fallback after an encode failure).
/// </summary>
internal static class ResponseMaterializer
{
    private static readonly ConcurrentDictionary<Type, Func<object, object>?> Cache = new();

    private static readonly System.Reflection.MethodInfo ToListMethod =
        typeof(ResponseMaterializer).GetMethod(nameof(ToList), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

    /// <summary>Returns <paramref name="value"/> itself when it is already a stable collection, otherwise a list of its items.</summary>
    public static object Materialize(object value)
    {
        if (value is string || value is Array || value is System.Collections.IList)
        {
            return value;
        }

        var converter = Cache.GetOrAdd(value.GetType(), static type =>
        {
            Type? element = null;
            foreach (var i in type.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                {
                    if (element is not null)
                    {
                        return null; // ambiguous
                    }

                    element = i.GetGenericArguments()[0];
                }
            }

            if (element is null || typeof(System.Collections.IDictionary).IsAssignableFrom(type))
            {
                return null;
            }

            return (Func<object, object>)ToListMethod.MakeGenericMethod(element).CreateDelegate(typeof(Func<object, object>));
        });

        return converter is null ? value : converter(value);
    }

    private static object ToList<T>(object value) => new List<T>((IEnumerable<T>)value);
}
