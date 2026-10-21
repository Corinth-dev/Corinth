using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace Ccf.AspNetCore.Tests;

/// <summary>The CCF viewer of <c>SKM.Ccf.Scalar</c> as used by the sample (Development): <c>POST /ccf/decode</c> and the script that wires it into Scalar.</summary>
public class CcfViewerTests
{
    private static async Task<HttpResponseMessage> DecodeAsync(TestHost host, byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new("application/vnd.ccf");
        return await host.Client.PostAsync("/ccf/decode", content);
    }

    [Theory]
    [InlineData("/products", "application/vnd.ccf; features=d")]   // dictionary columns
    [InlineData("/products", "application/vnd.ccf")]                // plain columns
    [InlineData("/products/1001", "application/vnd.ccf")]           // single object
    [InlineData("/products/empty", "application/vnd.ccf")]
    [InlineData("/min/products", "application/vnd.ccf")]
    [InlineData("/orders", "application/vnd.ccf; features=d")]      // nested objects and lists, nulls
    [InlineData("/all-types", "application/vnd.ccf")]               // every scalar type
    public async Task A_ccf_response_decodes_to_the_json_the_endpoint_serves(string path, string accept)
    {
        await using var host = await TestHost.StartAsync("Development");
        var (response, payload) = await host.GetBytesAsync(path, accept);
        Assert.StartsWith("application/vnd.ccf", response.Content.Headers.ContentType?.ToString());
        var expected = JsonNode.Parse(await host.Client.GetStringAsync(path));

        var decoded = await DecodeAsync(host, payload);

        Assert.Equal(HttpStatusCode.OK, decoded.StatusCode);
        Assert.Equal("application/json", decoded.Content.Headers.ContentType?.MediaType);
        Assert.Equal(CcfDecoder.PeekSchemaId(payload), decoded.Headers.GetValues("X-Ccf-Schema-Id").Single());
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(await decoded.Content.ReadAsStringAsync())));
    }

    [Fact]
    public async Task A_malformed_payload_is_a_400_with_the_error_code()
    {
        await using var host = await TestHost.StartAsync("Development");
        var (_, payload) = await host.GetBytesAsync("/products", "application/vnd.ccf");

        var truncated = await DecodeAsync(host, payload[..^3]);
        Assert.Equal(HttpStatusCode.BadRequest, truncated.StatusCode);
        Assert.Contains("Truncated", await truncated.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await DecodeAsync(host, Array.Empty<byte>())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecodeAsync(host, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 })).StatusCode);
    }

    [Fact]
    public async Task A_payload_with_an_unknown_schema_id_is_a_404()
    {
        await using var host = await TestHost.StartAsync("Development");
        var (_, payload) = await host.GetBytesAsync("/products/empty", "application/vnd.ccf");
        payload[4] ^= 0xFF; // the schema id lives in bytes 4-7

        var response = await DecodeAsync(host, payload);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("No schema with id", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_payload_size_limit_applies()
    {
        await using var host = await TestHost.StartAsync("Development", config: new() { ["Ccf:Limits:MaxPayloadBytes"] = "100" });
        var (_, payload) = await host.GetBytesAsync("/products/empty", "application/vnd.ccf"); // 19 bytes: small enough to encode

        Assert.Equal(HttpStatusCode.OK, (await DecodeAsync(host, payload)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecodeAsync(host, new byte[200])).StatusCode);
    }

    [Fact]
    public async Task The_script_is_served_and_scalar_is_configured_to_load_it()
    {
        await using var host = await TestHost.StartAsync("Development");
        var script = await host.GetAsync("/ccf/scalar-viewer.js");

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        var text = await script.Content.ReadAsStringAsync();
        Assert.Contains("customFetch", text);
        Assert.Contains("/ccf/decode", text);
        Assert.Contains("ccf-decode-toggle", text);   // the page-level toggle
        Assert.Contains("localStorage", text);        // remembered in the browser, off by default
        Assert.Contains("/ccf/scalar-viewer.js", await host.Client.GetStringAsync("/scalar/v1"));
    }

    [Fact]
    public async Task The_decode_endpoint_is_documented_and_the_script_is_not()
    {
        await using var host = await TestHost.StartAsync("Development");
        var document = JsonNode.Parse(await host.Client.GetStringAsync("/openapi/v1.json"))!;
        var paths = document["paths"]!.AsObject();

        Assert.True(paths.ContainsKey("/ccf/decode"));
        Assert.False(paths.ContainsKey("/ccf/scalar-viewer.js"));
    }
}
