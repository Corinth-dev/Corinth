using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Ccf;

internal delegate TProp ValueReader<out TProp>(ColumnData column, int row);

internal delegate void RefSetter<TRow, in TProp>(ref TRow row, TProp value);

/// <summary>Checked conversions from stored values to CLR types; <c>typeof(U)</c> tests fold away per instantiation.</summary>
internal static class Conv
{
    public static U FromLong<U>(long v)
    {
        if (typeof(U) == typeof(long)) return (U)(object)v;
        if (typeof(U) == typeof(int)) return (U)(object)checked((int)v);
        if (typeof(U) == typeof(uint)) return (U)(object)checked((uint)v);
        if (typeof(U) == typeof(short)) return (U)(object)checked((short)v);
        if (typeof(U) == typeof(ushort)) return (U)(object)checked((ushort)v);
        if (typeof(U) == typeof(sbyte)) return (U)(object)checked((sbyte)v);
        if (typeof(U) == typeof(byte)) return (U)(object)checked((byte)v);
        if (typeof(U) == typeof(ulong)) return (U)(object)checked((ulong)v);
        if (typeof(U).IsEnum) return (U)Enum.ToObject(typeof(U), Convert.ChangeType(v, Enum.GetUnderlyingType(typeof(U)), System.Globalization.CultureInfo.InvariantCulture));
        throw new InvalidOperationException("Unsupported integer target " + typeof(U));
    }

    public static U FromULong<U>(ulong v)
    {
        if (typeof(U) == typeof(ulong)) return (U)(object)v;
        if (typeof(U) == typeof(long)) return (U)(object)checked((long)v);
        if (typeof(U) == typeof(int)) return (U)(object)checked((int)v);
        if (typeof(U) == typeof(uint)) return (U)(object)checked((uint)v);
        if (typeof(U) == typeof(short)) return (U)(object)checked((short)v);
        if (typeof(U) == typeof(ushort)) return (U)(object)checked((ushort)v);
        if (typeof(U) == typeof(sbyte)) return (U)(object)checked((sbyte)v);
        if (typeof(U) == typeof(byte)) return (U)(object)checked((byte)v);
        if (typeof(U).IsEnum) return (U)Enum.ToObject(typeof(U), Convert.ChangeType(v, Enum.GetUnderlyingType(typeof(U)), System.Globalization.CultureInfo.InvariantCulture));
        throw new InvalidOperationException("Unsupported integer target " + typeof(U));
    }

    public static U FromDouble<U>(double v)
    {
        if (typeof(U) == typeof(double)) return (U)(object)v;
        if (typeof(U) == typeof(float)) return (U)(object)(float)v;
        throw new InvalidOperationException("Unsupported float target " + typeof(U));
    }

    public static U FromDateTime<U>(DateTime v)
    {
        if (typeof(U) == typeof(DateTime)) return (U)(object)v;
        if (typeof(U) == typeof(DateTimeOffset)) return (U)(object)new DateTimeOffset(v);
        throw new InvalidOperationException("Unsupported datetime target " + typeof(U));
    }
}

/// <summary>Builds readers that turn decoded columns into CLR values of a given type.</summary>
internal static class ReaderFactory
{
    private static readonly MethodInfo FullMethod = typeof(ReaderFactory).GetMethod(nameof(Full), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo NullableMethod = typeof(ReaderFactory).GetMethod(nameof(NullableStruct), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ObjectMethod = typeof(ReaderFactory).GetMethod(nameof(ObjectReader), BindingFlags.Static | BindingFlags.NonPublic)!;
    private static readonly MethodInfo ListMethod = typeof(ReaderFactory).GetMethod(nameof(ListReader), BindingFlags.Static | BindingFlags.NonPublic)!;

    /// <summary>Reader for <paramref name="clr"/>, returned as a <c>ValueReader&lt;clr&gt;</c>.</summary>
    public static object Create(Type clr, CcfField field, CcfSchemaOptions options) =>
        CcfBinding.Invoke(FullMethod.MakeGenericMethod(clr), field, options);

    private static ValueReader<TProp> Full<TProp>(CcfField f, CcfSchemaOptions options)
    {
        var clr = typeof(TProp);
        if (!CcfBinding.IsCompatible(f.Type, clr) && !(f.Type == CcfType.Enum && CcfBinding.Underlying(clr) == typeof(string)))
        {
            throw new InvalidOperationException(
                $"Schema field '{f.Name}' ({f.TypeName}) cannot be decoded into CLR type {clr.Name}.");
        }

        var underlying = Nullable.GetUnderlyingType(clr);
        if (underlying is not null)
        {
            return (ValueReader<TProp>)CcfBinding.Invoke(NullableMethod.MakeGenericMethod(underlying), f, options);
        }

        switch (f.Type)
        {
            case CcfType.Object:
                return (ValueReader<TProp>)CcfBinding.Invoke(ObjectMethod.MakeGenericMethod(clr), f, options);
            case CcfType.List:
                CcfBinding.TryGetEnumerableElement(clr, out var element);
                return (ValueReader<TProp>)CcfBinding.Invoke(ListMethod.MakeGenericMethod(clr, element), f, options);
            default:
                return Scalar<TProp>(f);
        }
    }

    /// <summary>Wraps the reader of <typeparamref name="U"/> so absent rows read as <c>null</c>.</summary>
    private static ValueReader<U?> NullableStruct<U>(CcfField f, CcfSchemaOptions options)
        where U : struct
    {
        var inner = Full<U>(f, options);
        if (f.Type == CcfType.Object)
        {
            return (c, i) => c.ChildStart![i] < 0 ? null : inner(c, i);
        }

        return (c, i) => c.IsPresent(i) ? inner(c, i) : null;
    }

    private static ValueReader<TRow> ObjectReader<TRow>(CcfField f, CcfSchemaOptions options)
    {
        var assembler = RowAssembler<TRow>.Create(f.Fields!, options);
        return (c, i) =>
        {
            var start = c.ChildStart![i];
            return start < 0 ? default! : assembler.Build(c.Children!, start);
        };
    }

    private static ValueReader<TList> ListReader<TList, TElement>(CcfField f, CcfSchemaOptions options)
    {
        Func<ColumnData[], int, TElement> element;
        if (f.Fields!.Count == 1 && f.Fields[0].Name == "$value")
        {
            var scalar = Full<TElement>(f.Fields[0], options);
            element = (cols, k) => scalar(cols[0], k);
        }
        else
        {
            var assembler = RowAssembler<TElement>.Create(f.Fields, options);
            element = assembler.Build;
        }

        var finish = ListConverter<TList, TElement>.Create();
        return (c, i) =>
        {
            if (!c.IsPresent(i))
            {
                return default!;
            }

            var count = c.ChildCount![i];
            var start = c.ChildStart![i];
            var list = new List<TElement>(count);
            var children = c.Children!;
            for (var k = 0; k < count; k++)
            {
                list.Add(element(children, start + k));
            }

            return finish(list);
        };
    }

    private static ValueReader<U> Scalar<U>(CcfField f)
    {
        switch (f.Type)
        {
            case CcfType.Bool:
                return (ValueReader<U>)(object)(ValueReader<bool>)((c, i) => c.Bools![i]);
            case CcfType.Int32:
            case CcfType.Int64:
            case CcfType.UInt32:
                return (c, i) => Conv.FromLong<U>(c.I64![i]);
            case CcfType.UInt64:
                return (c, i) => Conv.FromULong<U>(c.U64![i]);
            case CcfType.Float64:
                return (c, i) => Conv.FromDouble<U>(c.F64![i]);
            case CcfType.Decimal:
                var scale = f.Scale!.Value;
                return (ValueReader<U>)(object)(ValueReader<decimal>)((c, i) => CcfConvert.FromScaled(c.I64![i], scale));
            case CcfType.String:
                return (ValueReader<U>)(object)(ValueReader<string?>)((c, i) => c.Strings![i]);
            case CcfType.Guid:
                return (ValueReader<U>)(object)(ValueReader<Guid>)((c, i) => c.Guids![i]);
            case CcfType.DateTime:
                return (c, i) => Conv.FromDateTime<U>(CcfConvert.FromEpochMs(c.I64![i]));
            case CcfType.Enum:
                return EnumReader<U>(f);
            default:
                throw new InvalidOperationException("Unexpected scalar type " + f.TypeName);
        }
    }

    private static string Normalize(string name) =>
        name.Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant();

    private static ValueReader<U> EnumReader<U>(CcfField f)
    {
        var values = f.Values!;
        var table = new U[values.Count];
        var missing = new bool[values.Count];
        if (typeof(U) == typeof(string))
        {
            return (ValueReader<U>)(object)(ValueReader<string>)((c, i) => values[(int)c.I64![i]]);
        }

        var members = CcfTypeModel.EnumMembers(typeof(U));
        for (var i = 0; i < table.Length; i++)
        {
            // The server's names are what System.Text.Json wrote, possibly through a naming policy (camelCase,
            // snake_case, ...): match exactly first, then ignoring case, underscores and dashes.
            var match = Array.Find(members, m => m.Name == values[i]);
            if (match.Field is null)
            {
                var wanted = Normalize(values[i]);
                match = Array.Find(members, m => Normalize(m.Name) == wanted);
            }

            if (match.Field is null)
            {
                missing[i] = true;
            }
            else
            {
                table[i] = (U)Enum.ToObject(typeof(U), match.Field.GetRawConstantValue()!);
            }
        }

        return (c, i) =>
        {
            var index = (int)c.I64![i];
            return missing[index]
                ? throw new InvalidOperationException($"Enum {typeof(U).Name} has no member '{values[index]}'.")
                : table[index];
        };
    }
}

/// <summary>Turns a decoded <c>List&lt;T&gt;</c> into the property's collection type.</summary>
internal static class ListConverter<TList, TElement>
{
    public static Func<List<TElement>, TList> Create()
    {
        var target = typeof(TList);
        if (target.IsArray)
        {
            return list => (TList)(object)list.ToArray();
        }

        if (target.IsAssignableFrom(typeof(List<TElement>)))
        {
            return list => (TList)(object)list;
        }

        if (!target.IsAbstract && !target.IsInterface && typeof(ICollection<TElement>).IsAssignableFrom(target)
            && target.GetConstructor(Type.EmptyTypes) is not null)
        {
            return list =>
            {
                var collection = (ICollection<TElement>)Activator.CreateInstance(target)!;
                foreach (var e in list)
                {
                    collection.Add(e);
                }

                return (TList)collection;
            };
        }

        throw new InvalidOperationException($"Cannot decode a list into CLR type {target.Name}.");
    }
}

internal abstract class FieldAssigner<TRow>
{
    public abstract void Assign(ref TRow target, ColumnData[] columns, int row);
}

internal sealed class FieldAssigner<TRow, TProp> : FieldAssigner<TRow>
{
    private readonly int _column;
    private readonly ValueReader<TProp> _read;
    private readonly RefSetter<TRow, TProp> _set;

    public FieldAssigner(int column, ValueReader<TProp> read, RefSetter<TRow, TProp> set)
    {
        _column = column;
        _read = read;
        _set = set;
    }

    public override void Assign(ref TRow target, ColumnData[] columns, int row) =>
        _set(ref target, _read(columns[_column], row));
}

/// <summary>Creates rows of <typeparamref name="TRow"/> from decoded columns, with accessors compiled once.</summary>
internal sealed class RowAssembler<TRow>
{
    private readonly Func<TRow> _factory;
    private readonly FieldAssigner<TRow>[] _assigners;

    private RowAssembler(Func<TRow> factory, FieldAssigner<TRow>[] assigners)
    {
        _factory = factory;
        _assigners = assigners;
    }

    public TRow Build(ColumnData[] columns, int row)
    {
        var target = _factory();
        foreach (var a in _assigners)
        {
            a.Assign(ref target, columns, row);
        }

        return target;
    }

    /// <summary>
    /// Matches schema fields to settable properties by name (ARCHITECTURE §6.2): fields without a property are
    /// skipped, properties without a field keep their default.
    /// </summary>
    public static RowAssembler<TRow> Create(IReadOnlyList<CcfField> fields, CcfSchemaOptions options)
    {
        if (typeof(TRow).IsInterface || typeof(TRow).IsAbstract)
        {
            throw new InvalidOperationException($"Cannot decode into {typeof(TRow).Name}: it is not a concrete type.");
        }

        var members = CcfTypeModel.GetMembers(typeof(TRow), options);
        var assigners = new List<FieldAssigner<TRow>>();
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var member = Array.Find(members, m => m.Name == field.Name)
                ?? Array.Find(members, m => string.Equals(m.Name, field.Name, StringComparison.OrdinalIgnoreCase));
            if (member is null || member.Property.SetMethod is not { IsPublic: true })
            {
                continue;
            }

            var reader = ReaderFactory.Create(member.ClrType, field, options);
            var setter = BuildSetter(member.Property);
            var type = typeof(FieldAssigner<,>).MakeGenericType(typeof(TRow), member.ClrType);
            assigners.Add((FieldAssigner<TRow>)CcfBinding.CreateInstance(type, i, reader, setter));
        }

        return new RowAssembler<TRow>(BuildFactory(), assigners.ToArray());
    }

    private static Delegate BuildSetter(PropertyInfo property)
    {
        var row = Expression.Parameter(typeof(TRow).MakeByRefType(), "row");
        var value = Expression.Parameter(property.PropertyType, "value");
        var body = Expression.Assign(Expression.Property(row, property), value);
        return Expression.Lambda(typeof(RefSetter<,>).MakeGenericType(typeof(TRow), property.PropertyType), body, row, value).Compile();
    }

    private static Func<TRow> BuildFactory()
    {
        var type = typeof(TRow);
        if (type.IsValueType)
        {
            return () => default!;
        }

        if (type.GetConstructor(Type.EmptyTypes) is not null)
        {
            return Expression.Lambda<Func<TRow>>(Expression.New(type)).Compile();
        }

        // Records with positional constructors: every property has an init accessor we set afterwards.
        return () => (TRow)RuntimeHelpers.GetUninitializedObject(type);
    }
}

/// <summary>Root-level typed decoding: the whole table as <c>T</c> (a collection or a single object).</summary>
internal abstract class TypedRoot
{
    private static readonly ConcurrentDictionary<(string, Type, CcfSchemaOptions), TypedRoot> Cache = new();

    public abstract object Convert(ColumnData[] table, int rows);

    public static TypedRoot Get(CcfSchema schema, Type target, CcfSchemaOptions options) =>
        // Keyed by the canonical text, not the instance, so parsing a schema per call cannot grow the cache.
        Cache.GetOrAdd((schema.CanonicalJson, target, options), static (key, state) => Create(state, key.Item2, key.Item3), schema);

    private static TypedRoot Create(CcfSchema schema, Type target, CcfSchemaOptions options)
    {
        var collection = CcfBinding.TryGetEnumerableElement(target, out var element);
        var rowType = collection ? element : target;
        return (TypedRoot)CcfBinding.CreateInstance(typeof(TypedRoot<,>).MakeGenericType(rowType, target), schema, options, collection);
    }
}

internal sealed class TypedRoot<TRow, T> : TypedRoot
{
    private readonly RowAssembler<TRow> _assembler;
    private readonly bool _collection;
    private readonly Func<List<TRow>, T>? _finish;

    public TypedRoot(CcfSchema schema, CcfSchemaOptions options, bool collection)
    {
        _assembler = RowAssembler<TRow>.Create(schema.Fields, options);
        _collection = collection;
        if (collection)
        {
            _finish = ListConverter<T, TRow>.Create();
        }
    }

    public override object Convert(ColumnData[] table, int rows)
    {
        if (!_collection)
        {
            if (rows != 1)
            {
                throw new InvalidOperationException($"Cannot decode {rows} rows into the single object type {typeof(T).Name}.");
            }

            return _assembler.Build(table, 0)!;
        }

        var list = new List<TRow>(rows);
        for (var i = 0; i < rows; i++)
        {
            list.Add(_assembler.Build(table, i));
        }

        return _finish!(list)!;
    }
}
