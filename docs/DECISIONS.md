# Decision log

Decisions already made with the project owner. Do not reopen them during implementation; if one turns out to be unworkable, stop and raise it.

| # | Decision | Why | Rejected alternatives |
| --- | --- | --- | --- |
| 1 | Binary, column-oriented format | Payload size and parse speed together point to binary; columns put similar values together and allow tight per-type loops | Text formats (JSON, TOON): bulky and slow to parse; row-oriented binary (MessagePack): repeats keys |
| 2 | No field names on the wire; schema cached by id | Removes the largest repeated cost; schemas are tiny and fetched once | Self-describing payloads (like TOON); embedding the schema in every response |
| 3 | Schema id = first 4 bytes of SHA-256 of canonical schema | Stable across servers and deploys, no coordination, changes whenever the schema changes | Sequential ids (need a registry); type names (break on change) |
| 4 | Plain columns by default; dictionary mode opt-in by client and chosen per column by server | Dictionary only helps repetitive columns and hurts unique ones; the server sees the data, the client only declares what it can read | Global v1/v2 format versions; always-on dictionary |
| 5 | Media type `application/vnd.ccf` with a `features` parameter | One type and one formatter; features extend without combinatorial types; `vnd` is the standard vendor tree (the `x-` prefix is deprecated by RFC 6648) | `application/x-ccf-d` and similar per-feature types |
| 6 | Dictionary rule: `2k < p`, `k ≤ 65,536`, first-appearance order | Simple, deterministic, reproducible byte for byte | Cost-based estimates (non-deterministic across implementations) |
| 7 | Nulls via presence bitmap on nullable columns only | 1 bit per null; zero cost for non-nullable columns | Sentinel values; a bitmap on every column |
| 8 | Nested objects as child tables inside the parent's block; lists as counts + child table; scalar lists via `$value` | Keeps nested data columnar; block lengths make every column skippable | JSON-in-a-string; flattening lists |
| 9 | Single objects are a 1-row table with flag `S` | One code path for everything | A separate single-object layout |
| 10 | Every column block has a mode byte and length | Lets decoders skip unknown or unwanted columns and validate structure | No lengths (smaller by ~3 bytes per column but fragile) |
| 11 | Encode into a buffer before sending | Any encode failure can still fall back to JSON | Streaming encode (cannot fall back once bytes are sent) |
| 12 | JSON fallback everywhere, invisible to users | Zero disruption; CCF is an optimisation, never a dependency | Error responses when CCF cannot be served |
| 13 | Platforms: .NET server + .NET client + JS/TS client now; mobile later | Owner's priority; shared golden vectors keep future clients consistent | All platforms at once |
| 14 | Field names follow System.Text.Json naming | CCF and JSON responses describe the same contract | A separate CCF naming policy |
| 15 | Parse speed is a go/no-go benchmark, not an assumption | Only size has been measured so far | Shipping on size results alone |
| 16 | Product name **Corinth**; the format stays **CCF**, now expanded as *Corinthian Columnar Format* (owner, 2026-11-26) | "CCF" alone clashes with Microsoft's Confidential Consortium Framework; a Corinthian column names a columnar format and keeps the acronym, so `application/vnd.ccf` keeps its meaning. Packages, assemblies and namespaces are `Corinth.*` / `@corinth/*`; type names (`CcfEncoder`, `AddCcf`, `createCcfClient`) and runtime names (`Ccf` config section and meter, `ccf.*` metrics, `x-ccf-*` headers, `/ccf/...` routes) keep the format's name. SPEC §1 wording changed with the owner's approval; no wire change | `SKM.Ccf.*` (kept the clash); renaming types and runtime names too (larger break, mixes with the fixed `application/vnd.ccf` and `/ccf/schemas`) |
