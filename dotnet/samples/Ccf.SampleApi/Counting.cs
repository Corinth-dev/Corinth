using System.Collections;

namespace Ccf.SampleApi;

/// <summary>Counts enumerations of <see cref="CountingEnumerable{T}"/> (stands in for a lazy LINQ or EF query).</summary>
public sealed class EnumerationCounter
{
    private int _count;

    /// <summary>Number of times a sequence was enumerated since the last <see cref="Reset"/>.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Records one enumeration.</summary>
    public void Increment() => Interlocked.Increment(ref _count);

    /// <summary>Sets the count back to zero.</summary>
    public void Reset() => Interlocked.Exchange(ref _count, 0);
}

/// <summary>A sequence that is not a list and records every enumeration.</summary>
public sealed class CountingEnumerable<T> : IEnumerable<T>
{
    private readonly IEnumerable<T> _source;
    private readonly EnumerationCounter _counter;

    /// <summary>Wraps <paramref name="source"/>.</summary>
    public CountingEnumerable(IEnumerable<T> source, EnumerationCounter counter)
    {
        _source = source;
        _counter = counter;
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator()
    {
        _counter.Increment();
        return _source.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
