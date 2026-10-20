using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ccf.AspNetCore.Tests;

/// <summary>One sample host shared by the read-only integration tests.</summary>
public sealed class DefaultHostFixture : IAsyncLifetime
{
    public SampleHost Host { get; private set; } = null!;

    public async Task InitializeAsync() => Host = await SampleHost.StartAsync();

    public async Task DisposeAsync() => await Host.DisposeAsync();
}

public class IntegrationTests : IClassFixture<DefaultHostFixture>
{
    private readonly SampleHost _host;

    public IntegrationTests(DefaultHostFixture fixture)
    {
        _host = fixture.Host;
    }

    // ---- /products from one endpoint: JSON, plain CCF, dictionary CCF ------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    [InlineData("*/*")]
    [InlineData("text/html, */*;q=0.8")]
    [InlineData("application/json, application/vnd.ccf;q=0.5")]
    [InlineData("application/*")]
    [InlineData("application/vnd.ccf;q=0")]
    [InlineData("application/vnd.ccf; features=d;q=0, application/json")]
    // MVC ignores an Accept header that contains */* (RespectBrowserAcceptHeader is false by default) …
    [InlineData("application/vnd.ccf, */*;q=0.1")]
    [InlineData("application/vnd.ccf; features=d, */*")]
    // … and equal-quality ranges of different media types keep their order, so the first one wins.
    [InlineData("application/json, application/vnd.ccf; features=d")]
    public async Task Json_is_served_unless_ccf_is_explicitly_preferred(string? accept)
    {
        var (response, body) = await _host.GetBytesAsync("/products", accept);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.ContentType());
        Assert.Contains("Accept", response.Vary());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(500, doc.RootElement.GetArrayLength());
        Assert.Equal("Steel Monitor", doc.RootElement[0].GetProperty("name").GetString());
        Assert.Equal("active", doc.RootElement[0].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("application/vnd.ccf")]
    [InlineData("application/vnd.ccf, application/json;q=0.5")]
    [InlineData("APPLICATION/VND.CCF")]
    [InlineData("application/vnd.ccf; features=n")]
    [InlineData("application/vnd.ccf;features=\"n\"")]
    public async Task Plain_ccf_bytes_equal_vector_v01(string accept)
    {
        var (response, body) = await _host.GetBytesAsync("/products", accept);

        Assert.Equal("application/vnd.ccf", response.ContentType());
        Assert.Equal(Vectors.Bytes("v01-products-plain"), body);
        Assert.Contains("Accept", response.Vary());
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
    }

    [Theory]
    [InlineData("application/vnd.ccf; features=d")]
    [InlineData("application/vnd.ccf; features=d, application/json;q=0.5")]
    [InlineData("application/vnd.ccf;features=\"d\"")]
    [InlineData("application/vnd.ccf; features=\"d,n\"")]
    [InlineData("application/vnd.ccf; features=\"n,d\", application/json;q=0.5")]
    [InlineData("application/json;q=0.5, application/vnd.ccf; features=d")]
    [InlineData("application/vnd.ccf; features=d; q=0.9, application/json;q=0.8")]
    public async Task Dictionary_ccf_bytes_equal_vector_v02(string accept)
    {
        var (response, body) = await _host.GetBytesAsync("/products", accept);

        Assert.Equal("application/vnd.ccf; features=d", response.ContentType());
        Assert.Equal(Vectors.Bytes("v02-products-dict"), body);
        Assert.Contains("Accept", response.Vary());
    }

    [Fact]
    public async Task A_single_object_is_served_with_flag_S_and_equals_vector_v05()
    {
        var (response, body) = await _host.GetBytesAsync("/products/1001", "application/vnd.ccf");

        Assert.Equal("application/vnd.ccf", response.ContentType());
        Assert.Equal(Vectors.Bytes("v05-single-product"), body);
        Assert.Equal(2, body[3]);
        Assert.Contains("Accept", response.Vary());

        var json = await _host.GetAsync("/products/1001");
        using var doc = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
        Assert.Equal(1001, doc.RootElement.GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task Unknown_single_object_is_a_plain_404_not_ccf()
    {
        var (response, body) = await _host.GetBytesAsync("/products/999999", "application/vnd.ccf");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("application/vnd.ccf", response.ContentType());
        Assert.False(body.Length >= 2 && body[0] == 0x43 && body[1] == 0x43);
    }

    [Fact]
    public async Task Empty_list_equals_vector_v06_and_never_uses_a_dictionary()
    {
        var (response, body) = await _host.GetBytesAsync("/products/empty", "application/vnd.ccf; features=d");

        Assert.Equal("application/vnd.ccf", response.ContentType());
        Assert.Equal(Vectors.Bytes("v06-empty"), body);
    }

    [Fact]
    public async Task Nested_orders_equal_vector_v04()
    {
        var (response, body) = await _host.GetBytesAsync("/orders", "application/vnd.ccf; features=d");

        Assert.Equal("application/vnd.ccf; features=d", response.ContentType());
        Assert.Equal(Vectors.Bytes("v04-orders-nested"), body);
    }

    [Fact]
    public async Task Every_scalar_type_equals_vector_v03_and_stays_plain_despite_features()
    {
        var (response, body) = await _host.GetBytesAsync("/all-types", "application/vnd.ccf; features=d");

        Assert.Equal("application/vnd.ccf", response.ContentType()); // no column qualified for a dictionary
        Assert.Equal(Vectors.Bytes("v03-all-types"), body);
    }

    [Fact]
    public async Task A_linq_iterator_inside_an_IActionResult_is_served_through_IEnumerable()
    {
        var (response, body) = await _host.GetBytesAsync("/lazy-products", "application/vnd.ccf");

        Assert.Equal("application/vnd.ccf", response.ContentType());
        var products = CcfDecoder.Decode<List<SampleApi.Product>>(body, CcfSchema.Parse(Vectors.SchemaText("schema-Product.json")));
        Assert.NotEmpty(products);
        Assert.All(products, p => Assert.True(p.Stock > 500));
    }

    // ---- fallbacks -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/unsupported")]
    [InlineData("/scalar")]
    [InlineData("/min/unsupported")]
    public async Task Unsupported_types_and_scalars_are_served_as_json_even_when_ccf_is_requested(string path)
    {
        var (response, body) = await _host.GetBytesAsync(path, "application/vnd.ccf; features=d, application/json;q=0.5");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.ContentType());
        Assert.DoesNotContain("Accept", response.Vary()); // this endpoint cannot serve CCF
        Assert.NotEqual((byte)0x43, body[0]);
        JsonDocument.Parse(body).Dispose();
    }

    [Theory]
    [InlineData("/bad-decimal")]
    [InlineData("/min/bad-decimal")]
    public async Task An_encode_failure_falls_back_to_json_invisibly(string path)
    {
        var (response, body) = await _host.GetBytesAsync(path, "application/vnd.ccf");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("application/json", response.ContentType());
        Assert.Contains("Accept", response.Vary());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1.234m, doc.RootElement[0].GetProperty("price").GetDecimal());
    }

    // ---- minimal APIs --------------------------------------------------------------------------------------

    [Fact]
    public async Task Minimal_api_results_negotiate_like_controllers()
    {
        var (plain, plainBody) = await _host.GetBytesAsync("/min/products", "application/vnd.ccf");
        Assert.Equal("application/vnd.ccf", plain.ContentType());
        Assert.Equal(Vectors.Bytes("v01-products-plain"), plainBody);
        Assert.Contains("Accept", plain.Vary());

        var (dict, dictBody) = await _host.GetBytesAsync("/min/products", "application/vnd.ccf; features=d, application/json;q=0.5");
        Assert.Equal("application/vnd.ccf; features=d", dict.ContentType());
        Assert.Equal(Vectors.Bytes("v02-products-dict"), dictBody);

        var (_, singleBody) = await _host.GetBytesAsync("/min/products/1001", "application/vnd.ccf");
        Assert.Equal(Vectors.Bytes("v05-single-product"), singleBody);

        var json = await _host.GetAsync("/min/products");
        Assert.StartsWith("application/json", json.ContentType());
        Assert.Contains("Accept", json.Vary());
        using var doc = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
        Assert.Equal(500, doc.RootElement.GetArrayLength());

        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _host.GetAsync("/min/products/-5", "application/vnd.ccf")).StatusCode);
    }

    // ---- schema endpoint -----------------------------------------------------------------------------------

    [Theory]
    [InlineData("c33a3ed0", "schema-Product.json")]
    [InlineData("0c5aa30e", "schema-AllTypes.json")]
    [InlineData("63a83963", "schema-Order.json")]
    public async Task Schema_endpoint_returns_the_canonical_schema_with_immutable_caching(string id, string file)
    {
        var response = await _host.GetAsync("/ccf/schemas/" + id);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.ContentType());
        Assert.Equal("public, max-age=31536000, immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal(Vectors.SchemaText(file), await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("00000000")]
    [InlineData("deadbeef")]
    [InlineData("C33A3ED0")] // ids are lowercase
    [InlineData("c33a3ed")]
    [InlineData("c33a3ed00")]
    [InlineData("xyz")]
    [InlineData("c33a3ed0/extra")]
    public async Task Unknown_or_malformed_schema_ids_are_404_and_not_cached(string id)
    {
        var response = await _host.GetAsync("/ccf/schemas/" + id);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.CacheControl);
    }

    [Fact]
    public async Task Schema_endpoint_ignores_the_accept_header_and_only_serves_get()
    {
        var response = await _host.GetAsync("/ccf/schemas/c33a3ed0", "application/vnd.ccf");
        Assert.Equal("application/json", response.ContentType());

        var post = await _host.Client.PostAsync("/ccf/schemas/c33a3ed0", new StringContent(""));
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, post.StatusCode);
    }

    // ---- concurrency ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Concurrent_requests_get_identical_bytes()
    {
        var expected = Vectors.Bytes("v02-products-dict");
        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(async _ =>
            (await _host.GetBytesAsync("/products", "application/vnd.ccf; features=d")).Body));
        Assert.All(results, r => Assert.Equal(expected, r));
    }
}

public class AcceptHeaderOptionTests
{
    [Fact]
    public async Task With_RespectBrowserAcceptHeader_a_wildcard_in_accept_no_longer_hides_ccf()
    {
        await using var host = await SampleHost.StartAsync(b =>
        {
            b.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.RespectBrowserAcceptHeader = true);
        });

        var (response, body) = await host.GetBytesAsync("/products", "application/vnd.ccf, */*;q=0.1");
        Assert.Equal("application/vnd.ccf", response.ContentType());
        Assert.Equal(Vectors.Bytes("v01-products-plain"), body);

        var (min, minBody) = await host.GetBytesAsync("/min/products", "application/vnd.ccf; features=d, */*;q=0.1");
        Assert.Equal("application/vnd.ccf; features=d", min.ContentType());
        Assert.Equal(Vectors.Bytes("v02-products-dict"), minBody);

        // */* alone still means JSON.
        var json = await host.GetAsync("/products", "*/*");
        Assert.StartsWith("application/json", json.ContentType());
    }

    [Fact]
    public async Task Without_it_controllers_and_minimal_apis_agree_on_wildcard_headers()
    {
        await using var host = await SampleHost.StartAsync();
        foreach (var path in new[] { "/products", "/min/products" })
        {
            var response = await host.GetAsync(path, "application/vnd.ccf, */*;q=0.1");
            Assert.StartsWith("application/json", response.ContentType());
        }
    }

    [Fact]
    public async Task A_q0_range_means_not_acceptable_on_both_paths()
    {
        await using var host = await SampleHost.StartAsync();
        foreach (var path in new[] { "/products", "/min/products" })
        {
            var response = await host.GetAsync(path, "application/vnd.ccf;q=0");
            Assert.StartsWith("application/json", response.ContentType());
        }
    }
}
