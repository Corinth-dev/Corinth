using System.Text.Json;
using Xunit;

namespace Corinth.AspNetCore.Tests;

/// <summary>The sample's OpenAPI document and Scalar UI, including how they describe CCF.</summary>
public class OpenApiTests
{
    private static async Task<JsonElement> DocumentAsync(TestHost host)
    {
        var response = await host.GetAsync("/openapi/v1.json");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static string[] ResponseTypes(JsonElement document, string path) =>
        document.GetProperty("paths").GetProperty(path).GetProperty("get").GetProperty("responses").GetProperty("200")
            .GetProperty("content").EnumerateObject().Select(p => p.Name).ToArray();

    [Fact]
    public async Task The_document_is_served_with_a_title_and_the_ccf_opt_in_description()
    {
        await using var host = await TestHost.StartAsync();
        var document = await DocumentAsync(host);

        Assert.Equal("CCF sample API", document.GetProperty("info").GetProperty("title").GetString());
        var description = document.GetProperty("info").GetProperty("description").GetString()!;
        Assert.Contains("Try it with", description);                 // the sample's own text is kept
        Assert.Contains("**CCF.**", description);                    // AddCcf() appends the opt-in note
        Assert.Contains("application/vnd.ccf; features=d", description);
        Assert.Contains("/ccf/schemas/{id}", description);
    }

    [Theory]
    [InlineData("/products")]
    [InlineData("/products/{id}")]
    [InlineData("/orders")]
    [InlineData("/all-types")]
    [InlineData("/counted")]            // declared with ProducesResponseType on an IActionResult
    [InlineData("/min/products")]       // minimal API, declared with Produces<T>
    [InlineData("/min/null")]
    public async Task Endpoints_with_a_ccf_schema_list_application_vnd_ccf(string path)
    {
        await using var host = await TestHost.StartAsync();
        var types = ResponseTypes(await DocumentAsync(host), path);

        Assert.Contains("application/vnd.ccf", types);
        Assert.Contains("application/json", types);
    }

    [Theory]
    [InlineData("/unsupported")]        // no CCF schema for the type
    [InlineData("/min/unsupported")]
    [InlineData("/scalar-value")]             // a scalar
    [InlineData("/json-only")]          // [Produces("application/json")] keeps CCF away
    public async Task Other_endpoints_do_not_list_application_vnd_ccf(string path)
    {
        await using var host = await TestHost.StartAsync();
        Assert.DoesNotContain("application/vnd.ccf", ResponseTypes(await DocumentAsync(host), path));
    }

    [Fact]
    public async Task The_operation_names_the_schema_id_that_the_schema_endpoint_serves()
    {
        await using var host = await TestHost.StartAsync();
        var document = await DocumentAsync(host);
        var description = document.GetProperty("paths").GetProperty("/products").GetProperty("get").GetProperty("description").GetString()!;

        Assert.Contains("`c33a3ed0`", description);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await host.GetAsync("/ccf/schemas/c33a3ed0")).StatusCode);
    }

    [Fact]
    public async Task The_scalar_ui_is_served_and_points_at_the_document()
    {
        await using var host = await TestHost.StartAsync();
        var response = await host.GetAsync("/scalar/v1");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("openapi/v1.json", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_sample_scalar_value_endpoint_is_not_shadowed_by_the_ui()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal("42", await host.Client.GetStringAsync("/scalar-value"));
        Assert.Equal(System.Net.HttpStatusCode.OK, (await host.GetAsync("/scalar/v1")).StatusCode);
    }

    [Fact]
    public async Task Documentation_endpoints_are_never_served_as_ccf_and_are_not_counted()
    {
        await using var host = await TestHost.StartAsync();
        var (response, _) = await host.GetBytesAsync("/openapi/v1.json", "application/vnd.ccf");

        Assert.StartsWith("application/json", response.Content.Headers.ContentType?.ToString());
    }
}
