using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ccf.SampleApi;
using Xunit;

namespace Ccf.Client.Tests;

public class ClientIntegrationTests : IClassFixture<SampleApiFixture>
{
    private readonly SampleApiFixture _api;

    public ClientIntegrationTests(SampleApiFixture api)
    {
        _api = api;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static CcfClientOptions Options() => new() { JsonSerializerOptions = Json };

    private static string Serialize(object? value) => JsonSerializer.Serialize(value, Json);

    // ---- CCF and JSON paths -----------------------------------------------------------------------------

    [Fact]
    public async Task GetFromCcfAsync_reads_ccf_and_equals_what_the_json_path_returns()
    {
        var (http, handler) = _api.CreateClient();
        var client = new CcfClient(http, Options());

        var viaCcf = await client.GetAsync<List<Product>>("/products");
        var viaJson = await http.GetFromJsonAsync<List<Product>>("/products", Json);

        Assert.Equal(500, viaCcf!.Count);
        Assert.Equal(Serialize(viaJson), Serialize(viaCcf));
        Assert.Equal(361.48m, viaCcf[0].Price);

        var first = handler.ApiRequests[0];
        Assert.Equal("application/vnd.ccf; features=d, application/json;q=0.5", first.Accept);
        Assert.Equal(1, handler.SchemaRequests);
    }

    [Fact]
    public async Task The_extension_method_mirrors_GetFromJsonAsync_and_shares_the_schema_cache_per_HttpClient()
    {
        var (http, handler) = _api.CreateClient();

        var a = await http.GetFromCcfAsync<List<Product>>("/products");
        var b = await http.GetFromCcfAsync<List<Product>>(new Uri("/products", UriKind.Relative));
        var one = await http.GetFromCcfAsync<Product>("/products/1001");

        Assert.Equal(500, a!.Count);
        Assert.Equal(500, b!.Count);
        Assert.Equal("Steel Monitor", one!.Name);
        Assert.Equal(1, handler.SchemaRequests);
    }

    [Fact]
    public async Task A_single_object_nested_data_and_every_scalar_type_decode_into_the_sample_models()
    {
        var (http, _) = _api.CreateClient();
        var client = new CcfClient(http, Options());

        var orders = await client.GetAsync<List<Order>>("/orders");
        var ordersJson = await http.GetFromJsonAsync<List<Order>>("/orders", Json);
        Assert.Equal(Serialize(ordersJson), Serialize(orders));
        Assert.Null(orders![1].Customer);
        Assert.Empty(orders[1].Lines);
        Assert.Null(orders[1].Tags);

        var all = await client.GetAsync<List<AllTypes>>("/all-types");
        var allJson = await http.GetFromJsonAsync<List<AllTypes>>("/all-types", Json);
        Assert.Equal(Serialize(allJson), Serialize(all));
        Assert.Equal(ulong.MaxValue, all![0].U64);
        Assert.Equal(DateTimeKind.Utc, all[0].At!.Value.Kind);
    }

    [Fact]
    public async Task Plain_ccf_is_requested_when_the_client_cannot_read_dictionaries()
    {
        var (http, handler) = _api.CreateClient();
        var client = new CcfClient(http, new CcfClientOptions { Features = CcfFeatures.None, JsonSerializerOptions = Json });

        var products = await client.GetAsync<List<Product>>("/products");

        Assert.Equal(500, products!.Count);
        Assert.Equal("application/vnd.ccf, application/json;q=0.5", handler.ApiRequests[0].Accept);
    }

    [Fact]
    public async Task Json_is_used_transparently_when_the_server_does_not_serve_ccf()
    {
        var (http, handler) = _api.CreateClient();
        var client = new CcfClient(http, Options());

        var report = await client.GetAsync<UnsupportedReport>("/unsupported");
        var scalar = await client.GetAsync<int>("/scalar-value");

        Assert.Equal("report", report!.Title);
        Assert.Equal(42, scalar);
        Assert.Equal(0, handler.SchemaRequests);
        Assert.Equal(2, handler.ApiRequests.Length); // no retry: JSON was normal, not an error
    }

    [Fact]
    public async Task A_server_side_encode_failure_arrives_as_json_and_needs_no_retry()
    {
        var (http, handler) = _api.CreateClient();
        var client = new CcfClient(http, Options());

        var items = await client.GetAsync<List<PricedItem>>("/bad-decimal");

        Assert.Equal(1.234m, items![0].Price);
        Assert.Single(handler.ApiRequests);
    }

    [Fact]
    public async Task Error_statuses_throw_like_GetFromJsonAsync_and_are_not_repeated()
    {
        var (http, handler) = _api.CreateClient();
        var client = new CcfClient(http, Options());

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync<Product>("/products/999999"));

        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Single(handler.ApiRequests);
    }

    [Fact]
    public async Task A_null_body_gives_null()
    {
        var (http, _) = _api.CreateClient();
        var client = new CcfClient(http, Options());
        Assert.Null(await client.GetAsync<Product>("/null"));
    }

    // ---- schema caching and single flight ---------------------------------------------------------------

    [Fact]
    public async Task The_schema_is_fetched_once_for_many_sequential_requests()
    {
        var (http, handler) = _api.CreateClient();
        var client = new CcfClient(http, Options());

        for (var i = 0; i < 25; i++)
        {
            Assert.Equal(500, (await client.GetAsync<List<Product>>("/products"))!.Count);
        }

        Assert.Equal(25, handler.ApiRequests.Length);
        Assert.Equal(1, handler.SchemaRequests);
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_schema_share_one_fetch()
    {
        var (http, handler) = _api.CreateClient();
        handler.SchemaDelay = TimeSpan.FromMilliseconds(300);
        var client = new CcfClient(http, Options());

        var results = await Task.WhenAll(Enumerable.Range(0, 40).Select(_ => client.GetAsync<List<Product>>("/products")));

        Assert.All(results, r => Assert.Equal(500, r!.Count));
        Assert.Equal(1, handler.SchemaRequests);
    }

    [Fact]
    public async Task Each_distinct_schema_is_fetched_once()
    {
        var (http, handler) = _api.CreateClient();
        var client = new CcfClient(http, Options());

        await client.GetAsync<List<Product>>("/products");
        await client.GetAsync<List<Order>>("/orders");
        await client.GetAsync<List<AllTypes>>("/all-types");
        await client.GetAsync<List<Product>>("/products");
        await client.GetAsync<List<Order>>("/orders");

        Assert.Equal(3, handler.SchemaRequests);
    }

    [Fact]
    public async Task A_shared_schema_cache_spares_a_second_client_the_fetch()
    {
        var cache = new InMemoryCcfSchemaCache();
        var (http1, handler1) = _api.CreateClient();
        var (http2, handler2) = _api.CreateClient();

        await new CcfClient(http1, new CcfClientOptions { SchemaCache = cache, JsonSerializerOptions = Json }).GetAsync<List<Product>>("/products");
        await new CcfClient(http2, new CcfClientOptions { SchemaCache = cache, JsonSerializerOptions = Json }).GetAsync<List<Product>>("/products");

        Assert.Equal(1, handler1.SchemaRequests);
        Assert.Equal(0, handler2.SchemaRequests);
    }

    private sealed class SpyCache : ICcfSchemaCache
    {
        public List<string> Gets { get; } = new();
        public List<string> Sets { get; } = new();
        private readonly Dictionary<string, CcfSchema> _items = new();

        public ValueTask<CcfSchema?> GetAsync(string schemaId, CancellationToken cancellationToken)
        {
            lock (Gets) { Gets.Add(schemaId); }
            return new(_items.GetValueOrDefault(schemaId));
        }

        public ValueTask SetAsync(CcfSchema schema, CancellationToken cancellationToken)
        {
            lock (Sets) { Sets.Add(schema.IdHex); }
            _items[schema.IdHex] = schema;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task A_pluggable_cache_is_consulted_first_and_filled_after_a_verified_fetch()
    {
        var spy = new SpyCache();
        var (http, handler) = _api.CreateClient();
        var client = new CcfClient(http, new CcfClientOptions { SchemaCache = spy, JsonSerializerOptions = Json });

        await client.GetAsync<List<Product>>("/products");
        await client.GetAsync<List<Product>>("/products");

        Assert.Equal(new[] { "c33a3ed0" }, spy.Sets);
        Assert.Equal(new[] { "c33a3ed0", "c33a3ed0" }, spy.Gets);
        Assert.Equal(1, handler.SchemaRequests);
    }

    // ---- fallback to JSON -------------------------------------------------------------------------------

    private static Task<HttpResponseMessage> Replace(HttpResponseMessage original, Func<byte[], byte[]> change, string? contentType = null)
    {
        var bytes = original.Content.ReadAsByteArrayAsync().Result;
        var replacement = new HttpResponseMessage(original.StatusCode) { Content = new ByteArrayContent(change(bytes)) };
        replacement.Content.Headers.ContentType = original.Content.Headers.ContentType;
        if (contentType is not null)
        {
            replacement.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }

        return Task.FromResult(replacement);
    }

    private static bool IsApi(HttpRequestMessage r) => !r.RequestUri!.AbsolutePath.StartsWith("/ccf/schemas/", StringComparison.Ordinal);

    [Theory]
    [InlineData("truncated")]
    [InlineData("bad-magic")]
    [InlineData("trailing")]
    [InlineData("flipped-length")]
    [InlineData("empty")]
    public async Task A_corrupted_ccf_payload_is_repeated_once_as_json(string corruption)
    {
        var (http, handler) = _api.CreateClient();
        var logger = new CapturingLogger<CcfClient>();
        handler.Intercept = (request, response) =>
        {
            if (!IsApi(request) || response.Content.Headers.ContentType?.MediaType != "application/vnd.ccf")
            {
                return Task.FromResult(response);
            }

            return Replace(response, bytes => corruption switch
            {
                "truncated" => bytes[..^3],
                "bad-magic" => bytes.Select((b, i) => i == 0 ? (byte)0x58 : b).ToArray(),
                "trailing" => bytes.Append((byte)0).ToArray(),
                "flipped-length" => bytes.Select((b, i) => i == 10 ? (byte)(b + 1) : b).ToArray(),
                _ => Array.Empty<byte>(),
            });
        };
        var client = new CcfClient(http, new CcfClientOptions { JsonSerializerOptions = Json }, logger);

        var products = await client.GetAsync<List<Product>>("/products");

        Assert.Equal(500, products!.Count);
        Assert.Equal(361.48m, products[0].Price);
        var api = handler.ApiRequests;
        Assert.Equal(2, api.Length);
        Assert.Contains("vnd.ccf", api[0].Accept);
        Assert.Equal("application/json", api[1].Accept);
        var warning = Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Contains("repeating the request as JSON", warning.Message);
        Assert.IsAssignableFrom<CcfFormatException>(warning.Exception);
    }

    [Theory]
    [InlineData("server-error")]
    [InlineData("not-found")]
    [InlineData("wrong-content")]
    [InlineData("non-canonical")]
    [InlineData("invalid-schema")]
    [InlineData("not-json")]
    public async Task A_schema_that_cannot_be_fetched_or_verified_makes_the_client_repeat_the_request_as_json(string failure)
    {
        var (http, handler) = _api.CreateClient();
        handler.Intercept = (request, response) =>
        {
            if (IsApi(request))
            {
                return Task.FromResult(response);
            }

            return failure switch
            {
                "server-error" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)),
                "not-found" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
                // a well-formed schema, but not the one the id names
                "wrong-content" => Replace(response, _ => System.Text.Encoding.UTF8.GetBytes(Vectors.SchemaText("schema-Order.json"))),
                // the right schema with extra whitespace: its hash is not the id
                "non-canonical" => Replace(response, b => System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(b).Replace(",", ", "))),
                "invalid-schema" => Replace(response, _ => "{}"u8.ToArray()),
                _ => Replace(response, _ => "<html>"u8.ToArray()),
            };
        };
        var client = new CcfClient(http, new CcfClientOptions { JsonSerializerOptions = Json });

        var products = await client.GetAsync<List<Product>>("/products");

        Assert.Equal(500, products!.Count);
        Assert.Equal("application/json", handler.ApiRequests[^1].Accept);
        Assert.Equal(2, handler.ApiRequests.Length);
    }

    [Fact]
    public async Task A_failed_schema_fetch_is_not_remembered_the_next_request_tries_again_and_caches_on_success()
    {
        var (http, handler) = _api.CreateClient();
        var fail = true;
        handler.Intercept = (request, response) =>
            Task.FromResult(!IsApi(request) && fail ? new HttpResponseMessage(HttpStatusCode.BadGateway) : response);
        var client = new CcfClient(http, new CcfClientOptions { JsonSerializerOptions = Json });

        Assert.Equal(500, (await client.GetAsync<List<Product>>("/products"))!.Count); // retried as JSON
        Assert.Equal(1, handler.SchemaRequests);

        fail = false;
        Assert.Equal(500, (await client.GetAsync<List<Product>>("/products"))!.Count);
        Assert.Equal(2, handler.SchemaRequests);
        Assert.Equal(500, (await client.GetAsync<List<Product>>("/products"))!.Count);
        Assert.Equal(2, handler.SchemaRequests); // cached now
    }

    [Fact]
    public async Task Concurrent_requests_all_fall_back_when_the_shared_schema_fetch_fails()
    {
        var (http, handler) = _api.CreateClient();
        handler.SchemaDelay = TimeSpan.FromMilliseconds(200);
        handler.Intercept = (request, response) => Task.FromResult(IsApi(request) ? response : new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = new CcfClient(http, new CcfClientOptions { JsonSerializerOptions = Json });

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync<List<Product>>("/products")));

        Assert.All(results, r => Assert.Equal(500, r!.Count));
        Assert.Equal(1, handler.SchemaRequests);
    }

    [Fact]
    public async Task A_cancelled_token_cancels_the_request()
    {
        var (http, _) = _api.CreateClient();
        var client = new CcfClient(http, Options());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync<List<Product>>("/products", cts.Token));
    }

    [Fact]
    public async Task Cancelling_one_waiter_does_not_fail_the_others_sharing_the_schema_fetch()
    {
        var (http, handler) = _api.CreateClient();
        handler.SchemaDelay = TimeSpan.FromMilliseconds(300);
        var client = new CcfClient(http, Options());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var cancelled = client.GetAsync<List<Product>>("/products", cts.Token);
        var survivor = client.GetAsync<List<Product>>("/products");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(500, (await survivor)!.Count);
        Assert.Equal(1, handler.SchemaRequests);
    }
}
