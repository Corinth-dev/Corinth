import { describe, expect, it } from "vitest";
import { decode, parseSchema } from "../src/index.js";
import { PayloadBuilder, schemaOf, uv, zz } from "./support/payload.js";
import { readPayload, readSchemaText, vectors } from "./support/vectors.js";

const col = (rows: unknown, name: string): unknown[] => (rows as Record<string, unknown>[]).map((r) => r[name]);

describe("int64 / uint64 option (ARCHITECTURE §6.3)", async () => {
  const i64 = await schemaOf('{"name":"a","type":"int64"}');
  const u64 = await schemaOf('{"name":"a","type":"uint64"}');

  it("returns numbers by default when they are safe", () => {
    const payload = new PayloadBuilder(i64, 3).block(0, ...zz(-5), ...zz(2 ** 53 - 1), ...zz(-(2 ** 53 - 1))).build();
    expect(col(decode(payload, i64), "a")).toEqual([-5, 2 ** 53 - 1, -(2 ** 53 - 1)]);
  });

  it("throws a RangeError for a value beyond Number.MAX_SAFE_INTEGER, and bigint mode returns it exactly", () => {
    const payload = new PayloadBuilder(i64, 1).block(0, ...zz(2n ** 53n)).build();
    expect(() => decode(payload, i64)).toThrow(RangeError);
    expect(() => decode(payload, i64, { int64: "number" })).toThrow(RangeError);
    expect(col(decode(payload, i64, { int64: "bigint" }), "a")).toEqual([2n ** 53n]);

    const extremes = new PayloadBuilder(i64, 2).block(0, ...zz(-(2n ** 63n)), ...zz(2n ** 63n - 1n)).build();
    expect(col(decode(extremes, i64, { int64: "bigint" }), "a")).toEqual([-(2n ** 63n), 2n ** 63n - 1n]);
  });

  it("uint64 up to 2^64-1", () => {
    const payload = new PayloadBuilder(u64, 3).block(0, ...uv(0), ...uv(2 ** 53 - 1), ...uv(2n ** 64n - 1n)).build();
    expect(() => decode(payload, u64)).toThrow(RangeError);
    expect(col(decode(payload, u64, { int64: "bigint" }), "a")).toEqual([0n, BigInt(2 ** 53 - 1), 2n ** 64n - 1n]);
  });

  it("bigint mode returns bigint even for small values", () => {
    const payload = new PayloadBuilder(i64, 1).block(0, ...zz(7)).build();
    expect(col(decode(payload, i64, { int64: "bigint" }), "a")).toEqual([7n]);
  });
});

describe("decimal option", () => {
  const dec = (scale: number, extra = "") => schemaOf(`{"name":"d","type":"decimal","scale":${scale}${extra}}`);

  it.each([
    [2, 36148, "361.48", 361.48],
    [4, 0, "0.0000", 0],
    [4, -123456, "-12.3456", -12.3456],
    [4, 999999999999, "99999999.9999", 99999999.9999],
    [0, -42, "-42", -42],
    [2, 5, "0.05", 0.05],
    [2, -5, "-0.05", -0.05],
    [3, 1000, "1.000", 1],
  ])("scale %s, stored %s → %s", async (scale, stored, text, number) => {
    const s = await dec(scale);
    const payload = new PayloadBuilder(s, 1).block(0, ...zz(stored)).build();
    expect(col(decode(payload, s, { decimal: "string" }), "d")).toEqual([text]);
    expect(col(decode(payload, s), "d")).toEqual([number]);
    expect(col(decode(payload, s, { decimal: "number" }), "d")).toEqual([number]);
  });

  it("is exact as a string even beyond 2^53", async () => {
    const s = await dec(18);
    const payload = new PayloadBuilder(s, 2).block(0, ...zz(2n ** 63n - 1n), ...zz(-(2n ** 63n))).build();
    expect(col(decode(payload, s, { decimal: "string" }), "d")).toEqual(["9.223372036854775807", "-9.223372036854775808"]);
  });

  it("decodes unsigned decimals stored without zigzag", async () => {
    const s = await dec(2, ',"unsigned":true');
    const payload = new PayloadBuilder(s, 2).block(0, ...uv(36148), ...uv(2n ** 63n - 1n)).build();
    expect(col(decode(payload, s, { decimal: "string" }), "d")).toEqual(["361.48", "92233720368547758.07"]);
    const tooBig = new PayloadBuilder(s, 1).block(0, ...uv(2n ** 63n)).build();
    expect(() => decode(tooBig, s)).toThrowError(expect.objectContaining({ code: "ValueOutOfRange" }));
  });
});

describe("value mapping", () => {
  it("returns Date for datetime, lowercase strings for guid, names for enums, null for absent values", async () => {
    const s = await schemaOf(
      '{"name":"at","type":"datetime","nullable":true},{"name":"id","type":"guid"},{"name":"color","type":"enum","values":["red","green","blue"]}',
    );
    const guid = Uint8Array.from([0x6f, 0x96, 0x19, 0xff, 0x8b, 0x86, 0xd0, 0x11, 0xb4, 0x2d, 0x00, 0xc0, 0x4f, 0xc9, 0x64, 0xff]);
    const payload = new PayloadBuilder(s, 2)
      .block(0, 0b01, ...zz(1_700_000_000_123))
      .block(0, ...guid, ...guid)
      .block(0, 0b0110) // two 2-bit indexes, LSB first: 2 (blue), then 1 (green)
      .build();
    const rows = decode(payload, s) as Record<string, unknown>[];
    expect(rows[0]!["at"]).toEqual(new Date(1_700_000_000_123));
    expect(rows[1]!["at"]).toBeNull();
    expect(rows[0]!["id"]).toBe("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    expect(rows.map((r) => r["color"])).toEqual(["blue", "green"]);
  });

  it("keeps __proto__ as an ordinary own property", async () => {
    const s = await schemaOf('{"name":"__proto__","type":"int32"},{"name":"b","type":"int32"}');
    const payload = new PayloadBuilder(s, 1).block(0, 2).block(0, 4).build();
    const row = (decode(payload, s) as Record<string, unknown>[])[0]!;
    expect(Object.getPrototypeOf(row)).toBe(Object.prototype);
    expect(Object.getOwnPropertyDescriptor(row, "__proto__")?.value).toBe(1);
    expect(row["b"]).toBe(2);
  });

  it("decodes the vectors with the defaults (numbers, Dates)", async () => {
    const v = vectors.find((x) => x.name === "v01-products-plain")!;
    const rows = decode(readPayload(v), await parseSchema(readSchemaText(v))) as Record<string, unknown>[];
    expect(rows).toHaveLength(500);
    expect(rows[0]).toEqual({ id: 1001, name: "Steel Monitor", price: 361.48, status: "active", stock: 142 });
    const all = vectors.find((x) => x.name === "v03-all-types")!;
    const allRows = decode(readPayload(all), await parseSchema(readSchemaText(all)), { int64: "bigint" }) as Record<string, unknown>[];
    expect(allRows[0]!["at"]).toBeInstanceOf(Date);
    expect((allRows[3]!["at"] as Date).toISOString()).toBe("1969-12-31T23:59:59.999Z");
  });

  it("returns a single object for flag S", async () => {
    const v = vectors.find((x) => x.name === "v05-single-product")!;
    const row = decode(readPayload(v), await parseSchema(readSchemaText(v)));
    expect(Array.isArray(row)).toBe(false);
    expect(row).toEqual({ id: 1001, name: "Steel Monitor", price: 361.48, status: "active", stock: 142 });
  });
});
