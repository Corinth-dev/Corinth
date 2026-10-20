using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Ccf.AspNetCore;
using Microsoft.Extensions.Options;

namespace Ccf.SampleApi;

/// <summary>
/// A development aid for looking at CCF in the browser: <c>POST /ccf/decode</c> turns a CCF payload into JSON, and a
/// small script (<c>/ccf/scalar-viewer.js</c>) makes the Scalar UI use it, so a CCF response sent from Scalar is shown
/// as the JSON it encodes. Uses only the public decoder and the schema registry.
/// </summary>
internal static class CcfViewer
{
    public const string ScriptPath = "/ccf/scalar-viewer.js";

    public static void Map(WebApplication app)
    {
        app.MapPost("/ccf/decode", DecodeAsync)
            .WithTags("CCF")
            .WithSummary("Decodes a CCF payload (the request body) into JSON, using the schema registered for its schema id.")
            .Accepts<byte[]>("application/vnd.ccf")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound);

        var script = ReadScript();
        app.MapGet(ScriptPath, () => Results.Text(script, "text/javascript")).ExcludeFromDescription();
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

    private static string ReadScript()
    {
        var assembly = typeof(CcfViewer).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("scalar-viewer.js", StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }
}
