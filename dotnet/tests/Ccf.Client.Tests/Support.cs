using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Ccf.Client.Tests;

/// <summary>Records every request that passes through and lets a test rewrite the responses.</summary>
public sealed class RecordingHandler : DelegatingHandler
{
    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    /// <summary>Called with the request and the real response; may return a replacement.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage, Task<HttpResponseMessage>>? Intercept { get; set; }

    /// <summary>Delay applied to schema requests, to make concurrency visible.</summary>
    public TimeSpan SchemaDelay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.Accept.ToString()));
        if (SchemaDelay > TimeSpan.Zero && request.RequestUri!.AbsolutePath.StartsWith("/ccf/schemas/", StringComparison.Ordinal))
        {
            await Task.Delay(SchemaDelay, cancellationToken);
        }

        var response = await base.SendAsync(request, cancellationToken);
        return Intercept is null ? response : await Intercept(request, response);
    }

    public int SchemaRequests => Requests.Count(r => r.Uri.AbsolutePath.StartsWith("/ccf/schemas/", StringComparison.Ordinal));

    public RecordedRequest[] ApiRequests => Requests.Where(r => !r.Uri.AbsolutePath.StartsWith("/ccf/schemas/", StringComparison.Ordinal)).ToArray();
}

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Accept);

/// <summary>The sample API behind a <see cref="RecordingHandler"/>.</summary>
public sealed class SampleApiFixture : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory = new();

    public (HttpClient Client, RecordingHandler Handler) CreateClient()
    {
        var handler = new RecordingHandler();
        return (_factory.CreateDefaultClient(handler), handler);
    }

    public void Dispose() => _factory.Dispose();
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

    public static byte[] Bytes(string vectorName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "manifest.json")));
        var entry = doc.RootElement.GetProperty("vectors").EnumerateArray().Single(v => v.GetProperty("name").GetString() == vectorName);
        return File.ReadAllBytes(Path.Combine(Root, entry.GetProperty("file").GetString()!));
    }

    public static string SchemaText(string file) => File.ReadAllText(Path.Combine(Root, file));
}

/// <summary>A handler that answers from a table, for tests that need exact control over the responses.</summary>
public sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

    public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        _respond = respond;
    }

    public ConcurrentQueue<(HttpRequestMessage Request, byte[]? Body)> Seen { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Seen.Enqueue((request, body));
        return await _respond(request);
    }

    public static HttpResponseMessage Ccf(byte[] payload, string contentType = "application/vnd.ccf") =>
        new(HttpStatusCode.OK) { Content = Bytes(payload, contentType) };

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Text(string text, string contentType = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, new System.Net.Http.Headers.MediaTypeHeaderValue(contentType)) };

    private static ByteArrayContent Bytes(byte[] payload, string contentType)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        return content;
    }
}

/// <summary>A logger that keeps what it is given.</summary>
public sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Entries)
        {
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
