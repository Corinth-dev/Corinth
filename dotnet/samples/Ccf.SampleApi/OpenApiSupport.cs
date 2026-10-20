using Ccf.AspNetCore;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Ccf.SampleApi;

/// <summary>
/// OpenAPI additions that describe CCF: the opt-in header in the document description, and an
/// <c>application/vnd.ccf</c> response on every operation whose response type has a CCF schema.
/// </summary>
internal static class OpenApiSupport
{
    private const string CcfMediaType = "application/vnd.ccf";

    private const string Description = """
        JSON is the default. A client opts in to the compact binary **CCF** format (Columnar Compact Format) with
        `Accept: application/vnd.ccf; features=d`. Operations marked with an `application/vnd.ccf` response can
        answer in CCF; everything else, and anything CCF cannot serve, is answered in JSON.

        A CCF payload starts with a 4-byte schema id; fetch the schema once from `GET /ccf/schemas/{id}` and cache it.
        Try it with `curl -H 'Accept: application/vnd.ccf; features=d' http://localhost:5000/products --output out.ccf`.
        """;

    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "CCF sample API";
            document.Info.Version = "v1";
            document.Info.Description = Description;
            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            var registry = context.ApplicationServices.GetRequiredService<ISchemaRegistry>();
            var schemaOptions = context.Description.ActionDescriptor is ControllerActionDescriptor
                ? registry.SchemaOptions
                : registry.HttpSchemaOptions;

            // [Produces("application/json")] and the like restrict the offered media types: CCF is never offered there.
            if (context.Description.ActionDescriptor.EndpointMetadata.OfType<Microsoft.AspNetCore.Mvc.ProducesAttribute>().Any())
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
                operation.Description = $"CCF schema id: `{schema.IdHex}` (`GET /ccf/schemas/{schema.IdHex}`). " + operation.Description;
            }

            return Task.CompletedTask;
        });
    }
}
