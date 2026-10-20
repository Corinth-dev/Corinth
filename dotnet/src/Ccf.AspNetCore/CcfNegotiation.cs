using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Ccf.AspNetCore;

/// <summary>Content negotiation for <c>application/vnd.ccf</c> (SPEC §2).</summary>
public static class CcfNegotiation
{
    /// <summary>The CCF media type.</summary>
    public const string MediaType = "application/vnd.ccf";

    /// <summary>
    /// Decides whether the request's <c>Accept</c> header asks for CCF. CCF is opt-in: it is served only when
    /// <c>application/vnd.ccf</c> is listed <em>explicitly</em> with <c>q &gt; 0</c>, and no other explicit media type
    /// that the server can produce (<c>application/json</c>, <c>text/json</c>, <c>*+json</c>) beats it.
    /// </summary>
    /// <remarks>
    /// Wildcard ranges (<c>*/*</c>, <c>application/*</c>) never compete: they only mean "anything", so
    /// <c>application/vnd.ccf, */*;q=0.1</c> yields CCF even though MVC would ignore a header containing <c>*/*</c>.
    /// Among explicit ranges the highest quality wins and equal qualities keep their header order (MVC's rule), so
    /// <c>application/json, application/vnd.ccf</c> yields JSON. A range with <c>q=0</c> is "not acceptable".
    /// </remarks>
    /// <param name="request">The request.</param>
    /// <param name="features">The decoding features the client declared with <c>features=…</c>, quoted or bare.</param>
    public static bool TryNegotiate(HttpRequest request, out CcfFeatures features)
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

        var best = -1;
        var bestQuality = 0.0;
        var bestIsCcf = false;
        for (var i = 0; i < accept.Count; i++)
        {
            var range = accept[i];
            var quality = range.Quality ?? 1.0;
            if (quality <= 0 || !range.MediaType.HasValue || range.MatchesAllTypes || range.MatchesAllSubTypes
                || range.MatchesAllSubTypesWithoutSuffix)
            {
                continue;
            }

            var isCcf = range.MediaType.Equals(MediaType, StringComparison.OrdinalIgnoreCase);
            if (!isCcf && !IsExplicitJson(range))
            {
                continue;
            }

            if (quality > bestQuality) // strictly greater: equal qualities keep header order
            {
                best = i;
                bestQuality = quality;
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

    /// <summary>True when any <c>Accept</c> value mentions <c>application/vnd.ccf</c>, whatever its quality.</summary>
    public static bool Mentions(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (var value in request.Headers.Accept)
        {
            if (value is not null && value.Contains(MediaType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExplicitJson(MediaTypeHeaderValue range) =>
        range.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
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
