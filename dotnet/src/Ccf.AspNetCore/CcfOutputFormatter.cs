using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ccf.AspNetCore;

/// <summary>
/// MVC output formatter for <c>application/vnd.ccf</c>. It only claims a response when the client explicitly
/// asked for CCF and the response type has a schema; everything else (including an encode failure) is served
/// by the JSON formatter exactly as without CCF (ARCHITECTURE §3.2).
/// </summary>
public sealed class CcfOutputFormatter : OutputFormatter
{
    private readonly ISchemaRegistry _registry;
    private readonly CcfPaths _paths;
    private readonly IOptions<CcfOptions> _options;
    private readonly ILogger _logger;

    internal CcfOutputFormatter(ISchemaRegistry registry, CcfPaths paths, IOptions<CcfOptions> options, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _paths = paths;
        _options = options;
        _logger = loggerFactory.CreateLogger("Ccf");
        SupportedMediaTypes.Add(CcfNegotiation.MediaType);
    }

    /// <inheritdoc />
    protected override bool CanWriteType(Type? type) => type is not null && _registry.TryGetSchema(type, _paths.Mvc.SchemaOptions, out _);

    /// <inheritdoc />
    public override bool CanWriteResult(OutputFormatterCanWriteContext context)
    {
        // MVC offers each acceptable media type of the request as context.ContentType. Do not rely on the base
        // implementation: its MediaType.IsSubsetOf test requires the formatter's media type to carry every
        // parameter of the Accept range, so `application/vnd.ccf` would never match `application/vnd.ccf; features=d`
        // (verified by CcfMediaTypeMatchingTests). Compare type/subtype only and read `features` from the request.
        if (!context.ContentType.HasValue || context.Object is null)
        {
            return false; // no explicit media type (wildcard or no Accept): CCF is opt-in, leave it to JSON
        }

        var offered = context.ContentType.Value!;
        var semicolon = offered.IndexOf(';');
        var typeAndSubtype = (semicolon < 0 ? offered : offered[..semicolon]).Trim();
        if (!typeAndSubtype.Equals(CcfNegotiation.MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // MVC offers CCF whenever an Accept range names it, even with q=0; our negotiation is the authority.
        if (!CcfNegotiation.TryNegotiate(context.HttpContext.Request, out _))
        {
            return false;
        }

        return _registry.TryGetSchema(context.ObjectType ?? context.Object.GetType(), _paths.Mvc.SchemaOptions, out _);
    }

    /// <inheritdoc />
    public override async Task WriteResponseBodyAsync(OutputFormatterWriteContext context)
    {
        var http = context.HttpContext;
        var type = context.ObjectType ?? context.Object!.GetType();
        if (!_registry.TryGetSchema(type, _paths.Mvc.SchemaOptions, out var schema))
        {
            await WriteJsonAsync(context, context.Object!);
            return;
        }

        // A lazy IEnumerable<T> (LINQ, EF) is enumerated exactly once, here; the JSON fallback reuses the list.
        var value = ResponseMaterializer.Materialize(context.Object!);
        CcfNegotiation.TryNegotiate(http.Request, out var features);
        using var buffer = new PooledBufferWriter();
        CcfPayloadFlags flags;
        try
        {
            flags = _paths.Mvc.Encoder.Encode(value, schema, features, buffer, _options.Value.Limits);
        }
        catch (CcfEncodeException e)
        {
            // Nothing has been written yet (SPEC §10.2): fall back to JSON, invisibly to the client.
            _logger.LogWarning(e, "CCF encoding of schema {SchemaId} ({Type}) failed; serving JSON instead: {Reason}", schema.IdHex, type, e.Message);
            CcfDiagnostics.RecordEncodeFailure();
            CcfNegotiation.AddVaryAccept(http.Response);
            await WriteJsonAsync(context, value);
            return;
        }

        var response = http.Response;
        response.ContentType = CcfFeatureParser.ContentTypeFor(flags);
        response.ContentLength = buffer.WrittenCount;
        CcfNegotiation.AddVaryAccept(response);
        await response.Body.WriteAsync(buffer.WrittenMemory, http.RequestAborted);
        CcfDiagnostics.RecordServed(flags, buffer.WrittenCount);
    }

    private static Task WriteJsonAsync(OutputFormatterWriteContext context, object value)
    {
        var mvc = context.HttpContext.RequestServices.GetRequiredService<IOptions<MvcOptions>>().Value;
        var json = mvc.OutputFormatters.OfType<TextOutputFormatter>().FirstOrDefault(f => f.SupportedMediaTypes.Contains("application/json"))
            ?? throw new InvalidOperationException("CCF fell back to JSON but no JSON output formatter is registered.");

        // Write the materialized value (not the original lazy enumerable) through a fresh context.
        var jsonContext = ReferenceEquals(value, context.Object)
            ? context
            : new OutputFormatterWriteContext(context.HttpContext, context.WriterFactory, value.GetType(), value);
        jsonContext.ContentType = new Microsoft.Extensions.Primitives.StringSegment("application/json");
        jsonContext.ContentTypeIsServerDefined = false;
        return json.WriteAsync(jsonContext);
    }
}

/// <summary>
/// Adds <c>Vary: Accept</c> to every MVC response whose value has a CCF schema, including the ones served as
/// JSON (SPEC §2.3: "every response from an endpoint that can serve CCF").
/// </summary>
internal sealed class CcfVaryResultFilter : IResultFilter
{
    private readonly ISchemaRegistry _registry;

    public CcfVaryResultFilter(ISchemaRegistry registry)
    {
        _registry = registry;
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult { Value: { } value } result)
        {
            var type = result.DeclaredType is { } declared && declared != typeof(object) ? declared : value.GetType();
            if (_registry.TryGetSchema(type, out _))
            {
                CcfNegotiation.AddVaryAccept(context.HttpContext.Response);
            }
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
