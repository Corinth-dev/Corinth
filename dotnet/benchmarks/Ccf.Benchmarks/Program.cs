using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Running;
using Ccf.Benchmarks;

// dotnet run -c Release --project benchmarks/Ccf.Benchmarks -- export [dir]   write the datasets and a size table
// dotnet run -c Release --project benchmarks/Ccf.Benchmarks -- <BenchmarkDotNet args>   e.g. --filter "*Decode_Products500*"
if (args.Length > 0 && args[0] == "export")
{
    var dir = args.Length > 1 ? args[1] : Path.Combine(Datasets.RepoRoot(), "bench-data");
    Exporter.Run(dir);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(BenchConfig).Assembly).Run(args);

/// <summary>Writes every dataset in every encoding for the JavaScript benchmarks and prints the size table.</summary>
internal static class Exporter
{
    public static void Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var sizes = new List<Sizes>();
        Add(sizes, dir, Datasets.Products500Set());
        Add(sizes, dir, Datasets.Products5kSet());
        Add(sizes, dir, Datasets.UniqueStringsSet());
        Add(sizes, dir, Datasets.OrdersSet());

        // Guard: the sample is the vectors' product list, so the encoder must reproduce v01 and v02 byte for byte.
        var vectors = Path.Combine(Datasets.RepoRoot(), "test-vectors");
        Check(File.ReadAllBytes(Path.Combine(vectors, "v01-products-plain.ccf")), File.ReadAllBytes(Path.Combine(dir, "products-500.plain.ccf")), "v01");
        Check(File.ReadAllBytes(Path.Combine(vectors, "v02-products-dict.ccf")), File.ReadAllBytes(Path.Combine(dir, "products-500.dict.ccf")), "v02");

        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(sizes, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        File.WriteAllText(Path.Combine(dir, "sizes.md"), Table(sizes));
        Console.WriteLine(Table(sizes));
        Console.WriteLine($"Wrote {dir}");
    }

    private static void Check(byte[] expected, byte[] actual, string vector)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
        {
            throw new InvalidOperationException($"The benchmark sample no longer encodes to {vector}.");
        }
    }

    private static void Add<T>(List<Sizes> sizes, string dir, Dataset<T> d)
    {
        File.WriteAllBytes(Path.Combine(dir, d.Name + ".json"), d.Json);
        File.WriteAllBytes(Path.Combine(dir, d.Name + ".json.gz"), d.JsonGzip);
        File.WriteAllBytes(Path.Combine(dir, d.Name + ".json.br"), d.JsonBrotli);
        File.WriteAllBytes(Path.Combine(dir, d.Name + ".plain.ccf"), d.CcfPlain);
        File.WriteAllBytes(Path.Combine(dir, d.Name + ".dict.ccf"), d.CcfDict);
        File.WriteAllText(Path.Combine(dir, d.Name + ".schema.json"), d.Schema.CanonicalJson);
        sizes.Add(new Sizes(
            d.Name, d.Rows.Count, d.Schema.IdHex, d.Json.Length, d.JsonGzip.Length, d.JsonBrotli.Length,
            Compress.Gzip(d.Json, System.IO.Compression.CompressionLevel.Fastest).Length,
            Compress.Brotli(d.Json, System.IO.Compression.CompressionLevel.Fastest).Length,
            Brotli11(d.Json), d.CcfPlain.Length, d.CcfDict.Length,
            Compress.Gzip(d.CcfDict, System.IO.Compression.CompressionLevel.Optimal).Length,
            Compress.Brotli(d.CcfDict, System.IO.Compression.CompressionLevel.Optimal).Length));
    }

    private static int Brotli11(byte[] data)
    {
        var dest = new byte[System.IO.Compression.BrotliEncoder.GetMaxCompressedLength(data.Length)];
        return System.IO.Compression.BrotliEncoder.TryCompress(data, dest, out var n, 11, 22) ? n : -1;
    }

    private static string Table(List<Sizes> sizes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Dataset | Rows | JSON | JSON gzip -6 | JSON gzip fastest | JSON brotli 4 | JSON brotli fastest | JSON brotli 11 | CCF plain | CCF dictionary | CCF dict / gzip -6 | CCF dict / JSON |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var s in sizes)
        {
            sb.AppendLine($"| {s.Name} | {s.Rows:N0} | {s.Json:N0} | {s.Gzip:N0} | {s.GzipFastest:N0} | {s.Brotli:N0} | {s.BrotliFastest:N0} | {s.Brotli11:N0} | {s.CcfPlain:N0} | {s.CcfDict:N0} | {(double)s.CcfDict / s.Gzip:P0} | {(double)s.CcfDict / s.Json:P0} |");
        }

        sb.AppendLine();
        sb.AppendLine("With HTTP compression applied to both (Content-Encoding; the CCF format itself is never compressed):");
        sb.AppendLine();
        sb.AppendLine("| Dataset | JSON gzip -6 | CCF dict gzip -6 | ratio | JSON brotli 4 | CCF dict brotli 4 | ratio |");
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var s in sizes)
        {
            sb.AppendLine($"| {s.Name} | {s.Gzip:N0} | {s.CcfDictGzip:N0} | {(double)s.CcfDictGzip / s.Gzip:P0} | {s.Brotli:N0} | {s.CcfDictBrotli:N0} | {(double)s.CcfDictBrotli / s.Brotli:P0} |");
        }

        return sb.ToString();
    }
}

internal sealed record Sizes(
    string Name, int Rows, string SchemaId, int Json, int Gzip, int Brotli, int GzipFastest, int BrotliFastest, int Brotli11, int CcfPlain, int CcfDict,
    int CcfDictGzip, int CcfDictBrotli);
