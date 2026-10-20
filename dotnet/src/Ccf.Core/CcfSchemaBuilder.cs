using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Reflection;

namespace Ccf;

/// <summary>Builds a <see cref="CcfSchema"/> from a CLR type (ARCHITECTURE §5).</summary>
public static class CcfSchemaBuilder
{
    /// <summary>
    /// Tries to build the schema for <paramref name="type"/>: a collection of objects (a table) or a single
    /// object. Returns <c>false</c> with a reason when any reachable member is unsupported, in which case the
    /// response is served as JSON.
    /// </summary>
    public static bool TryBuild(
        Type type, CcfSchemaOptions? options, [NotNullWhen(true)] out CcfSchema? schema, [NotNullWhen(false)] out string? reason)
    {
        ArgumentNullException.ThrowIfNull(type);
        options ??= CcfSchemaOptions.Default;
        schema = null;
        try
        {
            var rowType = type;
            if (CcfTypeModel.TryGetCollectionElement(type, out var element))
            {
                rowType = element;
            }

            if (!Analyzer.IsObjectLike(rowType, out var why))
            {
                reason = $"{type.Name}: root must be an object or a collection of objects ({why})";
                return false;
            }

            var analyzer = new Analyzer(options);
            var fields = analyzer.ObjectFields(rowType, 1, rowType.Name);
            schema = CcfSchema.Create(StripArity(rowType.Name), fields);
            reason = null;
            return true;
        }
        catch (CcfUnsupportedTypeException e)
        {
            reason = e.Message;
            return false;
        }
        catch (CcfSchemaException e)
        {
            reason = e.Message;
            return false;
        }
    }

    private static string StripArity(string name)
    {
        var i = name.IndexOf('`');
        return i < 0 ? name : name[..i];
    }

    private sealed class Analyzer
    {
        private readonly CcfSchemaOptions _options;
        private readonly List<Type> _stack = new();

        public Analyzer(CcfSchemaOptions options)
        {
            _options = options;
        }

        public static bool IsObjectLike(Type t, out string why)
        {
            why = string.Empty;
            if (t.IsPrimitive || t.IsEnum || t.IsInterface || t.IsAbstract || t.IsGenericTypeDefinition || t.IsPointer
                || t.IsArray || t.IsGenericParameter || t == typeof(object) || t == typeof(string) || t == typeof(decimal)
                || t == typeof(Guid) || t == typeof(DateTime) || t == typeof(DateTimeOffset) || t == typeof(Nullable<>))
            {
                why = $"{t.Name} is not a record type";
                return false;
            }

            var ns = t.Namespace ?? string.Empty;
            if (ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns.StartsWith("Microsoft.", StringComparison.Ordinal))
            {
                why = $"{t.FullName} is a framework type";
                return false;
            }

            return true;
        }

        public List<CcfField> ObjectFields(Type objectType, int depth, string path)
        {
            if (_stack.Contains(objectType))
            {
                throw new CcfUnsupportedTypeException($"{path}: recursive type {objectType.Name}");
            }

            _stack.Add(objectType);
            var members = CcfTypeModel.GetMembers(objectType, _options);
            if (members.Length == 0)
            {
                throw new CcfUnsupportedTypeException($"{path}: {objectType.Name} has no serializable public properties");
            }

            var fields = new List<CcfField>(members.Length);
            foreach (var m in members)
            {
                fields.Add(MakeField(m.Name, m.ClrType, m.Nullability, m.ConditionallyIgnored, m.Property, depth, $"{path}.{m.Name}"));
            }

            _stack.RemoveAt(_stack.Count - 1);
            return fields;
        }

        // depth: depth of the table this field lives in (root = 1).
        private CcfField MakeField(
            string name, Type clr, NullabilityInfo? nullability, bool forceNullable, PropertyInfo? property, int depth, string path)
        {
            var type = clr;
            var nullable = forceNullable;
            var underlying = Nullable.GetUnderlyingType(clr);
            if (underlying is not null)
            {
                type = underlying;
                nullable = true;
            }
            else if (!clr.IsValueType && nullability is not { ReadState: NullabilityState.NotNull })
            {
                nullable = true; // annotated nullable, or oblivious (un-annotated) reference type
            }

            var wantsDelta = property?.GetCustomAttribute<CcfDeltaAttribute>() is not null;
            var decimalAttr = property?.GetCustomAttribute<CcfDecimalAttribute>();

            CcfField Scalar(CcfType t, bool delta = false)
            {
                if (wantsDelta && !delta)
                {
                    throw new CcfUnsupportedTypeException($"{path}: [CcfDelta] does not apply to {type.Name}");
                }

                if (decimalAttr is not null && t != CcfType.Decimal)
                {
                    throw new CcfUnsupportedTypeException($"{path}: [CcfDecimal] does not apply to {type.Name}");
                }

                return new CcfField(name, t) { Nullable = nullable, Delta = wantsDelta };
            }

            if (type == typeof(bool))
            {
                return Scalar(CcfType.Bool);
            }

            if (type == typeof(sbyte) || type == typeof(short) || type == typeof(int))
            {
                return Scalar(CcfType.Int32, delta: true);
            }

            if (type == typeof(byte) || type == typeof(ushort) || type == typeof(uint))
            {
                return Scalar(CcfType.UInt32, delta: true);
            }

            if (type == typeof(long))
            {
                return Scalar(CcfType.Int64, delta: true);
            }

            if (type == typeof(ulong))
            {
                return Scalar(CcfType.UInt64, delta: true);
            }

            if (type == typeof(float) || type == typeof(double))
            {
                return Scalar(CcfType.Float64);
            }

            if (type == typeof(decimal))
            {
                var scale = decimalAttr?.Scale ?? _options.DefaultDecimalScale;
                if (scale is < 0 or > 18)
                {
                    throw new CcfUnsupportedTypeException($"{path}: decimal scale {scale} is outside 0–18");
                }

                return new CcfField(name, CcfType.Decimal)
                {
                    Nullable = nullable,
                    Delta = wantsDelta,
                    Scale = scale,
                    Unsigned = decimalAttr?.Unsigned ?? false,
                };
            }

            if (type == typeof(string))
            {
                return Scalar(CcfType.String);
            }

            if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
            {
                return Scalar(CcfType.DateTime, delta: true);
            }

            if (type == typeof(Guid))
            {
                return Scalar(CcfType.Guid);
            }

            if (type.IsEnum)
            {
                var json = CcfEnumJson.Resolve(type, property, _options.JsonSerializerOptions);
                if (!json.IsString)
                {
                    // Written as a number by System.Text.Json: an integer column with the underlying value.
                    return Scalar(CcfEnumJson.NumericType(type), delta: true);
                }

                if (type.IsDefined(typeof(FlagsAttribute), inherit: false))
                {
                    throw new CcfUnsupportedTypeException($"{path}: [Flags] enum {type.Name} written as strings");
                }

                var values = json.Names!;
                if (values.Length == 0 || values.Length > 65_536)
                {
                    throw new CcfUnsupportedTypeException($"{path}: enum {type.Name} needs 1 to 65,536 members");
                }

                foreach (var v in values)
                {
                    if (!CcfTypeModel.IsIdentifier(v))
                    {
                        throw new CcfUnsupportedTypeException($"{path}: enum member name \"{v}\" is not an identifier");
                    }
                }

                var field = Scalar(CcfType.Enum);
                return new CcfField(name, CcfType.Enum) { Nullable = field.Nullable, Values = values };
            }

            if (type == typeof(char) || type == typeof(byte[]) || type == typeof(TimeSpan) || type == typeof(DateOnly)
                || type == typeof(TimeOnly) || type == typeof(Half) || type == typeof(Int128) || type == typeof(UInt128)
                || type == typeof(BigInteger) || type == typeof(nint) || type == typeof(nuint) || type == typeof(object))
            {
                throw new CcfUnsupportedTypeException($"{path}: unsupported type {type.Name}");
            }

            if (wantsDelta || decimalAttr is not null)
            {
                throw new CcfUnsupportedTypeException($"{path}: [CcfDelta]/[CcfDecimal] do not apply to {type.Name}");
            }

            if (CcfTypeModel.TryGetCollectionElement(type, out var element))
            {
                if (depth + 1 > CcfLimits.MaxDepth)
                {
                    throw new CcfUnsupportedTypeException($"{path}: nesting deeper than {CcfLimits.MaxDepth}");
                }

                var elementInfo = nullability is null
                    ? null
                    : type.IsArray ? nullability.ElementType : nullability.GenericTypeArguments.Length > 0 ? nullability.GenericTypeArguments[0] : null;

                IReadOnlyList<CcfField> children;
                if (Analyzer.IsObjectLike(element, out _) && Nullable.GetUnderlyingType(element) is null
                    && !CcfTypeModel.TryGetCollectionElement(element, out _))
                {
                    children = ObjectFields(element, depth + 1, path + "[]");
                }
                else
                {
                    children = new[] { MakeField("$value", element, elementInfo, false, null, depth + 1, path + "[]") };
                }

                return new CcfField(name, CcfType.List) { Nullable = nullable, Fields = children };
            }

            if (!IsObjectLike(type, out var why))
            {
                throw new CcfUnsupportedTypeException($"{path}: unsupported type {type.Name} ({why})");
            }

            if (depth + 1 > CcfLimits.MaxDepth)
            {
                throw new CcfUnsupportedTypeException($"{path}: nesting deeper than {CcfLimits.MaxDepth}");
            }

            return new CcfField(name, CcfType.Object) { Nullable = nullable, Fields = ObjectFields(type, depth + 1, path) };
        }
    }
}
