# Observability: dashboards and queries for the `Ccf` meter

`Ccf.AspNetCore` publishes one `System.Diagnostics.Metrics` meter, `Ccf`, and logs under the category `Ccf`.

## Instruments

| Instrument | Type | Unit | Tags | Recorded |
| --- | --- | --- | --- | --- |
| `ccf.responses` | counter | responses | `format` = `ccf` or `json-fallback`; `features` = `d` or empty | Once per response where CCF was selected: served as CCF (`ccf`) or fallen back after an encode failure (`json-fallback`). A request for CCF that gets JSON because the type is unsupported, the value is null or a scalar is not counted. |
| `ccf.bytes_written` | counter | `By` | none | Payload bytes of every CCF response. |
| `ccf.encode_failures` | counter | failures | none | Every encode that failed and fell back to JSON (also counted in `ccf.responses{format="json-fallback"}`). |

What is **not** recorded: JSON responses to clients that did not ask for CCF, and the size the JSON response would have had. This limits two of the questions below; see "Gaps".

## Collecting

- **OpenTelemetry:** `services.AddOpenTelemetry().WithMetrics(m => m.AddMeter("Ccf"))` plus your exporter. With the Prometheus exporter the instruments usually appear as `ccf_responses_total`, `ccf_bytes_written_bytes_total` and `ccf_encode_failures_total`; check the names your exporter produces.
- **Quick look on a host:** `dotnet-counters monitor --counters Ccf --process-id <pid>`.
- **Logs:** category `Ccf` logs a warning on every encode fallback (schema id and reason) and one warning at startup if the MVC and minimal-API naming policies differ.

## Panels (PromQL)

| Question | Query |
| --- | --- |
| How much of the CCF traffic is actually served as CCF? | `sum(rate(ccf_responses_total{format="ccf"}[5m])) / sum(rate(ccf_responses_total[5m]))` |
| Fallbacks per second | `sum(rate(ccf_responses_total{format="json-fallback"}[5m]))` |
| Encode failures per second (alert when above 0 for a few minutes) | `sum(rate(ccf_encode_failures_total[5m]))` |
| Dictionary mode uptake among CCF responses | `sum(rate(ccf_responses_total{format="ccf",features="d"}[5m])) / sum(rate(ccf_responses_total{format="ccf"}[5m]))` |
| Average CCF payload size | `sum(increase(ccf_bytes_written_bytes_total[1h])) / sum(increase(ccf_responses_total{format="ccf"}[1h]))` |
| CCF bytes sent per second | `sum(rate(ccf_bytes_written_bytes_total[5m]))` |
| Estimated bytes saved per hour (see below) | `sum(increase(ccf_bytes_written_bytes_total[1h])) * (1 / <r> - 1)` |

**Estimated bytes saved.** The server never builds the JSON, so savings are an estimate from a size ratio `r` = CCF bytes / JSON bytes that you measure once per endpoint family (serve the same data both ways and compare). From `docs/BENCHMARKS.md`: against *uncompressed* JSON, `r` is about 0.11–0.13 for repetitive data and 0.62 for mostly unique strings; against *gzip* JSON, `r` is about 0.68–0.85 and 3.1 for mostly unique strings (CCF larger). Use the one that matches what your clients receive today; for text-heavy endpoints the estimate is negative.

## Suggested alerts

- `ccf_encode_failures_total` rate above 0 for 10 minutes: a data change (a decimal with extra digits, a new null) is forcing JSON on a CCF endpoint.
- `json-fallback` share of `ccf.responses` above, say, 1%.
- Client side (your own telemetry): any spike in the .NET client warning "repeating the request as JSON" or in `@skm/ccf-client`'s `log` callback.

## Gaps (not covered by the current instruments)

1. **Share of all responses served as CCF.** `ccf.responses` counts only responses where CCF was selected, so the denominator "all responses of CCF-capable endpoints" is not available. A `ccf.responses{format="json"}` series for JSON responses of CCF-capable endpoints would close it.
2. **Measured bytes saved** would need the JSON-equivalent size, which costs a serialization per response; sample it offline instead.

Neither gap is addressed in code here; see Open questions in `docs/PLAN.md`.
