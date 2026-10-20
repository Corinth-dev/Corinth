using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace Ccf.AspNetCore.Tests;

public class NegotiationTests
{
    private static HttpRequest Request(string? accept)
    {
        var context = new DefaultHttpContext();
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        return context.Request;
    }

    [Theory]
    // opt-in only
    [InlineData(null, false, CcfFeatures.None)]
    [InlineData("", false, CcfFeatures.None)]
    [InlineData("application/json", false, CcfFeatures.None)]
    [InlineData("*/*", false, CcfFeatures.None)]
    [InlineData("application/*", false, CcfFeatures.None)]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", false, CcfFeatures.None)]
    // CCF requested
    [InlineData("application/vnd.ccf", true, CcfFeatures.None)]
    [InlineData("application/vnd.ccf; features=d", true, CcfFeatures.Dictionary)]
    [InlineData("application/vnd.ccf;features=d", true, CcfFeatures.Dictionary)]
    [InlineData("Application/VND.CCF; Features=D", true, CcfFeatures.None)] // letters are case-sensitive; unknown ones are ignored
    [InlineData("application/vnd.ccf; features=\"d,n\"", true, CcfFeatures.Dictionary)]
    [InlineData("application/vnd.ccf; features=\"n\"", true, CcfFeatures.None)]
    [InlineData("application/vnd.ccf; features=x", true, CcfFeatures.None)]
    [InlineData("application/vnd.ccf; features=d, application/json;q=0.5", true, CcfFeatures.Dictionary)]
    [InlineData("application/json;q=0.5, application/vnd.ccf; features=d", true, CcfFeatures.Dictionary)]
    [InlineData("application/vnd.ccf; features=d;q=0.9, application/json;q=0.8", true, CcfFeatures.Dictionary)]
    [InlineData("application/vnd.ccf; features=d, application/json;q=1.0", true, CcfFeatures.Dictionary)]
    [InlineData("application/vnd.ccf, application/vnd.ccf; features=d", true, CcfFeatures.None)] // first of equals
    // JSON preferred
    [InlineData("application/json, application/vnd.ccf;q=0.5", false, CcfFeatures.None)]
    [InlineData("application/json, application/vnd.ccf", false, CcfFeatures.None)] // equal quality: header order
    [InlineData("application/vnd.ccf;q=0.4, application/json;q=0.5", false, CcfFeatures.None)]
    [InlineData("application/vnd.ccf;q=0", false, CcfFeatures.None)]
    [InlineData("application/vnd.ccf; features=d; q=0", false, CcfFeatures.None)]
    [InlineData("application/vnd.ccf;q=0, application/json", false, CcfFeatures.None)]
    [InlineData("application/vnd.ccf, application/*;q=0.9", true, CcfFeatures.None)]
    [InlineData("application/hal+json, application/vnd.ccf;q=0.5", false, CcfFeatures.None)]
    // media types the server cannot produce do not compete
    [InlineData("text/html, application/vnd.ccf;q=0.1", true, CcfFeatures.None)]
    [InlineData("image/png, application/vnd.ccf; features=d;q=0.1, text/plain", true, CcfFeatures.Dictionary)]
    // malformed
    [InlineData("application/vnd.ccf; features=", true, CcfFeatures.None)]
    [InlineData(";;;", false, CcfFeatures.None)]
    public void Accept_headers_without_a_wildcard(string? accept, bool expectedCcf, CcfFeatures expectedFeatures)
    {
        var ccf = CcfNegotiation.TryNegotiate(Request(accept), out var features);

        Assert.Equal(expectedCcf, ccf);
        Assert.Equal(expectedFeatures, features);
    }

    [Theory]
    [InlineData("application/vnd.ccf, */*;q=0.1", true)]
    [InlineData("application/vnd.ccf; features=d, */*", true)]
    [InlineData("*/*;q=0.1, application/vnd.ccf", true)]
    [InlineData("*/*;q=0.9, application/vnd.ccf;q=0.2", false)] // the wildcard wins by quality when the header is respected
    public void A_wildcard_makes_mvc_ignore_the_header_unless_RespectBrowserAcceptHeader_is_set(string accept, bool ccfWhenRespected)
    {
        Assert.False(CcfNegotiation.TryNegotiate(Request(accept), out _));
        Assert.False(CcfNegotiation.TryNegotiate(Request(accept), respectBrowserAcceptHeader: false, out _));
        Assert.Equal(ccfWhenRespected, CcfNegotiation.TryNegotiate(Request(accept), respectBrowserAcceptHeader: true, out _));
    }

    [Fact]
    public void Features_come_from_the_range_that_won()
    {
        Assert.True(CcfNegotiation.TryNegotiate(Request("application/vnd.ccf;q=0.4, application/vnd.ccf; features=d;q=0.9"), out var features));
        Assert.Equal(CcfFeatures.Dictionary, features);
    }

    [Fact]
    public void Several_accept_header_lines_are_combined()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Append("Accept", "application/json;q=0.5");
        context.Request.Headers.Append("Accept", "application/vnd.ccf; features=d");
        Assert.True(CcfNegotiation.TryNegotiate(context.Request, out var features));
        Assert.Equal(CcfFeatures.Dictionary, features);
    }

    [Theory]
    [InlineData(null, new[] { "Accept" })]
    [InlineData("Accept", new[] { "Accept" })]
    [InlineData("accept", new[] { "accept" })]
    [InlineData("Accept-Encoding", new[] { "Accept-Encoding", "Accept" })]
    [InlineData("Origin, Accept", new[] { "Origin, Accept" })]
    [InlineData("*", new[] { "*" })]
    public void Vary_Accept_is_added_once(string? existing, string[] expected)
    {
        var context = new DefaultHttpContext();
        if (existing is not null)
        {
            context.Response.Headers.Vary = existing;
        }

        CcfNegotiation.AddVaryAccept(context.Response);
        CcfNegotiation.AddVaryAccept(context.Response);

        Assert.Equal(expected, context.Response.Headers.Vary.ToArray());
    }
}

/// <summary>
/// Answers the question ARCHITECTURE §3.2 asks to verify early: how does ASP.NET Core's media type matching treat
/// the <c>features</c> parameter, on the request's Accept header and on the formatter's supported type?
/// </summary>
public class CcfMediaTypeMatchingTests
{
    [Theory]
    [InlineData("application/vnd.ccf", "application/vnd.ccf", true)]
    [InlineData("application/vnd.ccf", "application/vnd.ccf; features=d", false)]   // formatter type lacks the parameter
    [InlineData("application/vnd.ccf; features=d", "application/vnd.ccf; features=d", true)]
    [InlineData("application/vnd.ccf; features=d", "application/vnd.ccf", true)]
    [InlineData("application/vnd.ccf", "application/*", true)]
    [InlineData("application/vnd.ccf", "*/*", true)]
    public void MediaType_IsSubsetOf_requires_the_candidate_to_carry_every_parameter_of_the_range(string supported, string range, bool expected)
    {
        var s = new MediaTypeHeaderValue(supported.Split(';')[0]);
        foreach (var p in supported.Split(';').Skip(1))
        {
            var kv = p.Split('=', 2, StringSplitOptions.TrimEntries);
            s.Parameters.Add(new NameValueHeaderValue(kv[0], kv[1]));
        }

        Assert.Equal(expected, s.IsSubsetOf(MediaTypeHeaderValue.Parse(range)));
    }

    /// <summary>A formatter that records what MVC offers it and what the stock OutputFormatter logic would answer.</summary>
    private sealed class Probe : Microsoft.AspNetCore.Mvc.Formatters.OutputFormatter
    {
        private readonly List<(string Offered, bool BaseWouldWrite)> _log;

        public Probe(List<(string, bool)> log)
        {
            _log = log;
            SupportedMediaTypes.Add("application/vnd.ccf");
        }

        public override bool CanWriteResult(Microsoft.AspNetCore.Mvc.Formatters.OutputFormatterCanWriteContext context)
        {
            // The base implementation rewrites context.ContentType on success: capture first.
            var offered = context.ContentType.HasValue ? context.ContentType.Value! : "(none)";
            var baseResult = base.CanWriteResult(context);
            _log.Add((offered, baseResult));
            return false;
        }

        public override Task WriteResponseBodyAsync(Microsoft.AspNetCore.Mvc.Formatters.OutputFormatterWriteContext context) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Mvc_offers_the_accept_range_and_the_stock_formatter_logic_rejects_a_features_parameter()
    {
        var log = new List<(string Offered, bool BaseWouldWrite)>();
        await using var host = await SampleHost.StartAsync(b =>
            b.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.OutputFormatters.Insert(0, new Probe(log))));

        await host.GetAsync("/products", "application/vnd.ccf");
        await host.GetAsync("/products", "application/vnd.ccf; features=d, application/json;q=0.5");
        await host.GetAsync("/products", "application/vnd.ccf; features=\"d,n\"");
        await host.GetAsync("/products");

        // What MVC passes as context.ContentType keeps the parameters of the Accept range …
        Assert.Contains(log, e => e.Offered == "application/vnd.ccf" && e.BaseWouldWrite);
        Assert.Contains(log, e => e.Offered.StartsWith("application/vnd.ccf; features=d", StringComparison.Ordinal));
        // … and the stock OutputFormatter.CanWriteResult says no to every offer that carries `features`,
        // which is why CcfOutputFormatter compares type/subtype itself.
        Assert.All(
            log.Where(e => e.Offered.Contains("features", StringComparison.Ordinal)),
            e => Assert.False(e.BaseWouldWrite, $"stock logic unexpectedly accepted '{e.Offered}'"));
        // Without an Accept header MVC offers nothing (the formatter would otherwise claim the response).
        Assert.Contains(log, e => e.Offered == "(none)");
    }
}
