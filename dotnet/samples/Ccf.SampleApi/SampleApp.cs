using System.Text.Json.Serialization;
using Ccf.AspNetCore;

namespace Ccf.SampleApi;

/// <summary>Builds the sample application; shared by <c>Program</c> and the integration tests.</summary>
public static class SampleApp
{
    /// <summary>Creates (but does not start) the application.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configure">Lets the caller adjust the builder (environment, configuration, options).</param>
    /// <param name="preRegisterSchemas">Registers the sample schemas at startup so the schema endpoint knows them before the first request.</param>
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null, bool preRegisterSchemas = true)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(SampleApp).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<SampleData>();
        builder.Services.AddCcf();

        configure?.Invoke(builder);
        var app = builder.Build();

        if (preRegisterSchemas)
        {
            var registry = app.Services.GetRequiredService<ISchemaRegistry>();
            registry.Register<List<Product>>();
            registry.Register<List<Order>>();
            registry.Register<List<AllTypes>>();
        }

        app.MapControllers();
        app.MapCcfSchemas();

        // The same data through minimal APIs.
        app.MapGet("/min/products", (SampleData data) => CcfResults.Ok(data.Products.ToList()));
        app.MapGet("/min/products/{id:long}", (long id, SampleData data) =>
            data.Products.FirstOrDefault(p => p.Id == id) is { } p ? CcfResults.Ok(p) : Results.NotFound());
        app.MapGet("/min/bad-decimal", () => CcfResults.Ok(new List<PricedItem> { new() { Name = "x", Price = 1.234m } }));
        app.MapGet("/min/unsupported", () => CcfResults.Ok(new UnsupportedReport { Title = "report", Details = new { a = 1 } }));

        return app;
    }
}
