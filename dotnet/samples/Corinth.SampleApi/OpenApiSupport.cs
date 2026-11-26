using Corinth.Scalar;
using Microsoft.AspNetCore.OpenApi;

namespace Corinth.SampleApi;

/// <summary>
/// The sample's own OpenAPI settings: title and a hint for trying it. The CCF parts (opt-in note, the
/// <c>application/vnd.ccf</c> responses) come from <c>AddCcf()</c> of <c>Corinth.Scalar</c>.
/// </summary>
internal static class OpenApiSupport
{
    public static void Configure(OpenApiOptions options)
    {
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "CCF sample API";
            document.Info.Version = "v1";
            document.Info.Description =
                "Try it with `curl -H 'Accept: application/vnd.ccf; features=d' http://localhost:5000/products --output out.ccf`, " +
                "or send a request from this page with `accept: application/vnd.ccf` and switch the **CCF decode** toggle (bottom right) on to read the response as JSON.";
            return Task.CompletedTask;
        });

        options.AddCcf();
    }
}
