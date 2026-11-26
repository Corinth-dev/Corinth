using System.Collections;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Corinth;

/// <summary>Maps CLR property types onto what the typed encoder and decoder can bind.</summary>
internal static class CcfBinding
{
    /// <summary>The element type of any non-string <c>IEnumerable&lt;T&gt;</c> (not a dictionary).</summary>
    public static bool TryGetEnumerableElement(Type type, out Type element)
    {
        element = typeof(object);
        if (type == typeof(string) || type == typeof(byte[]))
        {
            return false;
        }

        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1)
            {
                return false;
            }

            element = type.GetElementType()!;
            return true;
        }

        Type? found = null;
        foreach (var i in type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>))
            {
                return false;
            }

            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                if (found is not null && found != i)
                {
                    return false;
                }

                found = i;
            }
        }

        if (found is null)
        {
            return false;
        }

        element = found.GetGenericArguments()[0];
        return true;
    }

    public static Type Underlying(Type t) => Nullable.GetUnderlyingType(t) ?? t;

    /// <summary>Activator.CreateInstance that rethrows the constructor's own exception, not TargetInvocationException.</summary>
    public static object CreateInstance(Type type, params object[] args)
    {
        try
        {
            return Activator.CreateInstance(type, args)!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    /// <summary>MethodInfo.Invoke that rethrows the callee's own exception.</summary>
    public static object Invoke(MethodInfo method, params object[] args)
    {
        try
        {
            return method.Invoke(null, args)!;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
    }

    public static bool IsIntegral(Type t) =>
        t == typeof(sbyte) || t == typeof(short) || t == typeof(int) || t == typeof(long)
        || t == typeof(byte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong);

    /// <summary>Does the schema field type accept this CLR type?</summary>
    public static bool IsCompatible(CcfType type, Type clr)
    {
        var u = Underlying(clr);
        return type switch
        {
            CcfType.Bool => u == typeof(bool),
            CcfType.Int32 or CcfType.Int64 or CcfType.UInt32 or CcfType.UInt64 => IsIntegral(u) || (u.IsEnum && IsIntegral(Enum.GetUnderlyingType(u))),
            CcfType.Float64 => u == typeof(double) || u == typeof(float),
            CcfType.Decimal => u == typeof(decimal),
            CcfType.Enum => u.IsEnum,
            CcfType.String => u == typeof(string),
            CcfType.DateTime => u == typeof(DateTime) || u == typeof(DateTimeOffset),
            CcfType.Guid => u == typeof(Guid),
            CcfType.Object => !u.IsPrimitive && !u.IsEnum && u != typeof(string) && !u.IsInterface && !u.IsAbstract,
            CcfType.List => TryGetEnumerableElement(clr, out _),
            _ => false,
        };
    }
}

/// <summary>Extracts one field of a row type into a <see cref="ColumnData"/>.</summary>
internal abstract class FieldExtractor<TRow>
{
    protected FieldExtractor(CcfField field)
    {
        Field = field;
    }

    public CcfField Field { get; }

    public abstract ColumnData Extract(ReadOnlySpan<TRow> rows, CcfLimits limits);

    protected CcfEncodeException NullValue(int row) =>
        new($"Field '{Field.Name}' is not nullable but row {row} has no value.");
}

/// <summary>Extracts every field of a row type: the typed encoder's per-type, per-schema plan.</summary>
internal sealed class TableExtractor<TRow>
{
    private readonly FieldExtractor<TRow>[] _fields;

    private TableExtractor(FieldExtractor<TRow>[] fields)
    {
        _fields = fields;
    }

    public ColumnData[] Extract(ReadOnlySpan<TRow> rows, CcfLimits limits)
    {
        if (rows.Length > limits.MaxRows)
        {
            throw new CcfEncodeException($"{rows.Length} rows exceed the limit of {limits.MaxRows} rows per table.");
        }

        var columns = new ColumnData[_fields.Length];
        for (var i = 0; i < columns.Length; i++)
        {
            columns[i] = _fields[i].Extract(rows, limits);
        }

        return columns;
    }

    /// <summary>Binds <paramref name="fields"/> to the properties of <typeparamref name="TRow"/> (or to the row itself for a <c>$value</c> list).</summary>
    public static TableExtractor<TRow> Create(IReadOnlyList<CcfField> fields, CcfSchemaOptions options)
    {
        var row = Expression.Parameter(typeof(TRow), "row");
        var extractors = new FieldExtractor<TRow>[fields.Count];

        if (fields.Count == 1 && fields[0].Name == "$value")
        {
            extractors[0] = ExtractorFactory.Create<TRow>(fields[0], row, row, typeof(TRow), options);
            return new TableExtractor<TRow>(extractors);
        }

        var members = CcfTypeModel.GetMembers(typeof(TRow), options);
        for (var i = 0; i < extractors.Length; i++)
        {
            var field = fields[i];
            var member = Array.Find(members, m => m.Name == field.Name)
                ?? throw new CcfEncodeException($"Schema field '{field.Name}' has no matching property on {typeof(TRow).Name}.");
            extractors[i] = ExtractorFactory.Create<TRow>(field, row, Expression.Property(row, member.Property), member.ClrType, options, member.Property);
        }

        return new TableExtractor<TRow>(extractors);
    }
}

internal static class ExtractorFactory
{
    public static FieldExtractor<TRow> Create<TRow>(
        CcfField field, ParameterExpression row, Expression value, Type clr, CcfSchemaOptions options, PropertyInfo? property = null)
    {
        if (!CcfBinding.IsCompatible(field.Type, clr))
        {
            throw new CcfEncodeException(
                $"Field '{field.Name}' ({field.TypeName}) cannot be encoded from CLR type {clr.Name}.");
        }

        var u = CcfBinding.Underlying(clr);
        // An enum behind an integer column (STJ writes it as a number) is read through its underlying type.
        var iu = u.IsEnum && field.Type != CcfType.Enum ? Enum.GetUnderlyingType(u) : u;
        switch (field.Type)
        {
            case CcfType.Bool:
                return new BoolExtractor<TRow>(field, Getter<TRow, bool>(row, value, clr, e => e));
            case CcfType.Float64:
                return new DoubleExtractor<TRow>(field, Getter<TRow, double>(row, value, clr, e => Expression.Convert(e, typeof(double))));
            case CcfType.Decimal:
                return new DecimalExtractor<TRow>(field, Getter<TRow, decimal>(row, value, clr, e => e));
            case CcfType.Guid:
                return new GuidExtractor<TRow>(field, Getter<TRow, Guid>(row, value, clr, e => e));
            case CcfType.String:
                return new StringExtractor<TRow>(field, Expression.Lambda<Func<TRow, string?>>(value, row).Compile());
            case CcfType.DateTime:
                var toMs = typeof(CcfConvert).GetMethod(nameof(CcfConvert.ToEpochMs), new[] { u })!;
                return new LongExtractor<TRow>(field, Getter<TRow, long>(row, value, clr, e => Expression.Call(toMs, e)), null, null);
            case CcfType.Enum:
                return new LongExtractor<TRow>(field, Getter<TRow, long>(row, value, clr, e => Expression.Convert(e, typeof(long))), null, EnumMap(field, u, property, options));
            case CcfType.UInt64 when iu == typeof(ulong):
            case CcfType.Int32 when iu == typeof(ulong):
            case CcfType.Int64 when iu == typeof(ulong):
            case CcfType.UInt32 when iu == typeof(ulong):
                return new LongExtractor<TRow>(field, null, Getter<TRow, ulong>(row, value, clr, e => Expression.Convert(e, typeof(ulong))), null);
            default: // integers
                return new LongExtractor<TRow>(field, Getter<TRow, long>(row, value, clr, e => Expression.Convert(e, typeof(long))), null, null);
            case CcfType.Object:
                return MakeGeneric<TRow>(typeof(ObjectExtractor<,>), new[] { typeof(TRow), u }, field, row, value, clr, options);
            case CcfType.List:
                CcfBinding.TryGetEnumerableElement(clr, out var element);
                return MakeGeneric<TRow>(typeof(ListExtractor<,>), new[] { typeof(TRow), element }, field, row, value, clr, options);
        }
    }

    private static FieldExtractor<TRow> MakeGeneric<TRow>(
        Type definition, Type[] args, CcfField field, ParameterExpression row, Expression value, Type clr, CcfSchemaOptions options)
    {
        var type = definition.MakeGenericType(args);
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(args[1]);
        Delegate getter;
        if (definition == typeof(ListExtractor<,>))
        {
            getter = Expression.Lambda(typeof(Func<,>).MakeGenericType(typeof(TRow), enumerableType),
                Expression.Convert(value, enumerableType), row).Compile();
        }
        else
        {
            getter = Expression.Lambda<Func<TRow, object?>>(Expression.Convert(value, typeof(object)), row).Compile();
        }

        return (FieldExtractor<TRow>)CcfBinding.CreateInstance(type, field, getter, options);
    }

    /// <summary>Numeric value → index into <c>field.Values</c>, from the names System.Text.Json writes for the enum.</summary>
    private static Dictionary<long, int> EnumMap(CcfField field, Type enumType, PropertyInfo? property, CcfSchemaOptions options)
    {
        var json = CcfEnumJson.Resolve(enumType, property, options.JsonSerializerOptions);
        if (!json.IsString)
        {
            throw new CcfEncodeException($"Field '{field.Name}': {enumType.Name} is not written as strings by its JSON options, so it cannot go into an enum column.");
        }

        var map = new Dictionary<long, int>();
        foreach (var (name, value) in json.Members!)
        {
            var index = -1;
            for (var i = 0; i < field.Values!.Count; i++)
            {
                if (field.Values[i] == name)
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                map.TryAdd(value, index);
            }
        }

        return map;
    }

    /// <summary>
    /// Compiles <c>row =&gt; present ? (T?)convert(value) : null</c>, evaluating the property exactly once.
    /// </summary>
    private static Func<TRow, T?> Getter<TRow, T>(ParameterExpression row, Expression value, Type clr, Func<Expression, Expression> convert)
        where T : struct
    {
        var v = Expression.Variable(clr, "v");
        var isNullableStruct = Nullable.GetUnderlyingType(clr) is not null;
        Expression present = isNullableStruct ? Expression.Property(v, "HasValue") : Expression.Constant(true);
        Expression core = isNullableStruct ? Expression.Property(v, "Value") : v;
        var body = Expression.Block(
            new[] { v },
            Expression.Assign(v, value),
            Expression.Condition(
                present,
                Expression.Convert(convert(core), typeof(T?)),
                Expression.Default(typeof(T?))));
        return Expression.Lambda<Func<TRow, T?>>(body, row).Compile();
    }
}

/// <summary>Base for columns of nullable-struct getters: handles presence and the row loop.</summary>
internal abstract class StructExtractor<TRow, T> : FieldExtractor<TRow>
    where T : struct
{
    private readonly Func<TRow, T?> _get;

    protected StructExtractor(CcfField field, Func<TRow, T?> get)
        : base(field)
    {
        _get = get;
    }

    protected abstract void Allocate(ColumnData column);

    protected abstract void Store(ColumnData column, int row, T value);

    public sealed override ColumnData Extract(ReadOnlySpan<TRow> rows, CcfLimits limits)
    {
        var column = new ColumnData(Field, rows.Length);
        Allocate(column);
        var present = Field.Nullable ? new bool[rows.Length] : null;
        var count = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var v = _get(rows[i]);
            if (v.HasValue)
            {
                Store(column, i, v.Value);
                if (present is not null)
                {
                    present[i] = true;
                }

                count++;
            }
            else if (present is null)
            {
                throw NullValue(i);
            }
        }

        column.Present = present;
        column.PresentCount = count;
        return column;
    }
}

/// <summary>Integers, enums (via a numeric→index map) and datetimes (epoch milliseconds); long and ulong sources.</summary>
internal sealed class LongExtractor<TRow> : FieldExtractor<TRow>
{
    private readonly Func<TRow, long?>? _getLong;
    private readonly Func<TRow, ulong?>? _getULong;
    private readonly Dictionary<long, int>? _enumMap;

    public LongExtractor(CcfField field, Func<TRow, long?>? getLong, Func<TRow, ulong?>? getULong, Dictionary<long, int>? enumMap)
        : base(field)
    {
        _getLong = getLong;
        _getULong = getULong;
        _enumMap = enumMap;
    }

    public override ColumnData Extract(ReadOnlySpan<TRow> rows, CcfLimits limits)
    {
        var column = new ColumnData(Field, rows.Length);
        var isU64 = Field.Type == CcfType.UInt64;
        var longs = isU64 ? null : new long[rows.Length];
        var ulongs = isU64 ? new ulong[rows.Length] : null;
        var present = Field.Nullable ? new bool[rows.Length] : null;
        var count = 0;

        for (var i = 0; i < rows.Length; i++)
        {
            bool has;
            long l = 0;
            ulong u = 0;
            bool fromUnsigned;
            if (_getULong is not null)
            {
                var v = _getULong(rows[i]);
                has = v.HasValue;
                u = v.GetValueOrDefault();
                fromUnsigned = true;
            }
            else
            {
                var v = _getLong!(rows[i]);
                has = v.HasValue;
                l = v.GetValueOrDefault();
                fromUnsigned = false;
            }

            if (!has)
            {
                if (present is null)
                {
                    throw NullValue(i);
                }

                continue;
            }

            switch (Field.Type)
            {
                case CcfType.UInt64:
                    if (!fromUnsigned && l < 0)
                    {
                        throw OutOfRange(i);
                    }

                    ulongs![i] = fromUnsigned ? u : (ulong)l;
                    break;
                case CcfType.Enum:
                    if (!_enumMap!.TryGetValue(l, out var index))
                    {
                        throw new CcfEncodeException($"Field '{Field.Name}': enum value {l} has no member in the schema.");
                    }

                    longs![i] = index;
                    break;
                default:
                    if (fromUnsigned)
                    {
                        if (u > long.MaxValue)
                        {
                            throw OutOfRange(i);
                        }

                        l = (long)u;
                    }

                    if ((Field.Type == CcfType.Int32 && (l < int.MinValue || l > int.MaxValue))
                        || (Field.Type == CcfType.UInt32 && (l < 0 || l > uint.MaxValue)))
                    {
                        throw OutOfRange(i);
                    }

                    longs![i] = l;
                    break;
            }

            if (present is not null)
            {
                present[i] = true;
            }

            count++;
        }

        column.I64 = longs;
        column.U64 = ulongs;
        column.Present = present;
        column.PresentCount = count;
        return column;
    }

    private CcfEncodeException OutOfRange(int row) =>
        new($"Field '{Field.Name}' row {row}: value is outside the range of {Field.TypeName}.");
}

internal sealed class BoolExtractor<TRow> : StructExtractor<TRow, bool>
{
    public BoolExtractor(CcfField field, Func<TRow, bool?> get)
        : base(field, get)
    {
    }

    protected override void Allocate(ColumnData column) => column.Bools = new bool[column.Rows];

    protected override void Store(ColumnData column, int row, bool value) => column.Bools![row] = value;
}

internal sealed class DoubleExtractor<TRow> : StructExtractor<TRow, double>
{
    public DoubleExtractor(CcfField field, Func<TRow, double?> get)
        : base(field, get)
    {
    }

    protected override void Allocate(ColumnData column) => column.F64 = new double[column.Rows];

    protected override void Store(ColumnData column, int row, double value)
    {
        if (!double.IsFinite(value))
        {
            // SPEC §14.4: NaN and ±Infinity cannot be encoded (the response falls back to JSON); −0.0 is kept.
            throw new CcfEncodeException($"Field '{Field.Name}' row {row}: {value} cannot be encoded as float64.");
        }

        column.F64![row] = value;
    }
}

internal sealed class DecimalExtractor<TRow> : StructExtractor<TRow, decimal>
{
    public DecimalExtractor(CcfField field, Func<TRow, decimal?> get)
        : base(field, get)
    {
    }

    protected override void Allocate(ColumnData column) => column.I64 = new long[column.Rows];

    protected override void Store(ColumnData column, int row, decimal value)
    {
        if (!CcfConvert.TryToScaled(value, Field.Scale!.Value, out var stored))
        {
            throw new CcfEncodeException(
                $"Field '{Field.Name}' row {row}: {CcfConvert.FormatInvariant(value)} is not an exact int64 at scale {Field.Scale}.");
        }

        if (Field.Unsigned && stored < 0)
        {
            throw new CcfEncodeException($"Field '{Field.Name}' row {row}: unsigned decimal is negative.");
        }

        column.I64![row] = stored;
    }
}

internal sealed class GuidExtractor<TRow> : StructExtractor<TRow, Guid>
{
    public GuidExtractor(CcfField field, Func<TRow, Guid?> get)
        : base(field, get)
    {
    }

    protected override void Allocate(ColumnData column) => column.Guids = new Guid[column.Rows];

    protected override void Store(ColumnData column, int row, Guid value) => column.Guids![row] = value;
}

internal sealed class StringExtractor<TRow> : FieldExtractor<TRow>
{
    private readonly Func<TRow, string?> _get;

    public StringExtractor(CcfField field, Func<TRow, string?> get)
        : base(field)
    {
        _get = get;
    }

    public override ColumnData Extract(ReadOnlySpan<TRow> rows, CcfLimits limits)
    {
        var column = new ColumnData(Field, rows.Length);
        var strings = new string?[rows.Length];
        var present = Field.Nullable ? new bool[rows.Length] : null;
        var count = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var s = _get(rows[i]);
            if (s is null)
            {
                if (present is null)
                {
                    throw NullValue(i);
                }

                continue;
            }

            strings[i] = s;
            if (present is not null)
            {
                present[i] = true;
            }

            count++;
        }

        column.Strings = strings;
        column.Present = present;
        column.PresentCount = count;
        return column;
    }
}

internal sealed class ObjectExtractor<TRow, TChild> : FieldExtractor<TRow>
{
    private readonly Func<TRow, object?> _get;
    private readonly TableExtractor<TChild> _child;

    public ObjectExtractor(CcfField field, Func<TRow, object?> get, CcfSchemaOptions options)
        : base(field)
    {
        _get = get;
        _child = TableExtractor<TChild>.Create(field.Fields!, options);
    }

    public override ColumnData Extract(ReadOnlySpan<TRow> rows, CcfLimits limits)
    {
        var column = new ColumnData(Field, rows.Length);
        var present = Field.Nullable ? new bool[rows.Length] : null;
        var start = new int[rows.Length];
        var children = new List<TChild>();
        for (var i = 0; i < rows.Length; i++)
        {
            var o = _get(rows[i]);
            if (o is null)
            {
                if (present is null)
                {
                    throw NullValue(i);
                }

                start[i] = -1;
                continue;
            }

            if (present is not null)
            {
                present[i] = true;
            }

            start[i] = children.Count;
            children.Add((TChild)o);
        }

        column.Present = present;
        column.PresentCount = children.Count;
        column.ChildStart = start;
        column.ChildRows = children.Count;
        column.Children = _child.Extract(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(children), limits);
        return column;
    }
}

internal sealed class ListExtractor<TRow, TElement> : FieldExtractor<TRow>
{
    private readonly Func<TRow, IEnumerable<TElement>?> _get;
    private readonly TableExtractor<TElement> _child;
    private readonly bool _isScalarList;

    public ListExtractor(CcfField field, Func<TRow, IEnumerable<TElement>?> get, CcfSchemaOptions options)
        : base(field)
    {
        _get = get;
        _child = TableExtractor<TElement>.Create(field.Fields!, options);
        _isScalarList = field.Fields!.Count == 1 && field.Fields[0].Name == "$value";
    }

    public override ColumnData Extract(ReadOnlySpan<TRow> rows, CcfLimits limits)
    {
        var column = new ColumnData(Field, rows.Length);
        var present = Field.Nullable ? new bool[rows.Length] : null;
        var start = new int[rows.Length];
        var count = new int[rows.Length];
        var elements = new List<TElement>();
        var presentCount = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            var items = _get(rows[i]);
            if (items is null)
            {
                if (present is null)
                {
                    throw NullValue(i);
                }

                start[i] = elements.Count;
                continue;
            }

            if (present is not null)
            {
                present[i] = true;
            }

            presentCount++;
            start[i] = elements.Count;
            var before = elements.Count;
            elements.AddRange(items);
            if (!typeof(TElement).IsValueType && !_isScalarList)
            {
                for (var k = before; k < elements.Count; k++)
                {
                    if (elements[k] is null)
                    {
                        throw new CcfEncodeException($"List '{Field.Name}' of row {i} contains a null element.");
                    }
                }
            }

            count[i] = elements.Count - before;
            if (elements.Count > limits.MaxRows)
            {
                throw new CcfEncodeException($"List '{Field.Name}' exceeds the limit of {limits.MaxRows} rows per table.");
            }
        }

        column.Present = present;
        column.PresentCount = presentCount;
        column.ChildStart = start;
        column.ChildCount = count;
        column.ChildRows = elements.Count;
        column.Children = _child.Extract(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(elements), limits);
        return column;
    }
}
