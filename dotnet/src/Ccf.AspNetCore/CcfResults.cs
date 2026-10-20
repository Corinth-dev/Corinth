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
        var json = TypedResults.Ok(_value);

        if (_value is null)
        {
            await json.ExecuteAsync(httpContext);
            return;
        }

        var type = typeof(T) == typeof(object) ? _value.GetType() : typeof(T);
        if (!registry.TryGetSchema(type, out var schema))
        {
            await json.ExecuteAsync(httpContext);
            return;
        }

        CcfNegotiation.AddVaryAccept(httpContext.Response);
        var respect = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.MvcOptions>>().Value.RespectBrowserAcceptHeader;
        if (!CcfNegotiation.TryNegotiate(httpContext.Request, respect, out var features))
        {
            await json.ExecuteAsync(httpContext);
            return;
        }

        var encoder = services.GetRequiredService<CcfEncoder>();
        var options = services.GetRequiredService<IOptions<CcfOptions>>().Value;
        using var buffer = new PooledBufferWriter();
        CcfPayloadFlags flags;
        try
        {
            flags = encoder.Encode(_value, schema, features, buffer, options.Limits);
        }
        catch (CcfEncodeException e)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger("Ccf")
                .LogWarning(e, "CCF encoding of schema {SchemaId} ({Type}) failed; serving JSON instead: {Reason}", schema.IdHex, type, e.Message);
            CcfDiagnostics.RecordEncodeFailure();
            await json.ExecuteAsync(httpContext);
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
