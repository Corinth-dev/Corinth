import { describe, expect, it } from "vitest";
import { CcfSchemaError, createSchema, parseSchema } from "../src/index.js";
import { readSchemaText, vectors } from "./support/vectors.js";

describe("schema canonical form and id", () => {
  it("gives the Product schema the id of the spec", async () => {
    const s = await parseSchema(readSchemaText(vectors.find((v) => v.schema === "schema-Product.json")!));
    expect(s.idHex).toBe("c33a3ed0");
    expect(Array.from(s.id)).toEqual([0xc3, 0x3a, 0x3e, 0xd0]);
    expect(s.name).toBe("Product");
    expect(s.depth).toBe(1);
  });

  it.each(vectors.map((v) => [v.name, v] as const))("canonicalizes the schema of %s byte for byte", async (_n, v) => {
    const text = readSchemaText(v);
    const s = await parseSchema(text);
    expect(s.canonicalJson).toBe(text);
    expect(s.idHex).toBe(v.schemaId);
  });

  it("ignores whitespace and key order", async () => {
    const canonical = readSchemaText(vectors.find((v) => v.schema === "schema-Product.json")!);
    const messy = `{ "fields" : [
      { "type":"int64", "delta":true, "name":"id" },
      { "name":"name", "type":"string", "nullable": false },
      { "scale":2, "unsigned":true, "type":"decimal", "name":"price" },
      { "name":"status","type":"enum","values":["active","discontinued"] },
      { "type":"uint32", "name":"stock" } ],
      "name":"Product" }`;
    expect((await parseSchema(messy)).canonicalJson).toBe(canonical);
  });

  it("computes the nesting depth", async () => {
    const order = await parseSchema(readSchemaText(vectors.find((v) => v.schema === "schema-Order.json")!));
    expect(order.depth).toBe(2);
    const deep = await parseSchema(
      '{"name":"A","fields":[{"name":"b","type":"object","fields":[{"name":"c","type":"list","fields":[{"name":"$value","type":"list","fields":[{"name":"$value","type":"int32"}]}]}]}]}',
    );
    expect(deep.depth).toBe(4);
  });

  it("changes the id when anything changes", async () => {
    const base = await parseSchema('{"name":"X","fields":[{"name":"a","type":"int32"},{"name":"b","type":"string"}]}');
    for (const v of [
      '{"name":"X","fields":[{"name":"b","type":"string"},{"name":"a","type":"int32"}]}',
      '{"name":"X","fields":[{"name":"a","type":"int64"},{"name":"b","type":"string"}]}',
      '{"name":"X","fields":[{"name":"a","type":"int32","nullable":true},{"name":"b","type":"string"}]}',
      '{"name":"X","fields":[{"name":"a","type":"int32"},{"name":"c","type":"string"}]}',
      '{"name":"X","fields":[{"name":"a","type":"int32"}]}',
    ]) {
      expect((await parseSchema(v)).idHex).not.toBe(base.idHex);
    }
  });

  it("escapes only where JSON requires it", async () => {
    const s = await createSchema('a"b\\c\n', [{ name: "x", type: "bool", nullable: false, delta: false, unsigned: false }]);
    expect(s.canonicalJson.startsWith('{"name":"a\\"b\\\\c\\n",')).toBe(true);
    expect((await parseSchema(s.canonicalJson)).canonicalJson).toBe(s.canonicalJson);
  });
});

// SPEC §14.3: invalid schemas raise CcfSchemaError (not a format error).
const INVALID: [string, string][] = [
  ["not json", "not json"],
  ["array root", "[]"],
  ["missing name", '{"fields":[{"name":"a","type":"int32"}]}'],
  ["missing fields", '{"name":"X"}'],
  ["empty fields", '{"name":"X","fields":[]}'],
  ["name starts with a digit", '{"name":"X","fields":[{"name":"1a","type":"int32"}]}'],
  ["name with a dash", '{"name":"X","fields":[{"name":"a-b","type":"int32"}]}'],
  ["empty name", '{"name":"X","fields":[{"name":"","type":"int32"}]}'],
  ["duplicate names", '{"name":"X","fields":[{"name":"a","type":"int32"},{"name":"a","type":"string"}]}'],
  ["unknown type", '{"name":"X","fields":[{"name":"a","type":"int128"}]}'],
  ["decimal without scale", '{"name":"X","fields":[{"name":"a","type":"decimal"}]}'],
  ["scale 19", '{"name":"X","fields":[{"name":"a","type":"decimal","scale":19}]}'],
  ["scale -1", '{"name":"X","fields":[{"name":"a","type":"decimal","scale":-1}]}'],
  ["scale 1.5", '{"name":"X","fields":[{"name":"a","type":"decimal","scale":1.5}]}'],
  ["scale 2.0", '{"name":"X","fields":[{"name":"a","type":"decimal","scale":2.0}]}'],
  ["delta on string", '{"name":"X","fields":[{"name":"a","type":"string","delta":true}]}'],
  ["delta:false on string", '{"name":"X","fields":[{"name":"a","type":"string","delta":false}]}'],
  ["unsigned on int32", '{"name":"X","fields":[{"name":"a","type":"int32","unsigned":true}]}'],
  ["scale on int32", '{"name":"X","fields":[{"name":"a","type":"int32","scale":2}]}'],
  ["values on int32", '{"name":"X","fields":[{"name":"a","type":"int32","values":["x"]}]}'],
  ["fields on int32", '{"name":"X","fields":[{"name":"a","type":"int32","fields":[]}]}'],
  ["delta on float64", '{"name":"X","fields":[{"name":"a","type":"float64","delta":true}]}'],
  ["enum without values", '{"name":"X","fields":[{"name":"a","type":"enum"}]}'],
  ["enum with no values", '{"name":"X","fields":[{"name":"a","type":"enum","values":[]}]}'],
  ["enum duplicate value", '{"name":"X","fields":[{"name":"a","type":"enum","values":["x","x"]}]}'],
  ["enum value not an identifier", '{"name":"X","fields":[{"name":"a","type":"enum","values":["not ok"]}]}'],
  ["object without fields", '{"name":"X","fields":[{"name":"a","type":"object"}]}'],
  ["object with empty fields", '{"name":"X","fields":[{"name":"a","type":"object","fields":[]}]}'],
  ["unknown field key", '{"name":"X","fields":[{"name":"a","type":"int32","bogus":1}]}'],
  ["unknown root key", '{"name":"X","fields":[{"name":"a","type":"int32"}],"extra":1}'],
  ["duplicate key in a field", '{"name":"X","fields":[{"name":"a","type":"int32","type":"string"}]}'],
  ["duplicate key at the root", '{"name":"X","name":"Y","fields":[{"name":"a","type":"int32"}]}'],
  ["nullable as a string", '{"name":"X","fields":[{"name":"a","type":"int32","nullable":"yes"}]}'],
  ["$value at the root", '{"name":"X","fields":[{"name":"$value","type":"int32"}]}'],
  ["$value next to other list fields", '{"name":"X","fields":[{"name":"l","type":"list","fields":[{"name":"$value","type":"int32"},{"name":"b","type":"int32"}]}]}'],
  ["trailing content", '{"name":"X","fields":[{"name":"a","type":"int32"}]} x'],
  ["trailing comma", '{"name":"X","fields":[{"name":"a","type":"int32"},]}'],
];

describe("invalid schemas (SPEC §14.3)", () => {
  it.each(INVALID)("rejects %s with CcfSchemaError", async (_label, json) => {
    await expect(parseSchema(json)).rejects.toBeInstanceOf(CcfSchemaError);
  });

  it("accepts an enum of 65,536 values and rejects 65,537", async () => {
    const json = (n: number) =>
      `{"name":"X","fields":[{"name":"e","type":"enum","values":[${Array.from({ length: n }, (_, i) => `"v${i}"`).join(",")}]}]}`;
    await expect(parseSchema(json(65536))).resolves.toBeDefined();
    await expect(parseSchema(json(65537))).rejects.toBeInstanceOf(CcfSchemaError);
  });

  it("validates schemas created in code", async () => {
    await expect(createSchema("T", [{ name: "a", type: "decimal", nullable: false, delta: false, unsigned: false }])).rejects.toBeInstanceOf(CcfSchemaError);
    await expect(createSchema("T", [{ name: "a", type: "string", nullable: false, delta: true, unsigned: false }])).rejects.toBeInstanceOf(CcfSchemaError);
    await expect(createSchema("T", [])).rejects.toBeInstanceOf(CcfSchemaError);
  });
});
