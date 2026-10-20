using System.Globalization;

namespace Ccf;

/// <summary>Exact conversions between CLR values and their stored integer forms.</summary>
internal static class CcfConvert
{
    private const long MsPerSecondTicks = TimeSpan.TicksPerMillisecond;

    /// <summary>Smallest and largest epoch-millisecond value representable as a <see cref="DateTime"/>.</summary>
    public const long MinDateTimeMs = -62_135_596_800_000;

    public const long MaxDateTimeMs = 253_402_300_799_999;

    private static readonly decimal[] Pow10 = BuildPow10();

    private static decimal[] BuildPow10()
    {
        var p = new decimal[19];
        p[0] = 1m;
        for (var i = 1; i < p.Length; i++)
        {
            p[i] = p[i - 1] * 10m;
        }

        return p;
    }

    /// <summary>Builds <c>stored × 10^−scale</c> exactly, keeping <paramref name="scale"/> decimal digits.</summary>
    public static decimal FromScaled(long stored, int scale)
    {
        var negative = stored < 0;
        var magnitude = negative ? unchecked((ulong)(-(stored + 1))) + 1 : (ulong)stored;
        return new decimal((int)(magnitude & 0xFFFFFFFF), (int)(magnitude >> 32), 0, negative && magnitude != 0, (byte)scale);
    }

    /// <summary>Scales to <c>value × 10^scale</c> if that is an exact int64 (SPEC §6.3).</summary>
    public static bool TryToScaled(decimal value, int scale, out long stored)
    {
        stored = 0;
        decimal scaled;
        try
        {
            scaled = decimal.Multiply(value, Pow10[scale]);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (decimal.Truncate(scaled) != scaled || scaled < long.MinValue || scaled > long.MaxValue)
        {
            return false;
        }

        stored = (long)scaled;
        return true;
    }

    public static DateTime FromEpochMs(long ms) =>
        new(DateTime.UnixEpoch.Ticks + ms * MsPerSecondTicks, DateTimeKind.Utc);

    /// <summary>Converts to UTC epoch milliseconds, truncating (flooring) sub-millisecond ticks.</summary>
    public static long ToEpochMs(DateTime value)
    {
        // Unspecified is treated as UTC (ARCHITECTURE §5.1).
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return FloorDiv(utc.Ticks - DateTime.UnixEpoch.Ticks, MsPerSecondTicks);
    }

    public static long ToEpochMs(DateTimeOffset value) =>
        FloorDiv(value.UtcTicks - DateTime.UnixEpoch.Ticks, MsPerSecondTicks);

    private static long FloorDiv(long a, long b)
    {
        var q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    public static string FormatInvariant(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
