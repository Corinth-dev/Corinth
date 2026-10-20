# CCF Build Plan

Work through the phases in order. Tick a box only when its work is done and its tests pass. Each phase ends with an exit check; do not start the next phase until it passes. Overlap: the TypeScript decoder part of phase 3 may run in parallel with phase 1 once phase 0 is done; the .NET client and the TypeScript end-to-end test wait for phase 2.

## Repository layout (target)

```
/CLAUDE.md
/docs/                      SPEC.md, ARCHITECTURE.md, PLAN.md, DECISIONS.md
/test-vectors/              golden vectors (read-only)
/dotnet/
  Ccf.sln
  Directory.Build.props     net8.0;net10.0, nullable, warnings as errors, LangVersion latest
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
  packages/client/          @ccf/client (src/, test/)
  packages/typegen/         ccf-typegen CLI
  bench/
```

---

## Phase 0 — Scaffolding and conformance harness

- [x] Create the layout above; `dotnet build` and `npm test` run green on empty projects.
- [x] .NET test helper that loads `test-vectors/manifest.json` and yields one xUnit theory case per vector.
- [x] Same helper for vitest in `js/packages/client`.
- [x] Canonical-JSON comparer for decoded values (rules in `test-vectors/README.md` and SPEC §14.7) in both languages.
- [x] CI workflow (GitHub Actions or equivalent): build + test .NET on net8.0 and net10.0, test JS on Node 20 and 22. Conformance tests are tagged (`Category=Conformance` in .NET, `*.conformance.test.ts` in vitest) and run in separate non-blocking jobs until phase 1 (.NET) and phase 3 (JS). _Written and YAML-validated; first run on GitHub pending._

Conventions set up in phase 0:
- `dotnet test` and `npm test` run everything except the conformance tests. Run those with `dotnet test --filter "Category=Conformance"` and `npm run test:conformance` (in `js/`).
- Harness code reaches the implementation only through one seam: `ConformanceSubject` (.NET, `tests/Ccf.Core.Tests/Conformance/`) and `test/support/subject.ts` (JS). Each member throws `NotImplementedException` / `NotImplementedError` until wired up. Phase 1 / phase 3 replace the stubs and drop the default conformance exclusion (`VSTestTestCaseFilter` in `dotnet/Directory.Build.props`, `CCF_TESTS` default in `js/packages/client/vitest.config.ts`).

**Exit:** scaffold builds; all 15 vectors are discovered and fail with `NotImplementedException`; all other tests pass.

## Phase 1 — `Ccf.Core` (.NET)

Schema
- [ ] Schema model: `CcfSchema` (Name, Fields, Id as 4 bytes + hex string, CanonicalJson), `CcfField`, `CcfType`.
- [ ] Parse schema JSON; validate per SPEC §3.1 (names, applicable keys, scale range, enum size, unique names).
- [ ] Canonicalizer (SPEC §3.2) and id hashing (§3.3).
- [ ] Test: for each `schema-*.json`, parse → canonicalize gives identical bytes; id matches manifest.
- [ ] Invalid schemas raise `CcfSchemaException`, separate from the format error codes (SPEC §14.3): bad name, scale outside 0–18, inapplicable key (rejected, not dropped), duplicate names, unknown type or key.

Primitives
- [ ] uvarint read/write (shortest form, 10-byte max, overflow detection), zigzag, LSB-first bit packing.
- [ ] Unit tests for every example in SPEC §5, plus boundaries (0, 127, 128, 2⁶⁴−1, int64 min/max).
- [ ] Wire `ConformanceSubject` to `Ccf.Core`, replace the stubs, and remove the default conformance exclusion.

Decoder
- [ ] `CcfReader` over `ReadOnlySpan<byte>` with bounds checks raising `CcfFormatException(CcfErrorCode)` for every code in SPEC §11.
- [ ] Untyped decode to a value tree (for conformance tests and tools).
- [ ] Typed decode to `T` (property matching per ARCHITECTURE §6.2), with accessors cached per (schema, type).
- [ ] All valid vectors decode to their expected JSON; all invalid vectors throw exactly `expectedError`.
- [ ] One unit test per SPEC §14 clarification (rules 1–7) in `Ccf.Core.Tests`; no files added to `test-vectors/`.

Schema builder
- [ ] `CcfSchemaBuilder.TryBuild(Type, CcfSchemaOptions, out CcfSchema, out string reason)` per ARCHITECTURE §5 (mapping, names, order, attributes, nullability, unsupported list).
- [ ] `[CcfDelta]`, `[CcfDecimal(scale, Unsigned)]` attributes.
- [ ] Tests: C# types that model the 3 vector schemas (`Product`, `AllTypes`, `Order`) build exactly the canonical schema files; each unsupported case returns false with a reason.

Encoder
- [ ] `CcfEncoder.Encode(object value, CcfSchema, CcfFeatures, IBufferWriter<byte>, CcfLimits)` returning the flags produced.
- [ ] Dictionary rule and first-appearance order (SPEC §8.1); encode failures as `CcfEncodeException` (SPEC §10.2).
- [ ] Conformance: for each valid vector, decode `.expected.json` into the matching C# type, encode with `requestedFeatures`, compare to `.ccf` byte for byte; flags match `expectedFlags`.
- [ ] Round-trip property tests (random data for each schema → encode → decode → equal).

Hardening
- [ ] Fuzz the decoder (SharpFuzz or a random-mutation test over the vectors): only `CcfFormatException` may escape, no hangs, no allocation beyond limits. Run what the session allows and report the duration.
- [ ] Nightly CI job that runs the fuzzer for 1 hour.

**Exit:** all 15 vectors pass both directions on net8.0 and net10.0; fuzz run of 1 hour clean.

## Phase 2 — `Ccf.AspNetCore`

- [ ] `CcfOptions` (DictionaryThreshold, DefaultDecimalScale, Limits, SchemaPathPrefix, AllowQueryOverride) bound from configuration section `Ccf`.
- [ ] `ISchemaRegistry` (lazy registration, `Register<T>()`, id collision check, thread-safe).
- [ ] `AddCcf()` registrations per ARCHITECTURE §3.1.
- [ ] `CcfOutputFormatter` per ARCHITECTURE §3.2, including `features` parsing (quoted and bare), Content-Type echo, `Vary: Accept`, JSON fallback on encode failure.
- [ ] Confirm and test how `MediaType` matching handles the `features` parameter.
- [ ] `CcfResults.Ok<T>()` for minimal APIs.
- [ ] `MapCcfSchemas()` with immutable caching and 404.
- [ ] Logging and the `Ccf` meter (ARCHITECTURE §3.5).
- [ ] Development-only `?ccf=d` override (off unless `AllowQueryOverride` and the environment is Development).
- [ ] `samples/Ccf.SampleApi`: `/products` returning the 500 sample rows (from `test-vectors/v01-products-plain.expected.json`), `/orders`, `/products/{id}`, plus one unsupported type to show fallback.
- [ ] Integration tests with `WebApplicationFactory`: JSON by default; plain CCF for `Accept: application/vnd.ccf` (bytes equal `v01`); dictionary CCF for `features=d` (bytes equal `v02`); single object (`v05`); fallback for unsupported type; schema endpoint 200/404 and headers.

**Exit:** the sample API serves JSON, plain CCF and dictionary CCF from one endpoint, byte-identical to the vectors.

## Phase 3 — `Ccf.Client` (.NET) and `@ccf/client` (TypeScript)

.NET
- [ ] `GetFromCcfAsync<T>` with Accept header, schema fetch + hash check + cache, single-flight per schema id, JSON retry rules (ARCHITECTURE §6.1).
- [ ] Tests against the sample API: CCF path, JSON path, schema fetched once for many requests, corrupted payload → JSON retry.

TypeScript
- [ ] Reader with bounds checks and `CcfFormatError` (codes identical to .NET).
- [ ] Schema parse, canonicalize, SHA-256 id. Parsing is async and stores the precomputed id; decoding stays synchronous. Invalid schemas raise `CcfSchemaError` (SPEC §14.3).
- [ ] SHA-256 via WebCrypto (`crypto.subtle`) with a small pure-JS fallback for contexts where it is unavailable (insecure browser origins). Used only for schema verification, never on the hot path; no dependency.
- [ ] Wire `test/support/subject.ts` to the decoder, replace the stubs, and drop the default conformance exclusion.
- [ ] One unit test per SPEC §14 clarification that applies to the decoder (rules 1, 2, 3, 5, 7).
- [ ] Decoder for all types, options `int64` and `decimal` (ARCHITECTURE §6.3).
- [ ] All 15 vectors pass (decode and errors) in Node; one browser run (Playwright or vitest browser mode).
- [ ] `createCcfClient` with schema cache, single-flight, JSON retry; pluggable cache interface; an IndexedDB cache implementation.
- [ ] `ccf-typegen`: schema JSON or URL → `.ts` interfaces; snapshot tests for the 3 vector schemas.
- [ ] Package builds to ESM + `.d.ts`; zero runtime dependencies.

**Exit:** both clients decode every vector identically; end-to-end test from the TS client against the sample API passes.

## Phase 4 — Benchmarks (go / no-go)

- [ ] .NET (BenchmarkDotNet): encode and decode of the 500-row sample and 3 larger synthetic sets (5k rows; mostly-unique strings; deeply nested orders) vs System.Text.Json (serialize/deserialize), including Gzip/Brotli decompression time for JSON.
- [ ] JS: decode vs `JSON.parse` (+ `DecompressionStream` for gzip) in Node and one browser.
- [ ] Size table for the same sets (raw CCF plain/dict vs JSON raw/gzip/brotli).
- [ ] Write results into `docs/BENCHMARKS.md`.

**Go criteria:** CCF decode faster than JSON parse + decompress on both platforms for the 500-row and 5k sets, and raw CCF dictionary size ≤ Gzip JSON size. If .NET fails them, stop and report before phase 5. If .NET meets them but JS does not beat `JSON.parse`, report the numbers and continue.

## Phase 5 — Rollout readiness

- [ ] Package metadata, READMEs and XML docs for the three NuGet packages and the npm package.
- [ ] Opt-in guide: enabling CCF on selected endpoints, pre-registering schemas, configuration reference.
- [ ] Dashboards/queries for the `Ccf` meter: share of responses served as CCF, fallbacks, encode failures, bytes saved.
- [ ] Versioning policy: format version stays 1; packages use SemVer; new features add letters, never change existing encodings.

**Exit:** packages publishable; rollout guide reviewed by the project owner.

## Future — Mobile (not in current scope)

- [ ] Swift package (iOS) decoder + client, validated against `test-vectors/`.
- [ ] Kotlin library (Android) decoder + client, validated against `test-vectors/`.

## Open questions

_None open._ Ambiguities found before phase 0 were resolved by the project owner and recorded as SPEC §14 (clarifications v1.0.1). Add new questions here as they come up, with the phase and the SPEC section they concern.
