using System.Collections.Concurrent;
using System.Diagnostics;
using Corinth.Core.Tests.Conformance;

namespace Corinth.Core.Tests.Fuzz;

/// <summary>
/// Random-mutation fuzzer for the decoder: mutates the golden payloads and requires that decoding either
/// succeeds or throws <see cref="CcfFormatException"/> — nothing else — within bounded time and allocation.
/// </summary>
internal static class FuzzRunner
{
    internal sealed record Result(
        long Iterations,
        long Succeeded,
        IReadOnlyDictionary<string, long> ErrorCounts,
        long MaxAllocatedBytes,
        double MaxAllocRatio,
        double MaxMillis,
        IReadOnlyList<string> Failures);

    private sealed record Seed(string Name, byte[] Payload, CcfSchema Schema, Type Row);

    private static List<Seed> LoadSeeds()
    {
        var seeds = new List<Seed>();
        foreach (var v in VectorManifest.Vectors)
        {
            var schema = CcfSchema.Parse(v.ReadSchemaText());
            seeds.Add(new Seed(v.Name, v.ReadPayload(), schema, VectorModels.ModelFor(schema.Name)));
        }

        return seeds;
    }

    public static Result Run(TimeSpan duration, int threads, int baseSeed)
    {
        var seeds = LoadSeeds();
        var failures = new ConcurrentQueue<string>();
        var errors = new ConcurrentDictionary<string, long>();
        long iterations = 0, succeeded = 0, maxAlloc = 0;
        var maxRatioBits = BitConverter.DoubleToInt64Bits(0);
        var maxMillisBits = BitConverter.DoubleToInt64Bits(0);
        var deadline = Stopwatch.StartNew();

        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            var rng = new Random(unchecked(baseSeed * 7919 + t));
            while (deadline.Elapsed < duration && failures.Count < 20)
            {
                var seed = seeds[rng.Next(seeds.Count)];
                var payload = Mutate(rng, seed.Payload, seeds);
                var schema = seed.Schema;
                var useTyped = rng.Next(3) != 0;

                var before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                try
                {
                    DecodeBoth(payload, schema, seed.Row, useTyped);
                    Interlocked.Increment(ref succeeded);
                }
                catch (CcfFormatException e)
                {
                    errors.AddOrUpdate(e.Code.ToString(), 1, (_, n) => n + 1);
                }
                catch (Exception e)
                {
                    failures.Enqueue($"{e.GetType().Name}: {e.Message} | seed={seed.Name} typed={useTyped} payload={Convert.ToHexString(payload)}");
                }

                sw.Stop();
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                var ratio = allocated / (double)Math.Max(payload.Length, 1);
                // Bounded by the row limit: allocation must stay proportional to the bytes supplied.
                if (allocated > 2_000_000 + 4000L * payload.Length)
                {
                    failures.Enqueue($"allocation {allocated} bytes for a {payload.Length}-byte payload | seed={seed.Name} payload={Convert.ToHexString(payload)}");
                }

                if (sw.Elapsed > TimeSpan.FromSeconds(2))
                {
                    failures.Enqueue($"decode took {sw.Elapsed.TotalSeconds:F1}s | seed={seed.Name} payload={Convert.ToHexString(payload)}");
                }

                InterlockedMax(ref maxAlloc, allocated);
                InterlockedMaxDouble(ref maxRatioBits, ratio);
                InterlockedMaxDouble(ref maxMillisBits, sw.Elapsed.TotalMilliseconds);
                Interlocked.Increment(ref iterations);
            }
        }) { IsBackground = true }).ToList();

        workers.ForEach(w => w.Start());
        foreach (var w in workers)
        {
            // A hang inside the decoder would never join: fail loudly instead of hanging the run.
            var remaining = duration + TimeSpan.FromSeconds(60) - deadline.Elapsed;
            if (!w.Join(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(1)))
            {
                failures.Enqueue("a fuzz worker did not finish: probable hang in the decoder");
            }
        }

        return new Result(
            iterations,
            succeeded,
            errors.ToDictionary(kv => kv.Key, kv => kv.Value),
            maxAlloc,
            BitConverter.Int64BitsToDouble(maxRatioBits),
            BitConverter.Int64BitsToDouble(maxMillisBits),
            failures.ToList());
    }

    private static void DecodeBoth(byte[] payload, CcfSchema schema, Type row, bool typed)
    {
        if (!typed)
        {
            CcfDecoder.DecodeValue(payload, schema);
            return;
        }

        if (row == typeof(Product))
        {
            CcfDecoder.Decode<List<Product>>(payload, schema);
        }
        else if (row == typeof(AllTypes))
        {
            CcfDecoder.Decode<List<AllTypes>>(payload, schema);
        }
        else
        {
            CcfDecoder.Decode<List<Order>>(payload, schema);
        }
    }

    private static void InterlockedMax(ref long target, long value)
    {
        long current;
        while (value > (current = Interlocked.Read(ref target)) && Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    private static void InterlockedMaxDouble(ref long targetBits, double value)
    {
        long current;
        while (value > BitConverter.Int64BitsToDouble(current = Interlocked.Read(ref targetBits))
               && Interlocked.CompareExchange(ref targetBits, BitConverter.DoubleToInt64Bits(value), current) != current)
        {
        }
    }

    private static readonly byte[] Interesting = { 0, 1, 2, 3, 0x7F, 0x80, 0x81, 0xFE, 0xFF };

    private static readonly byte[][] Varints =
    {
        new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 },
        new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 },
        new byte[] { 0xFF, 0xFF, 0xFF, 0x7F },
        new byte[] { 0x80, 0x80, 0x04 },
        new byte[] { 0xA0, 0x8D, 0x06 }, // 100,000
        new byte[] { 0xA1, 0x8D, 0x06 }, // 100,001
    };

    /// <summary>Applies 1–4 random mutations to a copy of the payload.</summary>
    private static byte[] Mutate(Random rng, byte[] source, IReadOnlyList<Seed> seeds)
    {
        var data = source.ToList();
        for (var m = rng.Next(1, 5); m > 0; m--)
        {
            if (data.Count == 0)
            {
                data.Add((byte)rng.Next(256));
                continue;
            }

            switch (rng.Next(12))
            {
                case 0: // flip a bit
                    data[rng.Next(data.Count)] ^= (byte)(1 << rng.Next(8));
                    break;
                case 1: // random byte
                    data[rng.Next(data.Count)] = (byte)rng.Next(256);
                    break;
                case 2: // interesting byte
                    data[rng.Next(data.Count)] = Interesting[rng.Next(Interesting.Length)];
                    break;
                case 3: // header-biased byte
                    data[rng.Next(Math.Min(data.Count, 24))] = Interesting[rng.Next(Interesting.Length)];
                    break;
                case 4: // truncate
                    data = data.Take(rng.Next(data.Count + 1)).ToList();
                    break;
                case 5: // delete a range
                    var at = rng.Next(data.Count);
                    data.RemoveRange(at, Math.Min(rng.Next(1, 12), data.Count - at));
                    break;
                case 6: // insert random bytes
                    var ins = Enumerable.Range(0, rng.Next(1, 8)).Select(_ => (byte)rng.Next(256));
                    data.InsertRange(rng.Next(data.Count + 1), ins);
                    break;
                case 7: // overwrite with an extreme varint
                    var v = Varints[rng.Next(Varints.Length)];
                    var pos = rng.Next(data.Count);
                    for (var i = 0; i < v.Length && pos + i < data.Count; i++)
                    {
                        data[pos + i] = v[i];
                    }

                    break;
                case 8: // duplicate a chunk
                    var from = rng.Next(data.Count);
                    var len = Math.Min(rng.Next(1, 40), data.Count - from);
                    data.InsertRange(rng.Next(data.Count + 1), data.GetRange(from, len));
                    break;
                case 9: // splice from another vector
                    var other = seeds[rng.Next(seeds.Count)].Payload;
                    var o = rng.Next(other.Length);
                    var l = Math.Min(rng.Next(1, 64), other.Length - o);
                    var dest = rng.Next(data.Count);
                    for (var i = 0; i < l && dest + i < data.Count; i++)
                    {
                        data[dest + i] = other[o + i];
                    }

                    break;
                case 10: // set a byte after the header to a small number (lengths/counts)
                    data[rng.Next(Math.Min(data.Count, 64))] = (byte)rng.Next(0, 4);
                    break;
                default: // append bytes
                    data.AddRange(Enumerable.Range(0, rng.Next(1, 6)).Select(_ => (byte)rng.Next(256)));
                    break;
            }
        }

        return data.ToArray();
    }
}
