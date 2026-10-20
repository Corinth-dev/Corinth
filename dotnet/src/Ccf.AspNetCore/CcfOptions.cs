using Microsoft.Extensions.Options;

namespace Ccf.AspNetCore;

/// <summary>
/// Options of <c>AddCcf</c>. Bindable from the configuration section <c>Ccf</c>
/// (<c>"Ccf": { "DictionaryThreshold": 0.5, "Limits": { "MaxRows": 100000 } }</c>).
/// </summary>
public sealed class CcfOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Ccf";

    /// <summary>
    /// A string column uses dictionary mode when fewer than this fraction of its present values are distinct
    /// (and the client allowed feature <c>d</c>). 0.5 is the conformance default (SPEC §8.1).
    /// </summary>
    public double DictionaryThreshold { get; set; } = 0.5;

    /// <summary>Scale for <see cref="decimal"/> properties without <c>[CcfDecimal]</c>. Default 2.</summary>
    public int DefaultDecimalScale { get; set; } = 2;

    /// <summary>The limits of SPEC §9 enforced when encoding.</summary>
    public CcfLimits Limits { get; set; } = new();

    /// <summary>Route prefix of the schema endpoint (<c>GET {prefix}/{schemaId}</c>). Default <c>/ccf/schemas</c>.</summary>
    public string SchemaPathPrefix { get; set; } = "/ccf/schemas";

    /// <summary>
    /// Allows <c>?ccf=d</c> on any URL to request CCF without an <c>Accept</c> header (handy in a browser).
    /// Only honoured when the environment is Development.
    /// </summary>
    public bool AllowQueryOverride { get; set; }
}

internal sealed class CcfOptionsValidator : IValidateOptions<CcfOptions>
{
    public ValidateOptionsResult Validate(string? name, CcfOptions options)
    {
        var failures = new List<string>();
        if (!(options.DictionaryThreshold > 0 && options.DictionaryThreshold <= 1))
        {
            failures.Add("Ccf:DictionaryThreshold must be greater than 0 and at most 1.");
        }

        if (options.DefaultDecimalScale is < 0 or > 18)
        {
            failures.Add("Ccf:DefaultDecimalScale must be between 0 and 18.");
        }

        if (options.Limits is null)
        {
            failures.Add("Ccf:Limits must not be null.");
        }
        else if (options.Limits.MaxRows < 1 || options.Limits.MaxPayloadBytes < 1 || options.Limits.MaxStringBytes < 1)
        {
            failures.Add("Ccf:Limits values must be positive.");
        }

        if (string.IsNullOrEmpty(options.SchemaPathPrefix) || options.SchemaPathPrefix[0] != '/')
        {
            failures.Add("Ccf:SchemaPathPrefix must start with '/'.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
