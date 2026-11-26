using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Corinth;

/// <summary>Field types of SPEC §6.1.</summary>
public enum CcfType
{
    /// <summary><c>bool</c>.</summary>
    Bool,

    /// <summary><c>int32</c>.</summary>
    Int32,

    /// <summary><c>int64</c>.</summary>
    Int64,

    /// <summary><c>uint32</c>.</summary>
    UInt32,

    /// <summary><c>uint64</c>.</summary>
    UInt64,

    /// <summary><c>float64</c>.</summary>
    Float64,

    /// <summary><c>decimal</c> (scaled int64).</summary>
    Decimal,

    /// <summary><c>enum</c> (index into <see cref="CcfField.Values"/>).</summary>
    Enum,

    /// <summary><c>string</c>.</summary>
    String,

    /// <summary><c>datetime</c> (UTC milliseconds since the Unix epoch).</summary>
    DateTime,

    /// <summary><c>guid</c>.</summary>
    Guid,

    /// <summary><c>object</c> (nested record).</summary>
    Object,

    /// <summary><c>list</c> (counts plus child table).</summary>
    List,
}

/// <summary>One field of a schema (SPEC §3.1). Validated when a <see cref="CcfSchema"/> is created from it.</summary>
public sealed class CcfField
{
    /// <summary>Creates a field. Set the remaining properties with an object initializer.</summary>
    public CcfField(string name, CcfType type)
    {
        Name = name;
        Type = type;
    }

    /// <summary>The field name: an identifier, or exactly <c>$value</c> for the element of a scalar list.</summary>
    public string Name { get; }

    /// <summary>The field type.</summary>
    public CcfType Type { get; }

    /// <summary>The column carries a presence bitmap.</summary>
    public bool Nullable { get; init; }

    /// <summary>Values are stored as differences (SPEC §6.4).</summary>
    public bool Delta { get; init; }

    /// <summary>Decimal scaled values are non-negative.</summary>
    public bool Unsigned { get; init; }

    /// <summary>Decimal digits after the point (0–18); required for <see cref="CcfType.Decimal"/>.</summary>
    public int? Scale { get; init; }

    /// <summary>Enum member names in order; required for <see cref="CcfType.Enum"/>.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>Child fields; required for <see cref="CcfType.Object"/> and <see cref="CcfType.List"/>.</summary>
    public IReadOnlyList<CcfField>? Fields { get; init; }

    /// <summary>The name of the type in schema JSON.</summary>
    public string TypeName => CcfSchema.TypeNames[(int)Type];
}

/// <summary>
/// A CCF schema: ordered fields plus the canonical JSON text (SPEC §3.2) and its 4-byte id (SPEC §3.3).
/// </summary>
public sealed class CcfSchema
{
    internal static readonly string[] TypeNames =
    {
        "bool", "int32", "int64", "uint32", "uint64", "float64", "decimal", "enum", "string", "datetime", "guid",
        "object", "list",
    };

    private readonly byte[] _id;

    private CcfSchema(string name, IReadOnlyList<CcfField> fields)
    {
        Name = name;
        Fields = fields;
        Depth = ComputeDepth(fields);
        CanonicalJson = Canonicalize(name, fields);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson));
        _id = hash.AsSpan(0, 4).ToArray();
        IdHex = Convert.ToHexString(_id).ToLowerInvariant();
    }

    /// <summary>The type's name; informational.</summary>
    public string Name { get; }

    /// <summary>The root table's fields in order.</summary>
    public IReadOnlyList<CcfField> Fields { get; }

    /// <summary>The canonical schema text that is hashed and served by the schema endpoint.</summary>
    public string CanonicalJson { get; }

    /// <summary>The schema id as 8 lowercase hex characters.</summary>
    public string IdHex { get; }

    /// <summary>The 4 raw id bytes as written in the payload header.</summary>
    public ReadOnlySpan<byte> IdBytes => _id;

    /// <summary>Table nesting depth: 1 for the root, +1 per <c>object</c>/<c>list</c> level (SPEC §7.4).</summary>
    public int Depth { get; }

    /// <summary>Creates and validates a schema from fields built in code.</summary>
    /// <exception cref="CcfSchemaException">The schema is invalid (SPEC §14.3).</exception>
    public static CcfSchema Create(string name, IReadOnlyList<CcfField> fields)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(fields);
        ValidateFields(fields, "$", isListElement: false);
        return new CcfSchema(name, fields);
    }

    /// <summary>Parses and validates schema JSON (any whitespace and key order accepted).</summary>
    /// <exception cref="CcfSchemaException">The schema is invalid (SPEC §14.3).</exception>
    public static CcfSchema Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Parse(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Parses and validates schema JSON given as UTF-8.</summary>
    /// <exception cref="CcfSchemaException">The schema is invalid (SPEC §14.3).</exception>
    public static CcfSchema Parse(ReadOnlySpan<byte> utf8Json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(utf8Json.ToArray());
        }
        catch (JsonException e)
        {
            throw new CcfSchemaException("Schema is not valid JSON: " + e.Message, e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CcfSchemaException("Schema root must be an object.");
            }

            string? name = null;
            IReadOnlyList<CcfField>? fields = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in root.EnumerateObject())
            {
                if (!seen.Add(p.Name))
                {
                    throw new CcfSchemaException($"Duplicate key \"{p.Name}\" in schema root.");
                }

                switch (p.Name)
                {
                    case "name":
                        if (p.Value.ValueKind != JsonValueKind.String)
                        {
                            throw new CcfSchemaException("Schema \"name\" must be a string.");
                        }

                        name = p.Value.GetString()!;
                        break;
                    case "fields":
                        fields = ParseFields(p.Value, "$");
                        break;
                    default:
                        throw new CcfSchemaException($"Unknown key \"{p.Name}\" in schema root.");
                }
            }

            if (name is null)
            {
                throw new CcfSchemaException("Schema is missing \"name\".");
            }

            if (fields is null)
            {
                throw new CcfSchemaException("Schema is missing \"fields\".");
            }

            ValidateFields(fields, "$", isListElement: false);
            return new CcfSchema(name, fields);
        }
    }

    private static List<CcfField> ParseFields(JsonElement array, string path)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new CcfSchemaException($"{path}: \"fields\" must be an array.");
        }

        var list = new List<CcfField>(array.GetArrayLength());
        var i = 0;
        foreach (var e in array.EnumerateArray())
        {
            list.Add(ParseField(e, $"{path}.fields[{i++}]"));
        }

        return list;
    }

    private static CcfField ParseField(JsonElement e, string path)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            throw new CcfSchemaException($"{path}: a field must be an object.");
        }

        string? name = null;
        string? typeName = null;
        bool? nullable = null, delta = null, unsigned = null;
        int? scale = null;
        List<string>? values = null;
        List<CcfField>? children = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in e.EnumerateObject())
        {
            if (!keys.Add(p.Name))
            {
                throw new CcfSchemaException($"{path}: duplicate key \"{p.Name}\".");
            }

            var v = p.Value;
            switch (p.Name)
            {
                case "name":
                    name = RequireString(v, path, "name");
                    break;
                case "type":
                    typeName = RequireString(v, path, "type");
                    break;
                case "nullable":
                    nullable = RequireBool(v, path, "nullable");
                    break;
                case "delta":
                    delta = RequireBool(v, path, "delta");
                    break;
                case "unsigned":
                    unsigned = RequireBool(v, path, "unsigned");
                    break;
                case "scale":
                    if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var s))
                    {
                        throw new CcfSchemaException($"{path}: \"scale\" must be an integer.");
                    }

                    scale = s;
                    break;
                case "values":
                    if (v.ValueKind != JsonValueKind.Array)
                    {
                        throw new CcfSchemaException($"{path}: \"values\" must be an array.");
                    }

                    values = new List<string>(v.GetArrayLength());
                    foreach (var item in v.EnumerateArray())
                    {
                        values.Add(RequireString(item, path, "values[]"));
                    }

                    break;
                case "fields":
                    children = ParseFields(v, path);
                    break;
                default:
                    throw new CcfSchemaException($"{path}: unknown key \"{p.Name}\".");
            }
        }

        if (name is null)
        {
            throw new CcfSchemaException($"{path}: field is missing \"name\".");
        }

        if (typeName is null)
        {
            throw new CcfSchemaException($"{path} ({name}): field is missing \"type\".");
        }

        var typeIndex = Array.IndexOf(TypeNames, typeName);
        if (typeIndex < 0)
        {
            throw new CcfSchemaException($"{path} ({name}): unknown type \"{typeName}\".");
        }

        var type = (CcfType)typeIndex;

        // Inapplicable keys are rejected, not dropped (SPEC §14.3), even when they are `false`.
        void Applicable(string key, bool present, bool applies)
        {
            if (present && !applies)
            {
                throw new CcfSchemaException($"{path} ({name}): key \"{key}\" does not apply to type {typeName}.");
            }
        }

        Applicable("delta", delta is not null, type is CcfType.Int32 or CcfType.Int64 or CcfType.UInt32 or CcfType.UInt64
            or CcfType.Decimal or CcfType.DateTime);
        Applicable("unsigned", unsigned is not null, type == CcfType.Decimal);
        Applicable("scale", scale is not null, type == CcfType.Decimal);
        Applicable("values", values is not null, type == CcfType.Enum);
        Applicable("fields", children is not null, type is CcfType.Object or CcfType.List);

        return new CcfField(name, type)
        {
            Nullable = nullable ?? false,
            Delta = delta ?? false,
            Unsigned = unsigned ?? false,
            Scale = scale,
            Values = values,
            Fields = children,
        };
    }

    private static string RequireString(JsonElement v, string path, string key) =>
        v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new CcfSchemaException($"{path}: \"{key}\" must be a string.");

    private static bool RequireBool(JsonElement v, string path, string key) =>
        v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : throw new CcfSchemaException($"{path}: \"{key}\" must be a boolean.");

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || !(IsAsciiLetter(s[0]) || s[0] == '_'))
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!(IsAsciiLetter(c) || c == '_' || (c >= '0' && c <= '9')))
            {
                return false;
            }
        }

        return true;

        static bool IsAsciiLetter(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    }

    private static void ValidateFields(IReadOnlyList<CcfField> fields, string path, bool isListElement)
    {
        if (fields.Count == 0)
        {
            throw new CcfSchemaException($"{path}: a field list needs at least one field.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in fields)
        {
            if (f is null)
            {
                throw new CcfSchemaException($"{path}: null field.");
            }

            var fp = $"{path}.{f.Name}";
            if (f.Name == "$value")
            {
                // §7.3: `$value` is the single element field of a scalar list.
                if (!isListElement || fields.Count != 1)
                {
                    throw new CcfSchemaException($"{fp}: \"$value\" is only valid as the single field of a list.");
                }
            }
            else if (!IsIdentifier(f.Name))
            {
                throw new CcfSchemaException($"{fp}: invalid field name \"{f.Name}\".");
            }

            if (!names.Add(f.Name))
            {
                throw new CcfSchemaException($"{fp}: duplicate field name.");
            }

            if (!Enum.IsDefined(f.Type))
            {
                throw new CcfSchemaException($"{fp}: unknown type.");
            }

            var type = f.Type;
            Applies(f.Delta, "delta", type is CcfType.Int32 or CcfType.Int64 or CcfType.UInt32 or CcfType.UInt64
                or CcfType.Decimal or CcfType.DateTime);
            Applies(f.Unsigned, "unsigned", type == CcfType.Decimal);
            Applies(f.Scale is not null, "scale", type == CcfType.Decimal);
            Applies(f.Values is not null, "values", type == CcfType.Enum);
            Applies(f.Fields is not null, "fields", type is CcfType.Object or CcfType.List);

            switch (type)
            {
                case CcfType.Decimal:
                    if (f.Scale is not { } scale || scale < 0 || scale > 18)
                    {
                        throw new CcfSchemaException($"{fp}: decimal needs \"scale\" between 0 and 18.");
                    }

                    break;
                case CcfType.Enum:
                    if (f.Values is not { Count: > 0 and <= 65_536 } values)
                    {
                        throw new CcfSchemaException($"{fp}: enum needs 1 to 65,536 \"values\".");
                    }

                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var v in values)
                    {
                        if (v is null || !IsIdentifier(v))
                        {
                            throw new CcfSchemaException($"{fp}: invalid enum value \"{v}\".");
                        }

                        if (!seen.Add(v))
                        {
                            throw new CcfSchemaException($"{fp}: duplicate enum value \"{v}\".");
                        }
                    }

                    break;
                case CcfType.Object:
                case CcfType.List:
                    if (f.Fields is null)
                    {
                        throw new CcfSchemaException($"{fp}: {f.TypeName} needs \"fields\".");
                    }

                    ValidateFields(f.Fields, fp, isListElement: type == CcfType.List);
                    break;
            }

            void Applies(bool present, string key, bool applies)
            {
                if (present && !applies)
                {
                    throw new CcfSchemaException($"{fp}: key \"{key}\" does not apply to type {f.TypeName}.");
                }
            }
        }
    }

    private static int ComputeDepth(IReadOnlyList<CcfField> fields)
    {
        var max = 0;
        foreach (var f in fields)
        {
            if (f.Fields is not null)
            {
                max = Math.Max(max, ComputeDepth(f.Fields));
            }
        }

        return 1 + max;
    }

    private static string Canonicalize(string name, IReadOnlyList<CcfField> fields)
    {
        var sb = new StringBuilder();
        sb.Append("{\"name\":");
        AppendJsonString(sb, name);
        sb.Append(",\"fields\":");
        AppendFields(sb, fields);
        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendFields(StringBuilder sb, IReadOnlyList<CcfField> fields)
    {
        sb.Append('[');
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var f = fields[i];
            sb.Append("{\"name\":");
            AppendJsonString(sb, f.Name);
            sb.Append(",\"type\":\"").Append(f.TypeName).Append('"');
            if (f.Nullable)
            {
                sb.Append(",\"nullable\":true");
            }

            if (f.Delta)
            {
                sb.Append(",\"delta\":true");
            }

            if (f.Unsigned)
            {
                sb.Append(",\"unsigned\":true");
            }

            if (f.Type == CcfType.Decimal)
            {
                sb.Append(",\"scale\":").Append(f.Scale!.Value);
            }

            if (f.Type == CcfType.Enum)
            {
                sb.Append(",\"values\":[");
                for (var j = 0; j < f.Values!.Count; j++)
                {
                    if (j > 0)
                    {
                        sb.Append(',');
                    }

                    AppendJsonString(sb, f.Values[j]);
                }

                sb.Append(']');
            }

            if (f.Fields is not null)
            {
                sb.Append(",\"fields\":");
                AppendFields(sb, f.Fields);
            }

            sb.Append('}');
        }

        sb.Append(']');
    }

    /// <summary>JSON string with escaping only where JSON requires it (SPEC §3.2).</summary>
    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
