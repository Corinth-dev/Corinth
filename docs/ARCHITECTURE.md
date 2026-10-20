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
| `Ccf.Client` (.NET) | `HttpClient` extensions, schema cache, JSON fallback | `Ccf.Core`, `System.Net.Http.Json` |
| `@ccf/client` (npm) | Decoder, schema cache, `fetch` wrapper, TS type generator CLI | None at runtime |

## 3. Server: ASP.NET Core integration

### 3.1 Registration

```csharp
builder.Services.AddCcf(o =>
{
    o.DictionaryThreshold = 0.5;          // §8.1 of SPEC; 0.5 is the conformance default
    o.Limits.MaxRows = 100_000;
    o.SchemaPathPrefix = "/ccf/schemas";
});
// …
app.MapCcfSchemas();                        // GET /ccf/schemas/{id}
```

`AddCcf` registers:

| Service | Lifetime | Role |
| --- | --- | --- |
| `IOptions<CcfOptions>` | singleton | Threshold, limits, path prefix, dev query override; bindable from `appsettings.json` section `Ccf` |
| `ISchemaRegistry` | singleton | `Type → CcfSchema` cache and `id → CcfSchema` index, thread-safe (`ConcurrentDictionary`) |
| `CcfEncoder` | singleton | Stateless, thread-safe |
| `CcfOutputFormatter` | added to `MvcOptions.OutputFormatters` | MVC controllers get CCF through normal negotiation |

It is not a hosted/background service: nothing runs unless a request asks for CCF.

### 3.2 Output formatter

`CcfOutputFormatter : OutputFormatter`, supported media type `application/vnd.ccf`.

1. `CanWriteResult`: true only when the selected media type is `application/vnd.ccf` **and** `ISchemaRegistry.TryGetSchema(objectType)` succeeds **and** the value is not null. Otherwise false, so MVC moves on to the JSON formatter (fallback).
2. Parse `features` from the request's matching `Accept` media range (quoted or bare, SPEC §2.1). Unknown letters ignored.
3. Encode the whole payload into a pooled buffer (`ArrayBufferWriter<byte>` or `ArrayPool`-backed writer).
4. On `CcfEncodeException`: log a warning with the schema id and reason, then write the response as JSON with the app's configured `JsonSerializerOptions` (headers have not been sent yet).
5. On success: set `Content-Type` per SPEC §2.3 from the flags actually produced, add `Vary: Accept`, set `Content-Length`, copy the buffer to the body.

Verified in phase 2 (`CcfMediaTypeMatchingTests`): MVC passes the Accept range, parameters included, as `context.ContentType`, but the stock `OutputFormatter.CanWriteResult` only matches when the formatter's media type carries every parameter of that range, so `application/vnd.ccf` never matches `application/vnd.ccf; features=d`. `CcfOutputFormatter` therefore overrides `CanWriteResult`: it compares type/subtype only, requires that `CcfNegotiation.TryNegotiate` agrees (so `q=0` is honoured), reads `features` from the request, and sets the response `Content-Type` itself from the flags produced. It is registered after the built-in formatters and never claims a response without an explicit `application/vnd.ccf` offer, so wildcard and absent `Accept` headers stay JSON. MVC treats an `Accept` containing `*/*` as no preference (unless `RespectBrowserAcceptHeader`); `CcfResults` follows the same rules. The JSON fallback delegates to the first registered JSON `TextOutputFormatter`, and a result filter adds `Vary: Accept` to JSON responses of CCF-capable endpoints. The development override is an `IStartupFilter` middleware that rewrites `Accept`.

### 3.3 Minimal APIs

`CcfResults.Ok<T>(T value)` returns an `IResult` that performs the same negotiation (CCF if accepted and supported, otherwise `TypedResults.Ok(value)` as JSON). An endpoint filter `.WithCcf()` MAY be added to wrap existing handlers.

### 3.4 Schema endpoint

`MapCcfSchemas()` maps `GET {prefix}/{id:regex(^[0-9a-f]{{8}}$)}` to the registry. Returns the canonical JSON with the immutable cache header (SPEC §2.4), 404 for unknown ids. Schemas are registered lazily the first time a type is encoded; `ISchemaRegistry.Register<T>()` lets apps pre-register types at startup so the endpoint knows them before the first request (important when several server instances run behind a load balancer).

### 3.5 Observability

- `ILogger` category `Ccf`: debug on schema registration, warning on fallback after an encode failure.
- `System.Diagnostics.Metrics` meter `Ccf`: counters `ccf.responses` (tag `format` = ccf/json-fallback, `features`), `ccf.bytes_written`, `ccf.encode_failures`.

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
| `enum` (non-`[Flags]`) | `enum`, `values` = member names in declaration order (respecting `[JsonStringEnumMemberName]` if present) |
| `DateTime`, `DateTimeOffset` | `datetime` (`DateTime` Unspecified is treated as UTC) |
| `Guid` | `guid` |
| `Nullable<T>` | the type of `T`, `nullable: true` |
| class / record / struct with public properties | `object` |
| `T[]`, `List<T>`, `IList<T>`, `IReadOnlyList<T>`, `IEnumerable<T>`, `ICollection<T>` | `list` (scalar `T` → `$value` field) |

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

1. Send `Accept: application/vnd.ccf; features=d, application/json;q=0.5`.
2. If the response `Content-Type` is JSON → parse as JSON (fallback is normal, not an error).
3. If CCF → read the schema id from bytes 4–7; get the schema from the cache, or fetch `GET /ccf/schemas/{id}` once (concurrent requests for the same id share one fetch), verify the hash of the returned text equals the id, cache it.
4. Decode. On a CCF format error or a failed schema fetch → log it and **repeat the original request once with `Accept: application/json`**. Only requests that are safe to repeat (GET, HEAD, or explicitly marked idempotent) are retried; otherwise the error surfaces.
5. Schema cache: in memory by default; pluggable (`ICcfSchemaCache` / `SchemaCache` interface) for persistent storage (IndexedDB in browsers, disk on .NET).

### 6.2 .NET client (`Ccf.Client`)

- `HttpClient.GetFromCcfAsync<T>(string uri, CancellationToken)` mirroring `GetFromJsonAsync<T>`.
- Decodes into `T` by matching schema field names to properties with the same naming rules as the server (§5.2). Schema fields with no matching property are skipped (block length); properties with no matching field keep their default. Matching is by exact JSON name first, then case-insensitively. Narrowing into a smaller CLR type is range-checked (`ValueOutOfRange`). A CLR type that cannot receive the schema (incompatible property type, abstract type, several rows into a single object) raises `InvalidOperationException`: a usage error, not a format error. Datetimes outside the .NET `DateTime` range (years 1–9999) are rejected by the .NET decoder with `ValueOutOfRange`.
- Decimals decode exactly to `decimal`; datetimes to `DateTimeOffset` (offset 0) or `DateTime` (Kind Utc).

### 6.3 TypeScript client (`@ccf/client`)

- Runs in browsers and Node 20+; ES modules; zero runtime dependencies; uses `DataView`, `TextDecoder("utf-8", { fatal: true })`.
- `createCcfClient({ baseUrl, fetch?, schemaCache?, features?, int64?, decimal? })` → `client.get<T>(path, init?)`; lower level `decode(bytes, schema, options)`, `readSchemaId(bytes)`.
- Value mapping: int64/uint64 → `number` by default, throwing a `RangeError` if a value is beyond `Number.MAX_SAFE_INTEGER`; option `int64: "bigint"`. Decimal → `number` by default; option `decimal: "string"` for exact values. Datetime → `Date`. Guid → lowercase string. Enum → member name string.
- `ccf-typegen` CLI: fetches or reads schemas and writes TypeScript interfaces.

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

**Parse speed has not been measured yet**; it is the go/no-go gate in PLAN.md phase 4.

## 10. Future

- **Mobile clients**: Swift (iOS) and Kotlin (Android) decoders, validated against the same `test-vectors/`. Planned after phase 5.
- **Feature `n`** (numeric encoding: frame-of-reference / bit-packing for integer and decimal columns). Price is the next-largest column.
- **Request bodies**: an input formatter so clients can POST CCF. Not in this build.
