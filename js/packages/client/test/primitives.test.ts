import { createHash, randomBytes } from "node:crypto";
import { describe, expect, it, vi } from "vitest";
import { CcfFormatError, sha256, sha256Fallback } from "../src/index.js";
import { Reader, enumBitWidth, packedByteCount, readPacked, unZigZag } from "../src/reader.js";
import { toHex } from "../src/sha256.js";
import { uv, zz } from "./support/payload.js";

const hex = (s: string) => Uint8Array.from(s.replace(/ /g, "").match(/../g)!.map((h) => parseInt(h, 16)));

describe("uvarint (SPEC §5.1)", () => {
  it.each([
    [0, "00"],
    [127, "7f"],
    [128, "80 01"],
    [500, "f4 03"],
    [16384, "80 80 01"],
    [2n ** 64n - 1n, "ff ff ff ff ff ff ff ff ff 01"],
    [2n ** 63n, "80 80 80 80 80 80 80 80 80 01"],
    [2n ** 63n - 1n, "ff ff ff ff ff ff ff ff 7f"],
    [2n ** 53n - 1n, "ff ff ff ff ff ff ff 0f"],
    [2n ** 53n, "80 80 80 80 80 80 80 10"],
  ])("reads %s from %s", (value, bytes) => {
    const r = new Reader(hex(bytes));
    const v = r.readUVarintExact();
    expect(BigInt(v)).toBe(BigInt(value));
    expect(typeof v).toBe(BigInt(value) <= BigInt(Number.MAX_SAFE_INTEGER) ? "number" : "bigint");
    expect(r.remaining).toBe(0);
    expect(Buffer.from(uv(value)).toString("hex")).toBe(bytes.replace(/ /g, ""));
  });

  it("reports values above 2^53-1 as Infinity when used as a length", () => {
    expect(new Reader(hex("ff ff ff ff ff ff ff ff ff 01")).readUVarint()).toBe(Infinity);
    expect(new Reader(hex("f4 03")).readUVarint()).toBe(500);
  });

  it("rejects an 11th byte and values above 2^64-1 with VarintOverflow", () => {
    for (const bytes of ["80 80 80 80 80 80 80 80 80 80 01", "ff ff ff ff ff ff ff ff ff 02", "ff ff ff ff ff ff ff ff ff ff"]) {
      expect(() => new Reader(hex(bytes)).readUVarintExact()).toThrowError(expect.objectContaining({ code: "VarintOverflow" }));
    }
  });

  it("accepts non-shortest encodings", () => {
    expect(new Reader(hex("84 80 00")).readUVarintExact()).toBe(4);
  });
});

describe("zigzag (SPEC §5.2)", () => {
  it.each([
    [0, 0],
    [1, -1],
    [2, 1],
    [2002, 1001],
    [127, -64],
  ])("unzigzag(%s) = %s", (u, v) => expect(unZigZag(u)).toBe(v));

  it("handles the int64 extremes through bigint", () => {
    expect(unZigZag(2n ** 64n - 1n)).toBe(-(2n ** 63n));
    expect(unZigZag(2n ** 64n - 2n)).toBe(2n ** 63n - 1n);
    expect(unZigZag(2n ** 53n)).toBe(2 ** 52);
  });

  it("round-trips through zz()", () => {
    for (const v of [0, 1, -1, 1001, -1001, 2 ** 40, -(2 ** 40), 2n ** 62n, -(2n ** 62n), 2n ** 63n - 1n, -(2n ** 63n)]) {
      expect(BigInt(unZigZag(uvOf(zz(v))))).toBe(BigInt(v));
    }
    expect(Buffer.from(zz(1001)).toString("hex")).toBe("d20f");
  });
});

function uvOf(bytes: number[]): number | bigint {
  return new Reader(Uint8Array.from(bytes)).readUVarintExact();
}

describe("bit packing (SPEC §5.3)", () => {
  it("is LSB-first", () => {
    // 3-bit values 1, 2, 7 → byte0 = 0b11_010_001, byte1 = 0b0000_0001
    const packed = hex("d1 01");
    expect([0, 1, 2].map((i) => readPacked(packed, i, 3))).toEqual([1, 2, 7]);
  });

  it.each([1, 2, 3, 5, 8, 13, 16])("round-trips width %s", (width) => {
    const max = 2 ** width;
    const values = Array.from({ length: 37 }, (_, i) => ((i * 2654435761) >>> 7) % max);
    const packed = new Uint8Array(packedByteCount(values.length, width));
    values.forEach((v, i) => {
      for (let b = 0; b < width; b++) if ((v >> b) & 1) packed[(i * width + b) >> 3]! |= 1 << ((i * width + b) & 7);
    });
    expect(values.map((_, i) => readPacked(packed, i, width))).toEqual(values);
  });

  it.each([
    [1, 1],
    [2, 1],
    [3, 2],
    [4, 2],
    [5, 3],
    [256, 8],
    [257, 9],
    [65536, 16],
  ])("enum of %s values needs %s bits", (count, width) => expect(enumBitWidth(count)).toBe(width));
});

describe("reader scoping (SPEC §14.1)", () => {
  it("raises Truncated at payload level and LengthOverrun inside a block", () => {
    const r = new Reader(Uint8Array.from([0x00, 0x03, 0x01, 0x02, 0x03, 0x09]));
    const scope = r.enterBlock(false); // mode 0, length 3: body = 01 02 03
    expect(() => r.readBytes(4)).toThrowError(expect.objectContaining({ code: "LengthOverrun" })); // a 9 follows in the payload
    r.pos = 5; // end of the block body
    r.exitBlock(scope);
    expect(r.readByte()).toBe(9);
    expect(() => r.readByte()).toThrowError(expect.objectContaining({ code: "Truncated" }));
  });

  it("rejects a block longer than the payload with Truncated and unknown modes with InvalidMode", () => {
    expect(() => new Reader(Uint8Array.from([0, 9, 1])).enterBlock(false)).toThrowError(expect.objectContaining({ code: "Truncated" }));
    expect(() => new Reader(Uint8Array.from([2, 0])).enterBlock(true)).toThrowError(expect.objectContaining({ code: "InvalidMode" }));
    expect(() => new Reader(Uint8Array.from([1, 0])).enterBlock(false)).toThrowError(expect.objectContaining({ code: "InvalidMode" }));
  });

  it("only CcfFormatError comes out of the reader", () => {
    expect(() => new Reader(new Uint8Array(0)).readByte()).toThrow(CcfFormatError);
  });
});

describe("SHA-256", () => {
  const cases = ["", "abc", "a".repeat(55), "a".repeat(56), "a".repeat(63), "a".repeat(64), "a".repeat(65), "héllo ✓ 日本語".repeat(40)];

  it.each(cases.map((c) => [c.length > 20 ? `${c.slice(0, 8)}…(${c.length})` : c, c] as const))("fallback matches node:crypto for %j", (_label, text) => {
    const bytes = new TextEncoder().encode(text);
    expect(toHex(sha256Fallback(bytes))).toBe(createHash("sha256").update(bytes).digest("hex"));
  });

  it("matches the published test vectors", () => {
    expect(toHex(sha256Fallback(new Uint8Array(0)))).toBe("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    expect(toHex(sha256Fallback(new TextEncoder().encode("abc")))).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  });

  it("fallback matches node:crypto for random inputs of every length up to 300 bytes", () => {
    for (let n = 0; n <= 300; n++) {
      const bytes = new Uint8Array(randomBytes(n));
      expect(toHex(sha256Fallback(bytes))).toBe(createHash("sha256").update(bytes).digest("hex"));
    }
  });

  it("uses WebCrypto when present and the fallback when crypto.subtle is missing", async () => {
    const bytes = new TextEncoder().encode("ccf");
    const expected = createHash("sha256").update(bytes).digest("hex");
    expect(toHex(await sha256(bytes))).toBe(expected);

    vi.stubGlobal("crypto", {}); // an insecure browser context: no crypto.subtle
    try {
      expect(toHex(await sha256(bytes))).toBe(expected);
    } finally {
      vi.unstubAllGlobals();
    }
  });
});
