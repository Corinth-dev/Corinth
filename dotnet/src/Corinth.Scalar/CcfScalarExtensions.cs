using global::Scalar.AspNetCore;

namespace Corinth.Scalar;

/// <summary>Connects the Scalar API reference to the CCF viewer.</summary>
public static class CcfScalarExtensions
{
    /// <summary>
    /// Makes Scalar load the CCF viewer script: a "CCF decode" toggle (off by default, remembered in the browser,
    /// applies to the next request) decides whether a CCF response is shown as the server sent it or as decoded
    /// JSON. Needs <see cref="CcfViewerEndpointExtensions.MapCcfViewer"/>. Replaces any other
    /// <c>WithJavaScriptConfiguration</c> module.
    /// </summary>
    public static ScalarOptions WithCcfViewer(this ScalarOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.WithJavaScriptConfiguration(CcfViewer.ScriptPath);
    }
}
