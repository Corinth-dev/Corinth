import { type Browser, type Page, chromium } from "playwright-core";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { type TestServer, startServer } from "./server.js";

let server: TestServer;
let browser: Browser;

beforeAll(async () => {
  server = await startServer();
  browser = await chromium.launch();
});

afterAll(async () => {
  await browser?.close();
  await server?.close();
});

async function newPage(initScript?: string): Promise<{ page: Page; errors: string[] }> {
  const context = await browser.newContext();
  const page = await context.newPage();
  const errors: string[] = [];
  page.on("pageerror", (e) => errors.push(e.message));
  if (initScript) await page.addInitScript(initScript);
  await page.goto(server.origin + "/");
  return { page, errors };
}

/** Runs async browser code (a function body returning JSON) with the built package imported as `ccf`. */
async function run<T>(page: Page, body: string): Promise<T> {
  return (await page.evaluate(`(async () => { const ccf = await import("/dist/index.js"); ${body} })()`)) as T;
}

describe("in a real Chromium", () => {
  it("decodes every golden vector and raises the expected error for every invalid one", async () => {
    const { page, errors } = await newPage();
    const results = await run<{ failures: string[]; valid: number; invalid: number }>(page, `
      const manifest = await (await fetch("/vectors/manifest.json")).json();
      const canonical = (v) => typeof v === "bigint" ? v.toString() : v instanceof Date ? v.toISOString()
        : Array.isArray(v) ? v.map(canonical) : v !== null && typeof v === "object"
        ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, canonical(x)])) : v;
      const same = (a, b) => JSON.stringify(a, (k, v) => Object.is(v, -0) ? "-0" : v) === JSON.stringify(b, (k, v) => Object.is(v, -0) ? "-0" : v);
      const failures = []; let valid = 0, invalid = 0;
      for (const v of manifest.vectors) {
        const schemaText = await (await fetch("/vectors/" + v.schema)).text();
        const schema = await ccf.parseSchema(schemaText);
        if (schemaText !== schema.canonicalJson || schema.idHex !== v.schemaId) failures.push(v.name + ": schema");
        const bytes = new Uint8Array(await (await fetch("/vectors/" + v.file)).arrayBuffer());
        if (v.kind === "valid") {
          valid++;
          const expected = await (await fetch("/vectors/" + v.expected)).json();
          const actual = canonical(ccf.decode(bytes, schema, { int64: "bigint", decimal: "string" }));
          if (!same(actual, expected)) failures.push(v.name + ": decoded value differs");
        } else {
          invalid++;
          try { ccf.decode(bytes, schema, { int64: "bigint" }); failures.push(v.name + ": no error"); }
          catch (e) { if (!(e instanceof ccf.CcfFormatError) || e.code !== v.expectedError) failures.push(v.name + ": " + e); }
        }
      }
      return { failures, valid, invalid };`);

    expect(results.failures).toEqual([]);
    expect(results.valid).toBe(6);
    expect(results.invalid).toBe(9);
    expect(errors).toEqual([]);
  });

  it("runs the client flow: negotiate, fetch and verify the schema once, decode", async () => {
    server.requests.length = 0;
    const { page } = await newPage();
    const result = await run<{ count: number; first: unknown; again: number }>(page, `
      const client = ccf.createCcfClient({ baseUrl: location.origin });
      const rows = await client.get("/api/products");
      const again = await client.get("/api/products");
      return { count: rows.length, first: rows[0], again: again.length };`);

    expect(result.count).toBe(500);
    expect(result.again).toBe(500);
    expect(result.first).toEqual({ id: 1001, name: "Steel Monitor", price: 361.48, status: "active", stock: 142 });
    expect(server.requests.filter((r) => r.includes("/ccf/schemas/"))).toHaveLength(1);
    expect(server.requests.filter((r) => r === "GET /api/products")).toHaveLength(2);
  });

  it("verifies schemas with the pure-JS SHA-256 when crypto.subtle is unavailable (insecure context)", async () => {
    const { page } = await newPage("Object.defineProperty(globalThis.crypto, 'subtle', { value: undefined, configurable: true });");
    const result = await run<{ subtle: string; count: number; id: string }>(page, `
      const client = ccf.createCcfClient({ baseUrl: location.origin });
      const rows = await client.get("/api/products");
      const bytes = new TextEncoder().encode("abc");
      const hash = Array.from(await ccf.sha256(bytes)).map((b) => b.toString(16).padStart(2, "0")).join("");
      return { subtle: typeof crypto.subtle, count: rows.length, id: hash };`);

    expect(result.subtle).toBe("undefined");
    expect(result.count).toBe(500);
    expect(result.id).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  });

  it("persists schemas in IndexedDB: a reloaded page does not fetch the schema again", async () => {
    server.requests.length = 0;
    const { page } = await newPage();
    const script = `
      const cache = new ccf.IndexedDbSchemaCache({ databaseName: "ccf-test" });
      const client = ccf.createCcfClient({ baseUrl: location.origin, schemaCache: cache });
      const rows = await client.get("/api/products");
      const stored = await cache.get("c33a3ed0");
      return { count: rows.length, stored: typeof stored === "string" && stored.startsWith('{"name":"Product"') };`;

    expect(await run<{ count: number; stored: boolean }>(page, script)).toEqual({ count: 500, stored: true });
    expect(server.requests.filter((r) => r.includes("/ccf/schemas/"))).toHaveLength(1);

    await page.reload();
    expect(await run<{ count: number; stored: boolean }>(page, script)).toEqual({ count: 500, stored: true });
    expect(server.requests.filter((r) => r.includes("/ccf/schemas/"))).toHaveLength(1); // served from IndexedDB

    const cleared = await run<{ after: string | null }>(page, `
      const cache = new ccf.IndexedDbSchemaCache({ databaseName: "ccf-test" });
      await cache.clear();
      const after = (await cache.get("c33a3ed0")) ?? null;
      await cache.close();
      return { after };`);
    expect(cleared.after).toBeNull();
  });

  it("falls back to JSON in the browser when the CCF payload is corrupted", async () => {
    const { page } = await newPage();
    const result = await run<{ rows: unknown }>(page, `
      const real = globalThis.fetch.bind(globalThis);
      const corrupting = async (url, init) => {
        const response = await real(url, init);
        if (!String(url).includes("/api/products") || !(response.headers.get("content-type") ?? "").includes("vnd.ccf")) return response;
        const bytes = new Uint8Array(await response.arrayBuffer());
        return new Response(bytes.slice(0, bytes.length - 5), { status: 200, headers: response.headers });
      };
      const client = ccf.createCcfClient({ baseUrl: location.origin, fetch: corrupting });
      return { rows: await client.get("/api/products") };`);

    expect(result.rows).toEqual([{ id: 1, name: "json" }]);
  });
});
