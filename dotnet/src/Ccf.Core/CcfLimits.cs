namespace Ccf;

/// <summary>Configurable limits of SPEC §9, enforced by both the encoder and the decoder.</summary>
public sealed class CcfLimits
{
    /// <summary>Largest dictionary (SPEC §9, fixed).</summary>
    public const int MaxDictionaryEntries = 65_536;

    /// <summary>Deepest table nesting (SPEC §7.4, fixed).</summary>
    public const int MaxDepth = 8;

    /// <summary>The defaults of SPEC §9.</summary>
    public static CcfLimits Default { get; } = new();

    /// <summary>Largest payload in bytes. Default 32 MiB.</summary>
    public int MaxPayloadBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Most rows in any one table, root or child. Default 100,000.</summary>
    public int MaxRows { get; init; } = 100_000;

    /// <summary>Longest single string or dictionary entry in bytes. Default 1 MiB.</summary>
    public int MaxStringBytes { get; init; } = 1024 * 1024;
}

/// <summary>Decoding features a client can read, and features an encoder may use (SPEC §2.1).</summary>
[Flags]
public enum CcfFeatures
{
    /// <summary>No optional features.</summary>
    None = 0,

    /// <summary>Feature <c>d</c>: dictionary mode for string columns.</summary>
    Dictionary = 1,
}

/// <summary>Header flags (SPEC §4.1).</summary>
[Flags]
public enum CcfPayloadFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>Bit 0 <c>D</c>: at least one dictionary column.</summary>
    Dictionary = 1,

    /// <summary>Bit 1 <c>S</c>: the root is a single object.</summary>
    SingleObject = 2,
}

/// <summary>Parses the <c>features</c> media type parameter (SPEC §2.1).</summary>
public static class CcfFeatureParser
{
    /// <summary>
    /// Parses the value of the <c>features</c> parameter, quoted or bare (<c>d</c>, <c>"d,n"</c>).
    /// Unknown letters are ignored.
    /// </summary>
    public static CcfFeatures Parse(string? value)
    {
        var result = CcfFeatures.None;
        if (value is null)
        {
            return result;
        }

        foreach (var c in value)
        {
            if (c == 'd')
            {
                result |= CcfFeatures.Dictionary;
            }
        }

        return result;
    }

    /// <summary>The <c>Content-Type</c> for a payload with the given header flags (SPEC §2.3).</summary>
    public static string ContentTypeFor(CcfPayloadFlags flags) =>
        (flags & CcfPayloadFlags.Dictionary) != 0 ? "application/vnd.ccf; features=d" : "application/vnd.ccf";
}
