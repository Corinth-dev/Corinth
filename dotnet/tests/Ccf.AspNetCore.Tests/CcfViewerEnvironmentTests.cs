using System.Net;
using Ccf.AspNetCore;
using Ccf.Scalar;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ccf.AspNetCore.Tests;

/// <summary>Where <c>MapCcfViewer</c> is active (Development by default) and that the returned builder carries conventions.</summary>
public class CcfViewerEnvironmentTests
{
    private sealed record Item(int Id, string Name);

    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(string environment, Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddCcf();
        var app = builder.Build();
        app.MapCcfViewer();
        map?.Invoke(app);
        app.MapGet("/items", () => CcfResults.Ok(new List<Item> { new(1, "a"), new(2, "b") }));
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new("application/vnd.ccf");
        return await client.PostAsync(CcfViewer.DecodePath, content);
    }

    private static async Task<byte[]> CcfPayloadAsync(HttpClient client)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/items");
        request.Headers.TryAddWithoutValidation("Accept", "application/vnd.ccf");
        return await (await client.SendAsync(request)).Content.ReadAsByteArrayAsync();
    }

    [Fact]
    public async Task In_development_the_decode_endpoint_works_and_the_script_is_the_real_one()
    {
        var (app, client) = await StartAsync("Development");
        await using var _ = app;

        var decoded = await PostAsync(client, await CcfPayloadAsync(client));
        Assert.Equal(HttpStatusCode.OK, decoded.StatusCode);
        Assert.Contains("\"name\": \"b\"", await decoded.Content.ReadAsStringAsync());
        Assert.Contains("ccf-decode-toggle", await client.GetStringAsync(CcfViewer.ScriptPath));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Elsewhere_there_is_no_decode_endpoint_and_the_script_is_an_empty_module(string environment)
    {
        var (app, client) = await StartAsync(environment);
        await using var _ = app;

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, await CcfPayloadAsync(client))).StatusCode);
        var script = await client.GetStringAsync(CcfViewer.ScriptPath);
        Assert.Equal("export default {};\n", script);   // Scalar loads it without error and shows no toggle
    }

    [Fact]
    public async Task EnableOutsideDevelopment_turns_the_viewer_on_in_any_environment()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCcf();
        await using var app = builder.Build();
        app.MapCcfViewer(o => o.EnableOutsideDevelopment = true);
        app.MapGet("/items", () => CcfResults.Ok(new List<Item> { new(1, "a") }));
        await app.StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(client, await CcfPayloadAsync(client))).StatusCode);
        Assert.Contains("ccf-decode-toggle", await client.GetStringAsync(CcfViewer.ScriptPath));
    }

    [Fact]
    public async Task Conventions_added_to_the_returned_builder_apply_to_the_viewer_endpoints()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Services.AddCcf();
        await using var app = builder.Build();
        app.MapCcfViewer().AddEndpointFilter((_, _) => ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status401Unauthorized)));
        await app.StartAsync();
        var client = app.GetTestClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync(client, new byte[] { 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(CcfViewer.ScriptPath)).StatusCode);
    }
}
