# Opt-in guide: adding CCF to an ASP.NET Core API

CCF is an optimisation, never a dependency. JSON remains the default; a client gets CCF only when it asks for it with `Accept: application/vnd.ccf` (optionally `; features=d`). Everything CCF cannot serve is answered in JSON, so enabling it cannot break an existing client.

## 1. Before you start

- Measure first. CCF pays off on tables whose columns repeat, are numeric or are small (`docs/BENCHMARKS.md`: dictionary-mode decode 2.3x–6.1x faster than System.Text.Json source-gen across the four benchmark sets, and 11%–13% of the raw JSON size on repetitive data). It does **not** help payloads dominated by long, mostly unique strings (`docs/ARCHITECTURE.md` §9.2): keep those endpoints on JSON.
- Packages: `Ccf.AspNetCore` on the server (it brings `Ccf.Core`), `Ccf.Client` for .NET callers, `@ccf/client` for browsers and Node 20+. Server targets `net8.0` and `net10.0`.

## 2. Enable it

```csharp
builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));   // see "Contract pitfalls"
builder.Services.AddCcf();                 // options bind from the "Ccf" configuration section

var app = builder.Build();
app.MapControllers();
app.MapCcfSchemas();                       // GET /ccf/schemas/{id}: clients fetch schemas here
```

`AddCcf` adds the output formatter and the negotiation rule; nothing runs unless a request asks for CCF. `MapCcfSchemas` is required: a client that receives CCF fetches the schema once from the same origin and caches it forever (the id is a content hash).

Try it: `curl -H 'Accept: application/vnd.ccf; features=d' http://localhost:5000/products -o out.ccf` (the response `Content-Type` tells you what you got). In Development, set `Ccf:AllowQueryOverride` to `true` and use `?ccf=d` in a browser.

## 3. Choosing which endpoints serve CCF

| Endpoint kind | Default | How to opt in / out |
| --- | --- | --- |
| MVC controller action returning a supported type | CCF is available to clients that ask | Opt **out** per action or controller with `[Produces("application/json")]` (CCF is never offered there). |
| Minimal API | JSON only | Opt **in** per endpoint: `return CcfResults.Ok(value);` instead of `Results.Ok(value)`. |
| Unsupported type, `null`, a scalar, or data that fails to encode | JSON, automatically | Nothing to do. The reason is logged (category `Ccf`) and counted (`ccf.encode_failures`). |

Rollout order we recommend: one read-mostly list endpoint with repetitive columns, then the others, watching the metrics in `docs/OBSERVABILITY.md` after each step. There is no global "off" switch needed: clients that stop sending the CCF `Accept` header get JSON immediately, and removing `AddCcf` returns the server to JSON-only.

## 4. Contract pitfalls

The schema mirrors what System.Text.Json would emit (names, order, nullability), with these differences to check on each type (full table: `docs/ARCHITECTURE.md` §5):

- **Enums are always sent as member names.** If your JSON options do not use `JsonStringEnumConverter`, JSON sends numbers and CCF sends names, so the two contracts differ. Add the converter (as above) or keep such endpoints on JSON.
- **Decimals have a fixed scale.** `[CcfDecimal(scale)]`, else `Ccf:DefaultDecimalScale` (2). A value with more fractional digits than the scale makes the whole response fall back to JSON (counted as an encode failure), so set the scale to what the data really has.
- **Dates:** `DateTime`/`DateTimeOffset` become UTC datetimes with millisecond precision (sub-millisecond ticks are floored).
- **Unsupported members make the whole type JSON-only:** `object`, dictionaries, `JsonElement`/`JsonNode`, polymorphic types, `[Flags]` enums, `char`, `byte[]`, `TimeSpan`, `DateOnly`/`TimeOnly`, recursive types, nesting deeper than 8, custom `JsonConverter`s on a member.
- **Reference types without nullable annotations are treated as nullable.** Enable nullable reference types to get smaller non-nullable columns.
- **Hints:** `[CcfDelta]` on ids, counters and timestamps that grow steadily (delta-coded).
- **Lazy sequences** (`IEnumerable<T>`, EF queries) returned as the response are enumerated exactly once. Sequences nested inside an object are enumerated by the encoder and would be enumerated again by a JSON fallback; materialise them in the handler.
- **Naming.** CCF follows the JSON options of the path that serves it (MVC `JsonOptions` for controllers, `ConfigureHttpJsonOptions` for minimal APIs). If the two differ, the same type gets two schemas and ids, and a warning is logged at startup.

## 5. Pre-registering schemas (several instances)

Schemas are registered lazily the first time a type is encoded. Behind a load balancer a client may receive CCF from instance A and ask instance B for the schema before B has ever encoded that type, which gives a 404 and a JSON retry. Register at startup so every instance knows every schema:

```csharp
var registry = app.Services.GetRequiredService<ISchemaRegistry>();
registry.Register<List<Product>>();            // MVC naming
registry.Register(typeof(List<Order>), registry.HttpSchemaOptions);   // a type served by minimal APIs
```

`Register` throws if the type is unsupported or its id collides with another schema, so a bad type fails at startup instead of in production. A schema's id changes whenever the type's schema changes; during a rolling deployment both versions' schemas must stay reachable, which holds as long as each instance registers its own types at startup.

## 6. HTTP infrastructure

- **Caches and CDNs must honour `Vary: Accept`.** CCF sends it on every response to a request that mentioned `application/vnd.ccf` and on JSON responses of CCF-capable endpoints. A cache that ignores `Vary` can serve CCF to a JSON client.
- **Schema endpoint:** `Cache-Control: public, max-age=31536000, immutable` on hits; 404s are not cacheable. It must be reachable on the **same origin** as the API (clients refuse schemas that arrive after a cross-origin redirect).
- **Compression:** CCF applies none (out of scope). Server-side response compression of `application/vnd.ccf` is possible but unmeasured; CCF already beats gzip JSON on size for suitable data.

## 7. Configuration reference

Bound from the `Ccf` section of configuration (`appsettings.json`, environment, …) and/or `AddCcf(o => …)`. Validated on start.

| Option | Default | Meaning |
| --- | --- | --- |
| `DictionaryThreshold` | `0.5` | A string column uses dictionary mode when fewer than this fraction of its present values are distinct and the client allowed feature `d`. `0 < x <= 1`. 0.5 is the conformance default; other values produce valid but non-reference bytes. |
| `DefaultDecimalScale` | `2` | Scale of `decimal` properties without `[CcfDecimal]`. 0–18. |
| `Limits.MaxRows` | `100000` | Most rows in any one table, root or child. A larger response is served as JSON. |
| `Limits.MaxPayloadBytes` | `33554432` | Largest payload (32 MiB). |
| `Limits.MaxStringBytes` | `1048576` | Longest single string (1 MiB). |
| `SchemaPathPrefix` | `/ccf/schemas` | Route prefix of the schema endpoint; clients use the same default. Must start with `/`. |
| `AllowQueryOverride` | `false` | `?ccf=d` requests CCF without an `Accept` header. Honoured only in the Development environment. |

In code, `Limits` is an init-only object: `o.Limits = new CcfLimits { MaxRows = 50_000 };`.

## 8. Clients

- **.NET:** `await http.GetFromCcfAsync<List<Product>>("/products")`, or keep a `CcfClient` (see its README). Failures of CCF itself (corrupt payload, schema unavailable) are retried once as JSON for GET/HEAD and requests you mark idempotent.
- **Browser / Node:** `createCcfClient({ baseUrl })`, with `IndexedDbSchemaCache` to persist schemas across page loads. `ccf-typegen` generates TypeScript types from a schema. int64 values beyond 2^53 throw `RangeError` unless you ask for `int64: "bigint"`.
- Anything else keeps getting JSON.

## 9. Troubleshooting

| Symptom | Likely cause |
| --- | --- |
| Always JSON although `Accept: application/vnd.ccf` is sent | Minimal API without `CcfResults.Ok`; `[Produces("application/json")]`; an unsupported type (check the debug log of category `Ccf`); a JSON range with higher `q` listed explicitly; the proxy strips `Accept`. |
| `ccf.encode_failures` rising | Decimal with more digits than its scale, a null in a non-nullable column, a limit exceeded. The warning log names the schema id and reason. |
| Client logs "repeating the request as JSON" | The payload failed to decode or the schema fetch failed (404 behind a load balancer: pre-register schemas; redirect to another origin; cache/proxy altering bytes). |
| JSON and CCF values differ | Enum representation (section 4) or a naming-policy mismatch. |
