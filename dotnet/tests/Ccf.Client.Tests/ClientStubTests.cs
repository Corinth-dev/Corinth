using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ccf.Client.Tests;

public class ClientStubTests
{
    private static readonly byte[] ProductsPayload = Vectors.Bytes("v01-products-plain");
    private static readonly string ProductSchema = Vectors.SchemaText("schema-Product.json");
    private const string ProductJson = """[{"id":7,"name":"json","price":1.5,"status":"active","stock":3}]""";

    public sealed class Product
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public decimal Price { get; set; }
        public string Status { get; set; } = "";
        public uint Stock { get; set; }
    }

    private static bool IsSchema(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.Contains("/ccf/schemas/", StringComparison.Ordinal);

    private static Task<HttpResponseMessage> Server(HttpRequestMessage request, byte[]? payload = null, string? schema = null)
    {
        if (IsSchema(request))
        {
            return Task.FromResult(schema is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : StubHandler.Json(schema));
        }

        var accept = request.Headers.Accept.ToString();
        return Task.FromResult(accept.Contains("vnd.ccf") ? StubHandler.Ccf(payload ?? ProductsPayload) : StubHandler.Json(ProductJson));
    }

    private static HttpClient Http(StubHandler handler, string baseAddress = "https://api.test") =>
        new(handler) { BaseAddress = new Uri(baseAddress) };

    // ---- retry rules ------------------------------------------------------------------------------------

    private static byte[] Corrupt(byte[] payload) => payload[..^3];

    [Fact]
    public async Task A_failed_get_is_repeated_as_json_and_the_retry_keeps_headers_and_replaces_accept()
    {
        var handler = new StubHandler(r => Server(r, Corrupt(ProductsPayload), ProductSchema));
        var client = new CcfClient(Http(handler), new CcfClientOptions { JsonSerializerOptions = new(JsonSerializerDefaults.Web) });
        var request = new HttpRequestMessage(HttpMethod.Get, "/products");
        request.Headers.Authorization = new("Bearer", "t0k3n");
        request.Headers.TryAddWithoutValidation("X-Trace", "abc");

        var products = await client.SendAsync<List<Product>>(request);

        Assert.Equal("json", products![0].Name);
        var api = handler.Seen.Select(s => s.Request).Where(r => !IsSchema(r)).ToArray();
        Assert.Equal(2, api.Length);
        Assert.Equal("application/json", api[1].Headers.Accept.ToString());
        Assert.Equal("Bearer t0k3n", api[1].Headers.Authorization!.ToString());
        Assert.Equal("abc", api[1].Headers.GetValues("X-Trace").Single());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    public async Task Unsafe_methods_are_not_repeated_and_the_ccf_error_surfaces(string method)
    {
        var handler = new StubHandler(r => Server(r, Corrupt(ProductsPayload), ProductSchema));
        var logger = new CapturingLogger<CcfClient>();
        var client = new CcfClient(Http(handler), logger: logger);
        var request = new HttpRequestMessage(new HttpMethod(method), "/products") { Content = new StringContent("{}") };

        var ex = await Assert.ThrowsAsync<CcfFormatException>(() => client.SendAsync<List<Product>>(request));

        Assert.Equal(CcfErrorCode.Truncated, ex.Code);
        Assert.Single(handler.Seen, s => !IsSchema(s.Request));
        var warning = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("not safe to repeat", warning.Message);
        Assert.IsType<CcfFormatException>(warning.Exception);
    }

    [Fact]
    public async Task A_schema_failure_on_an_unsafe_request_surfaces_as_CcfSchemaFetchException()
    {
        var handler = new StubHandler(r => Server(r, ProductsPayload, schema: null)); // schema endpoint 404
        var client = new CcfClient(Http(handler));
        var request = new HttpRequestMessage(HttpMethod.Post, "/products") { Content = new StringContent("{}") };

        var ex = await Assert.ThrowsAsync<CcfSchemaFetchException>(() => client.SendAsync<List<Product>>(request));

        Assert.Equal("c33a3ed0", ex.SchemaId);
        Assert.Contains("404", ex.Message);
    }

    [Fact]
    public async Task An_idempotent_post_is_repeated_with_the_same_body_and_content_headers()
    {
        var handler = new StubHandler(r => Server(r, Corrupt(ProductsPayload), ProductSchema));
        var client = new CcfClient(Http(handler), new CcfClientOptions { IsIdempotent = r => r.Method == HttpMethod.Post });
        var request = new HttpRequestMessage(HttpMethod.Post, "/search")
        {
            Content = new StringContent("""{"q":"monitor"}""", Encoding.UTF8, "application/json"),
        };

        var products = await client.SendAsync<List<Product>>(request);

        Assert.Equal("json", products![0].Name);
        var api = handler.Seen.Where(s => !IsSchema(s.Request)).ToArray();
        Assert.Equal(2, api.Length);
        Assert.Equal(api[0].Body, api[1].Body);
        Assert.Equal("""{"q":"monitor"}""", Encoding.UTF8.GetString(api[1].Body!));
        Assert.Equal("application/json", api[1].Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(HttpMethod.Post, api[1].Request.Method);
    }

    [Fact]
    public async Task Head_requests_may_be_repeated_too()
    {
        var calls = 0;
        var handler = new StubHandler(r =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? StubHandler.Ccf(new byte[] { 1, 2, 3 }) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        });
        var client = new CcfClient(Http(handler));

        await client.SendAsync<string>(new HttpRequestMessage(HttpMethod.Head, "/x"));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task If_the_json_retry_fails_too_its_error_surfaces()
    {
        var handler = new StubHandler(r =>
            IsSchema(r) ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
            : Task.FromResult(r.Headers.Accept.ToString().Contains("vnd.ccf") ? StubHandler.Ccf(ProductsPayload) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var client = new CcfClient(Http(handler));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync<List<Product>>("/products"));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
    }

    // ---- what is not a ccf error ------------------------------------------------------------------------

    public sealed class WrongShape
    {
        public int Name { get; set; } // the schema says string
    }

    [Fact]
    public async Task A_target_type_that_cannot_receive_the_schema_is_a_usage_error_and_is_not_retried()
    {
        var handler = new StubHandler(r => Server(r, ProductsPayload, ProductSchema));
        var client = new CcfClient(Http(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync<List<WrongShape>>("/products"));

        Assert.Single(handler.Seen, s => !IsSchema(s.Request));
    }

    [Fact]
    public async Task An_unexpected_content_type_goes_down_the_json_path_without_a_ccf_attempt_or_a_retry()
    {
        var handler = new StubHandler(_ => Task.FromResult(StubHandler.Text("hello", "text/plain")));
        var client = new CcfClient(Http(handler));

        // Whatever the JSON deserializer does with a non-JSON body (the same as GetFromJsonAsync), it is not retried.
        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync<List<Product>>("/products"));

        Assert.Single(handler.Seen);
    }

    [Fact]
    public async Task The_media_type_parameters_do_not_matter_the_flags_in_the_header_decide()
    {
        var handler = new StubHandler(r => IsSchema(r)
            ? Task.FromResult(StubHandler.Json(ProductSchema))
            : Task.FromResult(StubHandler.Ccf(ProductsPayload, "application/vnd.ccf; features=d")));
        var client = new CcfClient(Http(handler));

        Assert.Equal(500, (await client.GetAsync<List<Product>>("/products"))!.Count); // plain payload despite features=d
    }

    // ---- origin rules -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_schema_url_is_built_from_the_requests_origin_and_the_configured_prefix()
    {
        var handler = new StubHandler(r => Server(r, ProductsPayload, ProductSchema));
        var client = new CcfClient(Http(handler, "https://base.test"), new CcfClientOptions { SchemaPathPrefix = "/schemas/" });

        await client.GetAsync<List<Product>>("https://other.test:8443/v1/products?x=1");

        var schemaRequest = handler.Seen.Select(s => s.Request).Single(IsSchemaPath);
        Assert.Equal("https://other.test:8443/schemas/c33a3ed0", schemaRequest.RequestUri!.ToString());

        static bool IsSchemaPath(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.StartsWith("/schemas/", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_schema_redirected_to_another_origin_is_refused()
    {
        // The handler answers as if the redirect had been followed: RequestMessage.RequestUri is the final URI.
        var handler = new StubHandler(r =>
        {
            if (!IsSchema(r))
            {
                return Task.FromResult(r.Headers.Accept.ToString().Contains("vnd.ccf") ? StubHandler.Ccf(ProductsPayload) : StubHandler.Json(ProductJson));
            }

            var response = StubHandler.Json(ProductSchema);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://evil.test/ccf/schemas/c33a3ed0");
            return Task.FromResult(response);
        });
        var client = new CcfClient(Http(handler), new CcfClientOptions { JsonSerializerOptions = new(JsonSerializerDefaults.Web) });

        var products = await client.GetAsync<List<Product>>("/products");

        Assert.Equal("json", products![0].Name); // the CCF schema was refused, so the JSON retry answered
        Assert.DoesNotContain(handler.Seen, s => s.Request.RequestUri!.Host == "evil.test");
        Assert.Null(await client.SchemaCache.GetAsync("c33a3ed0", default));
    }

    [Fact]
    public async Task A_relative_uri_without_a_base_address_fails_clearly()
    {
        var handler = new StubHandler(r => Task.FromResult(StubHandler.Ccf(ProductsPayload)));
        var client = new CcfClient(new HttpClient(handler));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => client.GetAsync<List<Product>>("products"));
    }

    // ---- verification -----------------------------------------------------------------------------------

    [Fact]
    public void Verify_accepts_only_canonical_text_that_hashes_to_the_id()
    {
        Assert.Equal("c33a3ed0", CcfClient.Verify("c33a3ed0", ProductSchema).IdHex);
        Assert.Throws<CcfSchemaFetchException>(() => CcfClient.Verify("00000000", ProductSchema));
        Assert.Throws<CcfSchemaFetchException>(() => CcfClient.Verify("c33a3ed0", ProductSchema + "\n"));
        Assert.Throws<CcfSchemaFetchException>(() => CcfClient.Verify("c33a3ed0", ProductSchema.Replace("\"name\":\"Product\"", "\"name\": \"Product\"")));
        Assert.Throws<CcfSchemaFetchException>(() => CcfClient.Verify("c33a3ed0", ""));
    }

    [Fact]
    public async Task The_default_naming_follows_the_json_options_and_matching_ignores_case_as_a_fallback()
    {
        var handler = new StubHandler(r => Server(r, ProductsPayload, ProductSchema));
        var client = new CcfClient(Http(handler), new CcfClientOptions { JsonSerializerOptions = new() { PropertyNamingPolicy = null } });

        var products = await client.GetAsync<List<Product>>("/products"); // schema has camelCase names, properties are PascalCase

        Assert.Equal("Steel Monitor", products![0].Name);
        Assert.Equal(1001, products[0].Id);
    }
}

public class ClientLoggingTests
{
    [Fact]
    public async Task The_container_supplies_the_logger_when_it_constructs_the_client()
    {
        var logger = new CapturingLogger<CcfClient>();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<CcfClient>>(logger);
        using var provider = services.BuildServiceProvider();
        var handler = new StubHandler(r => Task.FromResult(r.RequestUri!.AbsolutePath.Contains("/ccf/schemas/")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : r.Headers.Accept.ToString().Contains("vnd.ccf") ? StubHandler.Ccf(Vectors.Bytes("v01-products-plain")) : StubHandler.Json("[]")));

        var client = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<CcfClient>(
            provider, new HttpClient(handler) { BaseAddress = new Uri("https://api.test") });
        await client.GetAsync<List<ClientStubTests.Product>>("/products");

        Assert.Contains(logger.Entries, e => e.Message.Contains("repeating the request as JSON"));
    }

    [Fact]
    public async Task Without_a_container_nothing_is_logged_and_nothing_fails()
    {
        var handler = new StubHandler(r => Task.FromResult(r.RequestUri!.AbsolutePath.Contains("/ccf/schemas/")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : r.Headers.Accept.ToString().Contains("vnd.ccf") ? StubHandler.Ccf(Vectors.Bytes("v01-products-plain")) : StubHandler.Json("[]")));
        var client = new CcfClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") });

        Assert.Empty((await client.GetAsync<List<ClientStubTests.Product>>("/products"))!);
    }

    [Fact]
    public async Task Debug_output_notes_a_fetched_schema()
    {
        var logger = new CapturingLogger<CcfClient>();
        var handler = new StubHandler(r => Task.FromResult(r.RequestUri!.AbsolutePath.Contains("/ccf/schemas/")
            ? StubHandler.Json(Vectors.SchemaText("schema-Product.json"))
            : StubHandler.Ccf(Vectors.Bytes("v01-products-plain"))));
        var client = new CcfClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.test") }, logger: logger);

        await client.GetAsync<List<ClientStubTests.Product>>("/products");

        Assert.Contains(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Debug && e.Message.Contains("c33a3ed0"));
    }
}
