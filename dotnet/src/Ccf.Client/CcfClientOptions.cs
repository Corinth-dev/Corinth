using System.Text.Json;

namespace Ccf.Client;

/// <summary>Options of <see cref="CcfClient"/>.</summary>
public sealed class CcfClientOptions
{
    /// <summary>
    /// Decoding features this client can read, sent as <c>features=…</c> in <c>Accept</c>. Defaults to dictionary
    /// mode (<c>d</c>).
    /// </summary>
    public CcfFeatures Features { get; set; } = CcfFeatures.Dictionary;

    /// <summary>Route prefix of the server's schema endpoint. Default <c>/ccf/schemas</c> (SPEC §2.4).</summary>
    public string SchemaPathPrefix { get; set; } = "/ccf/schemas";

    /// <summary>Where verified schemas are kept. Defaults to a new <see cref="InMemoryCcfSchemaCache"/> per client.</summary>
    public ICcfSchemaCache? SchemaCache { get; set; }

    /// <summary>
    /// JSON options for the JSON path (fallback and retry). They also decide how schema field names map to
    /// properties: the same naming policy as the server's JSON output. Defaults to <see cref="JsonSerializerDefaults.Web"/>,
    /// like <c>GetFromJsonAsync</c>.
    /// </summary>
    public JsonSerializerOptions JsonSerializerOptions { get; set; } = new(JsonSerializerDefaults.Web);

    /// <summary>Limits applied when decoding (SPEC §9).</summary>
    public CcfLimits Limits { get; set; } = new();

    /// <summary>
    /// Decides whether a request may be repeated as JSON after a CCF failure. By default only <c>GET</c> and
    /// <c>HEAD</c> are repeated; return <c>true</c> for requests you know are idempotent.
    /// </summary>
    public Func<HttpRequestMessage, bool>? IsIdempotent { get; set; }

    /// <summary>Receives a message when CCF failed and the request is repeated as JSON (or the failure surfaces).</summary>
    public Action<string, Exception?>? Log { get; set; }
}
