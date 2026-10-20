using System.Diagnostics.Metrics;
using System.Text.Json;
using Ccf.SampleApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ccf.AspNetCore.Tests;

public class DevelopmentOverrideTests
{
    private static Task<SampleHost> Host(string environment, bool allow) =>
        SampleHost.StartAsync(
            b => b.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Ccf:AllowQueryOverride"] = allow ? "true" : "false" }),
            environment);

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
    private static Task<SampleHost> Host(Dictionary<string, string?> config, bool preRegister = true) =>
        SampleHost.StartAsync(b => b.Configuration.AddInMemoryCollection(config), preRegisterSchemas: preRegister);

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
        await Assert.ThrowsAsync<OptionsValidationException>(() => Host(new() { ["Ccf:DictionaryThreshold"] = "7" }));
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
        await using var host = await SampleHost.StartAsync(b =>
            b.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(o => o.JsonSerializerOptions.PropertyNamingPolicy = null));

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
        await using var host = await SampleHost.StartAsync(preRegisterSchemas: false);

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
        await using var host = await SampleHost.StartAsync();
        using var meter = new MeterCapture();

        await host.GetBytesAsync("/products", "application/vnd.ccf; features=d");
        await host.GetBytesAsync("/min/products", "application/vnd.ccf");
        await host.GetBytesAsync("/products"); // JSON: not counted

        Assert.Contains(("ccf.responses", 1L, "format=ccf,features=d"), meter.Measurements);
        Assert.Contains(("ccf.responses", 1L, "format=ccf,features="), meter.Measurements);
        Assert.Equal(5266L + 9317L, meter.Measurements.Where(m => m.Instrument == "ccf.bytes_written").Sum(m => m.Value));
        Assert.DoesNotContain(meter.Measurements, m => m.Instrument == "ccf.encode_failures");
    }

    [Theory]
    [InlineData("/bad-decimal")]
    [InlineData("/min/bad-decimal")]
    public async Task Encode_failures_are_counted_and_logged_as_a_warning_with_the_schema_id(string path)
    {
        await using var host = await SampleHost.StartAsync();
        using var meter = new MeterCapture();

        await host.GetBytesAsync(path, "application/vnd.ccf");

        Assert.Contains(meter.Measurements, m => m.Instrument == "ccf.encode_failures" && m.Value == 1);
        Assert.Contains(meter.Measurements, m => m.Instrument == "ccf.responses" && m.Tags == "format=json-fallback,features=");
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
        await using var host = await SampleHost.StartAsync();
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
