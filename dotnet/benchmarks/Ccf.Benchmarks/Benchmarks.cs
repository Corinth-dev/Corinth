using System.Buffers;
using System.IO.Compression;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;

namespace Ccf.Benchmarks;

/// <summary>One job for every benchmark: enough iterations for stable medians without hours of wall time.</summary>
public sealed class BenchConfig : ManualConfig
{
    public BenchConfig()
    {
        AddJob(Job.Default.WithWarmupCount(3).WithIterationCount(15).WithLaunchCount(1));
    }
}

/// <summary>Decode from bytes into <c>List&lt;T&gt;</c>. Everything starts from the wire bytes and produces the same CLR objects.</summary>
[MemoryDiagnoser]
[Config(typeof(BenchConfig))]
public abstract class DecodeBenchmarks<T>
{
    private Dataset<T> _d = null!;

    protected abstract Dataset<T> Create();

    [GlobalSetup]
    public void Setup() => _d = Create();

    [Benchmark] public List<T> Ccf_Plain() => CcfDecoder.Decode<List<T>>(_d.CcfPlain, _d.Schema);

    [Benchmark] public List<T> Ccf_Dictionary() => CcfDecoder.Decode<List<T>>(_d.CcfDict, _d.Schema);

    [Benchmark(Baseline = true)] public List<T> Json_SourceGen() => JsonSerializer.Deserialize(_d.Json, _d.SourceGen)!;

    [Benchmark] public List<T> Json_Reflection() => JsonSerializer.Deserialize<List<T>>(_d.Json, BenchJson.Options)!;

    [Benchmark]
    public List<T> Json_Gzip_SourceGen()
    {
        using var z = new GZipStream(new MemoryStream(_d.JsonGzip), CompressionMode.Decompress);
        return JsonSerializer.Deserialize(z, _d.SourceGen)!;
    }

    [Benchmark]
    public List<T> Json_Gzip_Reflection()
    {
        using var z = new GZipStream(new MemoryStream(_d.JsonGzip), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<List<T>>(z, BenchJson.Options)!;
    }

    [Benchmark]
    public List<T> Json_Brotli_SourceGen()
    {
        using var z = new BrotliStream(new MemoryStream(_d.JsonBrotli), CompressionMode.Decompress);
        return JsonSerializer.Deserialize(z, _d.SourceGen)!;
    }
}

/// <summary>Encode <c>List&lt;T&gt;</c> into a reused buffer.</summary>
[MemoryDiagnoser]
[Config(typeof(BenchConfig))]
public abstract class EncodeBenchmarks<T>
{
    private Dataset<T> _d = null!;
    private readonly CcfEncoder _encoder = new();
    private readonly ArrayBufferWriter<byte> _buffer = new(1 << 20);
    private Utf8JsonWriter _writer = null!;
    private byte[] _json = null!;

    protected abstract Dataset<T> Create();

    [GlobalSetup]
    public void Setup()
    {
        _d = Create();
        _writer = new Utf8JsonWriter(_buffer);
        _json = _d.Json;
    }

    [Benchmark] public int Ccf_Plain()
    {
        _buffer.ResetWrittenCount();
        _encoder.Encode(_d.Rows, _d.Schema, CcfFeatures.None, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark] public int Ccf_Dictionary()
    {
        _buffer.ResetWrittenCount();
        _encoder.Encode(_d.Rows, _d.Schema, CcfFeatures.Dictionary, _buffer);
        return _buffer.WrittenCount;
    }

    [Benchmark(Baseline = true)] public int Json_SourceGen()
    {
        _buffer.ResetWrittenCount();
        _writer.Reset(_buffer);
        JsonSerializer.Serialize(_writer, _d.Rows, _d.SourceGen);
        return _buffer.WrittenCount;
    }

    [Benchmark] public int Json_Reflection()
    {
        _buffer.ResetWrittenCount();
        _writer.Reset(_buffer);
        JsonSerializer.Serialize(_writer, _d.Rows, BenchJson.Options);
        return _buffer.WrittenCount;
    }

    /// <summary>JSON then gzip at the ASP.NET Core default level (Fastest), as a compressing server would do.</summary>
    [Benchmark]
    public int Json_SourceGen_Gzip()
    {
        using var ms = new MemoryStream(_json.Length / 4);
        using (var z = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            JsonSerializer.Serialize(z, _d.Rows, _d.SourceGen);
        }

        return (int)ms.Length;
    }

    [Benchmark]
    public int Json_SourceGen_Brotli()
    {
        using var ms = new MemoryStream(_json.Length / 4);
        using (var z = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            JsonSerializer.Serialize(z, _d.Rows, _d.SourceGen);
        }

        return (int)ms.Length;
    }
}

public class Decode_Products500 : DecodeBenchmarks<Product> { protected override Dataset<Product> Create() => Datasets.Products500Set(); }
public class Decode_Products5k : DecodeBenchmarks<Product> { protected override Dataset<Product> Create() => Datasets.Products5kSet(); }
public class Decode_UniqueStrings5k : DecodeBenchmarks<Contact> { protected override Dataset<Contact> Create() => Datasets.UniqueStringsSet(); }
public class Decode_Orders5k : DecodeBenchmarks<Order> { protected override Dataset<Order> Create() => Datasets.OrdersSet(); }

public class Encode_Products500 : EncodeBenchmarks<Product> { protected override Dataset<Product> Create() => Datasets.Products500Set(); }
public class Encode_Products5k : EncodeBenchmarks<Product> { protected override Dataset<Product> Create() => Datasets.Products5kSet(); }
public class Encode_UniqueStrings5k : EncodeBenchmarks<Contact> { protected override Dataset<Contact> Create() => Datasets.UniqueStringsSet(); }
public class Encode_Orders5k : EncodeBenchmarks<Order> { protected override Dataset<Order> Create() => Datasets.OrdersSet(); }
