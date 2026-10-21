# CCF Architecture

How the libraries are structured and how they behave around the wire format. The wire format itself is defined only in `SPEC.md`; where the two seem to disagree, `SPEC.md` wins.

## 1. Goals

1. **Smaller payloads** than JSON without needing HTTP compression.
2. **Faster parsing** than JSON: no scanning, no unescaping, no text-to-number conversion, no decompression.
3. **Zero disruption**: JSON stays the default; endpoints need no code changes; any problem falls back to JSON.

Scope of this build: .NET server, .NET client, JavaScript/TypeScript client. Mobile (Swift, Kotlin) is a future phase.

## 2. Components

```mermaid
flowchart LR
  subgraph Server["ASP.NET Core server"]
    EP[API endpoint<br/>controller or minimal API] --> NEG[Content negotiation<br/>Accept header]
    NEG --> FMT[CcfOutputFormatter<br/>or CcfResult]
    FMT --> REG[ISchemaRegistry<br/>build, hash, cache]
    FMT --> ENC[CcfEncoder<br/>plain / dictionary per column]
    SEP[Schema endpoint<br/>GET /ccf/schemas/:id] --> REG
  end
  subgraph Client["Client (.NET or TypeScript)"]
    FW[Fetch wrapper<br/>sets Accept, JSON fallback] --> DEC[CcfDecoder<br/>validate, read columns]
    DEC --> APP[Typed objects]
    DEC --> SC[Schema cache<br/>by schema id]
  end
  FW -- "1 request" --> EP
  FMT -- "2 CCF bytes" --> FW
  SC -- "3 schema (first time only)" --> SEP
```

### Packages

| Package | Contents | Depends on |
| --- | --- | --- |
| `Ccf.Core` (.NET) | Schema model, canonicalizer, hasher, schema builder, encoder, decoder, limits, errors | Nothing outside the BCL |
| `Ccf.AspNetCore` (.NET) | `AddCcf`, output formatter, `CcfResults`, schema endpoint, options | `Ccf.Core`, ASP.NET Core |
| `Ccf.Scalar` (.NET, development tooling) | `AddCcf()` for the OpenAPI document (an `application/vnd.ccf` response per CCF-capable operation), `MapCcfViewer()` (POST `/ccf/decode` and the Scalar script, Development only), `WithCcfViewer()` for the Scalar options | `Ccf.AspNetCore`, `Microsoft.AspNetCore.OpenApi`, `Scalar.AspNetCore` (2.18.x) |
| `Ccf.Client` (.NET) | `HttpClient` extensions, schema cache, JSON fallback | `Ccf.Core`, `System.Net.Http.Json`, `Microsoft.Extensions.Logging.Abstractions` |
| `@skm/ccf-client` (npm) | Decoder, schema cache, `fetch` wrapper, TS type generator CLI | None at runtime |

## 3. Server: ASP.NET Core integration

### 3.1 Registration

```csharp
builder.Services.AddCcf(o =>
{
    o.DictionaryThreshold = 0.5;          // §8.1 of SPEC; 0.5 is the conformance default
    o.Limits = new CcfLimits { MaxRows = 100_000 };   // Limits is init-only
    o.SchemaPathPrefix = "/ccf/schemas";
});
// …
app.MapCcfSchemas();                        // GET /ccf/schemas/{id}
```

`AddCcf` registers:

| Service | Lifetime | Role |
| --- | --- | --- |
| `IOptions<CcfOptions>` | singleton | Threshold, limits, path prefix, dev query override; bindable from `appsettings.json` section `Ccf`, validated on start |
| `ISchemaRegistry` | singleton | `(Type, naming policy) → CcfSchema` cache and `id → CcfSchema` index, thread-safe (`ConcurrentDictionary`) |
| `CcfEncoder` | singleton | The encoder of the MVC path; stateless, thread-safe (an internal twin serves the minimal-API path, see 3.3) |
| `CcfOutputFormatter` | added to `MvcOptions.OutputFormatters` | MVC controllers get CCF through normal negotiation |
| `OutputFormatterSelector` | singleton, replaced | Decorates MVC's `DefaultOutputFormatterSelector` (see 3.2) |
| `IStartupFilter` | transient | Development query override, `Vary: Accept` middleware, startup naming warning |

It is not a hosted/background service: nothing runs unless a request asks for CCF.

**Naming follows the path's own JSON options.** MVC responses use MVC's `JsonOptions.JsonSerializerOptions.PropertyNamingPolicy`; minimal APIs use `Microsoft.AspNetCore.Http.Json.JsonOptions` (`ConfigureHttpJsonOptions`). The registry is keyed by (type, naming policy), so differing policies produce different schemas and ids, each served by the schema endpoint. One warning is logged at startup if the two policies differ.

### 3.2 Content negotiation and the output formatter

**Rule (`CcfNegotiation.TryNegotiate`).** CCF is served only when `application/vnd.ccf` is listed *explicitly* with `q > 0` and no other explicit media type the server can produce (`application/json`, `text/json`, `*+json`) has a higher `q`. Wildcards (`*/*`, `application/*`) never compete, so `application/vnd.ccf, */*;q=0.1` yields CCF. Equal `q` keeps header order, so `application/json, application/vnd.ccf` yields JSON. `q=0` means not acceptable. `features` is read from the winning range, quoted or bare; unknown letters are ignored. `MvcOptions.RespectBrowserAcceptHeader` is left alone.

**MVC.** MVC ignores an `Accept` header containing `*/*`, so CCF wraps the selector: `CcfOutputFormatterSelector` applies the rule above first (honouring `[Produces]`/`ObjectResult.ContentTypes`, and falling back to `MvcOptions.OutputFormatters` when the result carries no formatters of its own) and otherwise delegates to MVC unchanged. `CcfOutputFormatter : OutputFormatter` (supported media type `application/vnd.ccf`):

1. `CanWriteResult`: true only when the offered media type is `application/vnd.ccf`, the rule above agrees, the value is not null and `ISchemaRegistry.TryGetSchema(objectType)` succeeds. It never claims a response without an explicit offer, so wildcard or absent `Accept` stays JSON, and it is registered after the built-in formatters.
2. Materialize a lazy `IEnumerable<T>` response **once** into a list (arrays and `IList` are used as they are), so an LINQ or EF query is never executed twice.
3. Encode the whole payload into a pooled buffer.
4. On `CcfEncodeException`: log a warning with the schema id and reason, then write the *materialized* value as JSON through the first registered JSON `TextOutputFormatter` (headers have not been sent yet).
5. On success: set `Content-Type` per SPEC §2.3 from the flags actually produced, set `Content-Length`, copy the buffer to the body.

**Why `CanWriteResult` is custom** (verified in `CcfMediaTypeMatchingTests`): MVC passes the Accept range, parameters included, as `context.ContentType`, but the stock `OutputFormatter.CanWriteResult` only matches when the formatter's media type carries every parameter of that range, so `application/vnd.ccf` never matches `application/vnd.ccf; features=d`. The formatter compares type/subtype only and reads `features` from the request.

**`Vary: Accept`.** Sent (a) on every response to a request whose `Accept` mentions `application/vnd.ccf`, whatever the outcome (CCF, JSON, fallback, 204, 404, unsupported type, scalar), by an early middleware that adds it when the response starts; and (b) on JSON responses of endpoints whose value type has a CCF schema even if the request did not mention CCF (SPEC §2.3), by a result filter (MVC) and inside `CcfResults.Ok`. It is never duplicated.

**Development override.** `?ccf=d` (or `?ccf=` for plain CCF) rewrites `Accept` as if the request had sent `application/vnd.ccf; features=…`; `?ccf=off|json|0` leaves it alone. Active only when `AllowQueryOverride` is set and the environment is Development.

### 3.3 Minimal APIs

`CcfResults.Ok<T>(T value)` returns an `IResult` that applies the same rule (CCF if accepted and supported, otherwise `TypedResults.Ok(value)` as JSON), materializes a lazy sequence once, falls back to JSON from the same list on an encode failure, and uses the Http `JsonOptions` naming. A `.WithCcf()` endpoint filter that wraps existing handlers is a possible later, optional addition; it is not implemented.

### 3.4 Schema endpoint

`MapCcfSchemas()` maps `GET {prefix}/{id:regex(^[0-9a-f]{{8}}$)}` to the registry. Returns the canonical JSON with the immutable cache header (SPEC §2.4), 404 for unknown ids. Schemas are registered lazily the first time a type is encoded; `ISchemaRegistry.Register<T>()` lets apps pre-register types at startup so the endpoint knows them before the first request (important when several server instances run behind a load balancer).

### 3.5 Observability

- `ILogger` category `Ccf`: debug on schema registration, warning on fallback after an encode failure.
- `System.Diagnostics.Metrics` meter `Ccf`: counters `ccf.responses` (tag `format` = `ccf` / `json` / `json-fallback`, tag `features`), `ccf.bytes_written`, `ccf.encode_failures`. `format=json` counts JSON responses of CCF-capable endpoints (the type has a schema: the client did not ask for CCF, or JSON ranked higher, or `[Produces]` excludes CCF); it is not recorded for unsupported types, null values or scalars, and a fallback is counted once, as `json-fallback`. Each response is counted at most once. There is no JSON-size counter: bytes saved are an estimate (docs/OBSERVABILITY.md).

## 4. Encoder design

- One pass per column over the row list, writing into the buffer; the column's block length is known only afterwards, so each block is written into a scratch region and its length prefix is written before copying, or space is reserved and the body shifted. Pick whichever profiles faster; both must produce the shortest uvarint.
- Property values are read through compiled accessors (expression trees or a source generator), cached per type in the registry — no reflection per value.
- API shape: `new CcfEncoder(CcfSchemaOptions? options, double dictionaryThreshold = 0.5)` (the options carry the naming rules used to build the schema) and `Encode(object value, CcfSchema schema, CcfFeatures features, IBufferWriter<byte> output, CcfLimits? limits)`, which returns the header flags actually produced (the Content-Type derives from them via `CcfFeatureParser.ContentTypeFor`). The payload is built completely in a pooled buffer and only then copied to `output`.
- Internally each column is first extracted into a row-aligned `ColumnData` (typed arrays plus a presence array) and then written by a CLR-independent writer; the decoder produces the same `ColumnData`, which the untyped value tree and the typed binder both read. Accessors are compiled once per (canonical schema text, CLR type).
- Dictionary decision per string column: collect distinct values with a `Dictionary<string,int>` (ordinal comparer), which also yields first-appearance order.
- Allocation goal: zero allocations per value; pooled buffers; `Span<byte>`/`stackalloc` for varints.

## 5. Schema building from .NET types

The schema mirrors what System.Text.Json would emit, so CCF and JSON field names always match.

### 5.1 Type mapping

| .NET type | CCF type |
| --- | --- |
| `bool` | `bool` |
| `sbyte`, `short`, `int` | `int32` |
| `byte`, `ushort`, `uint` | `uint32` |
| `long` | `int64` |
| `ulong` | `uint64` |
| `float`, `double` | `float64` |
| `decimal` | `decimal` (scale from `[CcfDecimal]`, else `CcfOptions.DefaultDecimalScale`, default 2) |
| `string` | `string` |
| `enum` that System.Text.Json writes as a **string** (a `JsonStringEnumConverter` applies: `[JsonConverter]` on the property, a converter in the path's `JsonSerializerOptions`, or `[JsonConverter]` on the enum type, in that order of precedence) | `enum`, `values` = the distinct strings STJ writes (member names in declaration order, through any naming policy or `[JsonStringEnumMemberName]`) |
| any other `enum`, including `[Flags]` (STJ writes the number) | the integer column of its underlying type: `sbyte`/`short`/`int` → `int32`, `byte`/`ushort`/`uint` → `uint32`, `long` → `int64`, `ulong` → `uint64`, holding the underlying value (values without a member are fine) |
| `DateTime`, `DateTimeOffset` | `datetime` (`DateTime` Unspecified is treated as UTC) |
| `Guid` | `guid` |
| `Nullable<T>` | the type of `T`, `nullable: true` |
| class / record / struct with public properties | `object` |
| `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `IEnumerable<T>`, `ICollection<T>` | `list` (scalar `T` → `$value` field) |

Enums mirror System.Text.Json (`CcfSchemaOptions.JsonSerializerOptions` carries the path's options; the registry passes MVC's or the minimal-API options, so each path follows its own). A `[Flags]` enum written as strings (`"Read, Write"`) and an enum with a custom converter make the type unsupported (JSON fallback). On the client, a string enum column is matched to the CLR enum by member name exactly, then ignoring case, `_` and `-`; an integer column decodes into an enum property by value (a value outside the underlying range is `ValueOutOfRange`).

Root: a collection of `T` → table of `T`; a single `T` → table of 1 row with flag `S`.

### 5.2 Names, order, attributes

- Field names: the JSON name — `[JsonPropertyName]`, else the MVC `JsonSerializerOptions.PropertyNamingPolicy` (camelCase by default).
- Order: System.Text.Json order, honoring `[JsonPropertyOrder]`.
- `[JsonIgnore]` (unconditional) excludes a property. Conditional ignore (`WhenWritingNull` etc.) does not change the schema; the field is simply nullable.
- `[CcfDelta]` sets `delta: true`. `[CcfDecimal(scale, Unsigned = true)]` sets scale/unsigned.
- Nullability of reference types from nullable reference type annotations (`NullabilityInfoContext`); un-annotated reference types are nullable.

### 5.3 Unsupported → JSON fallback

If any reachable member is unsupported, the whole response type is unsupported and its responses are served as JSON (logged once at debug level): `object`, `dynamic`, `JsonElement`/`JsonNode`, dictionaries, interfaces and abstract classes (other than the collection interfaces above), polymorphic hierarchies (`[JsonDerivedType]`), `[Flags]` enums, `char`, `byte[]`, `TimeSpan`, `DateOnly`, `TimeOnly`, `Half`, `Int128`, `BigInteger`, recursive types, depth > 8, names not matching the identifier pattern, custom `JsonConverter`s on a member.

## 6. Clients

### 6.1 Shared behaviour (.NET and TypeScript)

1. Send `Accept: application/vnd.ccf; features=d, application/json;q=0.5` (plain `application/vnd.ccf, application/json;q=0.5` when the client cannot read dictionaries).
2. If the response `Content-Type` is not CCF → parse as JSON (fallback is normal, not an error). An empty body or `204` is a `null` result.
3. If CCF → read the schema id from bytes 4–7; get the schema from the cache, or fetch `GET {prefix}/{id}` once (concurrent requests for the same id share one fetch), verify that the returned text hashes to the id **and** is in canonical form, cache it. The schema URL is built from the origin of the request; a schema that ends up on another origin (redirect) is refused (SPEC §2.4). A cache read is verified again, so a corrupted persistent cache is ignored and refetched.
4. Decode. On a CCF format error or a failed schema fetch (HTTP error, network error, unknown id, hash mismatch, invalid schema) → log it and **repeat the original request once with `Accept: application/json`**. Only requests that are safe to repeat (GET, HEAD, or explicitly marked idempotent; never a streamed body) are retried; otherwise the error surfaces. A failed fetch is not remembered: the next request tries again. Errors that are not CCF failures are never retried: an HTTP error status, a usage error such as a target type that cannot receive the schema (.NET `InvalidOperationException`), or the `RangeError` of `int64: "number"`.
5. Schema cache: in memory by default; pluggable for persistent storage (IndexedDB in browsers, disk on .NET). Entries never expire (the id is a content hash). A failing cache never fails a request.

### 6.2 .NET client (`Ccf.Client`)

- `CcfClient(HttpClient, CcfClientOptions?)` holds the schema cache and the single-flight map; `client.GetAsync<T>(uri, ct)` and `client.SendAsync<T>(HttpRequestMessage, ct)` (retry only for GET/HEAD or when `CcfClientOptions.IsIdempotent` says so; the request is captured first so it can be sent again).
- `HttpClient.GetFromCcfAsync<T>(string uri, CancellationToken)` mirrors `GetFromJsonAsync<T>` and uses one default `CcfClient` per `HttpClient`; create a `CcfClient` for custom options.
- `CcfClientOptions`: `Features`, `SchemaPathPrefix`, `SchemaCache` (`ICcfSchemaCache`, async `GetAsync`/`SetAsync`, default `InMemoryCcfSchemaCache`), `JsonSerializerOptions` (JSON path and the naming policy used to match properties; default web defaults), `Limits`, `IsIdempotent`. Logging goes through an optional `ILogger<CcfClient>` constructor argument (DI when registered, `NullLogger` otherwise).
- Failures to obtain a schema are `CcfSchemaFetchException` (client-side, not a format error); an error status is `HttpRequestException`.
- Decodes into `T` by matching schema field names to properties with the same naming rules as the server (§5.2). Schema fields with no matching property are skipped (block length); properties with no matching field keep their default. Matching is by exact JSON name first, then case-insensitively. Narrowing into a smaller CLR type is range-checked (`ValueOutOfRange`). A CLR type that cannot receive the schema (incompatible property type, abstract type, several rows into a single object) raises `InvalidOperationException`: a usage error, not a format error. Datetimes outside the .NET `DateTime` range (years 1–9999) are rejected by the .NET decoder with `ValueOutOfRange`.
- Decimals decode exactly to `decimal`; datetimes to `DateTimeOffset` (offset 0) or `DateTime` (Kind Utc).

### 6.3 TypeScript client (`@skm/ccf-client`)

- Runs in browsers and Node 20+; ES modules; zero runtime dependencies; uses `DataView`, `TextDecoder("utf-8", { fatal: true, ignoreBOM: true })` (the BOM is data, not a marker).
- `createCcfClient({ baseUrl?, fetch?, schemaCache?, features?, schemaPathPrefix?, int64?, decimal?, limits?, isIdempotent?, log? })` → `client.get<T>(path, init?)`, `client.request<T>(path, { method, body, idempotent, … })`, `client.getSchema(id, origin)`. Lower level: `decode(bytes, schema, options)` (synchronous), `readSchemaId(bytes)`, `parseSchema(text)` (**async**: it hashes with WebCrypto, falling back to a built-in pure-JS SHA-256 where `crypto.subtle` is unavailable, such as insecure browser origins), `verifySchema(id, text)`.
- Schema caches implement `CcfSchemaCache { get(id), set(id, canonicalJson) }` (sync or async, storing the canonical text, which is re-verified when read): `MemorySchemaCache` (default) and `IndexedDbSchemaCache`.
- Errors: `CcfFormatError` (same code strings as .NET), `CcfSchemaError` (invalid schema, SPEC §14.3), `CcfSchemaFetchError`, `CcfHttpError`.
- Value mapping: int64/uint64 → `number` by default, throwing a `RangeError` if a value is beyond `Number.MAX_SAFE_INTEGER`; option `int64: "bigint"`. Decimal → `number` by default (may round for values beyond 2^53 or many digits); option `decimal: "string"` for exact values with `scale` digits. Datetime → `Date` (values outside the range of `Date`, ±8.64e15 ms, are `ValueOutOfRange`). Guid → lowercase string. Enum → member name string. `null` for absent values. A single-object payload decodes to an object, a table to an array of objects.
- `ccf-typegen <schema file | - | URL> [-o out.ts] [--name Root] [--int64 number|bigint] [--decimal number|string]` reads a schema (a URL ending in an id is verified against it) and writes TypeScript interfaces: the root interface is named after the schema, nested objects become `<Parent><Field>` and object list elements `<Parent><Field>Item`; `--int64`/`--decimal` should match the client options.

## 7. Flows

### 7.1 First request for a schema

```mermaid
sequenceDiagram
  participant App as Application
  participant C as CCF client
  participant S as ASP.NET server
  participant E as Schema endpoint
  App->>C: load products
  C->>S: GET /products<br/>Accept: application/vnd.ccf; features=d, application/json;q=0.5
  Note over S: schema c33a3ed0 from registry<br/>encode: name column uses dictionary
  S-->>C: 200, Content-Type: application/vnd.ccf; features=d (5,266 bytes)
  Note over C: read header: schema c33a3ed0 not cached
  C->>E: GET /ccf/schemas/c33a3ed0 (first time only)
  E-->>C: canonical schema JSON, immutable
  Note over C: verify hash, cache, validate and decode 5 columns
  C-->>App: 500 typed objects
```

Every later request with the same schema skips the schema call.

### 7.2 Encoding choice per string column

```mermaid
flowchart LR
  A[Next string column] --> B{features=d<br/>requested?}
  B -- no --> P[Plain]
  B -- yes --> C{2k < p and<br/>k ≤ 65,536?}
  C -- yes --> D[Dictionary]
  C -- no --> P
```

### 7.3 Fallback paths (all end in JSON)

| Situation | Who falls back | How |
| --- | --- | --- |
| Client did not accept CCF | Server | JSON formatter selected by MVC |
| Response type unsupported, value null or scalar | Server | Formatter `CanWriteResult` returns false |
| Encode failure (bad decimal, null in non-nullable, limit) | Server | Formatter writes JSON from the same buffer-before-send design |
| Schema fetch fails or hash mismatch | Client | Repeat the request with `Accept: application/json` |
| Decoder throws a CCF format error | Client | Same retry |

## 8. Security

Summarised from SPEC §9 and §11: every length, count and index is checked against limits and remaining bytes before use; decoders never allocate from an unchecked count; strings are validated UTF-8; schemas come only from the API's own origin and their hash is verified; the encoder enforces the same limits so a server never emits what its clients would reject. Phase 1 includes a fuzz test of the decoder.

## 9. Measured metrics

Sample: 500 products (id, name, price, status, stock), 142 distinct names, final wire layout (10-byte header, mode byte and block length per column). Same data as `test-vectors/v01` and `v02`.

| Format | Raw | Gzip -9 | Brotli q11 | Raw vs JSON |
| --- | --- | --- | --- | --- |
| JSON, minified | 39,660 | 6,250 | 5,258 | 1× |
| TOON, tabular | 19,182 | 5,836 | 4,894 | 2.1× smaller |
| CCF plain (`v01`) | 9,317 | 4,229 | 3,625 | 4.3× smaller |
| CCF dictionary (`v02`) | **5,266** | 3,749 | 3,187 | 7.5× smaller |

Uncompressed dictionary-mode CCF is 16% smaller than Gzip JSON and within 8 bytes of Brotli JSON at maximum quality, with no decompression step. Where the dictionary-mode bytes go: name 2,323 (44%), price 1,423, stock 932, id 501, status 63, plus header and block prefixes.

### 9.1 Phase 4 benchmark numbers

Measured 2026-10-20 on a shared 4-vCPU Intel Xeon 2.10 GHz VM; .NET 10.0.13 (BenchmarkDotNet 0.15.8, Release), Node 22.22.0, Chromium 141. A shared VM is noisy, so read the ratios. `net8.0` was not run. Method, datasets and all further columns (reflection baselines, brotli, allocations, encode) are in `docs/BENCHMARKS.md`; the go/no-go judgement is the project owner's.

Datasets: products-500 (the vectors' sample), products-5k (same schema, ~300 distinct names), unique-strings-5k (name, email, note almost always unique), orders-5k (nullable object, object list, scalar list).

**Size in bytes** (gzip is level 6)

| Dataset | JSON | JSON gzip | JSON brotli 11 | CCF plain | CCF dictionary | dict ÷ gzip | dict ÷ JSON |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| products-500 | 39,721 | 6,558 | 5,254 | 9,317 | 5,266 | 80% | 13% |
| products-5k | 402,516 | 64,197 | 49,709 | 95,583 | 43,917 | 68% | 11% |
| unique-strings-5k | 857,655 | 170,385 | 124,783 | 603,787 | 532,003 | 312% | 62% |
| orders-5k | 1,209,532 | 156,003 | 94,450 | 330,051 | 131,941 | 85% | 11% |

**.NET decode** (wire bytes to `List<T>`; baseline is System.Text.Json source-gen)

| Dataset | CCF dictionary | CCF plain | STJ source-gen | STJ reflection | JSON gzip | JSON brotli |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| products-500 | 36.5 µs | 48.4 µs | 196 µs | 207 µs | 250 µs | 264 µs |
| products-5k | 0.31 ms | 0.53 ms | 1.88 ms | 2.10 ms | 3.45 ms | 2.78 ms |
| unique-strings-5k | 1.17 ms | 1.40 ms | 2.70 ms | 2.38 ms | 4.82 ms | 4.41 ms |
| orders-5k | 2.63 ms | 4.82 ms | 8.80 ms | 9.65 ms | 11.99 ms | 11.02 ms |

(Gzip and brotli columns use source-gen after decompression.) CCF decode allocates 0.8x–1.3x of what STJ does.

**.NET encode** (time relative to STJ source-gen = 1.00): CCF dictionary 0.85 / 0.70 / 2.99 / 1.07 and CCF plain 0.80 / 0.83 / 2.17 / 1.35 on products-500 / products-5k / unique-strings-5k / orders-5k. JSON + gzip (fastest) costs 4.11 / 4.44 / 9.47 / 3.49 and JSON + brotli (fastest) 2.82 / 3.28 / 6.86 / 3.21.

**JavaScript decode** (median ms, wire bytes to JS values; CCF with default options)

| Runtime | Dataset | JSON.parse | gunzip + JSON.parse | CCF plain | CCF dictionary | JSON.parse ÷ CCF dict | gzip JSON ÷ CCF dict |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Node 22 | products-500 | 0.160 | 0.914 | 0.155 | 0.109 | 1.47x | 8.40x |
| Node 22 | products-5k | 1.985 | 4.885 | 1.398 | 0.933 | 2.13x | 5.24x |
| Node 22 | unique-strings-5k | 2.343 | 9.753 | 3.466 | 3.187 | 0.74x | 3.06x |
| Node 22 | orders-5k | 9.523 | 16.356 | 9.035 | 4.010 | 2.37x | 4.08x |
| Chromium 141 | products-500 | 0.102 | 1.756 | 0.182 | 0.110 | 0.93x | 15.97x |
| Chromium 141 | products-5k | 1.100 | 4.680 | 2.300 | 0.930 | 1.18x | 5.03x |
| Chromium 141 | unique-strings-5k | 1.709 | 7.800 | 5.875 | 5.200 | 0.33x | 1.50x |
| Chromium 141 | orders-5k | 6.050 | 12.533 | 10.200 | 3.267 | 1.85x | 3.84x |

Chromium coarsens timers, so its small cases are quantised (0.93x on 500 rows is within noise).

**Against the go criteria** (PLAN.md phase 4): CCF dictionary decode is faster than JSON parse plus gzip decompression on the 500-row and 5k product sets on all three runtimes (6.9x and 11x on .NET, 8.4x and 5.2x on Node, 16x and 5.0x on Chromium), and raw CCF dictionary size is at most gzip JSON size on both (80% and 68%).

### 9.2 Known weak spot: data dominated by unique strings

CCF's advantage comes from columns whose values repeat (dictionary mode), are small integers or deltas, or are fixed-width. A table whose bulk is long, mostly unique text gains little, and some measures get worse. On unique-strings-5k (name, email and free-text note almost always distinct):

- **Size:** dictionary mode cannot apply (SPEC §8.1 rule `2k < p` fails), so strings are stored plain. CCF is 62% of raw JSON but **3.1x the size of gzip JSON** (532,003 vs 170,385 bytes) and 4.3x brotli 11. There is no compression in CCF, and JSON compressors exploit the redundancy inside text that CCF leaves in.
- **JavaScript decode** is slower than plain `JSON.parse`: 0.74x in Node and 0.33x in Chromium. UTF-8 decoding of many strings dominates, and `TextDecoder` and `JSON.parse` are native and heavily tuned. It still beats gzip JSON (3.1x in Node, 1.5x in Chromium).
- **.NET decode** still wins (0.44x of STJ time with the dictionary flag, 0.52x plain), but **.NET encode** is 2.2x (plain) to 3.0x (dictionary flag; the encoder hashes strings into a candidate dictionary until the distinct count shows the rule cannot hold, then writes them plain; this cost is the likely cause, not profiled) slower than STJ source-gen.
- **Allocations** are close to STJ's (0.95x–1.09x).

Consequences for rollout: opt in per endpoint, prefer endpoints with repetitive or numeric columns, and do not expect a size win over a compressing server for text-heavy payloads (content negotiation lets such endpoints stay on JSON). Possible mitigations, none decided: a per-endpoint or per-column opt-out, cheaper rejection of dictionary candidates in the encoder, and the numeric feature `n` in §10 (which does not help text). The compression-free design is a decision (DECISIONS #1, and compression is out of scope), so this limitation is accepted unless the owner reopens it.

## 10. Operating and releasing

- Enabling CCF on an API: `docs/GUIDE.md`. Metrics, dashboards and alerts: `docs/OBSERVABILITY.md`. Versioning and compatibility rules: `docs/VERSIONING.md`.
- Packages (version 0.1.0, UNLICENSED, private feed, prepared but not published by this repository): NuGet `SKM.Ccf.Core`, `SKM.Ccf.AspNetCore`, `SKM.Ccf.Client`, `SKM.Ccf.Scalar` (assemblies and namespaces stay `Ccf.*`); npm `@skm/ccf-client` and `@skm/ccf-typegen` (the CLI command is `ccf-typegen`). The npm scope is lower case because npm rejects capitals in new package names. Symbol packages (`.snupkg`) carry Source Link; CI sets `ContinuousIntegrationBuild`.

## 11. Future

- **Mobile clients**: Swift (iOS) and Kotlin (Android) decoders, validated against the same `test-vectors/`. Planned after phase 5.
- **Feature `n`** (numeric encoding: frame-of-reference / bit-packing for integer and decimal columns). Price is the next-largest column.
- **Request bodies**: an input formatter so clients can POST CCF. Not in this build.
