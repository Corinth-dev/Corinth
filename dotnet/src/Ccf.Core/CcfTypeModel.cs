using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ccf;

/// <summary>Thrown inside the schema builder when a CLR type cannot be expressed in CCF.</summary>
internal sealed class CcfUnsupportedTypeException : Exception
{
    public CcfUnsupportedTypeException(string message)
        : base(message)
    {
    }
}

/// <summary>A serializable property of a CLR type, following System.Text.Json's naming, ignore and order rules.</summary>
internal sealed class CcfMember
{
    public CcfMember(PropertyInfo property, string name, bool conditionallyIgnored, NullabilityInfo nullability)
    {
        Property = property;
        Name = name;
        ConditionallyIgnored = conditionallyIgnored;
        Nullability = nullability;
    }

    public PropertyInfo Property { get; }

    /// <summary>The JSON name, which is the field name.</summary>
    public string Name { get; }

    /// <summary>[JsonIgnore(Condition = When…)]: the field is kept but made nullable (ARCHITECTURE §5.2).</summary>
    public bool ConditionallyIgnored { get; }

    public NullabilityInfo Nullability { get; }

    public Type ClrType => Property.PropertyType;
}

/// <summary>Reflection helpers shared by the schema builder, the typed encoder and the typed decoder.</summary>
internal static class CcfTypeModel
{
    private static readonly ConcurrentDictionary<(Type, JsonNamingPolicy?), CcfMember[]> MemberCache = new();

    private static readonly Type[] CollectionDefinitions =
    {
        typeof(List<>), typeof(IList<>), typeof(IReadOnlyList<>), typeof(IEnumerable<>), typeof(ICollection<>),
    };

    public static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || !(IsLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(IsLetter(c) || c == '_' || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;

        static bool IsLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }

    /// <summary>If <paramref name="type"/> is one of the supported collection types, returns its element type.</summary>
    public static bool TryGetCollectionElement(Type type, out Type element)
    {
        element = typeof(object);
        if (type.IsArray)
        {
            if (type.GetArrayRank() != 1 || type.GetElementType() == typeof(byte))
            {
                return false;
            }

            element = type.GetElementType()!;
            return true;
        }

        if (type.IsGenericType && Array.IndexOf(CollectionDefinitions, type.GetGenericTypeDefinition()) >= 0)
        {
            element = type.GetGenericArguments()[0];
            return true;
        }

        return false;
    }

    /// <summary>
    /// The serializable members of an object type in System.Text.Json order (derived type first, then base
    /// types, then stably by <see cref="JsonPropertyOrderAttribute"/>).
    /// </summary>
    /// <exception cref="CcfUnsupportedTypeException">A member cannot be expressed in CCF.</exception>
    public static CcfMember[] GetMembers(Type type, CcfSchemaOptions options)
    {
        var key = (type, options.PropertyNamingPolicy);
        if (MemberCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var members = Discover(type, options);
        MemberCache[key] = members;
        return members;
    }

    private static CcfMember[] Discover(Type type, CcfSchemaOptions options)
    {
        if (type.GetCustomAttributes(typeof(JsonConverterAttribute), inherit: true).Length > 0)
        {
            throw new CcfUnsupportedTypeException($"{type.Name} has a custom JsonConverter");
        }

        if (type.GetCustomAttributes().Any(a => a.GetType().FullName is
                "System.Text.Json.Serialization.JsonDerivedTypeAttribute" or
                "System.Text.Json.Serialization.JsonPolymorphicAttribute"))
        {
            throw new CcfUnsupportedTypeException($"{type.Name} is polymorphic");
        }

        var context = new NullabilityInfoContext();
        var found = new List<(CcfMember Member, int Order)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var t = type; t is not null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
        {
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length > 0 || p.GetMethod is not { IsPublic: true } || !seen.Add(p.Name))
                {
                    continue;
                }

                var conditional = false;
                if (p.GetCustomAttribute<JsonIgnoreAttribute>() is { } ignore)
                {
                    if (ignore.Condition == JsonIgnoreCondition.Always)
                    {
                        continue;
                    }

                    conditional = ignore.Condition != JsonIgnoreCondition.Never;
                }

                // A converter on an enum property is judged by the schema builder (string-enum converters are mirrored).
                var propertyType = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                if (p.GetCustomAttribute<JsonConverterAttribute>() is not null && !propertyType.IsEnum)
                {
                    throw new CcfUnsupportedTypeException($"{type.Name}.{p.Name} has a custom JsonConverter");
                }

                var name = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                    ?? options.PropertyNamingPolicy?.ConvertName(p.Name) ?? p.Name;
                if (!IsIdentifier(name))
                {
                    throw new CcfUnsupportedTypeException($"{type.Name}.{p.Name}: name \"{name}\" is not an identifier");
                }

                if (!names.Add(name))
                {
                    throw new CcfUnsupportedTypeException($"{type.Name}: two properties map to the name \"{name}\"");
                }

                var order = p.GetCustomAttribute<JsonPropertyOrderAttribute>()?.Order ?? 0;
                found.Add((new CcfMember(p, name, conditional, context.Create(p)), order));
            }
        }

        // Stable sort: LINQ OrderBy is stable.
        return found.OrderBy(f => f.Order).Select(f => f.Member).ToArray();
    }

    /// <summary>The member names of an enum in declaration order, honouring [JsonStringEnumMemberName] when present.</summary>
    public static (string Name, FieldInfo Field)[] EnumMembers(Type enumType)
    {
        var fields = enumType.GetFields(BindingFlags.Public | BindingFlags.Static);
        var result = new (string, FieldInfo)[fields.Length];
        for (var i = 0; i < fields.Length; i++)
        {
            // JsonStringEnumMemberNameAttribute exists from .NET 9; look it up by name so net8.0 still builds.
            var name = fields[i].Name;
            foreach (var attr in fields[i].GetCustomAttributes())
            {
                if (attr.GetType().FullName == "System.Text.Json.Serialization.JsonStringEnumMemberNameAttribute"
                    && attr.GetType().GetProperty("Name")?.GetValue(attr) is string custom)
                {
                    name = custom;
                }
            }

            result[i] = (name, fields[i]);
        }

        return result;
    }
}
