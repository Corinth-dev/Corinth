using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ccf.AspNetCore.Tests;

/// <summary>The sample application hosted with <see cref="WebApplicationFactory{TEntryPoint}"/>.</summary>
public sealed class TestHost : IAsyncDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    private TestHost(WebApplicationFactory<Program> factory, HttpClient client, ConcurrentQueue<LogEntry> logs)
    {
        _factory = factory;
        Client = client;
        Logs = logs;
    }

    public HttpClient Client { get; }

    public ConcurrentQueue<LogEntry> Logs { get; }

    public IServiceProvider Services => _factory.Services;

    /// <param name="environment">Hosting environment name.</param>
    /// <param name="config">Extra configuration (for example <c>Ccf:DictionaryThreshold</c>, <c>Sample:PreRegisterSchemas</c>).</param>
    /// <param name="services">Service overrides applied after the application's own registrations.</param>
    public static Task<TestHost> StartAsync(
        string environment = "Production", Dictionary<string, string?>? config = null, Action<IServiceCollection>? services = null)
    {
        var logs = new ConcurrentQueue<LogEntry>();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (config is not null)
            {
                builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            }

            builder.ConfigureLogging(l =>
            {
                l.ClearProviders();
                l.SetMinimumLevel(LogLevel.Debug);
                l.AddProvider(new CapturingLoggerProvider(logs));
            });
            if (services is not null)
            {
                builder.ConfigureTestServices(services);
            }
        });

        try
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            return Task.FromResult(new TestHost(factory, client, logs));
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? accept = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        return Client.SendAsync(request);
    }

    public async Task<(HttpResponseMessage Response, byte[] Body)> GetBytesAsync(string path, string? accept = null)
    {
        var response = await GetAsync(path, accept);
        return (response, await response.Content.ReadAsByteArrayAsync());
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _factory.DisposeAsync();
    }
}
