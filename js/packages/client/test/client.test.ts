import { describe, expect, it } from "vitest";
import {
  CcfFormatError,
  CcfHttpError,
  CcfSchemaFetchError,
  type CcfSchemaCache,
  MemorySchemaCache,
  createCcfClient,
  verifySchema,
} from "../src/index.js";
import { readPayload, readSchemaText, vectors } from "./support/vectors.js";

const vector = (name: string) => vectors.find((v) => v.name === name)!;
const PRODUCTS = readPayload(vector("v01-products-plain"));
const PRODUCTS_D = readPayload(vector("v02-products-dict"));
const ORDERS = readPayload(vector("v04-orders-nested"));
const PRODUCT_SCHEMA = readSchemaText(vector("v01-products-plain"));
const ORDER_SCHEMA = readSchemaText(vector("v04-orders-nested"));
const PRODUCT_JSON = [{ id: 7, name: "json", price: 1.5, status: "active", stock: 3 }];

interface Call {
  readonly url: URL;
  readonly method: string;
  readonly accept: string;
  readonly headers: Headers;
  readonly body: string | undefined;
}

type Handler = (call: Call) => Response | Promise<Response>;

/** A fetch that records calls and answers from a handler. */
function mockFetch(handler: Handler) {
  const calls: Call[] = [];
  const fn = (async (input: RequestInfo | URL, init?: RequestInit) => {
    const headers = new Headers(init?.headers);
    const call: Call = {
      url: new URL(input.toString()),
      method: (init?.method ?? "GET").toUpperCase(),
      accept: headers.get("Accept") ?? "",
      headers,
      body: typeof init?.body === "string" ? init.body : undefined,
    };
    calls.push(call);
    return handler(call);
  }) as typeof fetch;
  return {
    fetch: fn,
    calls,
    get api() { return calls.filter((c) => !c.url.pathname.includes("/ccf/schemas/") && !c.url.pathname.startsWith("/schemas/")); },
    get schemas() { return calls.filter((c) => c.url.pathname.includes("/ccf/schemas/") || c.url.pathname.startsWith("/schemas/")); },
  };
}

const ccf = (bytes: Uint8Array, contentType = "application/vnd.ccf") => new Response(bytes as unknown as BodyInit, { status: 200, headers: { "content-type": contentType } });
const json = (value: unknown) => new Response(JSON.stringify(value), { status: 200, headers: { "content-type": "application/json" } });
const text = (body: string, status = 200) => new Response(body, { status, headers: { "content-type": "application/json" } });
const isSchemaCall = (c: Call) => c.url.pathname.includes("/schemas/");
const delay = (ms: number) => new Promise((r) => setTimeout(r, ms));

/** A server that serves CCF (or JSON when the Accept header does not ask for CCF) and the product schema. */
const productServer: Handler = (c) => {
  if (isSchemaCall(c)) return c.url.pathname.endsWith("c33a3ed0") ? text(PRODUCT_SCHEMA) : c.url.pathname.endsWith("63a83963") ? text(ORDER_SCHEMA) : new Response(null, { status: 404 });
  if (c.url.pathname === "/orders") return c.accept.includes("vnd.ccf") ? ccf(ORDERS) : json([]);
  return c.accept.includes("vnd.ccf") ? ccf(c.accept.includes("features=d") ? PRODUCTS_D : PRODUCTS) : json(PRODUCT_JSON);
};

const BASE = "https://api.test";

describe("requests and the two paths", () => {
  it("asks for CCF with dictionary mode by default and decodes the payload", async () => {
    const server = mockFetch(productServer);
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch, int64: "number" });

    const products = await client.get<{ id: number; name: string; price: number }[]>("/products");

    expect(products).toHaveLength(500);
    expect(products[0]).toEqual({ id: 1001, name: "Steel Monitor", price: 361.48, status: "active", stock: 142 });
    expect(server.api[0]!.accept).toBe("application/vnd.ccf; features=d, application/json;q=0.5");
    expect(server.schemas).toHaveLength(1);
    expect(server.schemas[0]!.url.toString()).toBe("https://api.test/ccf/schemas/c33a3ed0");
  });

  it("asks for plain CCF when the client cannot read dictionaries", async () => {
    const server = mockFetch(productServer);
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch, features: "" });

    expect(await client.get<unknown[]>("/products")).toHaveLength(500);
    expect(server.api[0]!.accept).toBe("application/vnd.ccf, application/json;q=0.5");
  });

  it("decodes nested data and passes the int64 and decimal options through", async () => {
    const server = mockFetch(productServer);
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch, int64: "bigint", decimal: "string" });

    const orders = await client.get<Record<string, unknown>[]>("/orders");
    const products = await client.get<Record<string, unknown>[]>("/products");

    expect(orders[0]!["lines"]).toEqual([
      { sku: "A-100", qty: 2, price: "9.99" },
      { sku: "A-100", qty: 1, price: "9.99" },
      { sku: "B-200", qty: 5, price: "120.50" },
    ]);
    expect(products[0]).toMatchObject({ id: 1001n, price: "361.48" });
  });

  it("uses JSON transparently when the server does not serve CCF, without a retry or schema fetch", async () => {
    const server = mockFetch(() => json({ hello: "world" }));
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    expect(await client.get("/anything")).toEqual({ hello: "world" });
    expect(server.calls).toHaveLength(1);
  });

  it("returns null for 204 and for an empty body", async () => {
    const client = createCcfClient({ baseUrl: BASE, fetch: mockFetch(() => new Response(null, { status: 204 })).fetch });
    expect(await client.get("/x")).toBeNull();
    const empty = createCcfClient({ baseUrl: BASE, fetch: mockFetch(() => text("")).fetch });
    expect(await empty.get("/x")).toBeNull();
  });

  it("throws CcfHttpError for error statuses and does not repeat the request", async () => {
    const server = mockFetch(() => new Response("nope", { status: 404, statusText: "Not Found" }));
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    const error = await client.get("/missing").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(CcfHttpError);
    expect((error as CcfHttpError).status).toBe(404);
    expect(server.calls).toHaveLength(1);
  });

  it("works without a baseUrl for absolute URLs and rejects relative ones", async () => {
    const server = mockFetch(productServer);
    const client = createCcfClient({ fetch: server.fetch });
    expect(await client.get<unknown[]>("https://other.test:8443/v1/products")).toHaveLength(500);
    expect(server.schemas[0]!.url.toString()).toBe("https://other.test:8443/ccf/schemas/c33a3ed0");
    await expect(client.get("/relative")).rejects.toThrow();
  });
});

describe("schema caching and single flight", () => {
  it("fetches a schema once for many sequential requests", async () => {
    const server = mockFetch(productServer);
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });
    for (let i = 0; i < 25; i++) expect(await client.get<unknown[]>("/products")).toHaveLength(500);
    expect(server.api).toHaveLength(25);
    expect(server.schemas).toHaveLength(1);
  });

  it("shares one fetch between concurrent requests", async () => {
    const server = mockFetch(async (c) => {
      if (isSchemaCall(c)) await delay(150);
      return productServer(c);
    });
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    const results = await Promise.all(Array.from({ length: 40 }, () => client.get<unknown[]>("/products")));

    for (const r of results) expect(r).toHaveLength(500);
    expect(server.schemas).toHaveLength(1);
  });

  it("fetches each distinct schema once", async () => {
    const server = mockFetch(productServer);
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });
    await client.get("/products");
    await client.get("/orders");
    await client.get("/products");
    await client.get("/orders");
    expect(server.schemas).toHaveLength(2);
  });

  it("a shared cache spares a second client the fetch", async () => {
    const cache = new MemorySchemaCache();
    const a = mockFetch(productServer);
    const b = mockFetch(productServer);
    await createCcfClient({ baseUrl: BASE, fetch: a.fetch, schemaCache: cache }).get("/products");
    await createCcfClient({ baseUrl: BASE, fetch: b.fetch, schemaCache: cache }).get("/products");
    expect(a.schemas).toHaveLength(1);
    expect(b.schemas).toHaveLength(0);
  });

  it("consults a pluggable cache first and fills it with the verified canonical text", async () => {
    const log: string[] = [];
    const store = new Map<string, string>();
    const cache: CcfSchemaCache = {
      get: async (id) => { log.push(`get ${id}`); return store.get(id); },
      set: async (id, text) => { log.push(`set ${id}`); store.set(id, text); },
    };
    const server = mockFetch(productServer);
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch, schemaCache: cache });

    await client.get("/products");
    await client.get("/products"); // the parsed schema is kept in memory: no second cache read

    expect(log).toEqual(["get c33a3ed0", "set c33a3ed0"]);
    expect(store.get("c33a3ed0")).toBe(PRODUCT_SCHEMA);
    expect(server.schemas).toHaveLength(1);
  });

  it("re-verifies what a persistent cache returns: a corrupted entry is ignored and fetched again", async () => {
    const cache = new MemorySchemaCache();
    cache.set("c33a3ed0", PRODUCT_SCHEMA.replace("Product", "Evil"));
    const server = mockFetch(productServer);
    const logs: string[] = [];
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch, schemaCache: cache, log: (m) => logs.push(m) });

    expect(await client.get<unknown[]>("/products")).toHaveLength(500);
    expect(server.schemas).toHaveLength(1);
    expect(logs.some((m) => m.includes("schema cache failed"))).toBe(true);
  });

  it("keeps working when the cache throws", async () => {
    const cache: CcfSchemaCache = {
      get: () => { throw new Error("disk on fire"); },
      set: () => { throw new Error("disk on fire"); },
    };
    const client = createCcfClient({ baseUrl: BASE, fetch: mockFetch(productServer).fetch, schemaCache: cache });
    expect(await client.get<unknown[]>("/products")).toHaveLength(500);
  });
});

describe("fallback to JSON", () => {
  const corruptions: [string, (b: Uint8Array) => Uint8Array][] = [
    ["truncated", (b) => b.slice(0, b.length - 3)],
    ["bad magic", (b) => Uint8Array.from(b, (x, i) => (i === 0 ? 0x58 : x))],
    ["trailing data", (b) => Uint8Array.from([...b, 0])],
    ["flipped block length", (b) => Uint8Array.from(b, (x, i) => (i === 10 ? x + 1 : x))],
    ["empty body", () => new Uint8Array(0)],
  ];

  it.each(corruptions)("repeats the request once as JSON after a corrupted payload (%s)", async (_name, corrupt) => {
    const server = mockFetch((c) => (!isSchemaCall(c) && c.accept.includes("vnd.ccf") ? ccf(corrupt(PRODUCTS)) : productServer(c)));
    const messages: string[] = [];
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch, log: (m) => messages.push(m) });

    expect(await client.get("/products")).toEqual(PRODUCT_JSON);

    expect(server.api).toHaveLength(2);
    expect(server.api[0]!.accept).toContain("vnd.ccf");
    expect(server.api[1]!.accept).toBe("application/json");
    expect(messages).toHaveLength(1);
    expect(messages[0]).toContain("repeating the request as JSON");
  });

  const schemaFailures: [string, Handler][] = [
    ["server error", () => new Response(null, { status: 500 })],
    ["not found", () => new Response(null, { status: 404 })],
    ["wrong content", () => text(ORDER_SCHEMA)],
    ["non-canonical text", () => text(PRODUCT_SCHEMA.replaceAll(",", ", "))],
    ["invalid schema", () => text("{}")],
    ["not json", () => text("<html>")],
  ];

  it.each(schemaFailures)("repeats the request as JSON when the schema cannot be fetched or verified (%s)", async (_name, schemaResponse) => {
    const server = mockFetch((c) => (isSchemaCall(c) ? schemaResponse(c) : productServer(c)));
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    expect(await client.get("/products")).toEqual(PRODUCT_JSON);
    expect(server.api.at(-1)!.accept).toBe("application/json");
    expect(server.api).toHaveLength(2);
  });

  it("repeats the request as JSON when the schema request fails at the network level", async () => {
    const server = mockFetch((c) => {
      if (isSchemaCall(c)) throw new TypeError("network down");
      return productServer(c);
    });
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });
    expect(await client.get("/products")).toEqual(PRODUCT_JSON);
  });

  it("does not remember a failed schema fetch: the next request tries again and caches on success", async () => {
    let fail = true;
    const server = mockFetch((c) => (isSchemaCall(c) && fail ? new Response(null, { status: 502 }) : productServer(c)));
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    expect(await client.get("/products")).toEqual(PRODUCT_JSON); // retried as JSON
    expect(server.schemas).toHaveLength(1);
    fail = false;
    expect(await client.get<unknown[]>("/products")).toHaveLength(500);
    expect(server.schemas).toHaveLength(2);
    expect(await client.get<unknown[]>("/products")).toHaveLength(500);
    expect(server.schemas).toHaveLength(2);
  });

  it("makes concurrent requests all fall back when the shared schema fetch fails", async () => {
    const server = mockFetch(async (c) => {
      if (isSchemaCall(c)) {
        await delay(100);
        return new Response(null, { status: 500 });
      }
      return productServer(c);
    });
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    const results = await Promise.all(Array.from({ length: 20 }, () => client.get("/products")));

    for (const r of results) expect(r).toEqual(PRODUCT_JSON);
    expect(server.schemas).toHaveLength(1);
  });

  it("if the JSON retry fails too, its error surfaces", async () => {
    const server = mockFetch((c) => (c.accept.includes("vnd.ccf") ? ccf(PRODUCTS.slice(0, 20)) : new Response(null, { status: 503 })));
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    const error = await client.get("/products").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(CcfHttpError);
    expect((error as CcfHttpError).status).toBe(503);
  });

  it("does not treat a RangeError (int64: number beyond 2^53) as a CCF failure", async () => {
    const v = vector("v03-all-types");
    const schemaText = readSchemaText(v);
    const server = mockFetch((c) => (isSchemaCall(c) ? text(schemaText) : ccf(readPayload(v))));
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    await expect(client.get("/all")).rejects.toBeInstanceOf(RangeError);
    expect(server.api).toHaveLength(1); // no JSON retry
  });
});

describe("retry rules", () => {
  const failing = (extra?: (c: Call) => Response | undefined): Handler => (c) =>
    extra?.(c) ?? (isSchemaCall(c) ? text(PRODUCT_SCHEMA) : c.accept.includes("vnd.ccf") ? ccf(PRODUCTS.slice(0, PRODUCTS.length - 3)) : json(PRODUCT_JSON));

  it.each(["POST", "PUT", "DELETE", "PATCH"])("a %s request is not repeated: the CCF error surfaces", async (method) => {
    const server = mockFetch(failing());
    const messages: string[] = [];
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch, log: (m) => messages.push(m) });

    const error = await client.request("/products", { method, body: "{}" }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(CcfFormatError);
    expect((error as CcfFormatError).code).toBe("Truncated");
    expect(server.api).toHaveLength(1);
    expect(messages[0]).toContain("not safe to repeat");
  });

  it("a schema failure on an unsafe request surfaces as CcfSchemaFetchError", async () => {
    const server = mockFetch((c) => (isSchemaCall(c) ? new Response(null, { status: 404 }) : ccf(PRODUCTS)));
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    const error = await client.request("/products", { method: "POST", body: "{}" }).catch((e: unknown) => e);

    expect(error).toBeInstanceOf(CcfSchemaFetchError);
    expect((error as CcfSchemaFetchError).schemaId).toBe("c33a3ed0");
    expect((error as Error).message).toContain("404");
  });

  it("an idempotent POST is repeated with the same body and headers, and Accept replaced", async () => {
    const server = mockFetch(failing());
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    const result = await client.request("/search", {
      method: "POST",
      body: '{"q":"monitor"}',
      headers: { "content-type": "application/json", authorization: "Bearer t0k3n", "x-trace": "abc" },
      idempotent: true,
    });

    expect(result).toEqual(PRODUCT_JSON);
    expect(server.api).toHaveLength(2);
    const retry = server.api[1]!;
    expect(retry.method).toBe("POST");
    expect(retry.body).toBe('{"q":"monitor"}');
    expect(retry.accept).toBe("application/json");
    expect(retry.headers.get("authorization")).toBe("Bearer t0k3n");
    expect(retry.headers.get("x-trace")).toBe("abc");
    expect(retry.headers.get("content-type")).toBe("application/json");
  });

  it("isIdempotent can declare requests safe", async () => {
    const server = mockFetch(failing());
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch, isIdempotent: (m, url) => m === "POST" && url.pathname === "/search" });

    expect(await client.request("/search", { method: "POST", body: "{}" })).toEqual(PRODUCT_JSON);
    await expect(client.request("/other", { method: "POST", body: "{}" })).rejects.toBeInstanceOf(CcfFormatError);
  });

  it("a streamed request body is never repeated", async () => {
    const server = mockFetch(failing());
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });
    const stream = new ReadableStream({ start: (c) => { c.enqueue(new TextEncoder().encode("{}")); c.close(); } });

    await expect(client.request("/search", { method: "POST", body: stream, idempotent: true, duplex: "half" } as RequestInit)).rejects.toBeInstanceOf(CcfFormatError);
    expect(server.api).toHaveLength(1);
  });

  it("repeats a failed HEAD request", async () => {
    let n = 0;
    const server = mockFetch(() => (n++ === 0 ? ccf(new Uint8Array([1, 2, 3])) : new Response(null, { status: 200 })));
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });
    await client.request("/x", { method: "HEAD" });
    expect(server.calls).toHaveLength(2);
  });
});

describe("origin rules and verification", () => {
  it("builds the schema URL from the request origin and the configured prefix", async () => {
    const server = mockFetch((c) => (isSchemaCall(c) ? text(PRODUCT_SCHEMA) : ccf(PRODUCTS)));
    const client = createCcfClient({ baseUrl: "https://base.test", fetch: server.fetch, schemaPathPrefix: "/schemas/" });

    await client.get("https://other.test:8443/v1/products?x=1");

    expect(server.schemas).toHaveLength(1);
    expect(server.schemas[0]!.url.toString()).toBe("https://other.test:8443/schemas/c33a3ed0");
  });

  it("refuses a schema that was redirected to another origin", async () => {
    const server = mockFetch((c) => {
      if (!isSchemaCall(c)) return c.accept.includes("vnd.ccf") ? ccf(PRODUCTS) : json(PRODUCT_JSON);
      const response = text(PRODUCT_SCHEMA);
      Object.defineProperty(response, "url", { value: "https://evil.test/ccf/schemas/c33a3ed0" });
      return response;
    });
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });

    expect(await client.get("/products")).toEqual(PRODUCT_JSON); // the schema was refused, the JSON retry answered
    expect(server.calls.some((c) => c.url.host === "evil.test")).toBe(false);
  });

  it("verifySchema accepts only canonical text that hashes to the id", async () => {
    expect((await verifySchema("c33a3ed0", PRODUCT_SCHEMA)).idHex).toBe("c33a3ed0");
    await expect(verifySchema("00000000", PRODUCT_SCHEMA)).rejects.toBeInstanceOf(CcfSchemaFetchError);
    await expect(verifySchema("c33a3ed0", PRODUCT_SCHEMA + "\n")).rejects.toBeInstanceOf(CcfSchemaFetchError);
    await expect(verifySchema("c33a3ed0", "")).rejects.toBeInstanceOf(CcfSchemaFetchError);
  });

  it("getSchema fetches and verifies on demand", async () => {
    const server = mockFetch(productServer);
    const client = createCcfClient({ baseUrl: BASE, fetch: server.fetch });
    const schema = await client.getSchema("c33a3ed0", BASE);
    expect(schema.name).toBe("Product");
    await expect(client.getSchema("00000000", BASE)).rejects.toBeInstanceOf(CcfSchemaFetchError);
  });
});
