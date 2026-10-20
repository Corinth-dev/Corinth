import { CcfFormatError } from "./errors.js";

const MAX_SAFE = Number.MAX_SAFE_INTEGER;

/**
 * Bounds-checked reader over a payload. Reads are scoped to the payload or to the current column block body;
 * running past the scope is `Truncated` at payload level and `LengthOverrun` inside a block (SPEC §14.1).
 */
export class Reader {
  pos = 0;
  private end: number;
  private inBlock = false;

  constructor(readonly data: Uint8Array) {
    this.end = data.length;
  }

  get remaining(): number {
    return this.end - this.pos;
  }

  overrun(): CcfFormatError {
    return this.inBlock
      ? new CcfFormatError("LengthOverrun", "read past the end of the column block")
      : new CcfFormatError("Truncated", "read past the end of the payload");
  }

  /** Fails with the overrun error unless `count` bytes remain in the current scope. */
  require(count: number): void {
    if (count > this.end - this.pos) throw this.overrun();
  }

  readByte(): number {
    if (this.pos >= this.end) throw this.overrun();
    return this.data[this.pos++]!;
  }

  readBytes(count: number): Uint8Array {
    this.require(count);
    const s = this.data.subarray(this.pos, this.pos + count);
    this.pos += count;
    return s;
  }

  /**
   * Reads a uvarint. Returns a `number` when it is at most 2^53−1 and a `bigint` otherwise (up to 2^64−1).
   * Values of more than 10 bytes, or above 2^64−1, are `VarintOverflow`.
   */
  readUVarintExact(): number | bigint {
    // Fast path: a single byte.
    if (this.pos < this.end) {
      const b = this.data[this.pos]!;
      if (b < 0x80) {
        this.pos++;
        return b;
      }
    }

    let result = 0;
    let mul = 1;
    let big: bigint | null = null;
    for (let i = 0; i < 10; i++) {
      const b = this.readByte();
      if (i === 9 && b > 1) throw new CcfFormatError("VarintOverflow", "uvarint does not fit in 64 bits");
      const low = b & 0x7f;
      if (i < 7) {
        result += low * mul; // up to 49 bits: exact
        mul *= 128;
      } else {
        big ??= BigInt(result);
        big |= BigInt(low) << BigInt(7 * i);
      }
      if ((b & 0x80) === 0) {
        if (big === null) return result;
        return big <= BigInt(MAX_SAFE) ? Number(big) : big;
      }
    }
    throw new CcfFormatError("VarintOverflow", "uvarint longer than 10 bytes");
  }

  /** A uvarint used as a length, count or row number: values above 2^53−1 read as `Infinity`, which every limit rejects. */
  readUVarint(): number {
    const v = this.readUVarintExact();
    return typeof v === "number" ? v : Infinity;
  }

  /**
   * Reads a column block's mode byte and length and narrows the scope to the body (SPEC §4.3). Call
   * {@link exitBlock} with the result when the body has been read.
   */
  enterBlock(dictionaryAllowed: boolean): BlockScope {
    const mode = this.readByte();
    if (mode > 1 || (mode === 1 && !dictionaryAllowed)) throw new CcfFormatError("InvalidMode", `mode byte ${mode.toString(16).padStart(2, "0")}`);
    const length = this.readUVarint();
    if (length > this.end - this.pos) throw this.overrun();
    const scope: BlockScope = { mode, savedEnd: this.end, savedInBlock: this.inBlock, end: this.pos + length };
    this.end = scope.end;
    this.inBlock = true;
    return scope;
  }

  /** Verifies the body was consumed exactly and restores the enclosing scope. */
  exitBlock(scope: BlockScope): void {
    if (this.pos !== scope.end) {
      throw new CcfFormatError("BlockLengthMismatch", `${scope.end - this.pos} bytes left unconsumed in the block body`);
    }
    this.end = scope.savedEnd;
    this.inBlock = scope.savedInBlock;
  }
}

export interface BlockScope {
  readonly mode: number;
  readonly savedEnd: number;
  readonly savedInBlock: boolean;
  readonly end: number;
}

/** Value `index` of a `width`-bit LSB-first packed run (SPEC §5.3). */
export function readPacked(packed: Uint8Array, index: number, width: number): number {
  const bit = index * width;
  const byteIndex = bit >>> 3;
  const shift = bit & 7;
  let acc = packed[byteIndex]!;
  if (shift + width > 8) {
    acc |= packed[byteIndex + 1]! << 8;
    if (shift + width > 16) acc |= packed[byteIndex + 2]! << 16;
  }
  return (acc >>> shift) & ((1 << width) - 1);
}

/** Smallest `w ≥ 1` with 2^w ≥ `count` (SPEC §6.1, enum). */
export function enumBitWidth(count: number): number {
  let w = 1;
  while (2 ** w < count) w++;
  return w;
}

/** Bytes needed for `count` values of `bits` bits. */
export function packedByteCount(count: number, bits: number): number {
  return Math.ceil((count * bits) / 8);
}

const TWO_POW_53 = 2 ** 53;

/** Zigzag-decodes an unsigned value; `number` when the result fits 2^53−1 in magnitude, otherwise `bigint`. */
export function unZigZag(u: number | bigint): number | bigint {
  if (typeof u === "number") {
    // u < 2^53, so |result| < 2^52.
    return u % 2 === 0 ? u / 2 : -(u + 1) / 2;
  }
  const v = (u >> 1n) ^ -(u & 1n);
  return v >= -BigInt(MAX_SAFE) && v <= BigInt(MAX_SAFE) ? Number(v) : v;
}

export { TWO_POW_53 };
