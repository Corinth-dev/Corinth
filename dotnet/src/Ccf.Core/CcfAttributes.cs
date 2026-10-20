using System.Text.Json;

namespace Ccf;

/// <summary>Marks an integer, decimal or datetime property whose column is stored as deltas (SPEC §6.4).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CcfDeltaAttribute : Attribute
{
}

/// <summary>Sets the scale (and optionally unsignedness) of a <see cref="decimal"/> property.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CcfDecimalAttribute : Attribute
{
    /// <summary>Creates the attribute with <paramref name="scale"/> digits after the decimal point (0–18).</summary>
    public CcfDecimalAttribute(int scale)
    {
        Scale = scale;
    }

    /// <summary>Digits after the decimal point.</summary>
    public int Scale { get; }

    /// <summary>Values are never negative; they are stored without zigzag.</summary>
    public bool Unsigned { get; init; }
}

/// <summary>Options shared by schema building and the typed encoder and decoder.</summary>
public sealed class CcfSchemaOptions
{
    /// <summary>Options with System.Text.Json's web defaults for naming (camelCase).</summary>
    public static CcfSchemaOptions Default { get; } = new();

    /// <summary>Converts CLR property names to field names. Defaults to camelCase.</summary>
    public JsonNamingPolicy? PropertyNamingPolicy { get; init; } = JsonNamingPolicy.CamelCase;

    /// <summary>Scale for <see cref="decimal"/> properties without <see cref="CcfDecimalAttribute"/>.</summary>
    public int DefaultDecimalScale { get; init; } = 2;
}
