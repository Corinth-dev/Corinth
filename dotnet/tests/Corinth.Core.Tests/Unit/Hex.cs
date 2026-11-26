namespace Corinth.Core.Tests;

internal static class Hex
{
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
