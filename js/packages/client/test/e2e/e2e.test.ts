import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
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

describe("@corinth/client against the .NET sample API", () => {
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

/** The Scalar viewer script the sample serves (/ccf/scalar-viewer.js), run here with a fake localStorage. */
describe("the sample's Scalar viewer script", () => {
  const storage = new Map<string, string>();
  let customFetch: typeof fetch;

  beforeAll(async () => {
    vi.stubGlobal("localStorage", {
      getItem: (k: string) => storage.get(k) ?? null,
      setItem: (k: string, v: string) => void storage.set(k, v),
    });
    const source = await (await fetch(`${sample.origin}/ccf/scalar-viewer.js`)).text();
    const module = (await import(`data:text/javascript;base64,${Buffer.from(source).toString("base64")}`)) as {
      default: { customFetch: typeof fetch };
    };
    customFetch = module.default.customFetch;
  });

  afterAll(() => {
    vi.unstubAllGlobals();
  });

  const ccf = { headers: { Accept: "application/vnd.ccf" } };

  it("shows the CCF response untouched while the toggle is off (the default)", async () => {
    storage.clear();
    const response = await customFetch(`${sample.origin}/min/products`, ccf);

    expect(response.headers.get("content-type")).toBe("application/vnd.ccf");
    expect(response.headers.get("x-ccf-decoded")).toBeNull();
    expect((await response.arrayBuffer()).byteLength).toBe(9317);
  });

  it("shows the decoded JSON, with the x-ccf headers, while the toggle is on", async () => {
    storage.set("ccf-scalar-decode", "on");
    const response = await customFetch(`${sample.origin}/min/products`, ccf);

    expect(response.headers.get("content-type")).toContain("application/json");
    expect(response.headers.get("x-ccf-decoded")).toBe("true");
    expect(response.headers.get("x-ccf-payload-bytes")).toBe("9317");
    expect(response.headers.get("x-ccf-schema-id")).toBe("c33a3ed0");
    expect(response.headers.get("x-ccf-original-content-type")).toBe("application/vnd.ccf");
    const viaJson = await (await fetch(`${sample.origin}/min/products`, { headers: { Accept: "application/json" } })).json();
    expect(await response.json()).toEqual(viaJson);
  });

  it("follows the toggle from one request to the next", async () => {
    storage.set("ccf-scalar-decode", "on");
    expect((await customFetch(`${sample.origin}/products/1001`, ccf)).headers.get("x-ccf-decoded")).toBe("true");
    storage.set("ccf-scalar-decode", "off");
    expect((await customFetch(`${sample.origin}/products/1001`, ccf)).headers.get("x-ccf-decoded")).toBeNull();
  });

  it("leaves JSON responses alone even when the toggle is on", async () => {
    storage.set("ccf-scalar-decode", "on");
    const response = await customFetch(`${sample.origin}/min/products`, { headers: { Accept: "application/json" } });

    expect(response.headers.get("x-ccf-decoded")).toBeNull();
    expect(((await response.json()) as unknown[]).length).toBe(500);
  });
});
