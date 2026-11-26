# CCF — Columnar Compact Format

**Smaller, faster API responses for ASP.NET Core, without changing your API.**

CCF is an opt-in binary response format for JSON APIs. A client that asks for it (`Accept: application/vnd.ccf; features=d`) gets the same data as the JSON response, laid out in columns, with no field names on the wire. Every other client keeps getting exactly the JSON it gets today. Anything CCF cannot encode falls back to JSON automatically.

> CCF here is the *Columnar Compact Format* for API responses. It is not related to Microsoft's Confidential Consortium Framework, which shares the acronym.

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE) ![.NET 10](https://img.shields.io/badge/.NET-10-512bd4) ![Node 20+](https://img.shields.io/badge/node-20%2B-339933) ![status: 0.1 preview](https://img.shields.io/badge/status-0.1%20preview-orange)

## Why

On read-heavy endpoints that return tables (lists of products, orders, rows for a grid or dashboard), measured on the repository's benchmark sets ([`docs/BENCHMARKS.md`](docs/BENCHMARKS.md)):

| | products, 5,000 rows | orders, 5,000 rows (nested) |
| --- | ---: | ---: |
| Size: raw CCF vs gzip JSON | **68 %** | **85 %** |
| Size: gzip CCF vs gzip JSON (both compressed) | **50 %** | **56 %** |
| .NET client decode vs gzip JSON + System.Text.Json | **11× faster** | **4.6× faster** |
| .NET server encode vs JSON + gzip | **6× less CPU** | **3× less CPU** |
| Browser decode vs `JSON.parse` (Chromium) | 1.2× faster | 1.85× faster |

In a browser the gain is mainly **bandwidth and server CPU**: browsers decompress JSON natively, so decode time there is roughly even. .NET and Node clients also get the large decode speed-up.

## How it works

- **Columnar.** All values of one field are stored together: integers as varints (optionally delta-coded), booleans and enums bit-packed, repetitive strings as a per-column dictionary, nulls as a bitmap only on nullable columns. Nested objects and lists stay columnar.
- **No field names.** A payload names its schema by a 4-byte content hash. The client fetches the schema once from `GET /ccf/schemas/{id}` (immutable, cached forever) and verifies the hash.
- **Same contract as JSON.** The schema is built from your C# types using System.Text.Json's names, order, enum handling and nullability, so CCF and JSON describe the same data. No `.proto` files, no `[Key]` attributes.
- **Never a dependency.** JSON is the default. Unsupported types, encode failures and old clients get JSON. The clients retry once as JSON if a CCF payload or schema fails.

The wire format is specified in [`docs/SPEC.md`](docs/SPEC.md) and pinned by golden test vectors in [`test-vectors/`](test-vectors/) that both the .NET and the TypeScript implementation pass in both directions.

## Quick start

**Server** (`dotnet add package SKM.Ccf.AspNetCore`)

```csharp
builder.Services.AddControllers();
builder.Services.AddCcf();

var app = builder.Build();
app.MapControllers();                                          // controllers: CCF is negotiated automatically
app.MapCcfSchemas();                                           // GET /ccf/schemas/{id}
app.MapGet("/products", (Db db) => CcfResults.Ok(db.Products)); // minimal APIs opt in per endpoint
```

```bash
curl -H 'Accept: application/vnd.ccf; features=d' http://localhost:5000/products -o products.ccf
```

**.NET client** (`dotnet add package SKM.Ccf.Client`)

```csharp
var products = await http.GetFromCcfAsync<List<Product>>("/products");
```

**Browser / Node 20+** (`npm install @skm/ccf-client`)

```ts
import { createCcfClient, IndexedDbSchemaCache } from "@skm/ccf-client";

const ccf = createCcfClient({ baseUrl: "https://api.example.com", schemaCache: new IndexedDbSchemaCache() });
const products = await ccf.get<Product[]>("/products"); // CCF when offered, JSON otherwise
```

Generate the TypeScript types from a schema with `npx @skm/ccf-typegen https://api.example.com/ccf/schemas/<id>`.

Read the [opt-in guide](docs/GUIDE.md) before rolling out: choosing endpoints, pre-registering schemas behind a load balancer, caches and `Vary: Accept`, enabling HTTP compression for CCF, and the configuration reference.

## When to use it, and when not

**Good fit:** list and table endpoints with numeric, boolean, enum, date or repetitive string columns; high-volume internal APIs; dashboards and data grids; .NET-to-.NET or Node-to-.NET calls; mobile or metered networks.

**Not a good fit:**
- Payloads dominated by long, mostly unique strings (free text, emails): raw CCF is 3× the size of gzip JSON there, and decoding in browsers is slower than `JSON.parse`.
- Small responses (a few hundred bytes): the gain is negligible.
- Public APIs whose consumers will not add a client library.
- Types CCF does not support yet, which are served as JSON: `DateOnly`, `TimeOnly`, `TimeSpan`, `byte[]`, dictionaries, polymorphic hierarchies, `object`/`JsonElement`, custom converters.

## Compared with other formats

| | CCF | MessagePack | Protobuf | MemoryPack | Apache Arrow IPC |
| --- | --- | --- | --- | --- | --- |
| Layout | columnar | row | row | row | columnar |
| Field names in each payload | no (schema by hash) | yes, or integer keys | no (field numbers) | no | schema in each stream |
| Changes to your C# types | none | `[Key]` or contractless | `.proto` or field numbers | `[MemoryPackable]` | build record batches |
| Negotiated next to JSON, with fallback | built in | formatter only | via gRPC/transcoding | formatter only | manual |
| TypeScript client | yes, zero dependencies | yes | yes | code generation | yes, large library |
| Best for | tabular REST responses | RPC, SignalR, caching | service-to-service | .NET-to-.NET | analytics data |

## Packages

| Package | What it is |
| --- | --- |
| `SKM.Ccf.Core` | Schema, encoder, decoder. BCL only. |
| `SKM.Ccf.AspNetCore` | Output formatter, negotiation, schema endpoint, `CcfResults.Ok`, metrics. |
| `SKM.Ccf.Client` | `HttpClient` helper with schema cache and JSON fallback. |
| `SKM.Ccf.Scalar` | Development tooling: CCF in the OpenAPI document and a decode toggle in the Scalar UI. |
| `@skm/ccf-client` | Browser and Node 20+ decoder and client. |
| `@skm/ccf-typegen` | TypeScript interfaces from a CCF schema. |

All packages share one version and follow [SemVer](docs/VERSIONING.md). They are `0.x`: the public API may still change; the wire format (version 1) will not.

## Quality

- Normative spec and 15 golden vectors, passed byte for byte by the .NET encoder and by both decoders.
- Decoder fuzzing: 728 million mutated payloads in one hour (.NET) with no unexpected exception, hang or unbounded allocation; a short fuzz run is part of every test run.
- About 950 tests across .NET and TypeScript; browser tests in Chromium, Firefox and WebKit; end-to-end tests of the TypeScript client against the real ASP.NET Core sample.
- Metrics (`Ccf` meter) with ready-made queries in [`docs/OBSERVABILITY.md`](docs/OBSERVABILITY.md).

## Documentation

[Specification](docs/SPEC.md) · [Opt-in guide](docs/GUIDE.md) · [Architecture](docs/ARCHITECTURE.md) · [Benchmarks](docs/BENCHMARKS.md) · [Observability](docs/OBSERVABILITY.md) · [Versioning](docs/VERSIONING.md)

## Building from source

```bash
cd dotnet && dotnet build && dotnet test      # .NET 10 SDK
cd js && npm install && npm test              # Node 20+
cd dotnet && dotnet run --project samples/Ccf.SampleApi   # sample API, Scalar UI at /scalar/v1
```

## License

[MIT](LICENSE)
