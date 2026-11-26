using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corinth.AspNetCore;

/// <summary>Endpoint mapping for the schema endpoint (SPEC §2.4).</summary>
public static class CcfEndpointExtensions
{
    /// <summary>
    /// Maps <c>GET {SchemaPathPrefix}/{schemaId}</c>: the canonical schema JSON with an immutable cache header,
    /// or 404 for an unknown or malformed id. Schemas are served from the same origin as the API.
    /// </summary>
    public static RouteHandlerBuilder MapCcfSchemas(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var prefix = endpoints.ServiceProvider.GetRequiredService<IOptions<CcfOptions>>().Value.SchemaPathPrefix.TrimEnd('/');
        return endpoints.MapGet(prefix + "/{id:regex(^[0-9a-f]{{8}}$)}", (string id, ISchemaRegistry registry, HttpContext context) =>
        {
            if (!registry.TryGetById(id, out var schema))
            {
                // Not cacheable: the id may be registered later (lazy registration, another instance).
                return Results.NotFound();
            }

            context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.Bytes(Encoding.UTF8.GetBytes(schema.CanonicalJson), "application/json");
        });
    }
}
