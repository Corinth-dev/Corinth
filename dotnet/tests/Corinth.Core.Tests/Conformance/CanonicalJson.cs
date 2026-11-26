using System.Text.Json;

namespace Corinth.Core.Tests.Conformance;

/// <summary>
/// Deep comparison of two values in the canonical JSON representation (test-vectors/README.md, SPEC §14.7).
/// Objects compare irrespective of key order, arrays in order, strings exactly, numbers by parsed double
/// equality with −0.0 distinct from 0.0.
/// </summary>
public static class CanonicalJson
{
    /// <summary>Returns <c>null</c> when equal, otherwise a description of the first difference with its path.</summary>
    public static string? Diff(JsonElement expected, JsonElement actual) => Diff("$", expected, actual);

    public static bool Equal(JsonElement expected, JsonElement actual) => Diff(expected, actual) is null;

    public static void AssertEqual(JsonElement expected, JsonElement actual)
    {
        var diff = Diff(expected, actual);
        if (diff is not null)
        {
            throw new Xunit.Sdk.XunitException("Decoded value differs from expected: " + diff);
        }
    }

    private static string? Diff(string path, JsonElement e, JsonElement a)
    {
        if (e.ValueKind != a.ValueKind && !(IsBool(e) && IsBool(a)))
        {
            return $"{path}: expected {e.ValueKind}, got {a.ValueKind}";
        }

        switch (e.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return e.ValueKind == a.ValueKind ? null : $"{path}: expected {e.ValueKind}, got {a.ValueKind}";

            case JsonValueKind.String:
                return string.Equals(e.GetString(), a.GetString(), StringComparison.Ordinal)
                    ? null
                    : $"{path}: expected \"{e.GetString()}\", got \"{a.GetString()}\"";

            case JsonValueKind.Number:
                var x = e.GetDouble();
                var y = a.GetDouble();
                return BitConverter.DoubleToInt64Bits(x) == BitConverter.DoubleToInt64Bits(y)
                    ? null
                    : $"{path}: expected {x:R}, got {y:R}";

            case JsonValueKind.Array:
                var ea = e.GetArrayLength();
                var aa = a.GetArrayLength();
                if (ea != aa)
                {
                    return $"{path}: expected {ea} elements, got {aa}";
                }

                var i = 0;
                using (var ei = e.EnumerateArray().GetEnumerator())
                using (var ai = a.EnumerateArray().GetEnumerator())
                {
                    while (ei.MoveNext() && ai.MoveNext())
                    {
                        var d = Diff($"{path}[{i++}]", ei.Current, ai.Current);
                        if (d is not null)
                        {
                            return d;
                        }
                    }
                }

                return null;

            case JsonValueKind.Object:
                var expectedProps = e.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                var actualProps = a.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                foreach (var (name, ev) in expectedProps)
                {
                    if (!actualProps.TryGetValue(name, out var av))
                    {
                        return $"{path}: missing property \"{name}\"";
                    }

                    var d = Diff($"{path}.{name}", ev, av);
                    if (d is not null)
                    {
                        return d;
                    }
                }

                var extra = actualProps.Keys.FirstOrDefault(k => !expectedProps.ContainsKey(k));
                return extra is null ? null : $"{path}: unexpected property \"{extra}\"";

            default:
                return $"{path}: unsupported JSON kind {e.ValueKind}";
        }
    }

    private static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;
}
