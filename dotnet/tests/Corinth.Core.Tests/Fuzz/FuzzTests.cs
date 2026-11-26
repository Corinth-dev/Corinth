using Xunit;
using Xunit.Abstractions;

namespace Corinth.Core.Tests.Fuzz;

[Trait("Category", "Fuzz")]
public class FuzzTests
{
    private readonly ITestOutputHelper _output;

    public FuzzTests(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// Runs for <c>CCF_FUZZ_SECONDS</c> seconds (default 4) on <c>CCF_FUZZ_THREADS</c> threads (default: up to 4).
    /// The nightly job sets 3600.
    /// </summary>
    [Fact]
    public void Mutated_payloads_only_ever_raise_CcfFormatException()
    {
        var seconds = double.TryParse(Environment.GetEnvironmentVariable("CCF_FUZZ_SECONDS"), out var s) ? s : 4;
        var threads = int.TryParse(Environment.GetEnvironmentVariable("CCF_FUZZ_THREADS"), out var t) ? t : Math.Min(4, Environment.ProcessorCount);
        var seed = int.TryParse(Environment.GetEnvironmentVariable("CCF_FUZZ_SEED"), out var sd) ? sd : Environment.TickCount;

        var result = FuzzRunner.Run(TimeSpan.FromSeconds(seconds), threads, seed);

        _output.WriteLine($"seed={seed} threads={threads} duration={seconds}s iterations={result.Iterations} decodedOk={result.Succeeded}");
        _output.WriteLine($"max allocation={result.MaxAllocatedBytes:N0} bytes, max allocation/payload={result.MaxAllocRatio:N0}x, slowest decode={result.MaxMillis:F1} ms");
        foreach (var (code, n) in result.ErrorCounts.OrderBy(k => k.Key))
        {
            _output.WriteLine($"  {code,-22} {n:N0}");
        }

        Assert.True(result.Failures.Count == 0, "Fuzz failures:\n" + string.Join("\n", result.Failures.Take(5)));
        Assert.True(result.Iterations > 100, "fuzzer made no progress");
    }
}
