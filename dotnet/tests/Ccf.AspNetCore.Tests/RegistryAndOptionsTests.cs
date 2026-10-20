using Ccf.SampleApi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ccf.AspNetCore.Tests;

public class RegistryTests
{
    private static CcfSchemaRegistry NewRegistry(Func<Type, CcfSchemaOptions, (CcfSchema?, string?)>? build = null, ILogger? logger = null) =>
        new(CcfSchemaOptions.Default, new CcfSchemaOptions { PropertyNamingPolicy = null }, logger ?? NullLogger.Instance, build);

    [Fact]
    public void Schemas_are_built_lazily_cached_and_found_by_id()
    {
        var registry = NewRegistry();
        Assert.Empty(registry.Schemas);

        Assert.True(registry.TryGetSchema(typeof(List<Product>), out var first));
        Assert.True(registry.TryGetSchema(typeof(List<Product>), out var second));
        Assert.Same(first, second);
        Assert.Equal("c33a3ed0", first!.IdHex);
        Assert.True(registry.TryGetById("c33a3ed0", out var byId));
        Assert.Same(first, byId);
        Assert.False(registry.TryGetById("00000000", out _));
        Assert.Single(registry.Schemas);
    }

    [Fact]
    public void Different_clr_shapes_of_the_same_record_share_one_schema_without_a_collision()
    {
        var registry = NewRegistry();
        foreach (var t in new[] { typeof(Product), typeof(List<Product>), typeof(Product[]), typeof(IEnumerable<Product>), typeof(IReadOnlyList<Product>) })
        {
            Assert.True(registry.TryGetSchema(t, out var s), t.ToString());
            Assert.Equal("c33a3ed0", s!.IdHex);
        }

        Assert.Single(registry.Schemas);
    }

    [Fact]
    public void The_registry_is_keyed_by_type_and_naming_policy()
    {
        var registry = NewRegistry();
        Assert.True(registry.TryGetSchema(typeof(Product), registry.SchemaOptions, out var camel));
        Assert.True(registry.TryGetSchema(typeof(Product), registry.HttpSchemaOptions, out var pascal));
        Assert.True(registry.TryGetSchema(typeof(Product), out var defaultPath));

        Assert.NotEqual(camel!.IdHex, pascal!.IdHex);
        Assert.Contains("\"name\":\"Name\"", pascal.CanonicalJson);
        Assert.Same(camel, defaultPath);
        Assert.Equal(2, registry.Schemas.Count);
        Assert.True(registry.TryGetById(pascal.IdHex, out var found));
        Assert.Same(pascal, found);
        Assert.Same(pascal, registry.Register(typeof(Product), registry.HttpSchemaOptions));
    }

    [Fact]
    public void Unsupported_types_are_cached_as_unsupported_and_register_throws_with_the_reason()
    {
        var registry = NewRegistry();
        Assert.False(registry.TryGetSchema(typeof(UnsupportedReport), out var schema));
        Assert.Null(schema);
        Assert.False(registry.TryGetSchema(typeof(int), out _));
        Assert.False(registry.TryGetSchema(typeof(Dictionary<string, int>), out _));
        Assert.Empty(registry.Schemas);

        var ex = Assert.Throws<InvalidOperationException>(() => registry.Register<UnsupportedReport>());
        Assert.Contains("details", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Register_makes_a_schema_available_before_any_request()
    {
        var registry = NewRegistry();
        var schema = registry.Register<List<Order>>();

        Assert.True(registry.TryGetById(schema.IdHex, out _));
        Assert.Same(schema, registry.Register<Order>());
    }

    private sealed class Hashset { public int A { get; set; } }

    [Fact]
    public void Runtime_collection_types_are_served_through_IEnumerable_of_the_element()
    {
        var registry = NewRegistry();
        var iterator = new[] { new Hashset() }.Where(_ => true).Select(x => x).GetType();
        Assert.True(registry.TryGetSchema(iterator, out var schema));
        Assert.Equal("Hashset", schema!.Name);
        Assert.True(registry.TryGetSchema(typeof(HashSet<Hashset>), out _));
    }

    [Fact]
    public void Parallel_lookups_agree()
    {
        var registry = NewRegistry();
        var types = new[] { typeof(List<Product>), typeof(List<Order>), typeof(List<AllTypes>), typeof(UnsupportedReport), typeof(Product) };
        var results = new bool[2000];
        Parallel.For(0, results.Length, i => results[i] = registry.TryGetSchema(types[i % types.Length], out _));

        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(types[i % types.Length] != typeof(UnsupportedReport), results[i]);
        }

        Assert.Equal(3, registry.Schemas.Count);
    }

    [Fact]
    public void Two_different_schemas_with_the_same_id_are_refused_and_the_second_type_is_served_as_json()
    {
        // Find two different canonical schemas whose 4-byte ids collide (birthday search, ~80k hashes).
        var seen = new Dictionary<string, CcfSchema>();
        CcfSchema? a = null, b = null;
        for (var i = 0; a is null; i++)
        {
            var s = CcfSchema.Create("T" + i, new[] { new CcfField("x", CcfType.Int32) });
            if (seen.TryGetValue(s.IdHex, out var other))
            {
                (a, b) = (other, s);
            }
            else
            {
                seen[s.IdHex] = s;
            }
        }

        Assert.Equal(a!.IdHex, b!.IdHex);
        Assert.NotEqual(a.CanonicalJson, b.CanonicalJson);

        var logs = new System.Collections.Concurrent.ConcurrentQueue<LogEntry>();
        var logger = new CapturingLoggerProvider(logs).CreateLogger("Ccf");
        var registry = NewRegistry((t, _) => t == typeof(Product) ? (a, null) : (b, null), logger);

        Assert.True(registry.TryGetSchema(typeof(Product), out var first));
        Assert.Same(a, first);
        Assert.False(registry.TryGetSchema(typeof(Order), out _));
        Assert.Throws<InvalidOperationException>(() => registry.Register<Order>());
        Assert.True(registry.TryGetSchema(typeof(Product), out _)); // the first registration keeps working
        Assert.Single(registry.Schemas);
        Assert.Contains(logs, l => l.Level == LogLevel.Error && l.Message.Contains("collides"));
    }
}

public class OptionsTests
{
    private static ServiceProvider Services(Dictionary<string, string?> config, Action<CcfOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeEnv());
        services.AddCcf(configure);
        return services.BuildServiceProvider();
    }

    private sealed class FakeEnv : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "t";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void Defaults_match_the_architecture()
    {
        var o = Services(new()).GetRequiredService<IOptions<CcfOptions>>().Value;
        Assert.Equal(0.5, o.DictionaryThreshold);
        Assert.Equal(2, o.DefaultDecimalScale);
        Assert.Equal(100_000, o.Limits.MaxRows);
        Assert.Equal(32 * 1024 * 1024, o.Limits.MaxPayloadBytes);
        Assert.Equal(1024 * 1024, o.Limits.MaxStringBytes);
        Assert.Equal("/ccf/schemas", o.SchemaPathPrefix);
        Assert.False(o.AllowQueryOverride);
    }

    [Fact]
    public void Options_bind_from_the_Ccf_configuration_section()
    {
        var o = Services(new()
        {
            ["Ccf:DictionaryThreshold"] = "0.25",
            ["Ccf:DefaultDecimalScale"] = "4",
            ["Ccf:Limits:MaxRows"] = "1234",
            ["Ccf:Limits:MaxPayloadBytes"] = "99999",
            ["Ccf:Limits:MaxStringBytes"] = "77",
            ["Ccf:SchemaPathPrefix"] = "/s",
            ["Ccf:AllowQueryOverride"] = "true",
        }).GetRequiredService<IOptions<CcfOptions>>().Value;

        Assert.Equal(0.25, o.DictionaryThreshold);
        Assert.Equal(4, o.DefaultDecimalScale);
        Assert.Equal(1234, o.Limits.MaxRows);
        Assert.Equal(99999, o.Limits.MaxPayloadBytes);
        Assert.Equal(77, o.Limits.MaxStringBytes);
        Assert.Equal("/s", o.SchemaPathPrefix);
        Assert.True(o.AllowQueryOverride);
        Assert.Equal(100_000, CcfLimits.Default.MaxRows); // binding never touches the shared default instance
    }

    [Fact]
    public void Code_configuration_wins_over_the_configuration_file()
    {
        var o = Services(new() { ["Ccf:DictionaryThreshold"] = "0.25" }, c => c.DictionaryThreshold = 0.75)
            .GetRequiredService<IOptions<CcfOptions>>().Value;
        Assert.Equal(0.75, o.DictionaryThreshold);
    }

    [Theory]
    [InlineData("Ccf:DictionaryThreshold", "0")]
    [InlineData("Ccf:DictionaryThreshold", "1.5")]
    [InlineData("Ccf:DefaultDecimalScale", "19")]
    [InlineData("Ccf:DefaultDecimalScale", "-1")]
    [InlineData("Ccf:Limits:MaxRows", "0")]
    [InlineData("Ccf:SchemaPathPrefix", "no-slash")]
    [InlineData("Ccf:SchemaPathPrefix", "")]
    public void Invalid_options_fail_validation(string key, string value)
    {
        var sp = Services(new() { [key] = value });
        Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<CcfOptions>>().Value);
    }

    [Fact]
    public void The_schema_and_encoder_follow_the_options()
    {
        var sp = Services(new() { ["Ccf:DefaultDecimalScale"] = "3" });
        var registry = sp.GetRequiredService<ISchemaRegistry>();
        Assert.Equal(3, registry.SchemaOptions.DefaultDecimalScale);
        Assert.Same(sp.GetRequiredService<CcfEncoder>(), sp.GetRequiredService<CcfEncoder>());
        Assert.Same(registry, sp.GetRequiredService<ISchemaRegistry>());
    }
}
