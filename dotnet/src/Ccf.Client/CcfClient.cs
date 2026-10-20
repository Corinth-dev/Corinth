using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;

namespace Ccf.Client;

/// <summary>
/// Requests CCF from an API that supports it and falls back to JSON whenever anything goes wrong
/// (ARCHITECTURE §6.1). Thread-safe; create one per <see cref="HttpClient"/> and reuse it so the schema cache and
/// single-flight schema fetching are shared.
/// </summary>
public sealed class CcfClient
{
    private readonly HttpClient _http;
    private readonly CcfClientOptions _options;
    private readonly CcfSchemaOptions _schemaOptions;
    private readonly ConcurrentDictionary<string, Task<CcfSchema>> _inflight = new(StringComparer.Ordinal);
    private readonly string _accept;

    /// <summary>Creates a client over <paramref name="httpClient"/>, which keeps its handlers, headers and timeout.</summary>
    public CcfClient(HttpClient httpClient, CcfClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _http = httpClient;
        _options = options ?? new CcfClientOptions();
        SchemaCache = _options.SchemaCache ?? new InMemoryCcfSchemaCache();
        _schemaOptions = new CcfSchemaOptions { PropertyNamingPolicy = _options.JsonSerializerOptions.PropertyNamingPolicy };
        _accept = (_options.Features & CcfFeatures.Dictionary) != 0
            ? "application/vnd.ccf; features=d, application/json;q=0.5"
            : "application/vnd.ccf, application/json;q=0.5";
    }

    /// <summary>The schema cache in use.</summary>
    public ICcfSchemaCache SchemaCache { get; }

    /// <summary>
    /// GETs <paramref name="requestUri"/> and deserializes the body into <typeparamref name="T"/>, as CCF when the
    /// server serves it and as JSON otherwise. A failed CCF response (malformed payload, schema unavailable) is
    /// retried once as JSON.
    /// </summary>
    /// <exception cref="HttpRequestException">The server answered with an error status.</exception>
    public Task<T?> GetAsync<T>(string? requestUri, CancellationToken cancellationToken = default) =>
        GetAsync<T>(requestUri is null ? null : new Uri(requestUri, UriKind.RelativeOrAbsolute), cancellationToken);

    /// <inheritdoc cref="GetAsync{T}(string?, CancellationToken)"/>
    public Task<T?> GetAsync<T>(Uri? requestUri, CancellationToken cancellationToken = default) =>
        SendAsync<T>(new HttpRequestMessage(HttpMethod.Get, requestUri), cancellationToken);

    /// <summary>
    /// Sends <paramref name="request"/> with the CCF <c>Accept</c> header. After a CCF failure the request is
    /// repeated once as JSON only when it is safe to repeat: <c>GET</c>, <c>HEAD</c>, or
    /// <see cref="CcfClientOptions.IsIdempotent"/> says so; otherwise the error surfaces.
    /// </summary>
    public async Task<T?> SendAsync<T>(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var retry = CanRetry(request) ? await RetryTemplate.CaptureAsync(request, cancellationToken).ConfigureAwait(false) : null;

        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", _accept);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (!IsCcf(response))
        {
            return await ReadJsonAsync<T>(response, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var origin = AbsoluteUri(request);
            var id = CcfDecoder.PeekSchemaId(payload);
            var schema = await GetSchemaAsync(id, origin, cancellationToken).ConfigureAwait(false);
            return CcfDecoder.Decode<T>(payload, schema, _schemaOptions, _options.Limits);
        }
        catch (Exception e) when (e is CcfFormatException or CcfSchemaFetchException)
        {
            if (retry is null)
            {
                _options.Log?.Invoke($"CCF response from {request.RequestUri} failed ({e.GetType().Name}: {e.Message}); the request is not safe to repeat, so the error surfaces.", e);
                throw;
            }

            _options.Log?.Invoke($"CCF response from {request.RequestUri} failed ({e.GetType().Name}: {e.Message}); repeating the request as JSON.", e);
            using var retryRequest = retry.CreateRequest();
            retryRequest.Headers.Remove("Accept");
            retryRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var retryResponse = await _http.SendAsync(retryRequest, cancellationToken).ConfigureAwait(false);
            retryResponse.EnsureSuccessStatusCode();
            return await ReadJsonAsync<T>(retryResponse, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool CanRetry(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get || request.Method == HttpMethod.Head || _options.IsIdempotent?.Invoke(request) == true;

    private static bool IsCcf(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/vnd.ccf", StringComparison.OrdinalIgnoreCase);

    private async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // 204 No Content (an endpoint that returned null) is a null result, not a JSON error.
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>(_options.JsonSerializerOptions, cancellationToken).ConfigureAwait(false);
    }

    private Uri AbsoluteUri(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            uri = _http.BaseAddress is { } baseAddress
                ? new Uri(baseAddress, uri ?? new Uri(string.Empty, UriKind.Relative))
                : throw new InvalidOperationException("The request URI is relative and the HttpClient has no BaseAddress.");
        }

        return uri;
    }

    // ---- schemas -----------------------------------------------------------------------------------------

    private async Task<CcfSchema> GetSchemaAsync(string id, Uri requestUri, CancellationToken cancellationToken)
    {
        var cached = await SchemaCache.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        // Concurrent requests for the same id share one fetch. The shared fetch is not tied to any single caller's
        // token: each caller just stops waiting when its own token is cancelled.
        var fetch = _inflight.GetOrAdd(id, _ => FetchAndCacheAsync(id, requestUri));
        _ = fetch.ContinueWith(
            static (task, state) =>
            {
                // Forget the entry once the fetch is over (success or failure) so a later request can retry, and observe the exception.
                var (self, key) = ((CcfClient, string))state!;
                self._inflight.TryRemove(new KeyValuePair<string, Task<CcfSchema>>(key, task));
                _ = task.Exception;
            },
            (this, id),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CcfSchema> FetchAndCacheAsync(string id, Uri requestUri)
    {
        await Task.Yield(); // never run the factory body inside GetOrAdd
        {
            // Schemas come from the API's own origin only (SPEC §2.4): build the URL from the request's origin.
            var prefix = _options.SchemaPathPrefix.TrimEnd('/');
            var schemaUri = new Uri(requestUri, prefix + "/" + id);
            using var request = new HttpRequestMessage(HttpMethod.Get, schemaUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                throw new CcfSchemaFetchException(id, $"Fetching schema {id} from {schemaUri} failed: {e.Message}", e);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new CcfSchemaFetchException(id, $"Schema {id} is not available from {schemaUri}: HTTP {(int)response.StatusCode}.");
                }

                var finalUri = response.RequestMessage?.RequestUri;
                if (finalUri is not null && !SameOrigin(finalUri, schemaUri))
                {
                    throw new CcfSchemaFetchException(id, $"Schema {id} was redirected to another origin ({finalUri.GetLeftPart(UriPartial.Authority)}); schemas are only accepted from the API's own origin.");
                }

                var text = await response.Content.ReadAsStringAsync(CancellationToken.None).ConfigureAwait(false);
                var schema = Verify(id, text);
                await SchemaCache.SetAsync(schema, CancellationToken.None).ConfigureAwait(false);
                return schema;
            }
        }
    }

    /// <summary>Parses schema text and checks that its hash is the id the payload named (ARCHITECTURE §6.1).</summary>
    internal static CcfSchema Verify(string id, string text)
    {
        var rawId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, 4)).ToLowerInvariant();
        if (rawId != id)
        {
            throw new CcfSchemaFetchException(id, $"The schema served for {id} hashes to {rawId}.");
        }

        CcfSchema schema;
        try
        {
            schema = CcfSchema.Parse(text);
        }
        catch (CcfSchemaException e)
        {
            throw new CcfSchemaFetchException(id, $"The schema served for {id} is invalid: {e.Message}", e);
        }

        if (schema.IdHex != id)
        {
            throw new CcfSchemaFetchException(id, $"The schema served for {id} is not in canonical form (its canonical form has id {schema.IdHex}).");
        }

        return schema;
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    // ---- retry -------------------------------------------------------------------------------------------

    /// <summary>A copy of a request taken before it is sent, because an HttpRequestMessage can only be sent once.</summary>
    private sealed class RetryTemplate
    {
        private readonly HttpMethod _method;
        private readonly Uri? _uri;
        private readonly Version _version;
        private readonly HttpVersionPolicy _versionPolicy;
        private readonly List<KeyValuePair<string, string[]>> _headers = new();
        private readonly List<KeyValuePair<string, object?>> _options = new();
        private byte[]? _content;
        private readonly List<KeyValuePair<string, string[]>> _contentHeaders = new();

        private RetryTemplate(HttpRequestMessage request)
        {
            _method = request.Method;
            _uri = request.RequestUri;
            _version = request.Version;
            _versionPolicy = request.VersionPolicy;
            foreach (var h in request.Headers)
            {
                _headers.Add(new(h.Key, h.Value.ToArray()));
            }

            foreach (var o in request.Options)
            {
                _options.Add(new(o.Key, o.Value));
            }
        }

        public static async Task<RetryTemplate> CaptureAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var template = new RetryTemplate(request);
            if (request.Content is { } content)
            {
                template._content = await content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                foreach (var h in content.Headers)
                {
                    template._contentHeaders.Add(new(h.Key, h.Value.ToArray()));
                }
            }

            return template;
        }

        public HttpRequestMessage CreateRequest()
        {
            var request = new HttpRequestMessage(_method, _uri) { Version = _version, VersionPolicy = _versionPolicy };
            foreach (var (name, values) in _headers)
            {
                request.Headers.TryAddWithoutValidation(name, values);
            }

            foreach (var (key, value) in _options)
            {
                request.Options.Set(new HttpRequestOptionsKey<object?>(key), value);
            }

            if (_content is not null)
            {
                request.Content = new ByteArrayContent(_content);
                foreach (var (name, values) in _contentHeaders)
                {
                    request.Content.Headers.TryAddWithoutValidation(name, values);
                }
            }

            return request;
        }
    }
}

/// <summary><see cref="HttpClient"/> extensions mirroring <c>GetFromJsonAsync</c>.</summary>
public static class CcfHttpClientExtensions
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HttpClient, CcfClient> Defaults = new();

    /// <summary>
    /// GETs <paramref name="requestUri"/> asking for CCF and deserializes the response into <typeparamref name="T"/>
    /// (JSON when the server does not serve CCF). Uses one default <see cref="CcfClient"/> per
    /// <see cref="HttpClient"/> (so schemas are cached and fetched once); create a <see cref="CcfClient"/> yourself to
    /// configure it.
    /// </summary>
    public static Task<T?> GetFromCcfAsync<T>(this HttpClient httpClient, string? requestUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        return Defaults.GetValue(httpClient, static c => new CcfClient(c)).GetAsync<T>(requestUri, cancellationToken);
    }

    /// <inheritdoc cref="GetFromCcfAsync{T}(HttpClient, string?, CancellationToken)"/>
    public static Task<T?> GetFromCcfAsync<T>(this HttpClient httpClient, Uri? requestUri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        return Defaults.GetValue(httpClient, static c => new CcfClient(c)).GetAsync<T>(requestUri, cancellationToken);
    }
}
