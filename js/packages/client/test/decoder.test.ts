import { describe, expect, it } from "vitest";
import { type CcfErrorCode, CcfFormatError, decode, parseSchema, readSchemaId } from "../src/index.js";
import { PayloadBuilder, cat, schemaOf, utf8, uv, zz } from "./support/payload.js";
import { readExpected, readPayload, readSchemaText, vectors } from "./support/vectors.js";

type Opts = Parameters<typeof decode>[2];

/** The error code a decode raises, failing the test if it succeeds or raises something else. */
function codeOf(payload: Uint8Array, schema: Awaited<ReturnType<typeof schemaOf>>, options?: Opts): CcfErrorCode {
  try {
    decode(payload, schema, options);
  } catch (e) {
    if (e instanceof CcfFormatError) return e.code;
    throw e;
  }
  throw new Error("decode succeeded");
}

const column = (rows: unknown, name: string): unknown[] => (rows as Record<string, unknown>[]).map((r) => r[name]);

describe("header", async () => {
  const one = await schemaOf('{"name":"a","type":"int32"}');

  it("reports an empty or one-byte payload as Truncated", () => {
    expect(codeOf(new Uint8Array(0), one)).toBe("Truncated");
    expect(codeOf(Uint8Array.from([0x43]), one)).toBe("Truncated");
  });

  it("reports a header cut anywhere as Truncated", () => {
    const full = new PayloadBuilder(one, 0).block(0).build();
    for (let cut = 2; cut < 9; cut++) expect(codeOf(full.slice(0, cut), one)).toBe("Truncated");
  });

  it.each([0x04, 0x08, 0x80, 0xfc])("rejects reserved flag bits (%s)", (flags) => {
    expect(codeOf(new PayloadBuilder(one, 0, flags).block(0).build(), one)).toBe("ReservedFlags");
  });

  it("requires row count 1 with the single-object flag", () => {
    expect(codeOf(new PayloadBuilder(one, 0, 2).block(0).build(), one)).toBe("SingleObjectRowCount");
    expect(codeOf(new PayloadBuilder(one, 2, 2).block(0, 0, 0).build(), one)).toBe("SingleObjectRowCount");
  });

  it("decodes a single object as an object", () => {
    expect(decode(new PayloadBuilder(one, 1, 2).block(0, 4).build(), one)).toEqual({ a: 2 });
  });

  it("checks magic, version, flags and schema id in that order", () => {
    const bad = Uint8Array.from([0x58, 0x43, 2, 0x80, 0, 0, 0, 0, 0, 0]);
    expect(codeOf(bad, one)).toBe("BadMagic");
    bad[0] = 0x43;
    expect(codeOf(bad, one)).toBe("UnsupportedVersion");
    bad[2] = 1;
    expect(codeOf(bad, one)).toBe("ReservedFlags");
    bad[3] = 0;
    expect(codeOf(bad, one)).toBe("SchemaMismatch");
  });

  it("readSchemaId returns the id and validates the header", () => {
    expect(readSchemaId(new PayloadBuilder(one, 0).block(0).build())).toBe(one.idHex);
    expect(() => readSchemaId(Uint8Array.from([1, 2, 1, 0, 0, 0, 0, 0, 0]))).toThrowError(expect.objectContaining({ code: "BadMagic" }));
    expect(() => readSchemaId(new PayloadBuilder(one, 0).block(0).build().slice(0, 6))).toThrowError(expect.objectContaining({ code: "Truncated" }));
  });
});

describe("SPEC §14.1 length errors", async () => {
  const one = await schemaOf('{"name":"a","type":"int32"}');
  const two = await schemaOf('{"name":"a","type":"int32"},{"name":"b","type":"int32"}');
  const obj = await schemaOf('{"name":"o","type":"object","fields":[{"name":"x","type":"int32"}]}');

  it("block length larger than the remaining payload is Truncated", () => {
    expect(codeOf(new PayloadBuilder(one, 1).blockWithLength(0, 5, 4).build(), one)).toBe("Truncated");
  });

  it("a block missing its mode or length byte is Truncated", () => {
    expect(codeOf(new PayloadBuilder(one, 1).build(), one)).toBe("Truncated");
    expect(codeOf(new PayloadBuilder(one, 1).raw(0).build(), one)).toBe("Truncated");
  });

  it("reading past the end of the block body is LengthOverrun even when more payload follows", () => {
    const payload = new PayloadBuilder(two, 2).block(0, 2).block(0, 2, 2).build();
    expect(codeOf(payload, two)).toBe("LengthOverrun");
  });

  it("a varint running off the end of its block is LengthOverrun", () => {
    expect(codeOf(new PayloadBuilder(one, 1).block(0, 0x80).raw(0x01).build(), one)).toBe("LengthOverrun");
  });

  it("a nested block longer than its enclosing block is LengthOverrun", () => {
    const payload = new PayloadBuilder(obj, 1).block(0, 0x00, 0x09, 0x00).raw(0, 0, 0, 0, 0, 0, 0, 0, 0, 0).build();
    expect(codeOf(payload, obj)).toBe("LengthOverrun");
    expect(codeOf(new PayloadBuilder(obj, 1).block(0).raw(0, 0).build(), obj)).toBe("LengthOverrun");
  });

  it("BlockLengthMismatch only when a body ends with bytes left over", () => {
    expect(codeOf(new PayloadBuilder(one, 1).block(0, 4, 0).build(), one)).toBe("BlockLengthMismatch");
    expect(codeOf(new PayloadBuilder(obj, 1).block(0, 0, 2, 4, 0).build(), obj)).toBe("BlockLengthMismatch");
  });

  it("bytes after the root table are TrailingData", () => {
    expect(codeOf(new PayloadBuilder(one, 1).block(0, 4).raw(0).build(), one)).toBe("TrailingData");
  });

  it.each([2, 0xff])("unknown mode %s is InvalidMode, and dictionary mode on a non-string is too", (mode) => {
    expect(codeOf(new PayloadBuilder(one, 0).block(mode).build(), one)).toBe("InvalidMode");
    expect(codeOf(new PayloadBuilder(one, 0).block(1).build(), one)).toBe("InvalidMode");
  });
});

describe("SPEC §14.2 delta decoding", async () => {
  const i64 = await schemaOf('{"name":"a","type":"int64","delta":true}');
  const i32 = await schemaOf('{"name":"a","type":"int32","delta":true}');
  const u32 = await schemaOf('{"name":"a","type":"uint32","delta":true}');
  const u64 = await schemaOf('{"name":"a","type":"uint64","delta":true}');
  const big: Opts = { int64: "bigint" };
  const body = (...diffs: (number | bigint)[]) => cat(...diffs.map(zz));

  it("int64 overflow is ValueOutOfRange", () => {
    const max = 2n ** 63n - 1n;
    const min = -(2n ** 63n);
    expect(codeOf(new PayloadBuilder(i64, 2).block(0, ...body(max, 1)).build(), i64, big)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(i64, 2).block(0, ...body(min, -1)).build(), i64, big)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(i64, 3).block(0, ...body(min, max, min)).build(), i64, big)).toBe("ValueOutOfRange");
  });

  it("int64 reaching the extremes exactly is accepted", () => {
    const max = 2n ** 63n - 1n;
    const min = -(2n ** 63n);
    const rows = decode(new PayloadBuilder(i64, 3).block(0, ...body(max, -max, min)).build(), i64, big);
    expect(column(rows, "a")).toEqual([max, 0n, min]);
  });

  it("int32 and uint32 leaving their range is ValueOutOfRange", () => {
    expect(codeOf(new PayloadBuilder(i32, 2).block(0, ...body(2147483647, 1)).build(), i32)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(i32, 2).block(0, ...body(-2147483648, -1)).build(), i32)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(u32, 2).block(0, ...body(1, -2)).build(), u32)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(u32, 2).block(0, ...body(4294967295, 1)).build(), u32)).toBe("ValueOutOfRange");
    const ok = decode(new PayloadBuilder(u32, 2).block(0, ...body(4294967295, -4294967295)).build(), u32);
    expect(column(ok, "a")).toEqual([4294967295, 0]);
  });

  it("uint64 overflow and underflow are ValueOutOfRange; the full range is reachable", () => {
    expect(codeOf(new PayloadBuilder(u64, 1).block(0, ...body(-1)).build(), u64, big)).toBe("ValueOutOfRange");
    const max63 = 2n ** 63n - 1n;
    const rows = decode(new PayloadBuilder(u64, 2).block(0, ...body(max63, max63)).build(), u64, big);
    expect(column(rows, "a")[1]).toBe(2n ** 64n - 2n);
    expect(codeOf(new PayloadBuilder(u64, 3).block(0, ...body(max63, max63, 2)).build(), u64, big)).toBe("ValueOutOfRange");
  });

  it("datetime and decimal deltas use int64 checked arithmetic", async () => {
    const dt = await schemaOf('{"name":"a","type":"datetime","delta":true}');
    const dec = await schemaOf('{"name":"a","type":"decimal","scale":2,"delta":true}');
    const max = 2n ** 63n - 1n;
    expect(codeOf(new PayloadBuilder(dt, 2).block(0, ...body(max, 1)).build(), dt)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(dec, 2).block(0, ...body(max, 1)).build(), dec)).toBe("ValueOutOfRange");
  });

  it("nulls are skipped in delta chains", async () => {
    const s = await schemaOf('{"name":"a","type":"int64","nullable":true,"delta":true}');
    const rows = decode(new PayloadBuilder(s, 3).block(0, 0b101, ...body(10, 3)).build(), s);
    expect(column(rows, "a")).toEqual([10, null, 13]);
  });

  it("plain int32/uint32 values outside their range are ValueOutOfRange", async () => {
    const a = await schemaOf('{"name":"a","type":"int32"}');
    const b = await schemaOf('{"name":"a","type":"uint32"}');
    expect(codeOf(new PayloadBuilder(a, 1).block(0, ...zz(2147483648)).build(), a)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(a, 1).block(0, ...zz(-2147483649)).build(), a)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(b, 1).block(0, ...uv(4294967296)).build(), b)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(b, 1).block(0, ...uv(2n ** 64n - 1n)).build(), b)).toBe("ValueOutOfRange");
  });
});

describe("SPEC §14.5 dictionary blocks", async () => {
  const one = await schemaOf('{"name":"s","type":"string"}');
  const nullable = await schemaOf('{"name":"s","type":"string","nullable":true}');

  it("k = 0 with p = 0 is valid", () => {
    expect(decode(new PayloadBuilder(one, 0).block(1, 0).build(), one)).toEqual([]);
    const rows = decode(new PayloadBuilder(nullable, 3).block(1, 0, 0).build(), nullable);
    expect(column(rows, "s")).toEqual([null, null, null]);
  });

  it("k = 0 with p > 0 fails IndexOutOfRange on the first index", () => {
    expect(codeOf(new PayloadBuilder(one, 2).block(1, 0, 0, 0).build(), one)).toBe("IndexOutOfRange");
  });

  it("an index equal to k is IndexOutOfRange", () => {
    expect(codeOf(new PayloadBuilder(one, 1).block(1, 1, 1, 97, 1).build(), one)).toBe("IndexOutOfRange");
  });

  it("validates structure only, not the encoder rule", () => {
    const distinct = decode(new PayloadBuilder(one, 2).block(1, 2, 1, 120, 1, 121, 1, 0).build(), one);
    expect(column(distinct, "s")).toEqual(["y", "x"]);
    const duplicates = decode(new PayloadBuilder(one, 2).block(1, 2, 1, 120, 1, 120, 0, 1).build(), one);
    expect(column(duplicates, "s")).toEqual(["x", "x"]);
  });

  it("more than 65,536 entries is LimitExceeded", () => {
    expect(codeOf(new PayloadBuilder(one, 1).block(1, ...uv(65537)).build(), one)).toBe("LimitExceeded");
  });

  it("257 entries use two-byte little-endian indexes", () => {
    const entries = Array.from({ length: 257 }, (_, i) => [1, "a".charCodeAt(0) + (i % 26)]).flat();
    const rows = decode(new PayloadBuilder(one, 1).block(1, ...uv(257), ...entries, 0x00, 0x01).build(), one);
    expect(column(rows, "s")).toEqual([String.fromCharCode("a".charCodeAt(0) + (256 % 26))]);
  });
});

describe("SPEC §14.6 limits", async () => {
  const list = await schemaOf('{"name":"l","type":"list","fields":[{"name":"$value","type":"int32"}]}');
  const one = await schemaOf('{"name":"a","type":"int32"}');
  const str = await schemaOf('{"name":"s","type":"string"}');

  it("the row limit applies to each table including list child tables", () => {
    const payload = new PayloadBuilder(list, 1).block(0, ...uv(3), ...uv(3), 0, 3, 2, 2, 2).build();
    expect(codeOf(payload, list, { limits: { maxRows: 2 } })).toBe("LimitExceeded");
    const rows = decode(payload, list, { limits: { maxRows: 3 } });
    expect(column(rows, "l")).toEqual([[1, 1, 1]]);
  });

  it("a root row count above the limit is LimitExceeded before any allocation", () => {
    const payload = new PayloadBuilder(one, 100_001).block(0).build();
    expect(codeOf(payload, one)).toBe("LimitExceeded");
    expect(codeOf(payload, one, { limits: { maxRows: 200_000 } })).toBe("LengthOverrun");
  });

  it("the payload limit", () => {
    const payload = new PayloadBuilder(one, 1).block(0, 4).build();
    expect(codeOf(payload, one, { limits: { maxPayloadBytes: payload.length - 1 } })).toBe("LimitExceeded");
    expect(decode(payload, one, { limits: { maxPayloadBytes: payload.length } })).toEqual([{ a: 2 }]);
  });

  it("the string limit, for plain strings and dictionary entries", () => {
    const plain = new PayloadBuilder(str, 1).block(0, 5, ...utf8("hello")).build();
    expect(codeOf(plain, str, { limits: { maxStringBytes: 4 } })).toBe("LimitExceeded");
    expect(decode(plain, str, { limits: { maxStringBytes: 5 } })).toEqual([{ s: "hello" }]);
    const dict = new PayloadBuilder(str, 1).block(1, 1, 5, ...utf8("hello"), 0).build();
    expect(codeOf(dict, str, { limits: { maxStringBytes: 4 } })).toBe("LimitExceeded");
  });

  it("a schema deeper than 8 is LimitExceeded", async () => {
    const nest = (depth: number) => {
      let inner = '{"name":"leaf","type":"int32"}';
      for (let i = 1; i < depth; i++) inner = `{"name":"o${i}","type":"object","fields":[${inner}]}`;
      return `{"name":"Deep","fields":[${inner}]}`;
    };
    const deep = await parseSchema(nest(9));
    expect(deep.depth).toBe(9);
    expect(codeOf(new PayloadBuilder(deep, 0).block(0).build(), deep)).toBe("LimitExceeded");
    expect((await parseSchema(nest(8))).depth).toBe(8);
  });
});

describe("other decoding rules", async () => {
  const str = await schemaOf('{"name":"s","type":"string"}');

  it.each([
    ["invalid byte", [0xff]],
    ["overlong NUL", [0xc0, 0x80]],
    ["encoded surrogate", [0xed, 0xa0, 0x80]],
    ["truncated sequence", [0xe2, 0x82]],
    ["above U+10FFFF", [0xf4, 0x90, 0x80, 0x80]],
  ])("rejects %s as InvalidUtf8", (_n, bytes) => {
    expect(codeOf(new PayloadBuilder(str, 1).block(0, bytes.length, ...bytes).build(), str)).toBe("InvalidUtf8");
  });

  it("keeps a BOM, NUL and supplementary characters", () => {
    const text = "﻿a\0b😀";
    const bytes = utf8(text);
    const rows = decode(new PayloadBuilder(str, 1).block(0, ...uv(bytes.length), ...bytes).build(), str);
    expect(column(rows, "s")).toEqual([text]);
  });

  it("CountMismatch when the list total differs from the sum of counts, including overflowing sums", async () => {
    const list = await schemaOf('{"name":"l","type":"list","fields":[{"name":"$value","type":"int32"}]}');
    expect(codeOf(new PayloadBuilder(list, 2).block(0, 1, 1, ...uv(3)).build(), list)).toBe("CountMismatch");
    const huge = 2n ** 64n - 1n;
    expect(codeOf(new PayloadBuilder(list, 2).block(0, ...uv(huge), ...uv(huge), 5).build(), list)).toBe("CountMismatch");
  });

  it("enum indexes at or above the count are IndexOutOfRange even when they fit the bit width", async () => {
    const three = await schemaOf('{"name":"e","type":"enum","values":["a","b","c"]}');
    expect(codeOf(new PayloadBuilder(three, 1).block(0, 0b11).build(), three)).toBe("IndexOutOfRange");
    const five = await schemaOf('{"name":"e","type":"enum","values":["a","b","c","d","e"]}');
    for (const bad of [5, 6, 7]) expect(codeOf(new PayloadBuilder(five, 1).block(0, bad).build(), five)).toBe("IndexOutOfRange");
    expect(decode(new PayloadBuilder(five, 1).block(0, 4).build(), five)).toEqual([{ e: "e" }]);
  });

  it("ignores unused high bits of packed bytes", async () => {
    const b = await schemaOf('{"name":"b","type":"bool"}');
    expect(column(decode(new PayloadBuilder(b, 3).block(0, 0b1111_1101).build(), b), "b")).toEqual([true, false, true]);
  });

  it("accepts non-shortest uvarints", async () => {
    const one = await schemaOf('{"name":"a","type":"int32"}');
    expect(decode(new PayloadBuilder(one, 1).block(0, 0x84, 0x80, 0x00).build(), one)).toEqual([{ a: 2 }]);
  });

  it("accepts any float64 bit pattern, keeping negative zero", async () => {
    const f = await schemaOf('{"name":"f","type":"float64"}');
    const bytes: number[] = [];
    const view = new DataView(new ArrayBuffer(8));
    for (const v of [NaN, Infinity, -Infinity, -0, Number.MIN_VALUE]) {
      view.setFloat64(0, v, true);
      bytes.push(...new Uint8Array(view.buffer));
    }
    const values = column(decode(new PayloadBuilder(f, 5).block(0, ...bytes).build(), f), "f") as number[];
    expect(values[0]).toBeNaN();
    expect(values[1]).toBe(Infinity);
    expect(values[2]).toBe(-Infinity);
    expect(Object.is(values[3], -0)).toBe(true);
    expect(values[4]).toBe(Number.MIN_VALUE);
  });

  it("datetime outside the range of Date is ValueOutOfRange; the extremes are accepted", async () => {
    const d = await schemaOf('{"name":"d","type":"datetime"}');
    expect(codeOf(new PayloadBuilder(d, 1).block(0, ...zz(8.64e15 + 1)).build(), d)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(d, 1).block(0, ...zz(-8.64e15 - 1)).build(), d)).toBe("ValueOutOfRange");
    expect(codeOf(new PayloadBuilder(d, 1).block(0, ...zz(2n ** 62n)).build(), d)).toBe("ValueOutOfRange");
    const rows = decode(new PayloadBuilder(d, 2).block(0, ...zz(-8.64e15), ...zz(8.64e15)).build(), d);
    expect((column(rows, "d") as Date[]).map((d) => d.getTime())).toEqual([-8.64e15, 8.64e15]);
  });

  it("SPEC §14.8: accepts at least 0001-01-01 to 9999-12-31 UTC", async () => {
    const d = await schemaOf('{"name":"d","type":"datetime"}');
    const year1 = -62135596800000;
    const last = 253402300799999;
    const rows = decode(new PayloadBuilder(d, 2).block(0, ...zz(year1), ...zz(last)).build(), d);
    expect((column(rows, "d") as Date[]).map((x) => x.toISOString())).toEqual(["0001-01-01T00:00:00.000Z", "9999-12-31T23:59:59.999Z"]);
  });

  it("an unsigned decimal going negative through deltas is ValueOutOfRange", async () => {
    const s = await schemaOf('{"name":"d","type":"decimal","scale":2,"unsigned":true,"delta":true}');
    expect(codeOf(new PayloadBuilder(s, 2).block(0, ...zz(5), ...zz(-6)).build(), s)).toBe("ValueOutOfRange");
  });

  it("nested nulls, empty and null lists appear as in the vectors", () => {
    const v = vectors.find((x) => x.name === "v04-orders-nested")!;
    return parseSchema(readSchemaText(v)).then((schema) => {
      const rows = decode(readPayload(v), schema, { int64: "bigint", decimal: "string" }) as Record<string, unknown>[];
      expect(rows[1]!["customer"]).toBeNull();
      expect(rows[1]!["lines"]).toEqual([]);
      expect(rows[1]!["tags"]).toBeNull();
      expect(rows[2]!["tags"]).toEqual([]);
      expect(readExpected(v)).toBeDefined();
    });
  });
});
