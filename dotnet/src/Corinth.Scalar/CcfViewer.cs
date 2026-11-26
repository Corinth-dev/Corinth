namespace Corinth.Scalar;

/// <summary>Paths of the CCF viewer endpoints.</summary>
public static class CcfViewer
{
    /// <summary>The endpoint that decodes a CCF payload (the request body) to JSON.</summary>
    public const string DecodePath = "/ccf/decode";

    /// <summary>The script that Scalar loads (see <see cref="CcfScalarExtensions.WithCcfViewer"/>).</summary>
    public const string ScriptPath = "/ccf/scalar-viewer.js";
}

/// <summary>Options of <see cref="CcfViewerEndpointExtensions.MapCcfViewer"/>.</summary>
public sealed class CcfViewerOptions
{
    /// <summary>
    /// The viewer is a development aid: <c>POST /ccf/decode</c> decodes any payload for a schema this server knows,
    /// without authentication unless you add it. It is mapped only in the Development environment unless this is
    /// <c>true</c>.
    /// </summary>
    public bool EnableOutsideDevelopment { get; set; }
}
