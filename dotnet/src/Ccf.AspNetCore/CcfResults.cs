using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ccf.AspNetCore;

/// <summary>Results for minimal APIs that negotiate CCF.</summary>
public static class CcfResults
{
    /// <summary>
    /// A 200 result that is CCF when the client accepts it and <typeparamref name="T"/> has a schema, and
    /// otherwise exactly <c>TypedResults.Ok(value)</c> (JSON). An encode failure also falls back to JSON.
    /// </summary>
    public static IResult Ok<T>(T value) => new CcfOkResult<T>(value);
}

internal sealed class CcfOkResult<T> : IResult
{
    private readonly T _value;

    public CcfOkResult(T value)
    {
        _value = value;
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var registry = services.GetRequiredService<ISchemaRegistry>();
        var path = services.GetRequiredService<CcfPaths>().Http;

        if (_value is null)
        {
            await TypedResults.Ok(_value).ExecuteAsync(httpContext);
            return;
        }

        var type = typeof(T) == typeof(object) ? _value.GetType() : typeof(T);
        if (!registry.TryGetSchema(type, path.SchemaOptions, out var schema))
        {
            await TypedResults.Ok(_value).ExecuteAsync(httpContext);
            return;
        }

        // An endpoint that can serve CCF varies on Accept, whichever representation this request gets.
        CcfNegotiation.AddVaryAccept(httpContext.Response);
        if (!CcfNegotiation.TryNegotiate(httpContext.Request, out var features))
        {
            await TypedResults.Ok(_value).ExecuteAsync(httpContext);
            return;
        }

        // A lazy IEnumerable<T> (LINQ, EF) is enumerated exactly once, here; the JSON fallback reuses the list.
        var value = ResponseMaterializer.Materialize(_value!);
        var options = services.GetRequiredService<IOptions<CcfOptions>>().Value;
        using var buffer = new PooledBufferWriter();
        CcfPayloadFlags flags;
        try
        {
            flags = path.Encoder.Encode(value, schema, features, buffer, options.Limits);
        }
        catch (CcfEncodeException e)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Ccf")
                .LogWarning(e, "CCF encoding of schema {SchemaId} ({Type}) failed; serving JSON instead: {Reason}", schema.IdHex, type, e.Message);
            CcfDiagnostics.RecordEncodeFailure();
            await TypedResults.Ok(value).ExecuteAsync(httpContext);
            return;
        }

        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = CcfFeatureParser.ContentTypeFor(flags);
        response.ContentLength = buffer.WrittenCount;
        await response.Body.WriteAsync(buffer.WrittenMemory, httpContext.RequestAborted);
        CcfDiagnostics.RecordServed(flags, buffer.WrittenCount);
    }
}
