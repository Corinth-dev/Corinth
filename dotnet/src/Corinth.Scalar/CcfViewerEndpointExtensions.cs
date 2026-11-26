using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Corinth.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Corinth.Scalar;

/// <summary>Maps the endpoints behind the CCF toggle of the Scalar API reference.</summary>
public static class CcfViewerEndpointExtensions
{
    /// <summary>
    /// Maps <c>POST /ccf/decode</c> (decodes a CCF payload with the public decoder and the schema registry of
    /// <c>AddCcf</c>, and answers JSON) and <c>GET /ccf/scalar-viewer.js</c> (the script Scalar loads through
    /// <see cref="CcfScalarExtensions.WithCcfViewer"/>).
    /// </summary>
    /// <remarks>
    /// Outside the Development environment (unless <see cref="CcfViewerOptions.EnableOutsideDevelopment"/> is set)
    /// the decode endpoint is not mapped and the script is an empty module, so the toggle does not appear.
    /// The returned builder carries conventions to every viewer endpoint, for example
    /// <c>MapCcfViewer().RequireAuthorization()</c>.
    /// </remarks>
    /// <returns>A group builder for the viewer endpoints.</returns>
    public static RouteGroupBuilder MapCcfViewer(this IEndpointRouteBuilder endpoints, Action<CcfViewerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = new CcfViewerOptions();
        configure?.Invoke(options);

        var group = endpoints.MapGroup(string.Empty);
        var enabled = options.EnableOutsideDevelopment || endpoints.ServiceProvider.GetRequiredService<IHostEnvironment>().IsDevelopment();

        var script = enabled ? ViewerScript.Text : "export default {};\n";
        group.MapGet(CcfViewer.ScriptPath, () => Results.Text(script, "text/javascript")).ExcludeFromDescription();

        if (enabled)
        {
            group.MapPost(CcfViewer.DecodePath, DecodeAsync)
                .WithTags("CCF")
                .WithSummary("Decodes a CCF payload (the request body) into JSON, using the schema registered for its schema id.")
                .Accepts<byte[]>("application/vnd.ccf")
                .Produces(StatusCodes.Status200OK, contentType: "application/json")
                .Produces(StatusCodes.Status400BadRequest)
                .Produces(StatusCodes.Status404NotFound);
        }

        return group;
    }

    private static async Task<IResult> DecodeAsync(HttpRequest request, ISchemaRegistry registry, IOptions<CcfOptions> options)
    {
        var limits = options.Value.Limits;
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int read;
            while ((read = await request.Body.ReadAsync(chunk)) > 0)
            {
                if (buffer.Length + read > limits.MaxPayloadBytes)
                {
                    return Results.Problem("The payload is larger than the configured limit.", statusCode: StatusCodes.Status400BadRequest);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        var payload = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        try
        {
            var id = CcfDecoder.PeekSchemaId(payload);
            if (!registry.TryGetById(id, out var schema))
            {
                return Results.Problem($"No schema with id {id} is registered on this server.", statusCode: StatusCodes.Status404NotFound);
            }

            var value = CcfDecoder.DecodeValue(payload, schema, limits);
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
            {
                WriteValue(writer, value);
            }

            return new SchemaIdResult(Results.Bytes(output.ToArray(), "application/json"), id);
        }
        catch (CcfFormatException e)
        {
            return Results.Problem($"{e.Code}: {e.Message}", statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>Adds the schema id as a response header, so the viewer can show which schema decoded the payload.</summary>
    private sealed class SchemaIdResult : IResult
    {
        private readonly IResult _inner;
        private readonly string _id;

        public SchemaIdResult(IResult inner, string id)
        {
            _inner = inner;
            _id = id;
        }

        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers["X-Ccf-Schema-Id"] = _id;
            return _inner.ExecuteAsync(httpContext);
        }
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case uint u: w.WriteNumberValue(u); break;
            case ulong ul: w.WriteNumberValue(ul); break;
            case decimal m: w.WriteNumberValue(m); break;
            case double d when double.IsFinite(d): w.WriteNumberValue(d); break;
            case double d: w.WriteStringValue(d.ToString(CultureInfo.InvariantCulture)); break; // NaN and infinities have no JSON number
            case string s: w.WriteStringValue(s); break;
            case DateTime dt: w.WriteStringValue(dt); break;
            case Guid g: w.WriteStringValue(g); break;
            case CcfObject o:
                w.WriteStartObject();
                for (var i = 0; i < o.Names.Count; i++)
                {
                    w.WritePropertyName(o.Names[i]);
                    WriteValue(w, o.Values[i]);
                }

                w.WriteEndObject();
                break;
            case object?[] list:
                w.WriteStartArray();
                foreach (var item in list)
                {
                    WriteValue(w, item);
                }

                w.WriteEndArray();
                break;
            default: w.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }
}

internal static class ViewerScript
{
    public static readonly string Text = Read();

    private static string Read()
    {
        using var stream = typeof(ViewerScript).Assembly.GetManifestResourceStream("Corinth.Scalar.scalar-viewer.js")
            ?? throw new InvalidOperationException("The embedded CCF viewer script is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
