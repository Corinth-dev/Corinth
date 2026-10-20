import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { createCcfClient, decode, parseSchema } from "../../src/index.js";
import { toCanonical } from "../support/subject.js";
import { readExpected, readPayload, readSchemaText, vectors } from "../support/vectors.js";
import { type RunningSample, startSample } from "./sampleApi.js";

let sample: RunningSample;

beforeAll(async () => {
  sample = await startSample();
});

afterAll(async () => {
  await sample?.stop();
});

const vector = (name: string) => vectors.find((v) => v.name === name)!;

/** A fetch that records what the client asks for and what comes back. */
function spy() {
  const seen: { url: string; accept: string; contentType: string }[] = [];
  const fn: typeof fetch = async (input, init) => {
    const response = await fetch(input, init);
    seen.push({ url: String(input), accept: new Headers(init?.headers).get("accept") ?? "", contentType: response.headers.get("content-type") ?? "" });
    return response;
  };
  return { fetch: fn, seen, schemaFetches: () => seen.filter((s) => s.url.includes("/ccf/schemas/")).length };
}

describe("@skm/ccf-client against the .NET sample API", () => {
  it("returns exactly what the server's own JSON contains", async () => {
    const s = spy();
    const client = createCcfClient({ baseUrl: sample.origin, fetch: s.fetch });

    const viaCcf = await client.get<unknown[]>("/products");
    const viaJson = (await (await fetch(`${sample.origin}/products`, { headers: { Accept: "application/json" } })).json()) as unknown[];

    expect(viaCcf).toHaveLength(500);
    expect(viaCcf).toEqual(viaJson);
    expect(s.seen[0]!.contentType).toBe("application/vnd.ccf; features=d");
    expect(s.schemaFetches()).toBe(1);
  });

  it("decodes every scalar type, nesting and nulls to exactly the vector's expected value", async () => {
    const cases: [string, string][] = [
      ["/all-types", "v03-all-types"],
      ["/orders", "v04-orders-nested"],
      ["/products/1001", "v05-single-product"],
      ["/products/empty", "v06-empty"],
      ["/products", "v02-products-dict"],
    ];
    const client = createCcfClient({ baseUrl: sample.origin, int64: "bigint", decimal: "string" });
    for (const [path, name] of cases) {
      expect(toCanonical(await client.get(path)), name).toEqual(readExpected(vector(name)));
    }
  });

  it("serves bytes identical to the golden vectors, and the TS decoder reads them", async () => {
    const cases: [string, string, string][] = [
      ["/products", "application/vnd.ccf", "v01-products-plain"],
      ["/products", "application/vnd.ccf; features=d", "v02-products-dict"],
      ["/all-types", "application/vnd.ccf; features=d", "v03-all-types"],
      ["/orders", "application/vnd.ccf; features=d", "v04-orders-nested"],
      ["/products/1001", "application/vnd.ccf", "v05-single-product"],
      ["/products/empty", "application/vnd.ccf; features=d", "v06-empty"],
    ];
    for (const [path, accept, name] of cases) {
      const bytes = new Uint8Array(await (await fetch(sample.origin + path, { headers: { Accept: accept } })).arrayBuffer());
      expect(Buffer.from(bytes).equals(Buffer.from(readPayload(vector(name)))), name).toBe(true);
      const schema = await parseSchema(readSchemaText(vector(name)));
      expect(toCanonical(decode(bytes, schema, { int64: "bigint", decimal: "string" })), name).toEqual(readExpected(vector(name)));
    }
  });

  it("serves CCF for an explicit application/vnd.ccf even when */* is present, and JSON otherwise", async () => {
    const get = async (accept: string) => (await fetch(`${sample.origin}/products`, { headers: { Accept: accept } })).headers.get("content-type");
    expect(await get("application/vnd.ccf, */*;q=0.1")).toBe("application/vnd.ccf");
    expect(await get("application/json, application/vnd.ccf;q=0.5")).toContain("application/json");
    expect(await get("application/vnd.ccf;q=0, */*")).toContain("application/json");
    expect(await get("*/*")).toContain("application/json");
  });

  it("falls back to JSON for what the server cannot serve as CCF", async () => {
    const s = spy();
    const client = createCcfClient({ baseUrl: sample.origin, fetch: s.fetch });

    expect(await client.get("/unsupported")).toMatchObject({ title: "report" });
    expect(await client.get("/bad-decimal")).toEqual([{ name: "x", price: 1.234 }]);
    expect(await client.get("/scalar-value")).toBe(42);
    expect(await client.get("/null")).toBeNull();
    expect(s.schemaFetches()).toBe(0);
  });

  it("repeats the request as JSON when a CCF payload arrives corrupted", async () => {
    const corrupting: typeof fetch = async (input, init) => {
      const response = await fetch(input, init);
      if (!(response.headers.get("content-type") ?? "").includes("vnd.ccf")) return response;
      const bytes = new Uint8Array(await response.arrayBuffer());
      return new Response(bytes.slice(0, bytes.length - 4), { status: 200, headers: response.headers });
    };
    const client = createCcfClient({ baseUrl: sample.origin, fetch: corrupting });

    expect(await client.get<unknown[]>("/products")).toHaveLength(500); // the JSON retry
  });

  it("answers errors with CcfHttpError and not-found schemas with a JSON fallback", async () => {
    const client = createCcfClient({ baseUrl: sample.origin });
    await expect(client.get("/products/999999")).rejects.toMatchObject({ name: "CcfHttpError", status: 404 });

    const noSchemas = createCcfClient({ baseUrl: sample.origin, schemaPathPrefix: "/wrong/prefix" });
    expect(await noSchemas.get<unknown[]>("/products")).toHaveLength(500); // schema 404 → JSON retry
  });
});
