using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Corinth;

/// <summary>
/// How System.Text.Json writes an enum, mirrored so a CCF field and the JSON value of the same property agree:
/// as a string when a <see cref="JsonStringEnumConverter"/> applies (a <c>[JsonConverter]</c> on the property,
/// a converter in the serializer options, or a <c>[JsonConverter]</c> on the enum type, in STJ's order of
/// precedence), otherwise as its underlying integer.
/// </summary>
internal sealed class CcfEnumJson
{
    private CcfEnumJson(string[]? names, (string Name, long Value)[]? members)
    {
        Names = names;
        Members = members;
    }

    /// <summary>The distinct strings STJ writes, in declaration order; <c>null</c> when the enum is written as a number.</summary>
    public string[]? Names { get; }

    /// <summary>Each member's string and its numeric value (aliases share a string).</summary>
    public (string Name, long Value)[]? Members { get; }

    public bool IsString => Names is not null;

    /// <summary>The CCF integer type of a numeric enum (SPEC §3: sbyte/short/int → int32, byte/ushort/uint → uint32, …).</summary>
    public static CcfType NumericType(Type enumType)
    {
        var u = Enum.GetUnderlyingType(enumType);
        if (u == typeof(sbyte) || u == typeof(short) || u == typeof(int))
        {
            return CcfType.Int32;
        }

        if (u == typeof(byte) || u == typeof(ushort) || u == typeof(uint))
        {
            return CcfType.UInt32;
        }

        return u == typeof(long) ? CcfType.Int64 : CcfType.UInt64;
    }

    /// <exception cref="CcfUnsupportedTypeException">A converter other than STJ's string-enum converters applies, or the names cannot be used.</exception>
    public static CcfEnumJson Resolve(Type enumType, PropertyInfo? property, JsonSerializerOptions? options)
    {
        var converter = FindConverter(enumType, property, options, out var propertyConverter);
        if (converter is null)
        {
            return new CcfEnumJson(null, null);
        }

        var effective = options is null ? new JsonSerializerOptions() : new JsonSerializerOptions(options);
        if (propertyConverter is not null)
        {
            effective.Converters.Insert(0, propertyConverter);
        }

        var members = new List<(string, long)>();
        var names = new List<string>();
        foreach (var field in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var value = field.GetValue(null)!;
            var json = JsonSerializer.Serialize(value, enumType, effective);
            if (json.Length < 2 || json[0] != '"')
            {
                // The converter writes this member as a number (for example a value without a name): not a string enum.
                throw new CcfUnsupportedTypeException($"{enumType.Name}: member {field.Name} is not written as a string by its converter");
            }

            var name = JsonSerializer.Deserialize<string>(json)!;
            if (!names.Contains(name))
            {
                names.Add(name);
            }

            members.Add((name, unchecked(Convert.ToInt64(field.GetRawConstantValue()!))));
        }

        return new CcfEnumJson(names.ToArray(), members.ToArray());
    }

    private static Type? FindConverter(Type enumType, PropertyInfo? property, JsonSerializerOptions? options, out JsonConverter? propertyConverter)
    {
        propertyConverter = null;

        // 1. [JsonConverter] on the property.
        var onProperty = property?.GetCustomAttribute<JsonConverterAttribute>();
        if (onProperty is not null)
        {
            var type = onProperty.ConverterType
                ?? throw new CcfUnsupportedTypeException($"{enumType.Name}: a [JsonConverter] without a converter type");
            Classify(type, enumType);
            propertyConverter = (JsonConverter)Activator.CreateInstance(type)!;
            return type;
        }

        // 2. Converters in the serializer options (first that can convert wins).
        if (options is not null)
        {
            foreach (var converter in options.Converters)
            {
                if (converter.CanConvert(enumType))
                {
                    Classify(converter.GetType(), enumType);
                    return converter.GetType();
                }
            }
        }

        // 3. [JsonConverter] on the enum type.
        var onType = enumType.GetCustomAttribute<JsonConverterAttribute>();
        if (onType is not null)
        {
            var type = onType.ConverterType
                ?? throw new CcfUnsupportedTypeException($"{enumType.Name}: a [JsonConverter] without a converter type");
            Classify(type, enumType);
            return type;
        }

        return null;
    }

    private static void Classify(Type converterType, Type enumType)
    {
        for (var t = converterType; t is not null; t = t.BaseType)
        {
            if (t == typeof(JsonStringEnumConverter) || (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(JsonStringEnumConverter<>)))
            {
                return;
            }
        }

        throw new CcfUnsupportedTypeException($"{enumType.Name}: custom JsonConverter {converterType.Name}");
    }
}
