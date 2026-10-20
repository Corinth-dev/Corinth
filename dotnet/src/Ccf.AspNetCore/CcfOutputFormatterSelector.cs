using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

namespace Ccf.AspNetCore;

/// <summary>
/// Decorates MVC's formatter selection. MVC ignores the whole <c>Accept</c> header when it contains <c>*/*</c>
/// (unless <c>RespectBrowserAcceptHeader</c> is set), which would hide an explicit <c>application/vnd.ccf</c>
/// range; this selector applies CCF's negotiation rule first and delegates everything else to MVC unchanged.
/// </summary>
internal sealed class CcfOutputFormatterSelector : OutputFormatterSelector
{
    private readonly OutputFormatterSelector _inner;
    private readonly IOptions<MvcOptions> _mvcOptions;

    public CcfOutputFormatterSelector(OutputFormatterSelector inner, IOptions<MvcOptions> mvcOptions)
    {
        _inner = inner;
        _mvcOptions = mvcOptions;
    }

    public override IOutputFormatter? SelectFormatter(
        OutputFormatterCanWriteContext context, IList<IOutputFormatter> formatters, MediaTypeCollection contentTypes)
    {
        if (CcfNegotiation.TryNegotiate(context.HttpContext.Request, out _) && OffersCcf(contentTypes))
        {
            // ObjectResultExecutor passes an empty list when the result has no formatters of its own; MVC's selector
            // then falls back to MvcOptions.OutputFormatters, and so must we.
            IList<IOutputFormatter> candidates = formatters.Count > 0 ? formatters : _mvcOptions.Value.OutputFormatters;
            foreach (var formatter in candidates)
            {
                if (formatter is CcfOutputFormatter ccf)
                {
                    var previous = context.ContentType;
                    var previousServerDefined = context.ContentTypeIsServerDefined;
                    context.ContentType = new Microsoft.Extensions.Primitives.StringSegment(CcfNegotiation.MediaType);
                    context.ContentTypeIsServerDefined = false;
                    if (ccf.CanWriteResult(context))
                    {
                        return ccf;
                    }

                    context.ContentType = previous;
                    context.ContentTypeIsServerDefined = previousServerDefined;
                }
            }
        }

        return _inner.SelectFormatter(context, formatters, contentTypes);
    }

    /// <summary>[Produces] and ObjectResult.ContentTypes restrict what may be offered: honour them.</summary>
    private static bool OffersCcf(MediaTypeCollection contentTypes)
    {
        if (contentTypes.Count == 0)
        {
            return true;
        }

        foreach (var type in contentTypes)
        {
            var semicolon = type.IndexOf(';');
            var name = (semicolon < 0 ? type : type[..semicolon]).Trim();
            if (name.Equals(CcfNegotiation.MediaType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
