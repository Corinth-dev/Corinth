using System.Text.Json;

namespace Corinth.Core.Tests.Conformance;

/// <summary>One entry of <c>test-vectors/manifest.json</c>.</summary>
public sealed record VectorEntry(
    string Name,
    string Kind,
    string File,
    string Schema,
    string SchemaId,
    string? RequestedFeatures,
    int? ExpectedFlags,
    string? ExpectedContentType,
    string? Expected,
    string? ExpectedError)
{
    public bool IsValid => Kind == "valid";

    public byte[] ReadPayload() => System.IO.File.ReadAllBytes(VectorManifest.PathOf(File));

    /// <summary>The canonical schema text, exactly as stored (no trailing newline).</summary>
    public string ReadSchemaText() => System.IO.File.ReadAllText(VectorManifest.PathOf(Schema));

    public JsonDocument ReadExpected() =>
        JsonDocument.Parse(System.IO.File.ReadAllText(VectorManifest.PathOf(Expected
            ?? throw new InvalidOperationException($"{Name} has no expected file"))));
}

/// <summary>
/// Loads the golden vectors from <c>test-vectors/manifest.json</c>. Tests are driven from here and never
/// from a hard-coded list of file names.
/// </summary>
public static class VectorManifest
{
    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly Lazy<IReadOnlyList<VectorEntry>> All = new(Load);

    public static IReadOnlyList<VectorEntry> Vectors => All.Value;

    public static VectorEntry Get(string name) =>
        Vectors.Single(v => v.Name == name);

    public static string PathOf(string file) => Path.Combine(Root.Value, file);

    /// <summary>Theory data: one case per vector of the given kind (<c>null</c> = all), keyed by vector name.</summary>
    public static IEnumerable<object[]> Names(string? kind = null) =>
        Vectors.Where(v => kind is null || v.Kind == kind).Select(v => new object[] { v.Name });

    public static IEnumerable<object[]> AllNames() => Names();
    public static IEnumerable<object[]> ValidNames() => Names("valid");
    public static IEnumerable<object[]> InvalidNames() => Names("invalid");

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "test-vectors");
            if (File.Exists(Path.Combine(candidate, "manifest.json")))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("test-vectors/manifest.json not found above " + AppContext.BaseDirectory);
    }

    private static IReadOnlyList<VectorEntry> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(PathOf("manifest.json")));
        var list = new List<VectorEntry>();
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            list.Add(new VectorEntry(
                v.GetProperty("name").GetString()!,
                v.GetProperty("kind").GetString()!,
                v.GetProperty("file").GetString()!,
                v.GetProperty("schema").GetString()!,
                v.GetProperty("schemaId").GetString()!,
                Str(v, "requestedFeatures"),
                v.TryGetProperty("expectedFlags", out var f) ? f.GetInt32() : null,
                Str(v, "expectedContentType"),
                Str(v, "expected"),
                Str(v, "expectedError")));
        }

        return list;

        static string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) ? p.GetString() : null;
    }
}
