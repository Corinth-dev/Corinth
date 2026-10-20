import { type CcfSchema, parseSchema } from "../../src/index.js";

/** uvarint bytes of a non-negative number or bigint. */
export function uv(value: number | bigint): number[] {
  let v = BigInt(value);
  const out: number[] = [];
  while (v >= 0x80n) {
    out.push(Number(v & 0x7fn) | 0x80);
    v >>= 7n;
  }
  out.push(Number(v));
  return out;
}

/** zigzag + uvarint bytes of a signed number or bigint. */
export function zz(value: number | bigint): number[] {
  const v = BigInt(value);
  return uv(BigInt.asUintN(64, (v << 1n) ^ (v >> 63n)));
}

export function cat(...parts: ArrayLike<number>[]): number[] {
  return parts.flatMap((p) => Array.from(p));
}

export function utf8(text: string): number[] {
  return Array.from(new TextEncoder().encode(text));
}

/** Parses a schema whose fields are given as the inner JSON of the `fields` array. */
export function schemaOf(fieldsJson: string): Promise<CcfSchema> {
  return parseSchema(`{"name":"T","fields":[${fieldsJson}]}`);
}

/** Hand-assembles payloads so tests can produce exactly the malformed bytes they need. */
export class PayloadBuilder {
  private readonly bytes: number[] = [];

  constructor(schema: CcfSchema, rows: number, flags = 0) {
    this.bytes.push(0x43, 0x43, 1, flags, ...schema.id, ...uv(rows));
  }

  raw(...bytes: number[]): this {
    this.bytes.push(...bytes);
    return this;
  }

  /** A column block with the correct length. */
  block(mode: number, ...body: number[]): this {
    return this.blockWithLength(mode, body.length, ...body);
  }

  /** A column block with an explicit (possibly wrong) declared length. */
  blockWithLength(mode: number, declaredLength: number, ...body: number[]): this {
    this.bytes.push(mode, ...uv(declaredLength), ...body);
    return this;
  }

  build(): Uint8Array {
    return Uint8Array.from(this.bytes);
  }
}
