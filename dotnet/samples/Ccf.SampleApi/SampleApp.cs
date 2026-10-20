using System.Text.Json.Serialization;
using Ccf.AspNetCore;
using Scalar.AspNetCore;

namespace Ccf.SampleApi;

/// <summary>Builds the sample application; shared by <c>Program</c> and the integration tests.</summary>
public static class SampleApp
{
    /// <summary>Creates (but does not start) the application.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <param name="configure">Lets the caller adjust the builder (environment, configuration, options).</param>
    public static WebApplication Create(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services
            .AddControllers()
            .AddApplicationPart(typeof(SampleApp).Assembly)
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<SampleData>();
        builder.Services.AddSingleton<EnumerationCounter>();
        builder.Services.AddCcf();
        builder.Services.AddOpenApi(OpenApiSupport.Configure);

        configure?.Invoke(builder);
        var app = builder.Build();

        // Registering schemas at startup lets the schema endpoint serve them before the first request
        // (several instances behind a load balancer). `Sample:PreRegisterSchemas=false` turns it off for tests.
        if (app.Configuration.GetValue("Sample:PreRegisterSchemas", true))
        {
            var registry = app.Services.GetRequiredService<ISchemaRegistry>();
            registry.Register<List<Product>>();
            registry.Register<List<Order>>();
            registry.Register<List<AllTypes>>();
        }

        app.MapControllers();
        app.MapCcfSchemas().WithTags("CCF").WithSummary("The canonical schema JSON for a schema id (cached forever).")
            .Produces<string>(StatusCodes.Status200OK, "application/json").Produces(StatusCodes.Status404NotFound);

        // OpenAPI document (/openapi/v1.json) and the Scalar UI (/scalar/v1, Scalar's default path).
        app.MapOpenApi();
        CcfViewer.Map(app);
        app.MapScalarApiReference(o => o.WithTitle("CCF sample API").DisableDefaultFonts().WithJavaScriptConfiguration(CcfViewer.ScriptPath));

        // The same data through minimal APIs: CcfResults.Ok opts each endpoint in; Produces<T> describes it for OpenAPI.
        var minimal = app.MapGroup("/min").WithTags("Minimal APIs");
        minimal.MapGet("/products", (SampleData data) => CcfResults.Ok(data.Products.ToList()))
            .WithSummary("All products (CCF-capable).").Produces<List<Product>>();
        minimal.MapGet("/products/{id:long}", (long id, SampleData data) =>
                data.Products.FirstOrDefault(p => p.Id == id) is { } p ? CcfResults.Ok(p) : Results.NotFound())
            .WithSummary("One product (CCF-capable).").Produces<Product>().Produces(StatusCodes.Status404NotFound);
        minimal.MapGet("/bad-decimal", () => CcfResults.Ok(new List<PricedItem> { new() { Name = "x", Price = 1.234m } }))
            .WithSummary("Data CCF cannot encode: falls back to JSON.").Produces<List<PricedItem>>();
        minimal.MapGet("/counted", (EnumerationCounter counter, SampleData data) =>
                CcfResults.Ok(new CountingEnumerable<Product>(data.Products.Take(5), counter)))
            .WithSummary("A lazy sequence that must be enumerated once.").Produces<List<Product>>();
        minimal.MapGet("/counted-bad", (EnumerationCounter counter) =>
                CcfResults.Ok(new CountingEnumerable<PricedItem>(new[] { new PricedItem { Name = "x", Price = 1.234m } }, counter)))
            .WithSummary("A lazy sequence CCF cannot encode: JSON fallback without a second enumeration.").Produces<List<PricedItem>>();
        minimal.MapGet("/null", () => CcfResults.Ok<Product?>(null))
            .WithSummary("A null result.").Produces<Product>();
        minimal.MapGet("/unsupported", () => CcfResults.Ok(new UnsupportedReport { Title = "report", Details = new { a = 1 } }))
            .WithSummary("A type with no CCF schema: always JSON.").Produces<UnsupportedReport>();

        return app;
    }
}
