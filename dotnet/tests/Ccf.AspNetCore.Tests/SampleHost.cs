using System.Collections.Concurrent;
using Ccf.SampleApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Ccf.AspNetCore.Tests;

/// <summary>
/// Runs the sample application in-process on a real Kestrel socket (port 0) and gives tests an HttpClient.
/// (WebApplicationFactory would need the Microsoft.AspNetCore.Mvc.Testing package; a real socket needs nothing
/// beyond the framework.)
/// </summary>
public sealed class SampleHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private SampleHost(WebApplication app, HttpClient client, ConcurrentQueue<LogEntry> logs)
    {
        _app = app;
        Client = client;
        Logs = logs;
    }

    public HttpClient Client { get; }

    public ConcurrentQueue<LogEntry> Logs { get; }

    public IServiceProvider Services => _app.Services;

    public static async Task<SampleHost> StartAsync(
        Action<WebApplicationBuilder>? configure = null, string environment = "Production", bool preRegisterSchemas = true)
    {
        var logs = new ConcurrentQueue<LogEntry>();
        var app = SampleApp.Create(
            new[] { "--environment=" + environment },
            builder =>
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Logging.SetMinimumLevel(LogLevel.Debug);
                builder.Logging.AddProvider(new CapturingLoggerProvider(logs));
                configure?.Invoke(builder);
            },
            preRegisterSchemas);
        await app.StartAsync();
        var client = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None })
        {
            BaseAddress = new Uri(app.Urls.First()),
        };
        return new SampleHost(app, client, logs);
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
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<LogEntry> _entries;

    public CapturingLoggerProvider(ConcurrentQueue<LogEntry> entries)
    {
        _entries = entries;
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger : ILogger
    {
        private readonly string _category;
        private readonly ConcurrentQueue<LogEntry> _entries;

        public CapturingLogger(string category, ConcurrentQueue<LogEntry> entries)
        {
            _category = category;
            _entries = entries;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(_category, logLevel, formatter(state, exception), exception));
    }
}

internal static class Vectors
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test-vectors");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("test-vectors/manifest.json not found");
    }

    /// <summary>Payload of a vector, resolved through the manifest instead of hard-coded file names.</summary>
    public static byte[] Bytes(string vectorName)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "manifest.json")));
        var entry = doc.RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == vectorName);
        return File.ReadAllBytes(Path.Combine(Root, entry.GetProperty("file").GetString()!));
    }

    public static string SchemaText(string schemaFile) => File.ReadAllText(Path.Combine(Root, schemaFile));
}

internal static class ResponseExtensions
{
    public static string ContentType(this HttpResponseMessage r) => r.Content.Headers.ContentType?.ToString() ?? "";

    public static string[] Vary(this HttpResponseMessage r) =>
        r.Headers.TryGetValues("Vary", out var v) ? v.SelectMany(x => x.Split(',', StringSplitOptions.TrimEntries)).ToArray() : Array.Empty<string>();
}
