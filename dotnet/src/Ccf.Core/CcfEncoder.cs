using System.Buffers;
using System.Collections.Concurrent;

namespace Ccf;

/// <summary>
/// Encodes CLR values to CCF payloads. Thread-safe and stateless apart from per-(schema, type) binding caches
/// (accessors are compiled once). The whole payload is built in a pooled buffer before anything is written to
/// the output, so a failure can still fall back to JSON (SPEC §10.2).
/// </summary>
public sealed class CcfEncoder
{
    private readonly CcfSchemaOptions _options;
    private readonly double _dictionaryThreshold;
    private readonly ConcurrentDictionary<(string, Type), RootBinding> _roots = new();

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">Naming rules; must match those used to build the schema.</param>
    /// <param name="dictionaryThreshold">
    /// Dictionary mode is used when fewer than this fraction of present values are distinct. 0.5 is the
    /// conformance default (SPEC §8.1).
    /// </param>
    public CcfEncoder(CcfSchemaOptions? options = null, double dictionaryThreshold = 0.5)
    {
        _options = options ?? CcfSchemaOptions.Default;
        _dictionaryThreshold = dictionaryThreshold;
    }

    /// <summary>
    /// Encodes <paramref name="value"/> — a collection of objects, or a single object (header flag <c>S</c>) —
    /// with <paramref name="schema"/> and writes the finished payload to <paramref name="output"/>.
    /// </summary>
    /// <returns>The header flags that were produced; derive the Content-Type from them.</returns>
    /// <exception cref="CcfEncodeException">The value cannot be encoded (SPEC §10.2). Nothing was written.</exception>
    public CcfPayloadFlags Encode(
        object value, CcfSchema schema, CcfFeatures features, IBufferWriter<byte> output, CcfLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(output);
        if (value is null)
        {
            throw new CcfEncodeException("Cannot encode null.");
        }

        limits ??= CcfLimits.Default;
        if (schema.Depth > CcfLimits.MaxDepth)
        {
            throw new CcfEncodeException($"Schema nesting depth {schema.Depth} exceeds {CcfLimits.MaxDepth}.");
        }

        // Keyed by the canonical text, not the instance, so parsing a schema per call cannot grow the cache.
        var binding = _roots.GetOrAdd(
            (schema.CanonicalJson, value.GetType()), static (_, state) => RootBinding.Create(state.Type, state.Schema, state.Options), (Type: value.GetType(), Schema: schema, Options: _options));
        var columns = binding.Extract(value, limits, out var rows, out var single);

        using var writer = new CcfByteWriter(limits.MaxPayloadBytes);
        writer.WriteByte(0x43);
        writer.WriteByte(0x43);
        writer.WriteByte(1);
        writer.WriteByte(0); // flags, patched below
        writer.WriteBytes(schema.IdBytes);
        writer.WriteUVarint((ulong)rows);

        var columnWriter = new CcfColumnWriter(writer, features, limits, _dictionaryThreshold);
        columnWriter.WriteTable(columns);

        var flags = CcfPayloadFlags.None;
        if (single)
        {
            flags |= CcfPayloadFlags.SingleObject;
        }

        if (columnWriter.UsedDictionary)
        {
            flags |= CcfPayloadFlags.Dictionary;
        }

        writer.SetByte(3, (byte)flags);
        if (writer.Length > limits.MaxPayloadBytes)
        {
            throw new CcfEncodeException($"Payload of {writer.Length} bytes exceeds the limit of {limits.MaxPayloadBytes}.");
        }

        output.Write(writer.WrittenSpan);
        return flags;
    }

    private abstract class RootBinding
    {
        public abstract ColumnData[] Extract(object value, CcfLimits limits, out int rows, out bool single);

        public static RootBinding Create(Type valueType, CcfSchema schema, CcfSchemaOptions options)
        {
            var collection = CcfBinding.TryGetEnumerableElement(valueType, out var element);
            var rowType = collection ? element : valueType;
            var type = typeof(RootBinding<>).MakeGenericType(rowType);
            return (RootBinding)CcfBinding.CreateInstance(type, schema, options, collection);
        }
    }

    private sealed class RootBinding<TRow> : RootBinding
    {
        private readonly TableExtractor<TRow> _table;
        private readonly bool _collection;

        public RootBinding(CcfSchema schema, CcfSchemaOptions options, bool collection)
        {
            _table = TableExtractor<TRow>.Create(schema.Fields, options);
            _collection = collection;
        }

        public override ColumnData[] Extract(object value, CcfLimits limits, out int rows, out bool single)
        {
            single = !_collection;
            if (!_collection)
            {
                rows = 1;
                return _table.Extract(new[] { (TRow)value }, limits);
            }

            if (value is TRow[] array)
            {
                rows = array.Length;
                CheckNoNulls(array);
                return _table.Extract(array, limits);
            }

            var list = new List<TRow>();
            foreach (var item in (IEnumerable<TRow>)value)
            {
                if (list.Count >= limits.MaxRows)
                {
                    throw new CcfEncodeException($"More than {limits.MaxRows} rows in the root table.");
                }

                list.Add(item);
            }

            CheckNoNulls(list);
            rows = list.Count;
            return _table.Extract(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list), limits);
        }

        private static void CheckNoNulls(IEnumerable<TRow> rows)
        {
            if (typeof(TRow).IsValueType)
            {
                return;
            }

            foreach (var r in rows)
            {
                if (r is null)
                {
                    throw new CcfEncodeException("The root collection contains a null element.");
                }
            }
        }
    }
}
