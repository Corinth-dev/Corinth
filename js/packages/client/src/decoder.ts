import { CcfFormatError } from "./errors.js";
import { type BlockScope, Reader, enumBitWidth, packedByteCount, readPacked, unZigZag } from "./reader.js";
import type { CcfField, CcfSchema } from "./schema.js";

/** Limits of SPEC §9. */
export interface CcfLimits {
  /** Largest payload in bytes. Default 32 MiB. */
  maxPayloadBytes: number;
  /** Most rows in any one table, root or child. Default 100,000. */
  maxRows: number;
  /** Longest single string or dictionary entry in bytes. Default 1 MiB. */
  maxStringBytes: number;
}

export const defaultLimits: Readonly<CcfLimits> = Object.freeze({
  maxPayloadBytes: 32 * 1024 * 1024,
  maxRows: 100_000,
  maxStringBytes: 1024 * 1024,
});

const MAX_DICTIONARY_ENTRIES = 65_536;
const MAX_DEPTH = 8;

/** Options of {@link decode}. */
export interface DecodeOptions {
  /**
   * How int64 and uint64 values are returned. `"number"` (default) throws a `RangeError` for a value beyond
   * `Number.MAX_SAFE_INTEGER`; `"bigint"` returns every int64/uint64 as a `bigint`.
   */
  int64?: "number" | "bigint";
  /** How decimals are returned: `"number"` (default, may round) or `"string"` (exact, with `scale` digits after the point). */
  decimal?: "number" | "string";
  /** Overrides of the limits of SPEC §9. */
  limits?: Partial<CcfLimits>;
}

const INT64_MIN = -(2n ** 63n);
const INT64_MAX = 2n ** 63n - 1n;
const UINT64_MAX = 2n ** 64n - 1n;
const MAX_SAFE_BIG = BigInt(Number.MAX_SAFE_INTEGER);
/** Range of `Date`: ±8.64e15 milliseconds. */
const DATE_LIMIT = 8.64e15;

const HEX = Array.from({ length: 256 }, (_, i) => i.toString(16).padStart(2, "0"));
const utf8 = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });

const valueOutOfRange = (what: string): CcfFormatError => new CcfFormatError("ValueOutOfRange", what);

interface Context {
  readonly limits: CcfLimits;
  readonly int64Bigint: boolean;
  readonly decimalString: boolean;
}

/** The decoded value of a row table is an array of objects, or one object when header flag `S` is set. */
export function decode<T = unknown>(payload: Uint8Array, schema: CcfSchema, options: DecodeOptions = {}): T {
  const limits: CcfLimits = { ...defaultLimits, ...options.limits };
  if (payload.length > limits.maxPayloadBytes) throw new CcfFormatError("LimitExceeded", "payload larger than the payload limit");

  const r = new Reader(payload);
  const header = readHeader(r, schema.id, limits);
  if (schema.depth > MAX_DEPTH) throw new CcfFormatError("LimitExceeded", "schema nesting deeper than 8");

  const ctx: Context = { limits, int64Bigint: options.int64 === "bigint", decimalString: options.decimal === "string" };
  const columns = readTable(r, schema.fields, header.rows, ctx);
  if (r.remaining !== 0) throw new CcfFormatError("TrailingData", `${r.remaining} bytes after the root table`);

  const rows = buildRows(columns, schema.fields, header.rows);
  return (header.single ? rows[0] : rows) as T;
}

/**
 * Reads the schema id (8 lowercase hex characters) from a payload header so the schema can be looked up before
 * decoding. Validates magic, version and flags like the decoder.
 */
export function readSchemaId(payload: Uint8Array): string {
  const r = new Reader(payload);
  readHeader(r, null, { ...defaultLimits, maxRows: Infinity });
  return HEX[payload[4]!]! + HEX[payload[5]!]! + HEX[payload[6]!]! + HEX[payload[7]!]!;
}

interface Header {
  readonly single: boolean;
  readonly rows: number;
}

function readHeader(r: Reader, expectedId: Uint8Array | null, limits: CcfLimits): Header {
  const magic = r.readBytes(2);
  if (magic[0] !== 0x43 || magic[1] !== 0x43) throw new CcfFormatError("BadMagic");
  const version = r.readByte();
  if (version !== 1) throw new CcfFormatError("UnsupportedVersion", `version ${version}`);
  const flags = r.readByte();
  if ((flags & 0xfc) !== 0) throw new CcfFormatError("ReservedFlags", `flags ${flags.toString(16).padStart(2, "0")}`);
  const id = r.readBytes(4);
  if (expectedId !== null) {
    for (let i = 0; i < 4; i++) if (id[i] !== expectedId[i]) throw new CcfFormatError("SchemaMismatch");
  }
  const rows = r.readUVarint();
  const single = (flags & 2) !== 0;
  if (single && rows !== 1) throw new CcfFormatError("SingleObjectRowCount", `row count ${rows}`);
  if (rows > limits.maxRows) throw new CcfFormatError("LimitExceeded", `row count ${rows}`);
  return { single, rows };
}

/** Reads one column array per field; absent values are `null`. */
function readTable(r: Reader, fields: readonly CcfField[], rows: number, ctx: Context): unknown[][] {
  if (rows > ctx.limits.maxRows) throw new CcfFormatError("LimitExceeded", `${rows} rows in one table`);
  const columns: unknown[][] = new Array(fields.length);
  for (let i = 0; i < fields.length; i++) columns[i] = readColumn(r, fields[i]!, rows, ctx);
  return columns;
}

const UNSAFE_KEYS = new Set(["__proto__"]);

function buildRows(columns: unknown[][], fields: readonly CcfField[], rows: number): Record<string, unknown>[] {
  const names = fields.map((f) => f.name);
  const hasUnsafe = names.some((n) => UNSAFE_KEYS.has(n));
  const out: Record<string, unknown>[] = new Array(rows);
  for (let i = 0; i < rows; i++) {
    const o: Record<string, unknown> = {};
    for (let c = 0; c < names.length; c++) {
      if (hasUnsafe && UNSAFE_KEYS.has(names[c]!)) {
        Object.defineProperty(o, names[c]!, { value: columns[c]![i], enumerable: true, writable: true, configurable: true });
      } else {
        o[names[c]!] = columns[c]![i];
      }
    }
    out[i] = o;
  }
  return out;
}

function readColumn(r: Reader, field: CcfField, rows: number, ctx: Context): unknown[] {
  const scope = r.enterBlock(field.type === "string");
  let present: Uint8Array | null = null;
  let p = rows;

  if (field.nullable) {
    const bitmap = r.readBytes(Math.ceil(rows / 8));
    present = new Uint8Array(rows);
    p = 0;
    for (let i = 0; i < rows; i++) {
      if ((bitmap[i >> 3]! >> (i & 7)) & 1) {
        present[i] = 1;
        p++;
      }
    }
  }

  let values: unknown[];
  switch (field.type) {
    case "bool": values = readBools(r, rows, present, p); break;
    case "int32": case "uint32": case "int64": case "uint64": case "decimal": case "datetime":
      values = readIntegers(r, field, rows, present, p, ctx); break;
    case "float64": values = readFloats(r, rows, present, p); break;
    case "guid": values = readGuids(r, rows, present, p); break;
    case "enum": values = readEnum(r, field, rows, present, p); break;
    case "string": values = scope.mode === 1 ? readDictionary(r, rows, present, p, ctx) : readPlainStrings(r, rows, present, p, ctx); break;
    case "object": values = readObjects(r, field, rows, present, p, ctx); break;
    case "list": values = readLists(r, field, rows, present, p, ctx); break;
  }

  r.exitBlock(scope satisfies BlockScope);
  return values;
}

function emptyColumn(rows: number): unknown[] {
  return new Array(rows).fill(null);
}

function readBools(r: Reader, rows: number, present: Uint8Array | null, p: number): unknown[] {
  const packed = r.readBytes(packedByteCount(p, 1));
  const out = emptyColumn(rows);
  let k = 0;
  for (let i = 0; i < rows; i++) {
    if (present === null || present[i]) {
      out[i] = ((packed[k >> 3]! >> (k & 7)) & 1) === 1;
      k++;
    }
  }
  return out;
}

function readFloats(r: Reader, rows: number, present: Uint8Array | null, p: number): unknown[] {
  r.require(8 * p);
  const out = emptyColumn(rows);
  const view = new DataView(r.data.buffer, r.data.byteOffset, r.data.byteLength);
  for (let i = 0; i < rows; i++) {
    if (present === null || present[i]) {
      out[i] = view.getFloat64(r.pos, true);
      r.pos += 8;
    }
  }
  return out;
}

function readGuids(r: Reader, rows: number, present: Uint8Array | null, p: number): unknown[] {
  r.require(16 * p);
  const out = emptyColumn(rows);
  for (let i = 0; i < rows; i++) {
    if (present === null || present[i]) {
      const b = r.readBytes(16);
      const h = (n: number) => HEX[b[n]!]!;
      out[i] = `${h(0)}${h(1)}${h(2)}${h(3)}-${h(4)}${h(5)}-${h(6)}${h(7)}-${h(8)}${h(9)}-${h(10)}${h(11)}${h(12)}${h(13)}${h(14)}${h(15)}`;
    }
  }
  return out;
}

function readEnum(r: Reader, field: CcfField, rows: number, present: Uint8Array | null, p: number): unknown[] {
  const values = field.values!;
  const width = enumBitWidth(values.length);
  const packed = r.readBytes(packedByteCount(p, width));
  const out = emptyColumn(rows);
  let k = 0;
  for (let i = 0; i < rows; i++) {
    if (present === null || present[i]) {
      const index = readPacked(packed, k++, width);
      if (index >= values.length) throw new CcfFormatError("IndexOutOfRange", `enum index ${index} of ${values.length}`);
      out[i] = values[index]!;
    }
  }
  return out;
}

// ---- integers ------------------------------------------------------------------------------------------

/** a + b for values that are `number` when safe and `bigint` otherwise; the result is normalised the same way. */
function add(a: number | bigint, b: number | bigint): number | bigint {
  if (typeof a === "number" && typeof b === "number") {
    const s = a + b;
    if (Number.isSafeInteger(s)) return s;
  }
  const s = BigInt(a) + BigInt(b);
  return s >= -MAX_SAFE_BIG && s <= MAX_SAFE_BIG ? Number(s) : s;
}

const isNegative = (v: number | bigint): boolean => v < 0;

function checkInt64(v: number | bigint): void {
  if (typeof v === "bigint" && (v < INT64_MIN || v > INT64_MAX)) throw valueOutOfRange("int64 value out of range");
}

function readIntegers(r: Reader, field: CcfField, rows: number, present: Uint8Array | null, p: number, ctx: Context): unknown[] {
  r.require(p); // every value takes at least one byte
  const out = emptyColumn(rows);
  const type = field.type;
  const delta = field.delta;
  let current: number | bigint = 0;

  for (let i = 0; i < rows; i++) {
    if (present !== null && !present[i]) continue;
    const raw = r.readUVarintExact();
    let v: number | bigint;

    switch (type) {
      case "uint32":
        if (delta) {
          current = add(current, unZigZag(raw));
          v = current;
        } else v = raw;
        if (typeof v !== "number" || v < 0 || v > 4294967295) throw valueOutOfRange("uint32 value out of range");
        out[i] = v;
        break;
      case "int32":
        v = delta ? (current = add(current, unZigZag(raw))) : unZigZag(raw);
        if (typeof v !== "number" || v < -2147483648 || v > 2147483647) throw valueOutOfRange("int32 value out of range");
        out[i] = v;
        break;
      case "uint64":
        if (delta) {
          current = add(current, unZigZag(raw));
          v = current;
          if (isNegative(v) || (typeof v === "bigint" && v > UINT64_MAX)) throw valueOutOfRange("uint64 delta out of range");
        } else v = raw;
        out[i] = ctx.int64Bigint ? BigInt(v) : bigToNumber(v, "uint64");
        break;
      case "int64":
        v = delta ? (current = add(current, unZigZag(raw))) : unZigZag(raw);
        checkInt64(v);
        out[i] = ctx.int64Bigint ? BigInt(v) : bigToNumber(v, "int64");
        break;
      case "decimal": {
        if (field.unsigned && !delta) {
          if (typeof raw === "bigint" && raw > INT64_MAX) throw valueOutOfRange("decimal value out of range");
          v = raw;
        } else {
          v = delta ? (current = add(current, unZigZag(raw))) : unZigZag(raw);
          checkInt64(v);
          if (field.unsigned && isNegative(v)) throw valueOutOfRange("unsigned decimal went negative");
        }
        out[i] = ctx.decimalString ? decimalToString(v, field.scale!) : Number(v) / 10 ** field.scale!;
        break;
      }
      default: { // datetime
        v = delta ? (current = add(current, unZigZag(raw))) : unZigZag(raw);
        checkInt64(v);
        if (typeof v !== "number" || v < -DATE_LIMIT || v > DATE_LIMIT) throw valueOutOfRange("datetime outside the range of Date");
        out[i] = new Date(v);
      }
    }
  }
  return out;
}

function bigToNumber(v: number | bigint, type: string): number {
  if (typeof v === "number") return v;
  throw new RangeError(`${type} value ${v} exceeds Number.MAX_SAFE_INTEGER; decode with { int64: "bigint" }`);
}

/** Exact decimal text with `scale` digits after the point. */
function decimalToString(scaled: number | bigint, scale: number): string {
  const negative = scaled < 0;
  let digits = (negative ? -scaled : scaled).toString();
  if (scale > 0 && digits.length <= scale) digits = "0".repeat(scale - digits.length + 1) + digits;
  const text = scale === 0 ? digits : `${digits.slice(0, digits.length - scale)}.${digits.slice(digits.length - scale)}`;
  return negative ? `-${text}` : text;
}

// ---- strings -------------------------------------------------------------------------------------------

function decodeUtf8(bytes: Uint8Array): string {
  if (bytes.length === 0) return "";
  try {
    return utf8.decode(bytes);
  } catch {
    throw new CcfFormatError("InvalidUtf8");
  }
}

function readPlainStrings(r: Reader, rows: number, present: Uint8Array | null, p: number, ctx: Context): unknown[] {
  r.require(p); // every length takes at least one byte
  const lengths = new Array<number>(p);
  let total = 0;
  for (let k = 0; k < p; k++) {
    const len = r.readUVarint();
    if (len > ctx.limits.maxStringBytes) throw new CcfFormatError("LimitExceeded", `string of ${len} bytes`);
    lengths[k] = len;
    total += len;
  }
  r.require(total);
  const out = emptyColumn(rows);
  let next = 0;
  for (let i = 0; i < rows; i++) {
    if (present === null || present[i]) out[i] = decodeUtf8(r.readBytes(lengths[next++]!));
  }
  return out;
}

function readDictionary(r: Reader, rows: number, present: Uint8Array | null, p: number, ctx: Context): unknown[] {
  const k = r.readUVarint();
  if (k > MAX_DICTIONARY_ENTRIES) throw new CcfFormatError("LimitExceeded", `dictionary of ${k} entries`);
  r.require(k); // every entry takes at least one length byte
  const entries = new Array<string>(k);
  for (let e = 0; e < k; e++) {
    const len = r.readUVarint();
    if (len > ctx.limits.maxStringBytes) throw new CcfFormatError("LimitExceeded", `dictionary entry of ${len} bytes`);
    entries[e] = decodeUtf8(r.readBytes(len));
  }
  if (p > 0 && k === 0) {
    r.readByte(); // the first index; with an empty dictionary it cannot be valid (SPEC §14.5)
    throw new CcfFormatError("IndexOutOfRange", "index into an empty dictionary");
  }
  const wide = k > 256;
  r.require(p * (wide ? 2 : 1));
  const out = emptyColumn(rows);
  for (let i = 0; i < rows; i++) {
    if (present !== null && !present[i]) continue;
    const index = wide ? r.readByte() | (r.readByte() << 8) : r.readByte();
    if (index >= k) throw new CcfFormatError("IndexOutOfRange", `dictionary index ${index} of ${k}`);
    out[i] = entries[index]!;
  }
  return out;
}

// ---- nested tables -------------------------------------------------------------------------------------

function readObjects(r: Reader, field: CcfField, rows: number, present: Uint8Array | null, p: number, ctx: Context): unknown[] {
  const children = buildRows(readTable(r, field.fields!, p, ctx), field.fields!, p);
  const out = emptyColumn(rows);
  let k = 0;
  for (let i = 0; i < rows; i++) if (present === null || present[i]) out[i] = children[k++];
  return out;
}

function readLists(r: Reader, field: CcfField, rows: number, present: Uint8Array | null, p: number, ctx: Context): unknown[] {
  r.require(p); // every count takes at least one byte
  const counts = new Array<number>(p);
  let sum: number | bigint = 0;
  for (let k = 0; k < p; k++) {
    const c = r.readUVarintExact();
    counts[k] = typeof c === "number" ? c : Infinity;
    sum = add(sum, c);
  }
  const total = r.readUVarintExact();
  if (sum !== total) throw new CcfFormatError("CountMismatch", `counts sum to ${sum}, total is ${total}`);
  if (typeof total !== "number" || total > ctx.limits.maxRows) throw new CcfFormatError("LimitExceeded", `${total} rows in one table`);

  const fields = field.fields!;
  const columns = readTable(r, fields, total, ctx);
  const isScalar = fields.length === 1 && fields[0]!.name === "$value";
  const elements = isScalar ? columns[0]! : buildRows(columns, fields, total);

  const out = emptyColumn(rows);
  let start = 0;
  let k = 0;
  for (let i = 0; i < rows; i++) {
    if (present === null || present[i]) {
      const count = counts[k++]!;
      out[i] = elements.slice(start, start + count);
      start += count;
    }
  }
  return out;
}
