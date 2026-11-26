# CCF Build Plan

Work through the phases in order. Tick a box only when its work is done and its tests pass. Each phase ends with an exit check; do not start the next phase until it passes. Overlap: the TypeScript decoder part of phase 3 may run in parallel with phase 1 once phase 0 is done; the .NET client and the TypeScript end-to-end test wait for phase 2.

## Repository layout (target)

```
/CLAUDE.md
/docs/                      SPEC.md, ARCHITECTURE.md, PLAN.md, DECISIONS.md
/test-vectors/              golden vectors (read-only)
/dotnet/
  Ccf.sln
  Directory.Build.props     net10.0, nullable, warnings as errors, LangVersion latest
  src/Ccf.Core/
  src/Ccf.AspNetCore/
  src/Ccf.Client/
  tests/Ccf.Core.Tests/
  tests/Ccf.AspNetCore.Tests/
  tests/Ccf.Client.Tests/
  samples/Ccf.SampleApi/
  benchmarks/Ccf.Benchmarks/
/js/
  package.json              workspace root
  packages/client/          @skm/ccf-client (src/, test/)
  packages/typegen/         ccf-typegen CLI
  bench/
```

---

## Phase 0 — Scaffolding and conformance harness

- [x] Create the layout above; `dotnet build` and `npm test` run green on empty projects.
- [x] .NET test helper that loads `test-vectors/manifest.json` and yields one xUnit theory case per vector.
- [x] Same helper for vitest in `js/packages/client`.
- [x] Canonical-JSON comparer for decoded values (rules in `test-vectors/README.md` and SPEC §14.7) in both languages.
- [x] CI workflow (GitHub Actions or equivalent): build + test .NET on net8.0 and net10.0 (net8.0 dropped in phase 5), test JS on Node 20 and 22. Conformance tests are tagged (`Category=Conformance` in .NET, `*.conformance.test.ts` in vitest) and run in separate non-blocking jobs until phase 1 (.NET) and phase 3 (JS). _Written and YAML-validated; first run on GitHub pending._

Conventions set up in phase 0:
- `dotnet test` and `npm test` run everything except the conformance tests. Run those with `dotnet test --filter "Category=Conformance"` and `npm run test:conformance` (in `js/`).
- Harness code reaches the implementation only through one seam: `ConformanceSubject` (.NET, `tests/Ccf.Core.Tests/Conformance/`) and `test/support/subject.ts` (JS). Each member throws `NotImplementedException` / `NotImplementedError` until wired up. Phase 1 / phase 3 replace the stubs and drop the default conformance exclusion (`VSTestTestCaseFilter` in `dotnet/Directory.Build.props`, `CCF_TESTS` default in `js/packages/client/vitest.config.ts`).

**Exit:** scaffold builds; all 15 vectors are discovered and fail with `NotImplementedException`; all other tests pass.

## Phase 1 — `Ccf.Core` (.NET)

Schema
- [x] Schema model: `CcfSchema` (Name, Fields, Id as 4 bytes + hex string, CanonicalJson), `CcfField`, `CcfType`.
- [x] Parse schema JSON; validate per SPEC §3.1 (names, applicable keys, scale range, enum size, unique names).
- [x] Canonicalizer (SPEC §3.2) and id hashing (§3.3).
- [x] Test: for each `schema-*.json`, parse → canonicalize gives identical bytes; id matches manifest.
- [x] Invalid schemas raise `CcfSchemaException`, separate from the format error codes (SPEC §14.3): bad name, scale outside 0–18, inapplicable key (rejected, not dropped), duplicate names, unknown type or key.

Primitives
- [x] uvarint read/write (shortest form, 10-byte max, overflow detection), zigzag, LSB-first bit packing.
- [x] Unit tests for every example in SPEC §5, plus boundaries (0, 127, 128, 2⁶⁴−1, int64 min/max).
- [x] Wire `ConformanceSubject` to `Ccf.Core`, replace the stubs, and remove the default conformance exclusion.

Decoder
- [x] `CcfReader` over `ReadOnlySpan<byte>` with bounds checks raising `CcfFormatException(CcfErrorCode)` for every code in SPEC §11. (`CcfReader` is an internal `ref struct`; the public entry points are `CcfDecoder.DecodeValue`, `Decode<T>` and `PeekSchemaId`.)
- [x] Untyped decode to a value tree (for conformance tests and tools).
- [x] Typed decode to `T` (property matching per ARCHITECTURE §6.2), with accessors cached per (schema, type).
- [x] All valid vectors decode to their expected JSON; all invalid vectors throw exactly `expectedError`.
- [x] One unit test per SPEC §14 clarification (rules 1–7; rule 8 added in phase 3) in `Ccf.Core.Tests`; no files added to `test-vectors/`.

Schema builder
- [x] `CcfSchemaBuilder.TryBuild(Type, CcfSchemaOptions, out CcfSchema, out string reason)` per ARCHITECTURE §5 (mapping, names, order, attributes, nullability, unsupported list).
- [x] `[CcfDelta]`, `[CcfDecimal(scale, Unsigned)]` attributes.
- [x] Tests: C# types that model the 3 vector schemas (`Product`, `AllTypes`, `Order`) build exactly the canonical schema files; each unsupported case returns false with a reason.

Encoder
- [x] `CcfEncoder.Encode(object value, CcfSchema, CcfFeatures, IBufferWriter<byte>, CcfLimits)` returning the flags produced. (Instance method: the constructor takes the `CcfSchemaOptions` naming rules and the dictionary threshold.)
- [x] Dictionary rule and first-appearance order (SPEC §8.1); encode failures as `CcfEncodeException` (SPEC §10.2).
- [x] Conformance: for each valid vector, decode `.expected.json` into the matching C# type, encode with `requestedFeatures`, compare to `.ccf` byte for byte; flags match `expectedFlags`.
- [x] Round-trip property tests (random data for each schema → encode → decode → equal).

Hardening
- [x] Fuzz the decoder (SharpFuzz or a random-mutation test over the vectors): only `CcfFormatException` may escape, no hangs, no allocation beyond limits. Run what the session allows and report the duration.
- [x] Nightly CI job that runs the fuzzer for 1 hour. _Written and YAML-validated; first run on GitHub pending._

**Exit:** all 15 vectors pass both directions on net8.0 and net10.0; fuzz run of 1 hour clean.

_Exit check passed (2026-10-20): all 15 vectors pass both directions on net8.0 and net10.0. Fuzz run of 1 hour (08:50–09:50 UTC, net10.0 Release, 3 threads, seed 20261020): 728,529,220 mutated payloads, 20,191,430 decoded successfully, the rest raised only `CcfFormatException` (all 16 codes seen except none missing from the histogram); no other exception, no hang (slowest decode 404 ms), peak allocation 148,744 bytes for any single decode._

## Phase 2 — `Ccf.AspNetCore`

- [x] `CcfOptions` (DictionaryThreshold, DefaultDecimalScale, Limits, SchemaPathPrefix, AllowQueryOverride) bound from configuration section `Ccf`.
- [x] `ISchemaRegistry` (lazy registration, `Register<T>()`, id collision check, thread-safe).
- [x] `AddCcf()` registrations per ARCHITECTURE §3.1.
- [x] `CcfOutputFormatter` per ARCHITECTURE §3.2, including `features` parsing (quoted and bare), Content-Type echo, `Vary: Accept`, JSON fallback on encode failure.
- [x] Confirm and test how `MediaType` matching handles the `features` parameter. _Result: MVC offers the Accept range with its parameters as `context.ContentType`, and the stock `OutputFormatter.CanWriteResult` (`MediaTypeHeaderValue.IsSubsetOf`) rejects `application/vnd.ccf` against `application/vnd.ccf; features=d`, so `CcfOutputFormatter` compares type/subtype itself and reads `features` from the request. See `CcfMediaTypeMatchingTests`._
- [x] `CcfResults.Ok<T>()` for minimal APIs.
- [x] `MapCcfSchemas()` with immutable caching and 404.
- [x] Logging and the `Ccf` meter (ARCHITECTURE §3.5).
- [x] Development-only `?ccf=d` override (off unless `AllowQueryOverride` and the environment is Development).
- [x] `samples/Ccf.SampleApi`: `/products` returning the 500 sample rows (from `test-vectors/v01-products-plain.expected.json`), `/orders`, `/products/{id}`, plus one unsupported type to show fallback.
- [x] Integration tests with `WebApplicationFactory` (plus one real-Kestrel smoke test): JSON by default; plain CCF for `Accept: application/vnd.ccf` (bytes equal `v01`); dictionary CCF for `features=d` (bytes equal `v02`); single object (`v05`); fallback for unsupported type; schema endpoint 200/404 and headers.

**Exit:** the sample API serves JSON, plain CCF and dictionary CCF from one endpoint, byte-identical to the vectors.

_Exit check passed (2026-10-20): `/products` serves JSON, plain CCF (bytes equal `v01`) and dictionary CCF (bytes equal `v02`); `/products/1001` equals `v05`, `/products/empty` `v06`, `/orders` `v04`, `/all-types` `v03`; minimal APIs return the same bytes. 199 AspNetCore tests pass on net8.0 and net10.0; re-run after the owner's phase 2 decisions (new Accept rule, naming per path, single enumeration, Vary)._

## Phase 3 — `Ccf.Client` (.NET) and `@skm/ccf-client` (TypeScript)

.NET
- [x] `GetFromCcfAsync<T>` with Accept header, schema fetch + hash check + cache, single-flight per schema id, JSON retry rules (ARCHITECTURE §6.1).
- [x] Tests against the sample API: CCF path, JSON path, schema fetched once for many requests, corrupted payload → JSON retry.

TypeScript
- [x] Reader with bounds checks and `CcfFormatError` (codes identical to .NET).
- [x] Schema parse, canonicalize, SHA-256 id. Parsing is async and stores the precomputed id; decoding stays synchronous. Invalid schemas raise `CcfSchemaError` (SPEC §14.3).
- [x] SHA-256 via WebCrypto (`crypto.subtle`) with a small pure-JS fallback for contexts where it is unavailable (insecure browser origins). Used only for schema verification, never on the hot path; no dependency.
- [x] Wire `test/support/subject.ts` to the decoder, replace the stubs, and drop the default conformance exclusion.
- [x] One unit test per SPEC §14 clarification that applies to the decoder (rules 1, 2, 3, 5, 7).
- [x] Decoder for all types, options `int64` and `decimal` (ARCHITECTURE §6.3).
- [x] All 15 vectors pass (decode and errors) in Node; one browser run (Playwright or vitest browser mode). _Browser run: `npm run test:browser` drives a real Chromium through `playwright-core` (decoder over every vector, client flow, real IndexedDB cache across a page reload, pure-JS SHA-256 with `crypto.subtle` removed, corrupted-payload fallback)._
- [x] `createCcfClient` with schema cache, single-flight, JSON retry; pluggable cache interface; an IndexedDB cache implementation.
- [x] `ccf-typegen`: schema JSON or URL → `.ts` interfaces; snapshot tests for the 3 vector schemas.
- [x] Package builds to ESM + `.d.ts`; zero runtime dependencies.

**Exit:** both clients decode every vector identically; end-to-end test from the TS client against the sample API passes.

_TypeScript decoder fuzz (11:15–11:30 UTC, Node 22, seed 20261020): 8,067,241 mutated payloads, 223,366 decoded, all others raised only `CcfFormatError`; slowest decode 46.6 ms; no other error._

_Exit check passed (2026-10-20): .NET and TypeScript decoders both pass all 15 vectors (decode and errors) against the same `manifest.json`; the .NET client and the TS client decode the sample API's responses to exactly the vectors' expected values (`npm run test:e2e` runs the TS client against the real .NET sample: 7 tests). Re-run after the owner's phase 3 decisions: .NET client 48, Core 356/357, AspNetCore 199 per framework; TypeScript 259 (client) + 28 (typegen) in Node 22 (Node 20.20 verified earlier via `npx node@20`); 5 browser tests in Chromium; 7 end-to-end tests; Release build with 0 warnings._

## Phase 4 — Benchmarks (go / no-go)

- [x] .NET (BenchmarkDotNet): encode and decode of the 500-row sample and 3 larger synthetic sets (5k rows; mostly-unique strings; deeply nested orders) vs System.Text.Json (serialize/deserialize), including Gzip/Brotli decompression time for JSON.
- [x] JS: decode vs `JSON.parse` (+ `DecompressionStream` for gzip) in Node and one browser.
- [x] Size table for the same sets (raw CCF plain/dict vs JSON raw/gzip/brotli).
- [x] Write results into `docs/BENCHMARKS.md`.

**Go criteria:** CCF decode faster than JSON parse + decompress on both platforms for the 500-row and 5k sets, and raw CCF dictionary size ≤ Gzip JSON size. If .NET fails them, stop and report before phase 5. If .NET meets them but JS does not beat `JSON.parse`, report the numbers and continue.

## Phase 5 — Rollout readiness

- [x] Package metadata, READMEs and XML docs for the three NuGet packages and the npm packages. _Version 0.1.0 (SemVer, one number for all packages); NuGet ids `SKM.Ccf.*`, npm `@skm/ccf-client` and `@skm/ccf-typegen`; LICENSE, Source Link symbols; `dotnet pack` and `npm pack` run in CI; throwaway consumer projects installed the .nupkg files and the tarballs and decoded a golden vector. Nothing is published (the owner publishes)._
- [x] Opt-in guide: enabling CCF on selected endpoints, pre-registering schemas, configuration reference. _`docs/GUIDE.md`._
- [x] Dashboards/queries for the `Ccf` meter: share of responses served as CCF, fallbacks, encode failures, bytes saved. _`docs/OBSERVABILITY.md` (PromQL panels and alerts); `ccf.responses{format=json}` added so the share of all responses can be computed; bytes saved is an estimate._
- [x] Browser tests on WebKit and Firefox as well as Chromium (the pinned `playwright-core@1.56.1`; CI installs the matching browsers). _The test takes `CCF_BROWSER=chromium|firefox|webkit` and CI runs a three-engine matrix; Chromium passed locally; Firefox and WebKit could not be downloaded in the sandbox, so CI was their first run: PR #3, run 39439255203, `js-browser (chromium)`, `(firefox)` and `(webkit)` all passed (5 tests each)._
- [x] Versioning policy: format version stays 1; packages use SemVer; new features add letters, never change existing encodings. _`docs/VERSIONING.md`._
- [x] `SKM.Ccf.Scalar` (owner request): OpenAPI and Scalar tooling so projects do not copy the sample's code: `AddCcf()` for OpenAPI, `MapCcfViewer()` (decode endpoint and script, Development only unless enabled) and `WithCcfViewer()` for Scalar, with the "CCF decode" toggle (off by default, applies to the next request). Approved dependencies: `Scalar.AspNetCore` (2.18.x at first; widened to `[2.18.2,3.0.0)` on 2026-11-26 after the .NET tests and the Chromium toggle test passed on 2.21.0) and `Microsoft.AspNetCore.OpenApi`. Tests: .NET (decode, environments, conventions, OpenAPI) and a Chromium test of the toggle in the real Scalar UI (`js-e2e`).

- [x] Public release preparation (owner request, 2026-11-26): MIT license (was internal/UNLICENSED); a real root `README.md` (pitch, quick start, honest numbers, when not to use it, comparison with other formats); package READMEs and tags say "Columnar Compact Format" and that it is unrelated to Microsoft's Confidential Consortium Framework; `Scalar.AspNetCore` range widened to `[2.18.2,3.0.0)` (tests and the Chromium toggle test pass on 2.21.0); the `export` command also measures CCF with HTTP gzip/brotli (44–61 % of compressed JSON on repetitive data); `docs/BENCHMARKS.md` explains that browsers decompress natively, so the browser comparison is against `JSON.parse`; `docs/GUIDE.md` shows how to enable response compression for CCF (checked against a running app and `GetFromCcfAsync`).

**Exit:** packages publishable; rollout guide reviewed by the project owner.

## Future — Mobile (not in current scope)

- [ ] Swift package (iOS) decoder + client, validated against `test-vectors/`.
- [ ] Kotlin library (Android) decoder + client, validated against `test-vectors/`.

## Open questions

Ambiguities found before phase 0 were resolved by the project owner and recorded as SPEC §14 (clarifications v1.0.1). Add new questions here as they come up, with the phase and the SPEC section they concern.

_None open._

### Resolved by the project owner after phase 5

1. **License.** ~~INTERNAL, UNLICENSED, private feed only.~~ Changed by the owner on 2026-11-26: **MIT**, for a public release on nuget.org and npmjs.com. The `LICENSE` file at the repository root is copied into each npm package (checked by CI); NuGet packages declare `PackageLicenseExpression` `MIT`; npm `license` is `MIT`. The repository still publishes nothing: the owner publishes the prepared packages.
2. **Package ids.** NuGet `SKM.Ccf.Core`, `SKM.Ccf.AspNetCore`, `SKM.Ccf.Client` (assemblies and namespaces stay `Ccf.*`); author `SamuraiKenshin`. npm: the requested `@SKM/ccf-client` and `@SKM/ccf-typegen` are **invalid** (npm rejects capital letters in new package names; checked with npm's own validator), so the packages are `@skm/ccf-client` and `@skm/ccf-typegen`. **Scope confirmed by the owner.** The CLI command is still `ccf-typegen`.
3. **Source Link** with the SDK built-in support (no package): `PublishRepositoryUrl`, `EmbedUntrackedSources`, `IncludeSymbols`, `SymbolPackageFormat=snupkg`; `ContinuousIntegrationBuild=true` set for all CI jobs. Checked: the pdb in the `.snupkg` carries the Source Link mapping to the repository.
4. **Metrics.** `ccf.responses{format=json}` added for JSON responses of CCF-capable endpoints (MVC and minimal APIs; each response counted once, a fallback only as `json-fallback`), tests included. No JSON-size counter: "bytes saved" is documented as an estimate from the ratios in `docs/BENCHMARKS.md`.
5. **Enums mirror System.Text.Json.** String-written enums (global converter, `[JsonConverter]` on the property or the type) are CCF enums with the names STJ writes; all others are integer columns with the underlying value (`int32` for `int`-backed enums; other underlying types map to their matching integer type). `[Flags]` written as strings and custom converters stay JSON-only. Changed behaviour: an enum with no string converter used to be an enum column and is now an integer column, so applications without `JsonStringEnumConverter` get new schema ids for such types.

### Resolved by the project owner after phase 3

1. **Logging.** `Ccf.Client` depends on `Microsoft.Extensions.Logging.Abstractions` (approved) and takes an optional `ILogger<CcfClient>` (from DI when registered, `NullLogger` otherwise). The `Log` callback is gone; the TS client keeps its `log` option.
2. **`playwright-core` is pinned** to exactly `1.56.1`; CI installs browsers matching the pinned version. Follow-up in phase 5: run the browser tests on WebKit and Firefox too.
3. **Datetime range** is now SPEC §14.8: decoders MAY reject values outside the platform's range with `ValueOutOfRange` but must accept 0001-01-01 to 9999-12-31 UTC (a test in each implementation covers both ends).
4. **Client behaviour**, documented in both client READMEs: `204 No Content` and an empty body give `null`; only a CCF format error or an unverifiable schema triggers the JSON retry, only for requests safe to repeat; HTTP error statuses and usage errors surface unchanged; the TS `int64: "number"` `RangeError` beyond 2^53 is intentional (JSON would silently lose precision) and is never retried.
5. **End-to-end CI job is required** on pull requests: `js-e2e` runs on every PR, and an aggregate `required` job gives one check name to mark as required in branch protection (a repository setting that must be switched on by an admin; it cannot be done from the workflow). The command is in CLAUDE.md.

Kept as decided earlier in phase 3: TS decimals default to `number` (`decimal: "string"` is exact); schema verification is strict (hash and canonical form, no cross-origin redirects, persistent entries re-verified); `ccf-typegen` depends on `@skm/ccf-client`; the end-to-end test needs the .NET SDK and is a separate script and CI job; no on-disk `ICcfSchemaCache` ships.

### Resolved by the project owner after phase 2

1. **Integration tests use `WebApplicationFactory`** (`Microsoft.AspNetCore.Mvc.Testing`, test projects only, approved). One real-Kestrel smoke test (`KestrelSmokeTests`) remains.
2. **Accept rule.** MVC semantics with one exception, implemented inside CCF (selector wrapper for MVC, same logic in `CcfResults`; `RespectBrowserAcceptHeader` untouched): an explicit `application/vnd.ccf` with `q > 0` wins over `*/*` unless an explicit media type has a higher `q`. `q=0` is not acceptable. Consequences of the literal rule: wildcards never compete (so `*/*;q=0.9, application/vnd.ccf;q=0.2` yields CCF) and equal-`q` explicit ranges keep header order (`application/json, application/vnd.ccf` yields JSON). Tests cover the four requested headers on both paths.
3. **Naming per path.** CCF uses the JSON options of the path it serves (MVC `JsonOptions`; Http `JsonOptions` for minimal APIs). The registry is keyed by (type, naming policy); a startup warning is logged once if the two policies differ.
4. **`.WithCcf()`** is skipped; noted in ARCHITECTURE §3.3 as later and optional.
5. **Single enumeration.** A lazy `IEnumerable<T>` response is materialized exactly once before encoding and the JSON fallback reuses the list; tests with a counting enumerable (controllers and minimal APIs, CCF, fallback and JSON) assert one enumeration. Lazy sequences nested inside an object are enumerated once by the encoder but would be re-enumerated by a JSON fallback; materialize them in the handler if that matters.
6. **`Vary: Accept`** is sent on every response to a request whose `Accept` mentions `application/vnd.ccf`, whatever the outcome. The earlier rule (JSON responses of CCF-capable endpoints vary too, SPEC §2.3 MUST) is kept as well.

Smaller choices kept from phase 2: runtime collection types (LINQ iterators, `HashSet<T>`) are served through `IEnumerable<T>`; schema 404s carry no cache header (the id may be registered later).

### Decisions taken in phase 1 that go beyond SPEC §14 (please confirm or overrule)

None of these changes the wire format; each is a point the spec leaves open.

1. **`$value` placement (§3.1, §7.3).** The reserved name `$value` is accepted only as the single field of a `list`; anywhere else (root, object, or next to other list fields) the schema is rejected with `CcfSchemaException`.
2. **Enum members (§3.1).** Enum `values` must be unique identifiers; duplicates or non-identifiers are rejected as invalid schemas.
3. **Empty field lists (§3.1).** The root `fields` array needs at least one field, like child lists.
4. **Datetime range (§6.5).** Resolved: now SPEC §14.8 (see phase 3 decisions).
5. **Datetime truncation (§6.5).** Sub-millisecond ticks are floored (toward −∞), so 1969-12-31T23:59:59.9999999Z encodes as −1 ms.
6. **Unsigned decimals (§3.1, §6.4).** A decoder rejects an unsigned decimal whose stored value is negative (including a negative running delta) or above int64 with `ValueOutOfRange`.
7. **Header flag D (§8).** The decoder does not verify that flag `D` is set whenever a dictionary column exists; no error code is defined for it.
8. **Typed decode usage errors.** `CcfDecoder.Decode<T>` raises `InvalidOperationException` (not a `CcfFormatException`) when `T` cannot receive the schema, so clients do not mistake it for a bad payload and retry as JSON.
9. **Inapplicable keys given as `false` (§14.3).** Rejected too (e.g. `"delta": false` on a string), since the spec says inapplicable keys are rejected, not dropped.
