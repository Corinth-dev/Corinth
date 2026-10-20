using System.Diagnostics.Metrics;
using System.Text.Json;
using Ccf.SampleApi;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ccf.AspNetCore.Tests;

public class DevelopmentOverrideTests
{
    private static Task<TestHost> Host(string environment, bool allow) =>
        TestHost.StartAsync(environment, new() { ["Ccf:AllowQueryOverride"] = allow ? "true" : "false" });

    [Theory]
    [InlineData("?ccf=d", "v02-products-dict", "application/vnd.ccf; features=d")]
    [InlineData("?ccf=", "v01-products-plain", "application/vnd.ccf")]
    [InlineData("?ccf", "v01-products-plain", "application/vnd.ccf")]
    [InlineData("?ccf=dn", "v02-products-dict", "application/vnd.ccf; features=d")]
    public async Task In_development_with_the_option_the_query_selects_ccf_without_an_accept_header(string query, string vector, string contentType)
    {
        await using var host = await Host("Development", allow: true);

        var (response, body) = await host.GetBytesAsync("/products" + query);

        Assert.Equal(contentType, response.ContentType());
        Assert.Equal(Vectors.Bytes(vector), body);
    }

    [Fact]
    public async Task The_query_replaces_an_existing_accept_header_and_also_works_for_minimal_apis()
    {
        await using var host = await Host("Development", allow: true);

        var (a, body) = await host.GetBytesAsync("/products?ccf=d", "application/json");
        Assert.Equal(Vectors.Bytes("v02-products-dict"), body);
        Assert.Equal("application/vnd.ccf; features=d", a.ContentType());

        var (_, minBody) = await host.GetBytesAsync("/min/products?ccf=d");
        Assert.Equal(Vectors.Bytes("v02-products-dict"), minBody);
    }

    [Theory]
    [InlineData("?ccf=off")]
    [InlineData("?ccf=json")]
    [InlineData("?ccf=0")]
    [InlineData("?other=1")]
    public async Task Off_and_unrelated_queries_leave_the_request_alone(string query)
    {
        await using var host = await Host("Development", allow: true);
        var response = await host.GetAsync("/products" + query);
        Assert.StartsWith("application/json", response.ContentType());
    }

    [Fact]
    public async Task Without_the_option_in_development_the_query_is_ignored()
    {
        await using var host = await Host("Development", allow: false);
        Assert.StartsWith("application/json", (await host.GetAsync("/products?ccf=d")).ContentType());
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Outside_development_the_query_is_ignored_even_with_the_option(string environment)
    {
        await using var host = await Host(environment, allow: true);
        Assert.StartsWith("application/json", (await host.GetAsync("/products?ccf=d")).ContentType());
    }
}

public class ConfigurationTests
{
    private static Task<TestHost> Host(Dictionary<string, string?> config, bool preRegister = true)
    {
        config["Sample:PreRegisterSchemas"] = preRegister ? "true" : "false";
        return TestHost.StartAsync(config: config);
    }

    [Fact]
    public async Task Row_limit_from_configuration_makes_big_responses_fall_back_to_json_while_small_ones_stay_ccf()
    {
        await using var host = await Host(new() { ["Ccf:Limits:MaxRows"] = "100" });

        var big = await host.GetAsync("/products", "application/vnd.ccf");
        Assert.StartsWith("application/json", big.ContentType());
        using var doc = JsonDocument.Parse(await big.Content.ReadAsStringAsync());
        Assert.Equal(500, doc.RootElement.GetArrayLength());

        var (small, body) = await host.GetBytesAsync("/orders", "application/vnd.ccf; features=d");
        Assert.Equal("application/vnd.ccf; features=d", small.ContentType());
        Assert.Equal(Vectors.Bytes("v04-orders-nested"), body);
    }

    [Fact]
    public async Task Dictionary_threshold_from_configuration_changes_the_per_column_decision()
    {
        // 142 distinct of 500 = 0.284: dictionary at the default 0.5, plain at 0.2.
        await using var host = await Host(new() { ["Ccf:DictionaryThreshold"] = "0.2" });
        var (response, body) = await host.GetBytesAsync("/products", "application/vnd.ccf; features=d");

        Assert.Equal("application/vnd.ccf", response.ContentType());
        Assert.Equal(Vectors.Bytes("v01-products-plain"), body);
    }

    [Fact]
    public async Task The_schema_path_prefix_is_configurable()
    {
        await using var host = await Host(new() { ["Ccf:SchemaPathPrefix"] = "/schemas/" });

        Assert.Equal(System.Net.HttpStatusCode.OK, (await host.GetAsync("/schemas/c33a3ed0")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await host.GetAsync("/ccf/schemas/c33a3ed0")).StatusCode);
    }

    [Fact]
    public async Task Invalid_configuration_stops_the_host_from_starting()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => Host(new() { ["Ccf:DictionaryThreshold"] = "7" }));
        Assert.True(
            ex is OptionsValidationException || ex.InnerException is OptionsValidationException || ex.ToString().Contains("DictionaryThreshold"),
            ex.ToString());
    }

    [Fact]
    public async Task Schemas_registered_lazily_become_available_on_first_use_and_pre_registered_ones_immediately()
    {
        await using var cold = await Host(new(), preRegister: false);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await cold.GetAsync("/ccf/schemas/c33a3ed0")).StatusCode);
        await cold.GetAsync("/products", "application/vnd.ccf");
        Assert.Equal(System.Net.HttpStatusCode.OK, (await cold.GetAsync("/ccf/schemas/c33a3ed0")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await cold.GetAsync("/ccf/schemas/63a83963")).StatusCode);

        await using var warm = await Host(new(), preRegister: true);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await warm.GetAsync("/ccf/schemas/63a83963")).StatusCode);
    }

    [Fact]
    public async Task Field_names_follow_the_mvc_json_naming_policy()
    {
        await using var host = await TestHost.StartAsync(services: s =>
            s.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.PropertyNamingPolicy = null));

        var json = await host.GetAsync("/products/1001");
        using var doc = JsonDocument.Parse(await json.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("Name", out _)); // PascalCase in JSON …

        var (_, body) = await host.GetBytesAsync("/products/1001", "application/vnd.ccf");
        var schema = await host.GetAsync("/ccf/schemas/" + CcfDecoder.PeekSchemaId(body));
        Assert.Contains("\"name\":\"Name\"", await schema.Content.ReadAsStringAsync()); // … and in the schema
    }
}

public class ClientFlowTests
{
    [Fact]
    public async Task A_client_can_read_the_schema_id_fetch_and_verify_the_schema_and_decode()
    {
        await using var host = await TestHost.StartAsync(config: new() { ["Sample:PreRegisterSchemas"] = "false" });

        var (response, payload) = await host.GetBytesAsync("/products", "application/vnd.ccf; features=d, application/json;q=0.5");
        Assert.Equal("application/vnd.ccf; features=d", response.ContentType());

        var id = CcfDecoder.PeekSchemaId(payload);
        var schemaResponse = await host.GetAsync("/ccf/schemas/" + id);
        var schemaText = await schemaResponse.Content.ReadAsStringAsync();
        var schema = CcfSchema.Parse(schemaText);

        Assert.Equal(id, schema.IdHex);           // hash of the returned text equals the id
        Assert.Equal(schemaText, schema.CanonicalJson);

        var products = CcfDecoder.Decode<List<Product>>(payload, schema);
        Assert.Equal(500, products.Count);
        Assert.Equal(361.48m, products[0].Price);
        Assert.Equal(ProductStatus.active, products[0].Status);
    }
}

public class DiagnosticsTests
{
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        public List<(string Instrument, long Value, string Tags)> Measurements { get; } = new();

        public MeterCapture()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == CcfDiagnostics.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                lock (Measurements)
                {
                    Measurements.Add((instrument.Name, value, string.Join(",", tags.ToArray().Select(t => $"{t.Key}={t.Value}"))));
                }
            });
            _listener.Start();
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public async Task Served_ccf_responses_are_counted_with_format_features_and_bytes()
    {
        await using var host = await TestHost.StartAsync();
        using var meter = new MeterCapture();

        await host.GetBytesAsync("/products", "application/vnd.ccf; features=d");
        await host.GetBytesAsync("/min/products", "application/vnd.ccf");

        Assert.Contains(("ccf.responses", 1L, "format=ccf,features=d"), meter.Measurements);
        Assert.Contains(("ccf.responses", 1L, "format=ccf,features="), meter.Measurements);
        Assert.Equal(5266L + 9317L, meter.Measurements.Where(m => m.Instrument == "ccf.bytes_written").Sum(m => m.Value));
        Assert.DoesNotContain(meter.Measurements, m => m.Instrument == "ccf.encode_failures");
    }

    [Theory]
    [InlineData("/products", null)]                                 // MVC, client did not ask for CCF
    [InlineData("/min/products", null)]                             // minimal API, same
    [InlineData("/json-only", "application/vnd.ccf")]               // [Produces] keeps CCF away even when asked
    [InlineData("/products", "application/json, application/vnd.ccf")] // an explicit JSON range ranks first
    [InlineData("/min/products", "application/json, application/vnd.ccf")]
    public async Task Json_responses_of_ccf_capable_endpoints_are_counted_once_as_format_json(string path, string? accept)
    {
        await using var host = await TestHost.StartAsync();
        using var meter = new MeterCapture();

        await host.GetBytesAsync(path, accept);

        Assert.Equal(new[] { ("ccf.responses", 1L, "format=json,features=") }, meter.Measurements.ToArray());
    }

    [Theory]
    [InlineData("/unsupported")]       // the type has no schema: not a CCF-capable endpoint
    [InlineData("/min/unsupported")]
    [InlineData("/null")]              // no value, no schema
    [InlineData("/min/null")]
    public async Task Responses_of_endpoints_that_cannot_serve_ccf_are_not_counted(string path)
    {
        await using var host = await TestHost.StartAsync();
        using var meter = new MeterCapture();

        await host.GetBytesAsync(path);
        await host.GetBytesAsync(path, "application/vnd.ccf");

        Assert.Empty(meter.Measurements);
    }

    [Theory]
    [InlineData("/bad-decimal")]
    [InlineData("/min/bad-decimal")]
    public async Task Encode_failures_are_counted_and_logged_as_a_warning_with_the_schema_id(string path)
    {
        await using var host = await TestHost.StartAsync();
        using var meter = new MeterCapture();

        await host.GetBytesAsync(path, "application/vnd.ccf");

        Assert.Contains(meter.Measurements, m => m.Instrument == "ccf.encode_failures" && m.Value == 1);
        Assert.Contains(meter.Measurements, m => m.Instrument == "ccf.responses" && m.Tags == "format=json-fallback,features=");
        // A fallback is one response, counted as json-fallback only (not also as json).
        Assert.Single(meter.Measurements, m => m.Instrument == "ccf.responses");
        Assert.DoesNotContain(meter.Measurements, m => m.Instrument == "ccf.bytes_written");

        var warning = Assert.Single(host.Logs, l => l.Category == "Ccf" && l.Level == LogLevel.Warning);
        Assert.Contains("PricedItem", warning.Message);
        Assert.Contains("1.234", warning.Message);
        Assert.Matches("[0-9a-f]{8}", warning.Message);
        Assert.IsType<CcfEncodeException>(warning.Exception);
    }

    [Fact]
    public async Task Registration_and_unsupported_types_are_logged_at_debug_level_in_the_Ccf_category()
    {
        await using var host = await TestHost.StartAsync();
        await host.GetAsync("/unsupported", "application/vnd.ccf");
        await host.GetAsync("/bad-decimal", "application/vnd.ccf");

        Assert.Contains(host.Logs, l => l.Category == "Ccf" && l.Level == LogLevel.Debug && l.Message.Contains("Registered CCF schema c33a3ed0"));
        Assert.Contains(host.Logs, l => l.Category == "Ccf" && l.Level == LogLevel.Debug && l.Message.Contains("UnsupportedReport") && l.Message.Contains("JSON fallback"));
        // Each type is analysed once, however many requests follow.
        var before = host.Logs.Count(l => l.Category == "Ccf" && l.Message.Contains("UnsupportedReport"));
        await host.GetAsync("/unsupported", "application/vnd.ccf");
        Assert.Equal(before, host.Logs.Count(l => l.Category == "Ccf" && l.Message.Contains("UnsupportedReport")));
        Assert.Equal(1, before);
    }
}

public class AcceptRuleTests
{
    // The four cases the project owner asked for, on both paths.
    [Theory]
    [InlineData("application/vnd.ccf, */*;q=0.1", true)]
    [InlineData("application/json, application/vnd.ccf;q=0.5", false)]
    [InlineData("application/vnd.ccf;q=0, */*", false)]
    [InlineData("*/*", false)]
    // and a few neighbours
    [InlineData("application/vnd.ccf; features=d, */*", true)]
    [InlineData("*/*;q=0.9, application/vnd.ccf;q=0.2", true)]
    [InlineData("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8", false)]
    [InlineData("application/json;q=0.4, application/vnd.ccf;q=0.5, */*", true)]
    public async Task Controllers_and_minimal_apis_apply_the_same_rule(string accept, bool expectCcf)
    {
        await using var host = await TestHost.StartAsync();
        foreach (var path in new[] { "/products", "/min/products" })
        {
            var response = await host.GetAsync(path, accept);
            if (expectCcf)
            {
                Assert.StartsWith("application/vnd.ccf", response.ContentType());
            }
            else
            {
                Assert.StartsWith("application/json", response.ContentType());
            }
        }
    }

    [Fact]
    public async Task The_exception_does_not_change_RespectBrowserAcceptHeader()
    {
        await using var host = await TestHost.StartAsync();
        Assert.False(host.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.MvcOptions>>().Value.RespectBrowserAcceptHeader);

        // Other media types keep MVC's behaviour: a wildcard in the header still makes MVC ignore it.
        var response = await host.GetAsync("/products", "text/xml, */*;q=0.1");
        Assert.StartsWith("application/json", response.ContentType());
    }

    [Fact]
    public async Task Produces_restricts_the_offered_types_so_ccf_is_never_served_there()
    {
        await using var host = await TestHost.StartAsync();
        foreach (var accept in new[] { "application/vnd.ccf", "application/vnd.ccf, */*;q=0.1", "application/vnd.ccf; features=d, application/json;q=0.5" })
        {
            var response = await host.GetAsync("/json-only", accept);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
            Assert.StartsWith("application/json", response.ContentType());
            Assert.Contains("Accept", response.Vary());
        }
    }
}

public class SingleEnumerationTests
{
    private static EnumerationCounter Counter(TestHost host) => host.Services.GetRequiredService<EnumerationCounter>();

    [Theory]
    [InlineData("/counted")]
    [InlineData("/min/counted")]
    public async Task A_lazy_sequence_is_enumerated_once_when_served_as_ccf(string path)
    {
        await using var host = await TestHost.StartAsync();
        Counter(host).Reset();

        var (response, body) = await host.GetBytesAsync(path, "application/vnd.ccf; features=d");

        Assert.StartsWith("application/vnd.ccf", response.ContentType());
        Assert.Equal(1, Counter(host).Count);
        var products = CcfDecoder.Decode<List<Product>>(body, CcfSchema.Parse(Vectors.SchemaText("schema-Product.json")));
        Assert.Equal(5, products.Count);
        Assert.Equal(1001, products[0].Id);
    }

    [Theory]
    [InlineData("/counted-bad")]
    [InlineData("/min/counted-bad")]
    public async Task The_json_fallback_after_an_encode_failure_does_not_enumerate_again(string path)
    {
        await using var host = await TestHost.StartAsync();
        Counter(host).Reset();

        var (response, body) = await host.GetBytesAsync(path, "application/vnd.ccf");

        Assert.StartsWith("application/json", response.ContentType());
        Assert.Equal(1, Counter(host).Count);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        Assert.Equal(1.234m, doc.RootElement[0].GetProperty("price").GetDecimal());
    }

    [Theory]
    [InlineData("/counted", null)]
    [InlineData("/counted", "application/json")]
    [InlineData("/min/counted", null)]
    [InlineData("/min/counted", "application/json")]
    [InlineData("/counted-bad", null)]
    public async Task Plain_json_responses_enumerate_once_too(string path, string? accept)
    {
        await using var host = await TestHost.StartAsync();
        Counter(host).Reset();

        var response = await host.GetAsync(path, accept);

        Assert.StartsWith("application/json", response.ContentType());
        Assert.Equal(1, Counter(host).Count);
    }
}

public class VaryTests
{
    [Theory]
    [InlineData("/products")]
    [InlineData("/products/1001")]
    [InlineData("/products/999999")]   // 404
    [InlineData("/scalar-value")]
    [InlineData("/unsupported")]
    [InlineData("/bad-decimal")]       // encode failure → JSON
    [InlineData("/null")]              // 204
    [InlineData("/json-only")]
    [InlineData("/min/products")]
    [InlineData("/min/null")]
    [InlineData("/min/unsupported")]
    [InlineData("/min/bad-decimal")]
    [InlineData("/ccf/schemas/c33a3ed0")]
    [InlineData("/ccf/schemas/00000000")]
    [InlineData("/no/such/route")]
    public async Task Vary_Accept_is_sent_whenever_the_request_mentions_ccf_whatever_the_outcome(string path)
    {
        await using var host = await TestHost.StartAsync();
        foreach (var accept in new[] { "application/vnd.ccf", "application/json, application/vnd.ccf;q=0.5", "application/vnd.ccf;q=0, */*" })
        {
            var response = await host.GetAsync(path, accept);
            Assert.Contains("Accept", response.Vary());
            Assert.Single(response.Vary(), "Accept"); // never duplicated
        }
    }

    [Fact]
    public async Task Endpoints_that_can_serve_ccf_vary_on_Accept_even_for_requests_that_do_not_mention_it()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Contains("Accept", (await host.GetAsync("/products")).Vary());
        Assert.Contains("Accept", (await host.GetAsync("/min/products", "application/json")).Vary());
        Assert.Contains("Accept", (await host.GetAsync("/products/1001", "*/*")).Vary());
    }

    [Fact]
    public async Task Endpoints_that_cannot_serve_ccf_do_not_vary_for_requests_that_do_not_mention_it()
    {
        await using var host = await TestHost.StartAsync();
        Assert.DoesNotContain("Accept", (await host.GetAsync("/scalar-value")).Vary());
        Assert.DoesNotContain("Accept", (await host.GetAsync("/unsupported", "application/json")).Vary());
        Assert.DoesNotContain("Accept", (await host.GetAsync("/min/unsupported")).Vary());
    }
}

public class NamingPerPathTests
{
    private static async Task<string> SchemaIdOf(TestHost host, string path)
    {
        var (_, body) = await host.GetBytesAsync(path, "application/vnd.ccf");
        return CcfDecoder.PeekSchemaId(body);
    }

    [Fact]
    public async Task By_default_both_paths_use_camel_case_share_one_schema_and_log_no_warning()
    {
        await using var host = await TestHost.StartAsync();

        Assert.Equal(await SchemaIdOf(host, "/products/1001"), await SchemaIdOf(host, "/min/products/1001"));
        Assert.DoesNotContain(host.Logs, l => l.Level == LogLevel.Warning && l.Category == "Ccf");
    }

    [Fact]
    public async Task Each_path_follows_its_own_json_options_so_the_schemas_and_ids_differ()
    {
        await using var host = await TestHost.StartAsync(services: s =>
            s.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = null)); // minimal APIs: PascalCase; MVC stays camelCase

        var mvcId = await SchemaIdOf(host, "/products/1001");
        var httpId = await SchemaIdOf(host, "/min/products/1001");
        Assert.Equal("c33a3ed0", mvcId);
        Assert.NotEqual(mvcId, httpId);

        var mvcSchema = await (await host.GetAsync("/ccf/schemas/" + mvcId)).Content.ReadAsStringAsync();
        var httpSchema = await (await host.GetAsync("/ccf/schemas/" + httpId)).Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"name\"", mvcSchema);
        Assert.Contains("\"name\":\"Name\"", httpSchema);

        // The CCF payload of each path decodes against its own schema, and matches that path's JSON field names.
        var (_, httpBody) = await host.GetBytesAsync("/min/products/1001", "application/vnd.ccf");
        var product = CcfDecoder.Decode<Product>(httpBody, CcfSchema.Parse(httpSchema), new CcfSchemaOptions { PropertyNamingPolicy = null });
        Assert.Equal("Steel Monitor", product.Name);
        using var json = JsonDocument.Parse(await (await host.GetAsync("/min/products/1001")).Content.ReadAsStringAsync());
        Assert.True(json.RootElement.TryGetProperty("Name", out _));

        var registry = host.Services.GetRequiredService<ISchemaRegistry>();
        Assert.Equal(2, registry.Schemas.Count(s => s.Name == "Product"));
    }

    [Fact]
    public async Task A_startup_warning_is_logged_once_when_the_two_option_sets_name_fields_differently()
    {
        await using var host = await TestHost.StartAsync(services: s =>
            s.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower));

        await host.GetAsync("/products");
        await host.GetAsync("/min/products");
        var warning = Assert.Single(host.Logs, l => l.Level == LogLevel.Warning && l.Category == "Ccf");
        Assert.Contains("different property naming policies", warning.Message);
    }
}
