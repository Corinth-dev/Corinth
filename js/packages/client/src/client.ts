import { type CcfLimits, type DecodeOptions, decode, readSchemaId } from "./decoder.js";
import { CcfFormatError, CcfHttpError, CcfSchemaError, CcfSchemaFetchError } from "./errors.js";
import { type CcfSchema, parseSchema } from "./schema.js";
import { type CcfSchemaCache, MemorySchemaCache } from "./schemaCache.js";
import { sha256, toHex } from "./sha256.js";

/** Options of {@link createCcfClient}. */
export interface CcfClientOptions {
  /** Base URL that relative request paths are resolved against. */
  baseUrl?: string | URL;
  /** The `fetch` to use. Defaults to the global one. */
  fetch?: typeof fetch;
  /** Where verified schemas are kept. Defaults to a {@link MemorySchemaCache}; use `IndexedDbSchemaCache` in browsers to persist them. */
  schemaCache?: CcfSchemaCache;
  /** Decoding features this client can read, as letters. Default `"d"` (dictionary mode); `""` asks for plain CCF. */
  features?: string;
  /** Route prefix of the server's schema endpoint. Default `"/ccf/schemas"` (SPEC §2.4). */
  schemaPathPrefix?: string;
  /** See {@link DecodeOptions.int64}. */
  int64?: DecodeOptions["int64"];
  /** See {@link DecodeOptions.decimal}. */
  decimal?: DecodeOptions["decimal"];
  /** Overrides of the limits of SPEC §9 used when decoding. */
  limits?: Partial<CcfLimits>;
  /** Decides whether a request may be repeated as JSON after a CCF failure; by default only GET and HEAD are repeated. */
  isIdempotent?: (method: string, url: URL, init: RequestInit) => boolean;
  /** Receives a message when CCF failed and the request is repeated as JSON (or the failure surfaces). */
  log?: (message: string, error?: unknown) => void;
}

/** `RequestInit` plus a per-request idempotency declaration. */
export interface CcfRequestInit extends RequestInit {
  /** Mark a non-GET request as safe to repeat as JSON after a CCF failure. */
  idempotent?: boolean;
}

/** A client that asks for CCF and falls back to JSON whenever anything goes wrong (ARCHITECTURE §6.1). */
export interface CcfClient {
  /** GET `path` and return the decoded body (CCF or JSON). */
  get<T = unknown>(path: string | URL, init?: CcfRequestInit): Promise<T>;
  /**
   * Send a request with the CCF `Accept` header. After a CCF failure (malformed payload, schema unavailable) the
   * request is repeated once with `Accept: application/json`, but only when it is safe to repeat: GET, HEAD, or
   * declared idempotent. Otherwise the error surfaces.
   */
  request<T = unknown>(path: string | URL, init?: CcfRequestInit): Promise<T>;
  /** The schema with this id, fetched from `origin` (verified) unless already cached. */
  getSchema(schemaId: string, origin: string | URL): Promise<CcfSchema>;
}

const SAFE_METHODS = new Set(["GET", "HEAD"]);

/** Creates a client. Reuse it: the schema cache and single-flight schema fetching live inside. */
export function createCcfClient(options: CcfClientOptions = {}): CcfClient {
  const doFetch: typeof fetch = options.fetch ?? ((input, init) => globalThis.fetch(input, init));
  const cache: CcfSchemaCache = options.schemaCache ?? new MemorySchemaCache();
  const prefix = (options.schemaPathPrefix ?? "/ccf/schemas").replace(/\/+$/, "");
  const features = options.features ?? "d";
  const accept = `application/vnd.ccf${features ? `; features=${features}` : ""}, application/json;q=0.5`;
  const decodeOptions: DecodeOptions = {
    ...(options.int64 !== undefined ? { int64: options.int64 } : {}),
    ...(options.decimal !== undefined ? { decimal: options.decimal } : {}),
    ...(options.limits !== undefined ? { limits: options.limits } : {}),
  };
  const parsed = new Map<string, CcfSchema>();
  const inflight = new Map<string, Promise<CcfSchema>>();
  const log = options.log ?? (() => undefined);

  async function fetchSchema(schemaId: string, origin: URL): Promise<CcfSchema> {
    // Schemas come from the API's own origin only (SPEC §2.4): the URL is built from the request's origin.
    const schemaUrl = new URL(`${prefix}/${schemaId}`, origin);
    let response: Response;
    try {
      response = await doFetch(schemaUrl.toString(), { headers: { Accept: "application/json" } });
    } catch (e) {
      throw new CcfSchemaFetchError(schemaId, `Fetching schema ${schemaId} from ${schemaUrl} failed: ${(e as Error).message}`, { cause: e });
    }
    if (!response.ok) throw new CcfSchemaFetchError(schemaId, `Schema ${schemaId} is not available from ${schemaUrl}: HTTP ${response.status}.`);
    if (response.url && new URL(response.url).origin !== schemaUrl.origin) {
      throw new CcfSchemaFetchError(schemaId, `Schema ${schemaId} was redirected to another origin (${new URL(response.url).origin}); schemas are only accepted from the API's own origin.`);
    }
    const schema = await verifySchema(schemaId, await response.text());
    try {
      await cache.set(schemaId, schema.canonicalJson);
    } catch (e) {
      log(`Storing schema ${schemaId} in the schema cache failed; continuing without it.`, e);
    }
    return schema;
  }

  async function getSchema(schemaId: string, originLike: string | URL): Promise<CcfSchema> {
    const known = parsed.get(schemaId);
    if (known !== undefined) return known;

    try {
      const text = await cache.get(schemaId);
      if (text !== undefined) {
        const schema = await verifySchema(schemaId, text);
        parsed.set(schemaId, schema);
        return schema;
      }
    } catch (e) {
      log(`Reading schema ${schemaId} from the schema cache failed; fetching it instead.`, e);
    }

    // Concurrent requests for the same id share one fetch.
    let pending = inflight.get(schemaId);
    if (pending === undefined) {
      const origin = new URL(originLike);
      pending = fetchSchema(schemaId, origin).then(
        (schema) => {
          parsed.set(schemaId, schema);
          inflight.delete(schemaId);
          return schema;
        },
        (error: unknown) => {
          inflight.delete(schemaId); // a failure is not remembered: the next request tries again
          throw error;
        },
      );
      inflight.set(schemaId, pending);
    }
    return pending;
  }

  async function request<T>(path: string | URL, init: CcfRequestInit = {}): Promise<T> {
    const url = new URL(path, options.baseUrl);
    const method = (init.method ?? "GET").toUpperCase();
    const { idempotent, ...fetchInit } = init;
    const retryable =
      (SAFE_METHODS.has(method) || idempotent === true || options.isIdempotent?.(method, url, fetchInit) === true) &&
      !(typeof ReadableStream !== "undefined" && fetchInit.body instanceof ReadableStream);

    const headers = new Headers(fetchInit.headers);
    headers.set("Accept", accept);
    const response = await doFetch(url.toString(), { ...fetchInit, headers });
    if (!response.ok) throw new CcfHttpError(response);

    if (isCcf(response)) {
      try {
        const bytes = new Uint8Array(await response.arrayBuffer());
        const schema = await getSchema(readSchemaId(bytes), url);
        return decode<T>(bytes, schema, decodeOptions);
      } catch (e) {
        if (!(e instanceof CcfFormatError || e instanceof CcfSchemaFetchError)) throw e;
        if (!retryable) {
          log(`CCF response from ${url} failed (${e.name}: ${e.message}); the request is not safe to repeat, so the error surfaces.`, e);
          throw e;
        }
        log(`CCF response from ${url} failed (${e.name}: ${e.message}); repeating the request as JSON.`, e);
        const retryHeaders = new Headers(fetchInit.headers);
        retryHeaders.set("Accept", "application/json");
        const retry = await doFetch(url.toString(), { ...fetchInit, headers: retryHeaders });
        if (!retry.ok) throw new CcfHttpError(retry);
        return readJson<T>(retry);
      }
    }
    return readJson<T>(response);
  }

  return {
    request,
    get: (path, init) => request(path, { ...init, method: "GET" }),
    getSchema,
  };
}

function isCcf(response: Response): boolean {
  const type = response.headers.get("content-type") ?? "";
  return type.split(";")[0]!.trim().toLowerCase() === "application/vnd.ccf";
}

async function readJson<T>(response: Response): Promise<T> {
  // 204 No Content (an endpoint that returned null) is a null result, not a JSON error.
  if (response.status === 204) return null as T;
  const text = await response.text();
  return (text === "" ? null : JSON.parse(text)) as T;
}

/**
 * Parses schema text and checks that its hash is the id the payload named: the text must hash to the id and be in
 * canonical form (ARCHITECTURE §6.1).
 *
 * @throws CcfSchemaFetchError the text does not belong to `schemaId` or is not a valid schema.
 */
export async function verifySchema(schemaId: string, text: string): Promise<CcfSchema> {
  const rawId = toHex((await sha256(new TextEncoder().encode(text))).subarray(0, 4));
  if (rawId !== schemaId) throw new CcfSchemaFetchError(schemaId, `The schema served for ${schemaId} hashes to ${rawId}.`);
  let schema: CcfSchema;
  try {
    schema = await parseSchema(text);
  } catch (e) {
    if (e instanceof CcfSchemaError) throw new CcfSchemaFetchError(schemaId, `The schema served for ${schemaId} is invalid: ${e.message}`, { cause: e });
    throw e;
  }
  if (schema.idHex !== schemaId) {
    throw new CcfSchemaFetchError(schemaId, `The schema served for ${schemaId} is not in canonical form (its canonical form has id ${schema.idHex}).`);
  }
  return schema;
}
