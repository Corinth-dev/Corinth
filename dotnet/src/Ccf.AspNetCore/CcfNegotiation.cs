using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Ccf.AspNetCore;

/// <summary>Content negotiation for <c>application/vnd.ccf</c> (SPEC §2).</summary>
public static class CcfNegotiation
{
    /// <summary>The CCF media type.</summary>
    public const string MediaType = "application/vnd.ccf";

    /// <summary>
    /// Decides whether the request's <c>Accept</c> header asks for CCF, with MVC's default rules (an <c>Accept</c>
    /// containing <c>*/*</c> counts as "no preference"). See <see cref="TryNegotiate(HttpRequest, bool, out CcfFeatures)"/>.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="features">The decoding features the client declared with <c>features=…</c>, quoted or bare.</param>
    public static bool TryNegotiate(HttpRequest request, out CcfFeatures features) =>
        TryNegotiate(request, respectBrowserAcceptHeader: false, out features);

    /// <summary>
    /// Decides whether the request's <c>Accept</c> header asks for CCF. CCF is opt-in: only an explicit
    /// <c>application/vnd.ccf</c> range counts, never a wildcard, and a range with <c>q=0</c> is "not acceptable".
    /// Ranges are ordered like MVC orders them (quality, then specificity, then position); CCF is served when it
    /// comes first among the ranges that name a representation the server can produce (CCF or JSON).
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="respectBrowserAcceptHeader">
    /// Mirrors <c>MvcOptions.RespectBrowserAcceptHeader</c>. When <c>false</c> (MVC's default) an <c>Accept</c> header
    /// that contains <c>*/*</c> is treated as no preference, so a browser-style header never selects CCF; controllers
    /// and minimal APIs therefore behave the same.
    /// </param>
    /// <param name="features">The decoding features the client declared with <c>features=…</c>, quoted or bare.</param>
    public static bool TryNegotiate(HttpRequest request, bool respectBrowserAcceptHeader, out CcfFeatures features)
    {
        ArgumentNullException.ThrowIfNull(request);
        features = CcfFeatures.None;
        if (!request.Headers.ContainsKey(HeaderNames.Accept))
        {
            return false;
        }

        IList<MediaTypeHeaderValue> accept;
        try
        {
            accept = request.GetTypedHeaders().Accept;
        }
        catch (Exception)
        {
            return false; // malformed header: behave as if absent (JSON)
        }

        if (accept.Count == 0)
        {
            return false;
        }

        if (!respectBrowserAcceptHeader)
        {
            foreach (var range in accept)
            {
                if (range.MatchesAllTypes)
                {
                    return false;
                }
            }
        }

        var best = -1;
        var bestKey = default((double Quality, int Specificity));
        var bestIsCcf = false;
        for (var i = 0; i < accept.Count; i++)
        {
            var range = accept[i];
            var quality = range.Quality ?? 1.0;
            if (quality <= 0 || !range.MediaType.HasValue)
            {
                continue;
            }

            bool isCcf = range.MediaType.Equals(MediaType, StringComparison.OrdinalIgnoreCase);
            if (!isCcf && !IsJsonCapable(range))
            {
                continue;
            }

            // Equal quality and specificity keep their order in the header, as in MVC.
            var key = (quality, range.MatchesAllTypes ? 0 : range.MatchesAllSubTypes ? 1 : 2);
            if (best < 0 || key.CompareTo(bestKey) > 0)
            {
                best = i;
                bestKey = key;
                bestIsCcf = isCcf;
            }
        }

        if (best < 0 || !bestIsCcf)
        {
            return false;
        }

        foreach (var p in accept[best].Parameters)
        {
            if (p.Name.Equals("features", StringComparison.OrdinalIgnoreCase))
            {
                features = CcfFeatureParser.Parse(p.Value.Value);
            }
        }

        return true;
    }

    private static bool IsJsonCapable(MediaTypeHeaderValue range) =>
        range.MatchesAllTypes
        || range.MediaType.Equals("application/*", StringComparison.OrdinalIgnoreCase)
        || range.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || range.MediaType.Equals("text/json", StringComparison.OrdinalIgnoreCase)
        || (range.Suffix.HasValue && range.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds <c>Vary: Accept</c> unless the response already varies on it (SPEC §2.3).</summary>
    public static void AddVaryAccept(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        foreach (var value in response.Headers.Vary)
        {
            if (value is null)
            {
                continue;
            }

            foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "*" || token.Equals("Accept", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        response.Headers.Append(HeaderNames.Vary, "Accept");
    }
}
