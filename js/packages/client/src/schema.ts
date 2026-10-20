import { CcfSchemaError } from "./errors.js";
import { sha256, toHex } from "./sha256.js";
import { type JsonValue, parseStrictJson } from "./strictJson.js";

/** Field types of SPEC §6.1. */
export type CcfType =
  | "bool" | "int32" | "int64" | "uint32" | "uint64" | "float64" | "decimal" | "enum" | "string" | "datetime" | "guid"
  | "object" | "list";

const TYPES: ReadonlySet<string> = new Set([
  "bool", "int32", "int64", "uint32", "uint64", "float64", "decimal", "enum", "string", "datetime", "guid", "object", "list",
]);

const DELTA_TYPES: ReadonlySet<string> = new Set(["int32", "int64", "uint32", "uint64", "decimal", "datetime"]);

/** One field of a schema (SPEC §3.1). */
export interface CcfField {
  readonly name: string;
  readonly type: CcfType;
  readonly nullable: boolean;
  readonly delta: boolean;
  readonly unsigned: boolean;
  /** Digits after the decimal point (decimal fields). */
  readonly scale?: number;
  /** Enum member names in order (enum fields). */
  readonly values?: readonly string[];
  /** Child fields (object and list fields). */
  readonly fields?: readonly CcfField[];
}

/** A validated schema with its canonical text and 4-byte id (SPEC §3.2, §3.3). */
export interface CcfSchema {
  readonly name: string;
  readonly fields: readonly CcfField[];
  /** The canonical schema text that is hashed. */
  readonly canonicalJson: string;
  /** The 4 id bytes as written in the payload header. */
  readonly id: Uint8Array;
  /** The id as 8 lowercase hex characters. */
  readonly idHex: string;
  /** Table nesting depth: 1 for the root, +1 per object/list level (SPEC §7.4). */
  readonly depth: number;
}

const IDENTIFIER = /^[A-Za-z_][A-Za-z0-9_]*$/;

/**
 * Parses and validates schema JSON and computes its id. Async because the id is a SHA-256 hash; decoding with the
 * resulting schema is synchronous.
 *
 * @throws CcfSchemaError the schema is invalid (SPEC §14.3).
 */
export async function parseSchema(json: string): Promise<CcfSchema> {
  const root = parseStrictJson(json);
  if (!isObject(root)) throw new CcfSchemaError("Schema root must be an object.");

  let name: string | undefined;
  let fieldsValue: JsonValue | undefined;
  for (const [key, value] of root.members) {
    if (key === "name") {
      if (typeof value !== "string") throw new CcfSchemaError('Schema "name" must be a string.');
      name = value;
    } else if (key === "fields") {
      fieldsValue = value;
    } else {
      throw new CcfSchemaError(`Unknown key "${key}" in schema root.`);
    }
  }
  if (name === undefined) throw new CcfSchemaError('Schema is missing "name".');
  if (fieldsValue === undefined) throw new CcfSchemaError('Schema is missing "fields".');

  const fields = parseFields(fieldsValue, "$");
  return createSchema(name, fields);
}

/** Validates fields built in code and computes canonical form and id. */
export async function createSchema(name: string, fields: readonly CcfField[]): Promise<CcfSchema> {
  validateFields(fields, "$", false);
  const canonicalJson = canonicalize(name, fields);
  const hash = await sha256(new TextEncoder().encode(canonicalJson));
  const id = hash.slice(0, 4);
  return { name, fields, canonicalJson, id, idHex: toHex(id), depth: depthOf(fields) };
}

function isObject(v: JsonValue | undefined): v is { members: [string, JsonValue][] } {
  return typeof v === "object" && v !== null && !Array.isArray(v) && "members" in v;
}

function parseFields(value: JsonValue, path: string): CcfField[] {
  if (!Array.isArray(value)) throw new CcfSchemaError(`${path}: "fields" must be an array.`);
  return value.map((f, i) => parseField(f, `${path}.fields[${i}]`));
}

function parseField(value: JsonValue, path: string): CcfField {
  if (!isObject(value)) throw new CcfSchemaError(`${path}: a field must be an object.`);

  let name: string | undefined;
  let typeName: string | undefined;
  let nullable: boolean | undefined;
  let delta: boolean | undefined;
  let unsigned: boolean | undefined;
  let scale: number | undefined;
  let values: string[] | undefined;
  let children: CcfField[] | undefined;

  const requireString = (v: JsonValue, key: string): string => {
    if (typeof v !== "string") throw new CcfSchemaError(`${path}: "${key}" must be a string.`);
    return v;
  };
  const requireBool = (v: JsonValue, key: string): boolean => {
    if (typeof v !== "boolean") throw new CcfSchemaError(`${path}: "${key}" must be a boolean.`);
    return v;
  };

  for (const [key, v] of value.members) {
    switch (key) {
      case "name": name = requireString(v, key); break;
      case "type": typeName = requireString(v, key); break;
      case "nullable": nullable = requireBool(v, key); break;
      case "delta": delta = requireBool(v, key); break;
      case "unsigned": unsigned = requireBool(v, key); break;
      case "scale": {
        if (typeof v !== "object" || v === null || Array.isArray(v) || !("number" in v) || !/^-?\d+$/.test(v.number)) {
          throw new CcfSchemaError(`${path}: "scale" must be an integer.`);
        }
        scale = Number(v.number);
        break;
      }
      case "values": {
        if (!Array.isArray(v)) throw new CcfSchemaError(`${path}: "values" must be an array.`);
        values = v.map((item) => requireString(item, "values[]"));
        break;
      }
      case "fields": children = parseFields(v, path); break;
      default: throw new CcfSchemaError(`${path}: unknown key "${key}".`);
    }
  }

  if (name === undefined) throw new CcfSchemaError(`${path}: field is missing "name".`);
  if (typeName === undefined) throw new CcfSchemaError(`${path} (${name}): field is missing "type".`);
  if (!TYPES.has(typeName)) throw new CcfSchemaError(`${path} (${name}): unknown type "${typeName}".`);

  // Inapplicable keys are rejected, not dropped (SPEC §14.3), even when they are `false`.
  const applicable = (key: string, present: boolean, applies: boolean) => {
    if (present && !applies) throw new CcfSchemaError(`${path} (${name}): key "${key}" does not apply to type ${typeName}.`);
  };
  applicable("delta", delta !== undefined, DELTA_TYPES.has(typeName));
  applicable("unsigned", unsigned !== undefined, typeName === "decimal");
  applicable("scale", scale !== undefined, typeName === "decimal");
  applicable("values", values !== undefined, typeName === "enum");
  applicable("fields", children !== undefined, typeName === "object" || typeName === "list");

  return {
    name,
    type: typeName as CcfType,
    nullable: nullable ?? false,
    delta: delta ?? false,
    unsigned: unsigned ?? false,
    ...(scale !== undefined ? { scale } : {}),
    ...(values !== undefined ? { values } : {}),
    ...(children !== undefined ? { fields: children } : {}),
  };
}

function validateFields(fields: readonly CcfField[], path: string, isListElement: boolean): void {
  if (fields.length === 0) throw new CcfSchemaError(`${path}: a field list needs at least one field.`);

  const names = new Set<string>();
  for (const f of fields) {
    const fp = `${path}.${f.name}`;
    if (f.name === "$value") {
      // §7.3: `$value` is the single element field of a scalar list.
      if (!isListElement || fields.length !== 1) {
        throw new CcfSchemaError(`${fp}: "$value" is only valid as the single field of a list.`);
      }
    } else if (!IDENTIFIER.test(f.name)) {
      throw new CcfSchemaError(`${fp}: invalid field name "${f.name}".`);
    }
    if (names.has(f.name)) throw new CcfSchemaError(`${fp}: duplicate field name.`);
    names.add(f.name);
    if (!TYPES.has(f.type)) throw new CcfSchemaError(`${fp}: unknown type.`);

    const applies = (present: boolean, key: string, ok: boolean) => {
      if (present && !ok) throw new CcfSchemaError(`${fp}: key "${key}" does not apply to type ${f.type}.`);
    };
    applies(f.delta, "delta", DELTA_TYPES.has(f.type));
    applies(f.unsigned, "unsigned", f.type === "decimal");
    applies(f.scale !== undefined, "scale", f.type === "decimal");
    applies(f.values !== undefined, "values", f.type === "enum");
    applies(f.fields !== undefined, "fields", f.type === "object" || f.type === "list");

    if (f.type === "decimal") {
      if (f.scale === undefined || !Number.isInteger(f.scale) || f.scale < 0 || f.scale > 18) {
        throw new CcfSchemaError(`${fp}: decimal needs "scale" between 0 and 18.`);
      }
    } else if (f.type === "enum") {
      if (f.values === undefined || f.values.length < 1 || f.values.length > 65536) {
        throw new CcfSchemaError(`${fp}: enum needs 1 to 65,536 "values".`);
      }
      const seen = new Set<string>();
      for (const v of f.values) {
        if (!IDENTIFIER.test(v)) throw new CcfSchemaError(`${fp}: invalid enum value "${v}".`);
        if (seen.has(v)) throw new CcfSchemaError(`${fp}: duplicate enum value "${v}".`);
        seen.add(v);
      }
    } else if (f.type === "object" || f.type === "list") {
      if (f.fields === undefined) throw new CcfSchemaError(`${fp}: ${f.type} needs "fields".`);
      validateFields(f.fields, fp, f.type === "list");
    }
  }
}

function depthOf(fields: readonly CcfField[]): number {
  let max = 0;
  for (const f of fields) if (f.fields !== undefined) max = Math.max(max, depthOf(f.fields));
  return 1 + max;
}

/** The canonical form of SPEC §3.2. */
export function canonicalize(name: string, fields: readonly CcfField[]): string {
  return `{"name":${jsonString(name)},"fields":${canonicalFields(fields)}}`;
}

function canonicalFields(fields: readonly CcfField[]): string {
  return "[" + fields.map(canonicalField).join(",") + "]";
}

function canonicalField(f: CcfField): string {
  let s = `{"name":${jsonString(f.name)},"type":"${f.type}"`;
  if (f.nullable) s += ',"nullable":true';
  if (f.delta) s += ',"delta":true';
  if (f.unsigned) s += ',"unsigned":true';
  if (f.type === "decimal") s += `,"scale":${f.scale}`;
  if (f.type === "enum") s += `,"values":[${f.values!.map(jsonString).join(",")}]`;
  if (f.fields !== undefined) s += `,"fields":${canonicalFields(f.fields)}`;
  return s + "}";
}

/** JSON string with escaping only where JSON requires it (SPEC §3.2). */
function jsonString(s: string): string {
  let out = '"';
  for (let i = 0; i < s.length; i++) {
    const c = s[i]!;
    switch (c) {
      case '"': out += '\\"'; break;
      case "\\": out += "\\\\"; break;
      case "\b": out += "\\b"; break;
      case "\f": out += "\\f"; break;
      case "\n": out += "\\n"; break;
      case "\r": out += "\\r"; break;
      case "\t": out += "\\t"; break;
      default:
        out += c < " " ? "\\u" + c.charCodeAt(0).toString(16).padStart(4, "0") : c;
    }
  }
  return out + '"';
}
