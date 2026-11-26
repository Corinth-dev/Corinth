using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Corinth.AspNetCore;

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
        services.TryAddSingleton<CcfPaths>();
        services.TryAddSingleton(sp => sp.GetRequiredService<CcfPaths>().Mvc.Encoder);
        services.TryAddSingleton(sp => new CcfOutputFormatter(
            sp.GetRequiredService<ISchemaRegistry>(),
            sp.GetRequiredService<CcfPaths>(),
            sp.GetRequiredService<IOptions<CcfOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.TryAddEnumerable(ServiceDescriptor.Transient<IConfigureOptions<MvcOptions>, CcfMvcOptionsSetup>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, CcfStartupFilter>());

        // Decorate MVC's formatter selection (works whether AddControllers ran before or after AddCcf: the
        // replacement builds MVC's own default selector as the inner one).
        services.Replace(ServiceDescriptor.Singleton<Microsoft.AspNetCore.Mvc.Infrastructure.OutputFormatterSelector>(sp =>
            new CcfOutputFormatterSelector(
                new Microsoft.AspNetCore.Mvc.Infrastructure.DefaultOutputFormatterSelector(
                    sp.GetRequiredService<IOptions<MvcOptions>>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()),
                sp.GetRequiredService<IOptions<MvcOptions>>())));
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
/// Startup work that needs no hosted service: warns once when the MVC and minimal-API JSON options name fields
/// differently, adds the development-only <c>?ccf=d</c> override, and sends <c>Vary: Accept</c> on every response
/// to a request whose <c>Accept</c> mentions <c>application/vnd.ccf</c> (SPEC §2.3), whatever the outcome.
/// </summary>
/// <remarks>
/// The override is active only when <see cref="CcfOptions.AllowQueryOverride"/> is set and the environment is
/// Development: <c>?ccf=d</c> (or <c>?ccf=</c> for plain CCF) behaves as if the request had sent
/// <c>Accept: application/vnd.ccf; features=…</c>; <c>?ccf=off</c>, <c>json</c> or <c>0</c> leave the request alone.
/// </remarks>
internal sealed class CcfStartupFilter : IStartupFilter
{
    private static readonly string[] NamingProbes = { "PascalCaseName", "ID", "URLValue", "a1B2", "snake_Case", "X" };

    private readonly IOptions<CcfOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly ISchemaRegistry _registry;
    private readonly Microsoft.Extensions.Logging.ILoggerFactory _loggerFactory;

    public CcfStartupFilter(
        IOptions<CcfOptions> options, IHostEnvironment environment, ISchemaRegistry registry, Microsoft.Extensions.Logging.ILoggerFactory loggerFactory)
    {
        _options = options;
        _environment = environment;
        _registry = registry;
        _loggerFactory = loggerFactory;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            WarnIfNamingDiffers();

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

            app.Use((HttpContext context, RequestDelegate nextMiddleware) =>
            {
                if (CcfNegotiation.Mentions(context.Request))
                {
                    // Added when the response starts so that every outcome is covered: CCF, JSON, fallback, 404, errors.
                    context.Response.OnStarting(static state =>
                    {
                        CcfNegotiation.AddVaryAccept((HttpResponse)state);
                        return Task.CompletedTask;
                    }, context.Response);
                }

                return nextMiddleware(context);
            });

            next(app);
        };

    private void WarnIfNamingDiffers()
    {
        var mvc = _registry.SchemaOptions.PropertyNamingPolicy;
        var http = _registry.HttpSchemaOptions.PropertyNamingPolicy;
        foreach (var probe in NamingProbes)
        {
            if ((mvc?.ConvertName(probe) ?? probe) != (http?.ConvertName(probe) ?? probe))
            {
                _loggerFactory.CreateLogger("Ccf").LogWarning(
                    "MVC JsonOptions ({Mvc}) and minimal-API JsonOptions ({Http}) use different property naming policies: CCF follows each path's own policy, so the same type gets a different schema and id on the two paths.",
                    mvc?.GetType().Name ?? "none",
                    http?.GetType().Name ?? "none");
                return;
            }
        }
    }
}
