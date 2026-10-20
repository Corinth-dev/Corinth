import { type CcfFormatError, decode, parseSchema } from "../../src/index.js";

/** Parses schema JSON and returns its canonical text (SPEC §3.2). */
export async function canonicalize(schemaJson: string): Promise<string> {
  return (await parseSchema(schemaJson)).canonicalJson;
}

/** Returns the schema id as 8 lowercase hex characters (SPEC §3.3). */
export async function schemaIdHex(schemaJson: string): Promise<string> {
  return (await parseSchema(schemaJson)).idHex;
}

/**
 * Decodes `payload` with the schema and returns the value in the canonical JSON representation of
 * test-vectors/README.md (int64/uint64 as strings, decimal as exact strings, datetime as ISO strings).
 */
export async function decodeToCanonical(payload: Uint8Array, schemaJson: string): Promise<unknown> {
  const schema = await parseSchema(schemaJson);
  return toCanonical(decode(payload, schema, { int64: "bigint", decimal: "string" }));
}

/** The error code string (SPEC §11) carried by a decoder failure, e.g. `"BadMagic"`. */
export function errorCodeOf(error: unknown): string | undefined {
  return (error as CcfFormatError | undefined)?.name === "CcfFormatError"
    ? (error as CcfFormatError).code
    : `Unexpected ${(error as Error | undefined)?.name}: ${(error as Error | undefined)?.message}`;
}

/** Converts decoder output into the canonical JSON representation: bigint → decimal string, Date → ISO string. */
export function toCanonical(value: unknown): unknown {
  if (typeof value === "bigint") return value.toString();
  if (value instanceof Date) return value.toISOString();
  if (Array.isArray(value)) return value.map(toCanonical);
  if (value !== null && typeof value === "object") {
    const out: Record<string, unknown> = {};
    for (const [k, v] of Object.entries(value)) out[k] = toCanonical(v);
    return out;
  }
  return value;
}
