using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Ccf.AspNetCore;

/// <summary>Registration of CCF in an ASP.NET Core application.</summary>
public static class CcfServiceCollectionExtensions
{
    /// <summary>
    /// Adds CCF: options (bound from the <c>Ccf</c> configuration section), the schema registry, the encoder,
    /// the MVC output formatter and the development query override. Nothing runs unless a request asks for CCF.
    /// </summary>
    public static IServiceCollection AddCcf(this IServiceCollection services, Action<CcfOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = services.AddOptions<CcfOptions>().BindConfiguration(CcfOptions.SectionName);
        if (configure is not null)
        {
            builder.Configure(configure);
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<CcfOptions>, CcfOptionsValidator>());
        builder.ValidateOnStart();

        services.TryAddSingleton<ISchemaRegistry, CcfSchemaRegistry>();
        services.TryAddSingleton(sp =>
        {
            var registry = sp.GetRequiredService<ISchemaRegistry>();
            return new CcfEncoder(registry.SchemaOptions, sp.GetRequiredService<IOptions<CcfOptions>>().Value.DictionaryThreshold);
        });
        services.TryAddSingleton<CcfOutputFormatter>();
        services.TryAddEnumerable(ServiceDescriptor.Transient<IConfigureOptions<MvcOptions>, CcfMvcOptionsSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, CcfQueryOverrideStartupFilter>());
        return services;
    }
}

internal sealed class CcfMvcOptionsSetup : IConfigureOptions<MvcOptions>
{
    private readonly IServiceProvider _services;

    public CcfMvcOptionsSetup(IServiceProvider services)
    {
        _services = services;
    }

    public void Configure(MvcOptions options)
    {
        // Appended after the built-in formatters: with no explicit media type MVC picks the first formatter
        // that can write, and that must remain JSON.
        options.OutputFormatters.Add(_services.GetRequiredService<CcfOutputFormatter>());
        options.Filters.Add(new CcfVaryResultFilterFactory());
    }

    private sealed class CcfVaryResultFilterFactory : Microsoft.AspNetCore.Mvc.Filters.IFilterFactory
    {
        public bool IsReusable => true;

        public Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
            new CcfVaryResultFilter(serviceProvider.GetRequiredService<ISchemaRegistry>());
    }
}

/// <summary>
/// Development-only convenience: <c>?ccf=d</c> (or <c>?ccf=</c> for plain CCF) behaves as if the request had sent
/// <c>Accept: application/vnd.ccf; features=…</c>. Active only when <see cref="CcfOptions.AllowQueryOverride"/> is
/// set and the environment is Development; <c>?ccf=off</c> or <c>?ccf=json</c> leaves the request alone.
/// </summary>
internal sealed class CcfQueryOverrideStartupFilter : IStartupFilter
{
    private readonly IOptions<CcfOptions> _options;
    private readonly IHostEnvironment _environment;

    public CcfQueryOverrideStartupFilter(IOptions<CcfOptions> options, IHostEnvironment environment)
    {
        _options = options;
        _environment = environment;
    }

    public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) =>
        app =>
        {
            if (_options.Value.AllowQueryOverride && _environment.IsDevelopment())
            {
                app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
                {
                    if (context.Request.Query.TryGetValue("ccf", out var value))
                    {
                        var v = value.ToString();
                        if (!(v.Equals("off", StringComparison.OrdinalIgnoreCase) || v.Equals("json", StringComparison.OrdinalIgnoreCase) || v == "0"))
                        {
                            var features = CcfFeatureParser.Parse(v);
                            context.Request.Headers[HeaderNames.Accept] =
                                (features & CcfFeatures.Dictionary) != 0 ? "application/vnd.ccf; features=d" : "application/vnd.ccf";
                        }
                    }

                    return nextMiddleware(context);
                });
            }

            next(app);
        };
}
