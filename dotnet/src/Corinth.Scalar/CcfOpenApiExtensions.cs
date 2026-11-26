using Corinth.AspNetCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace Corinth.Scalar;

/// <summary>Describes CCF in the OpenAPI document.</summary>
public static class CcfOpenApiExtensions
{
    private const string CcfMediaType = "application/vnd.ccf";

    /// <summary>
    /// Adds CCF to the generated OpenAPI document: a note on how to opt in (appended to the document description),
    /// and an <c>application/vnd.ccf</c> (binary) response next to JSON on every operation whose 200 response type
    /// has a CCF schema, with the schema id in the operation description. Operations restricted by
    /// <c>[Produces]</c> are skipped, because CCF is never offered there. Needs <c>AddCcf</c>; minimal APIs must
    /// declare their response type (<c>Produces&lt;T&gt;()</c>) to be listed.
    /// </summary>
    public static OpenApiOptions AddCcf(this OpenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddDocumentTransformer((document, context, _) =>
        {
            var prefix = context.ApplicationServices.GetRequiredService<IOptions<CcfOptions>>().Value.SchemaPathPrefix.TrimEnd('/');
            var note = "**CCF.** JSON is the default. A client opts in to the compact binary CCF format (Corinthian Columnar Format) with "
                + "`Accept: application/vnd.ccf; features=d`. Operations that list an `application/vnd.ccf` response can answer in CCF; "
                + "everything else, and anything CCF cannot serve, is answered in JSON. A CCF payload starts with a 4-byte schema id: "
                + $"fetch the schema once from `GET {prefix}/{{id}}` and cache it.";
            document.Info ??= new OpenApiInfo();
            document.Info.Description = string.IsNullOrEmpty(document.Info.Description) ? note : document.Info.Description + "\n\n" + note;
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var registry = context.ApplicationServices.GetRequiredService<ISchemaRegistry>();
            var prefix = context.ApplicationServices.GetRequiredService<IOptions<CcfOptions>>().Value.SchemaPathPrefix.TrimEnd('/');
            var schemaOptions = context.Description.ActionDescriptor is ControllerActionDescriptor
                ? registry.SchemaOptions
                : registry.HttpSchemaOptions;

            // [Produces("application/json")] and the like restrict the offered media types: CCF is never offered there.
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ProducesAttribute>().Any())
            {
                return Task.CompletedTask;
            }

            var ok = context.Description.SupportedResponseTypes.FirstOrDefault(r => r.StatusCode == 200);
            if (ok?.Type is { } type && type != typeof(void) && registry.TryGetSchema(type, schemaOptions, out var schema)
                && operation.Responses is not null && operation.Responses.TryGetValue("200", out var response)
                && response.Content is not null)
            {
                response.Content[CcfMediaType] = new OpenApiMediaType
                {
                    Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" },
                };
                operation.Description = $"CCF schema id: `{schema.IdHex}` (`GET {prefix}/{schema.IdHex}`). " + operation.Description;
            }

            return Task.CompletedTask;
        });

        return options;
    }
}
