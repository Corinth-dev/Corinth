using Microsoft.AspNetCore.Http;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Corinth.AspNetCore;

/// <summary>The <c>Ccf</c> meter (ARCHITECTURE §3.5).</summary>
public static class CcfDiagnostics
{
    /// <summary>The meter name to subscribe to.</summary>
    public const string MeterName = "Ccf";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> Responses = Meter.CreateCounter<long>(
        "ccf.responses", description: "Responses from CCF-capable endpoints, tagged format=ccf|json|json-fallback and features.");

    private static readonly Counter<long> BytesWritten = Meter.CreateCounter<long>(
        "ccf.bytes_written", unit: "By", description: "CCF payload bytes written.");

    private static readonly Counter<long> EncodeFailures = Meter.CreateCounter<long>(
        "ccf.encode_failures", description: "Encodes that failed and fell back to JSON.");

    private const string CountedKey = "Corinth.ResponseCounted";

    internal static void RecordServed(HttpContext context, CcfPayloadFlags flags, long bytes)
    {
        context.Items[CountedKey] = true;
        var features = (flags & CcfPayloadFlags.Dictionary) != 0 ? "d" : string.Empty;
        Responses.Add(1, new TagList { { "format", "ccf" }, { "features", features } });
        BytesWritten.Add(bytes);
    }

    internal static void RecordEncodeFailure(HttpContext context)
    {
        context.Items[CountedKey] = true;
        EncodeFailures.Add(1);
        Responses.Add(1, new TagList { { "format", "json-fallback" }, { "features", string.Empty } });
    }

    /// <summary>
    /// Counts a JSON response of a CCF-capable endpoint (the type has a schema): the client did not ask for CCF,
    /// or asked for something else ranked higher. Responses already counted as <c>ccf</c> or <c>json-fallback</c>
    /// are not counted again.
    /// </summary>
    internal static void RecordJson(HttpContext context)
    {
        if (context.Items.ContainsKey(CountedKey))
        {
            return;
        }

        context.Items[CountedKey] = true;
        Responses.Add(1, new TagList { { "format", "json" }, { "features", string.Empty } });
    }
}
