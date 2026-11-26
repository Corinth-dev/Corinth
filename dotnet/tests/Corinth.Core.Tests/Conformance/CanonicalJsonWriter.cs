using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Corinth.Core.Tests.Conformance;

/// <summary>
/// Converts the untyped value tree returned by <see cref="CcfDecoder.DecodeValue"/> into the canonical JSON
/// representation of test-vectors/README.md.
/// </summary>
public static class CanonicalJsonWriter
{
    public static string Write(object? value)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            WriteValue(w, value);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case uint u: w.WriteNumberValue(u); break;
            case long l: w.WriteStringValue(l.ToString(CultureInfo.InvariantCulture)); break;
            case ulong ul: w.WriteStringValue(ul.ToString(CultureInfo.InvariantCulture)); break;
            case double d: w.WriteNumberValue(d); break;
            case decimal m: w.WriteStringValue(m.ToString(CultureInfo.InvariantCulture)); break;
            case string s: w.WriteStringValue(s); break;
            case DateTime dt:
                Assert.Equal(DateTimeKind.Utc, dt.Kind);
                w.WriteStringValue(dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                break;
            case Guid g: w.WriteStringValue(g.ToString("D")); break;
            case CcfObject o:
                w.WriteStartObject();
                for (var i = 0; i < o.Names.Count; i++)
                {
                    w.WritePropertyName(o.Names[i]);
                    WriteValue(w, o.Values[i]);
                }

                w.WriteEndObject();
                break;
            case object?[] items:
                w.WriteStartArray();
                foreach (var item in items)
                {
                    WriteValue(w, item);
                }

                w.WriteEndArray();
                break;
            default:
                throw new InvalidOperationException("Unexpected value " + value.GetType());
        }
    }
}
