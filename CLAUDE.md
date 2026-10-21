# CCF — Columnar Compact Format

You are building CCF: a compact binary response format for ASP.NET Core APIs, with a .NET server library, a .NET client and a TypeScript client. JSON stays the default; clients opt in with `Accept: application/vnd.ccf; features=d`.

## Read first, in this order

1. `docs/SPEC.md` — the wire format. **Normative. Do not change it.**
2. `test-vectors/README.md` — the conformance suite and how tests must use it.
3. `docs/ARCHITECTURE.md` — packages, ASP.NET Core integration, clients, fallback behaviour.
4. `docs/PLAN.md` — phases and checkboxes. This is your work queue.
5. `docs/DECISIONS.md` — decisions already made; do not reopen them.

## Sources of truth

- The wire format is defined by `docs/SPEC.md`. `test-vectors/` was generated from it by an independent reference encoder and verified.
- If your implementation disagrees with a vector, assume your code is wrong. If you are confident the vector contradicts the spec, **stop and report** — never edit, delete or regenerate files in `test-vectors/`, and never change `docs/SPEC.md`.
- If the spec is ambiguous, pick nothing silently: ask, or record the question at the bottom of `docs/PLAN.md` under "Open questions" and continue with unrelated work.

## How to work

- Follow `docs/PLAN.md` phase by phase. Tick boxes as items are completed and tested. Do not start a phase before the previous exit check passes (phases 1 and 3 may overlap after phase 0).
- Drive conformance tests from `test-vectors/manifest.json`, never from hard-coded file lists.
- Every bug fix comes with a test that failed before the fix.
- Keep `docs/ARCHITECTURE.md` accurate if a non-normative detail changes (for example a class name); mention such changes in your summary.

## Stack and conventions

.NET
- Target `net10.0` (net8.0 was dropped in phase 5: end of support in November 2026), C# latest, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- `Ccf.Core` depends on the BCL only. No third-party runtime dependencies in any package without asking.
- Tests: xUnit. Benchmarks: BenchmarkDotNet. Fuzzing: SharpFuzz or a mutation test.
- Hot paths (encoder, decoder): `Span<byte>`, `ReadOnlySpan<byte>`, `IBufferWriter<byte>`, `ArrayPool<byte>`; no LINQ, no boxing, no reflection per value (compile accessors once per type).
- Every decode failure is `CcfFormatException` with a `CcfErrorCode` from SPEC §11; nothing else may escape the decoder. Encode failures are `CcfEncodeException`.
- Public APIs get XML doc comments.

TypeScript
- TypeScript 5, strict mode, ES modules, Node 20+ and modern browsers. Zero runtime dependencies.
- Tests: vitest. Errors: `CcfFormatError` with the same code strings as .NET.
- Use `DataView`, `TextDecoder("utf-8", { fatal: true })`, `crypto.subtle` for SHA-256.

## Commands

```bash
# .NET
cd dotnet && dotnet build && dotnet test
dotnet run -c Release --project benchmarks/Ccf.Benchmarks -- --filter "*"   # BenchmarkDotNet (about 15 min)
cd dotnet && dotnet run -c Release --project benchmarks/Ccf.Benchmarks -- export   # datasets + size table into bench-data/
cd js && npm run build && npm run bench && npm run bench:browser   # JS decode benchmarks (Node, Chromium)

# TypeScript
cd js && npm install && npm test
npm run build

# Run the sample API (JSON by default; try `curl -H 'Accept: application/vnd.ccf; features=d' http://localhost:5000/products`)
cd dotnet && dotnet run --project samples/Ccf.SampleApi
# The sample also serves its OpenAPI document at /openapi/v1.json and the Scalar UI at /scalar/v1 (CCF-capable operations list application/vnd.ccf; both come from the SKM.Ccf.Scalar package in src/Ccf.Scalar, the CCF viewer is mapped in Development only).
# To read a CCF response in Scalar, send the request with `Accept: application/vnd.ccf` and switch the "CCF decode" toggle (bottom right, off by default) ON before sending: a script (/ccf/scalar-viewer.js) posts the payload to POST /ccf/decode and Scalar shows the decoded JSON (x-ccf-* response headers show size and schema id). OFF shows the raw binary response.

# TypeScript: real Chromium (needs `npx playwright-core install chromium` once)
cd js && npm run test:browser
# TypeScript client against the real .NET sample API, and the Scalar toggle in real Chromium (needs the .NET SDK and `npx playwright-core install chromium`; builds and starts the sample itself).
# The matching CI job `js-e2e` is required on pull requests (via the aggregate `required` check).
cd js && npm run build && npm run test:e2e
# Generate TypeScript interfaces from a schema
cd js && npm run build && node packages/typegen/dist/cli.js ../test-vectors/schema-Product.json

# Golden-vector conformance tests (part of the default run in both languages)
cd dotnet && dotnet test --filter "Category=Conformance"
cd js && npm run test:conformance

# Decoder fuzzing: a short smoke run is part of both default test runs; set the duration for a longer run
cd dotnet && CCF_FUZZ_SECONDS=600 dotnet test -c Release --filter "Category=Fuzz" --logger "console;verbosity=detailed"
cd js/packages/client && CCF_FUZZ_SECONDS=600 npx vitest run test/fuzz.test.ts
```

Update this section if you add commands.

## Definition of done (every task)

- Builds with no warnings; all tests pass on net10.0 and on Node 20.
- Conformance vectors that the task touches pass in both directions where applicable.
- No new dependencies, no changes to `docs/SPEC.md` or `test-vectors/`.
- Checkbox ticked in `docs/PLAN.md`.

## Do not

- Change the wire format, error codes, media type or schema canonical form.
- Add compression, encryption or request-body (input formatter) support — out of scope.
- Start mobile (Swift/Kotlin) work — future phase.
- Commit secrets, credentials or machine-specific paths.
