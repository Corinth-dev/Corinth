import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { parseSchema } from "@corinth/client";
import ts from "typescript";
import { describe, expect, it } from "vitest";
import { type CliIo, generateTypes, main, pascal } from "../src/index.js";

const root = (() => {
  let dir = dirname(fileURLToPath(import.meta.url));
  while (tryRead(resolve(dir, "test-vectors/manifest.json")) === undefined) dir = dirname(dir);
  return resolve(dir, "test-vectors");
})();

function tryRead(path: string): string | undefined {
  try {
    return readFileSync(path, "utf8");
  } catch {
    return undefined;
  }
}

const manifest = JSON.parse(readFileSync(resolve(root, "manifest.json"), "utf8")) as { vectors: { schema: string }[] };
const schemaFiles = [...new Set(manifest.vectors.map((v) => v.schema))].sort();
const schemaText = (file: string) => readFileSync(resolve(root, file), "utf8");

/** Type-checks generated source in memory; returns the diagnostics. */
function check(source: string, usage = ""): string[] {
  const fileName = "/virtual/generated.ts";
  const options: ts.CompilerOptions = { strict: true, noEmit: true, target: ts.ScriptTarget.ES2022, lib: ["lib.es2022.d.ts"], types: [] };
  const host = ts.createCompilerHost(options);
  const original = host.getSourceFile.bind(host);
  host.getSourceFile = (name, languageVersion, ...rest) =>
    name === fileName ? ts.createSourceFile(name, source + usage, languageVersion) : original(name, languageVersion, ...rest);
  host.fileExists = ((exists) => (name: string) => name === fileName || exists(name))(host.fileExists.bind(host));
  const program = ts.createProgram([fileName], options, host);
  return ts.getPreEmitDiagnostics(program).map((d) => ts.flattenDiagnosticMessageText(d.messageText, "\n"));
}

describe("generated types for the vector schemas", () => {
  it.each(schemaFiles)("%s (defaults)", async (file) => {
    expect(generateTypes(await parseSchema(schemaText(file)))).toMatchSnapshot();
  });

  it.each(schemaFiles)("%s (bigint and string decimals)", async (file) => {
    expect(generateTypes(await parseSchema(schemaText(file)), { int64: "bigint", decimal: "string" })).toMatchSnapshot();
  });

  it.each(schemaFiles)("%s compiles under strict TypeScript", async (file) => {
    const source = generateTypes(await parseSchema(schemaText(file)));
    expect(check(source)).toEqual([]);
  });

  it("types a decoded row so that wrong shapes do not compile", async () => {
    const source = generateTypes(await parseSchema(schemaText("schema-Product.json")), { int64: "bigint", decimal: "string" });
    const good = 'const p: Product = { id: 1001n, name: "Steel Monitor", price: "361.48", status: "active", stock: 142 }; void p;';
    const wrongStatus = 'const p: Product = { id: 1001n, name: "x", price: "1.00", status: "retired", stock: 1 }; void p;';
    const wrongId = 'const p: Product = { id: 1001, name: "x", price: "1.00", status: "active", stock: 1 }; void p;';
    expect(check(source, "\n" + good)).toEqual([]);
    expect(check(source, "\n" + wrongStatus)).not.toEqual([]);
    expect(check(source, "\n" + wrongId)).not.toEqual([]);
  });
});

describe("mapping rules", () => {
  const schema = (fields: string) => parseSchema(`{"name":"order_line","fields":[${fields}]}`);

  it("names the root after the PascalCase schema name unless told otherwise", async () => {
    const s = await schema('{"name":"a","type":"bool"}');
    expect(generateTypes(s)).toContain("export interface OrderLine {");
    expect(generateTypes(s, { rootName: "Row" })).toContain("export interface Row {");
  });

  it("maps every scalar type and nullability", async () => {
    const out = generateTypes(
      await schema(
        '{"name":"b","type":"bool"},{"name":"i","type":"int32","nullable":true},{"name":"l","type":"int64"},{"name":"u","type":"uint64","nullable":true},{"name":"f","type":"float64"},{"name":"d","type":"decimal","scale":2},{"name":"e","type":"enum","values":["x","y"],"nullable":true},{"name":"s","type":"string"},{"name":"t","type":"datetime"},{"name":"g","type":"guid"}',
      ),
    );
    for (const line of [
      "b: boolean;", "i: number | null;", "l: number;", "u: number | null;", "f: number;", "d: number;",
      'e: "x" | "y" | null;', "s: string;", "t: Date;", "g: string;",
    ]) expect(out).toContain(`  ${line}`);
  });

  it("generates nested interfaces for objects, object lists and scalar lists", async () => {
    const out = generateTypes(
      await schema(
        '{"name":"customer","type":"object","nullable":true,"fields":[{"name":"name","type":"string"}]},{"name":"lines","type":"list","fields":[{"name":"sku","type":"string"}]},{"name":"tags","type":"list","nullable":true,"fields":[{"name":"$value","type":"string","nullable":true}]},{"name":"grid","type":"list","fields":[{"name":"$value","type":"list","fields":[{"name":"$value","type":"int32"}]}]}',
      ),
    );
    expect(out).toContain("customer: OrderLineCustomer | null;");
    expect(out).toContain("lines: OrderLineLinesItem[];");
    expect(out).toContain("tags: (string | null)[] | null;");
    expect(out).toContain("grid: number[][];");
    expect(out).toContain("export interface OrderLineCustomer {");
    expect(out).toContain("export interface OrderLineLinesItem {");
    expect(out.indexOf("export interface OrderLine {")).toBeLessThan(out.indexOf("export interface OrderLineCustomer {"));
  });

  it("keeps generated names unique", async () => {
    const out = generateTypes(
      await schema('{"name":"a_b","type":"object","fields":[{"name":"x","type":"int32"}]},{"name":"aB","type":"object","fields":[{"name":"y","type":"int32"}]}'),
    );
    expect(out).toContain("export interface OrderLineAB {");
    expect(out).toContain("export interface OrderLineAB2 {");
    expect(check(out)).toEqual([]);
  });

  it("quotes property names that are not identifiers", async () => {
    const s = await parseSchema('{"name":"X","fields":[{"name":"l","type":"list","fields":[{"name":"$value","type":"int32"}]}]}');
    expect(generateTypes(s)).toContain("l: number[];");
    expect(pascal("snake_case-name")).toBe("SnakeCaseName");
    expect(pascal("9lives")).toBe("_9lives");
  });

  it("starts with a header naming the schema id", async () => {
    const out = generateTypes(await parseSchema(schemaText("schema-Product.json")));
    expect(out.split("\n")[0]).toBe("// Generated by corinth-typegen from schema c33a3ed0 (Product). Do not edit.");
  });
});

/** An in-memory CLI environment. */
function fakeIo(files: Record<string, string> = {}, urls: Record<string, string> = {}, stdin = "") {
  const out: string[] = [];
  const err: string[] = [];
  const written: Record<string, string> = {};
  const io: CliIo = {
    stdout: (t) => void out.push(t),
    stderr: (t) => void err.push(t),
    readFile: (p) => {
      if (p in files) return files[p]!;
      throw new Error(`ENOENT: ${p}`);
    },
    readStdin: () => stdin,
    writeFile: (p, t) => void (written[p] = t),
    fetchText: async (u) => {
      if (u in urls) return urls[u]!;
      throw new Error(`GET ${u} failed: HTTP 404`);
    },
  };
  return { io, out: () => out.join(""), err: () => err.join(""), written };
}

describe("CLI", () => {
  const product = schemaText("schema-Product.json");

  it("reads a file and prints interfaces to stdout", async () => {
    const t = fakeIo({ "p.json": product });
    expect(await main(["p.json"], t.io)).toBe(0);
    expect(t.out()).toBe(generateTypes(await parseSchema(product)));
    expect(t.err()).toBe("");
  });

  it("writes to a file with --out and honours --name, --int64 and --decimal", async () => {
    const t = fakeIo({ "p.json": product });
    expect(await main(["p.json", "--out", "p.ts", "--name", "Item", "--int64", "bigint", "--decimal", "string"], t.io)).toBe(0);
    expect(t.out()).toBe("");
    expect(t.written["p.ts"]).toContain("export interface Item {");
    expect(t.written["p.ts"]).toContain("id: bigint;");
    expect(t.written["p.ts"]).toContain("price: string;");
  });

  it("reads stdin for -", async () => {
    const t = fakeIo({}, {}, product);
    expect(await main(["-"], t.io)).toBe(0);
    expect(t.out()).toContain("export interface Product {");
  });

  it("fetches a schema URL and verifies it against the id in the URL", async () => {
    const url = "https://api.test/ccf/schemas/c33a3ed0";
    const ok = fakeIo({}, { [url]: product });
    expect(await main([url], ok.io)).toBe(0);
    expect(ok.out()).toContain("export interface Product {");

    const tampered = fakeIo({}, { [url]: product.replace("Product", "Evil") });
    expect(await main([url], tampered.io)).toBe(1);
    expect(tampered.err()).toContain("hashes to");
    expect(tampered.out()).toBe("");
  });

  it("fetches a URL that does not end in an id without verification", async () => {
    const url = "https://api.test/schemas/product.json";
    const t = fakeIo({}, { [url]: product });
    expect(await main([url], t.io)).toBe(0);
  });

  it("exits 1 with a message for an invalid schema and for unreadable input", async () => {
    const bad = fakeIo({ "bad.json": '{"name":"X","fields":[]}' });
    expect(await main(["bad.json"], bad.io)).toBe(1);
    expect(bad.err()).toContain("invalid schema");
    const missing = fakeIo();
    expect(await main(["nope.json"], missing.io)).toBe(1);
    expect(missing.err()).toContain("ENOENT");
  });

  it.each([
    [[], "no schema given"],
    [["a.json", "b.json"], "only one schema"],
    [["a.json", "--bogus"], "unknown option"],
    [["a.json", "--int64"], "needs a value"],
    [["a.json", "--int64", "float"], "must be one of"],
  ])("exits 2 with usage for %j", async (args, message) => {
    const t = fakeIo({ "a.json": product });
    expect(await main(args, t.io)).toBe(2);
    expect(t.err()).toContain(message);
    expect(t.err()).toContain("Usage:");
  });

  it("prints usage for --help", async () => {
    const t = fakeIo();
    expect(await main(["--help"], t.io)).toBe(0);
    expect(t.out()).toContain("Usage: corinth-typegen");
  });
});
