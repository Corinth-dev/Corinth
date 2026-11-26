# @corinth/client

CCF (Corinthian Columnar Format) client for browsers and Node 20+. Zero runtime dependencies, ES modules, strict TypeScript.

```ts
import { createCcfClient, IndexedDbSchemaCache } from "@corinth/client";

const ccf = createCcfClient({ baseUrl: "https://api.example.com", schemaCache: new IndexedDbSchemaCache() });
const products = await ccf.get<Product[]>("/products"); // CCF when the server offers it, JSON otherwise
```

Create the client once and reuse it: the schema cache and the single-flight schema fetch live inside it. Generate the `Product` type with `corinth-typegen` (`@corinth/typegen`).

## Behaviour you should know

- **`null` for `204 No Content` or an empty body**, whether the server answered in CCF or JSON.
- **When the request is repeated as JSON.** Only a CCF failure triggers the retry: a malformed payload (`CcfFormatError`), or a schema that cannot be fetched or does not verify (`CcfSchemaFetchError`). `log` is called, and the same request is sent once more with `Accept: application/json`. If the retry fails, its error surfaces.
- **When it is _not_ repeated.** The retry happens only for requests that are safe to repeat: `GET` and `HEAD`, requests marked `idempotent: true`, or those the `isIdempotent` option approves. For everything else the error surfaces unchanged. These also surface unchanged and are never retried:
  - HTTP error statuses (`CcfHttpError` for `4xx`/`5xx`);
  - usage errors, and the `RangeError` described next.
- **`int64`/`uint64` default to `number`, and a value beyond ±2^53−1 throws `RangeError`. This is intentional.** `JSON.parse` would silently round such a value and hand you a wrong number; CCF refuses instead. It is not a CCF failure, so it is never retried as JSON (the JSON answer would be the lossy one). Pass `int64: "bigint"` to receive exact `bigint`s. `decimal` defaults to `number` (can round for many digits); `decimal: "string"` is exact.
- **Schemas are verified.** The fetched text must hash to the id in the payload and be in canonical form; a schema that arrives after a cross-origin redirect is refused; persistent cache entries are verified again when read. `crypto.subtle` is used for SHA-256, with a small pure-JS fallback for insecure browser origins.

## Options

`baseUrl`, `fetch`, `schemaCache` (`MemorySchemaCache` by default, `IndexedDbSchemaCache` to persist), `features` (default `"d"`), `schemaPathPrefix` (default `/ccf/schemas`), `int64`, `decimal`, `limits`, `isIdempotent`, `log`.
