namespace Ccf;

/// <summary>Decoder error codes, exactly the codes of SPEC §11.</summary>
public enum CcfErrorCode
{
    /// <summary>Bytes 0–1 are not <c>43 43</c>.</summary>
    BadMagic,

    /// <summary>The format version byte is not 1.</summary>
    UnsupportedVersion,

    /// <summary>A reserved flag bit (2–7) is set.</summary>
    ReservedFlags,

    /// <summary>The header schema id differs from the schema supplied.</summary>
    SchemaMismatch,

    /// <summary>Flag <c>S</c> is set and the row count is not 1.</summary>
    SingleObjectRowCount,

    /// <summary>A read runs past the end of the payload.</summary>
    Truncated,

    /// <summary>A read or length runs past the end of its enclosing block.</summary>
    LengthOverrun,

    /// <summary>A block body ended with bytes left unconsumed.</summary>
    BlockLengthMismatch,

    /// <summary>Bytes remain after the root table.</summary>
    TrailingData,

    /// <summary>A uvarint is longer than 10 bytes or above 2⁶⁴−1.</summary>
    VarintOverflow,

    /// <summary>The mode byte is not <c>00</c>/<c>01</c>, or <c>01</c> on a non-string field.</summary>
    InvalidMode,

    /// <summary>An enum or dictionary index is out of range.</summary>
    IndexOutOfRange,

    /// <summary>A value is outside the range of its type.</summary>
    ValueOutOfRange,

    /// <summary>A list's total element count differs from the sum of its counts.</summary>
    CountMismatch,

    /// <summary>String bytes are not valid UTF-8.</summary>
    InvalidUtf8,

    /// <summary>A limit of SPEC §9 is exceeded.</summary>
    LimitExceeded,
}

/// <summary>
/// The only exception a decoder lets escape for malformed input. Carries one of the codes of SPEC §11.
/// </summary>
public sealed class CcfFormatException : Exception
{
    /// <summary>Creates the exception for <paramref name="code"/>.</summary>
    public CcfFormatException(CcfErrorCode code, string? message = null)
        : base(message is null ? code.ToString() : $"{code}: {message}")
    {
        Code = code;
    }

    /// <summary>The SPEC §11 error code.</summary>
    public CcfErrorCode Code { get; }
}

/// <summary>An encoder failure (SPEC §10.2). Servers react by falling back to JSON.</summary>
public sealed class CcfEncodeException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CcfEncodeException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>An invalid schema (SPEC §14.3). Separate from <see cref="CcfFormatException"/>.</summary>
public sealed class CcfSchemaException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CcfSchemaException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
