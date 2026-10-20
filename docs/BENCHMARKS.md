# Benchmarks (phase 4)

Measured 2026-10-20 on a 4-vCPU Intel Xeon 2.10 GHz Linux VM (shared cloud container), .NET 10.0.13 (Release, `net10.0`), Node 22.22.0, Chromium 141. Numbers from a shared VM are noisy: read ratios, not absolute times. `net8.0` was not run.

## Datasets

| Name | Rows | Shape |
| --- | ---: | --- |
| products-500 | 500 | the golden vectors' product list (v01/v02); the encoder reproduces both vectors byte for byte |
| products-5k | 5,000 | same schema; ~300 distinct names, ids mostly consecutive |
| unique-strings-5k | 5,000 | name, email and note almost always unique (worst case for dictionary mode) |
| orders-5k | 5,000 | nullable customer object, 0–8 order lines (object list), optional tag list (scalar list) |

Generated deterministically by `dotnet/benchmarks/Ccf.Benchmarks/Datasets.cs`. The `export` command writes every dataset in every encoding (JSON, gzip, brotli, CCF plain, CCF dictionary) to `bench-data/` (git-ignored) for the JavaScript runs.

## Method

- **.NET:** BenchmarkDotNet 0.15.8, 3 warm-up and 15 measured iterations, one launch, `MemoryDiagnoser`. Every decode starts from the wire bytes and produces `List<T>`. Baseline = System.Text.Json source-generated deserialization from UTF-8 bytes; the reflection variant is shown too. Gzip and Brotli rows decompress with `GZipStream`/`BrotliStream` straight into the deserializer (as `HttpClient` does). Encode writes into a reused `ArrayBufferWriter`; JSON gzip/brotli encode uses `CompressionLevel.Fastest` (the ASP.NET Core default).
- **JavaScript:** wire bytes to JS values. JSON = `TextDecoder` + `JSON.parse`; gzip = `DecompressionStream("gzip")` + the same; CCF = `decode()` with default options (int64 and decimal as `number`). Each case is calibrated to batches of about 25 ms, 25 batches, median reported. The decoded CCF value is checked equal to the `JSON.parse` value before timing. Brotli is Node only (`zlib`); Chromium has no `DecompressionStream("br")`. Chromium coarsens `performance.now()`, so its small cases are quantised.
- CCF is never compressed (out of scope); the comparison is CCF raw against JSON as a server would really send it (gzip/brotli).

## Size

| Dataset | Rows | JSON | JSON gzip -6 | JSON gzip fastest | JSON brotli 4 | JSON brotli fastest | JSON brotli 11 | CCF plain | CCF dictionary | CCF dict / gzip -6 | CCF dict / JSON |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| products-500 | 500 | 39,721 | 6,558 | 8,324 | 6,822 | 7,283 | 5,254 | 9,317 | 5,266 | 80 % | 13 % |
| products-5k | 5,000 | 402,516 | 64,197 | 83,864 | 69,928 | 75,663 | 49,709 | 95,583 | 43,917 | 68 % | 11 % |
| unique-strings-5k | 5,000 | 857,655 | 170,385 | 208,722 | 207,300 | 208,355 | 124,783 | 603,787 | 532,003 | 312 % | 62 % |
| orders-5k | 5,000 | 1,209,532 | 156,003 | 209,357 | 135,337 | 176,118 | 94,450 | 330,051 | 131,941 | 85 % | 11 % |

JSON gzip -6 is `CompressionLevel.Optimal`; "brotli 4" is .NET's `Optimal`; brotli 11 is the maximum.

## .NET decode (net10.0)

**products-500**

| Method | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Ccf_Plain | 48.42 μs | 0.25 | 77.14 KB |
| Ccf_Dictionary | 36.48 μs | 0.19 | 59.46 KB |
| Json_SourceGen | 196.41 μs | 1.00 | 59.55 KB |
| Json_Reflection | 206.52 μs | 1.05 | 59.55 KB |
| Json_Gzip_SourceGen | 250.43 μs | 1.28 | 59.86 KB |
| Json_Gzip_Reflection | 297.95 μs | 1.52 | 59.86 KB |
| Json_Brotli_SourceGen | 263.58 μs | 1.35 | 59.73 KB |

**products-5k**

| Method | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Ccf_Plain | 528.0 μs | 0.28 | 768.14 KB |
| Ccf_Dictionary | 308.7 μs | 0.17 | 525.38 KB |
| Json_SourceGen | 1,884.3 μs | 1.01 | 642.13 KB |
| Json_Reflection | 2,101.9 μs | 1.12 | 642.13 KB |
| Json_Gzip_SourceGen | 3,449.8 μs | 1.84 | 642.44 KB |
| Json_Gzip_Reflection | 3,289.6 μs | 1.76 | 642.44 KB |
| Json_Brotli_SourceGen | 2,781.4 μs | 1.49 | 642.31 KB |

**unique-strings-5k**

| Method | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Ccf_Plain | 1.396 ms | 0.52 | 2.15 MB |
| Ccf_Dictionary | 1.168 ms | 0.44 | 1.88 MB |
| Json_SourceGen | 2.700 ms | 1.02 | 1.97 MB |
| Json_Reflection | 2.384 ms | 0.90 | 1.97 MB |
| Json_Gzip_SourceGen | 4.820 ms | 1.81 | 1.97 MB |
| Json_Gzip_Reflection | 4.960 ms | 1.86 | 1.97 MB |
| Json_Brotli_SourceGen | 4.406 ms | 1.66 | 1.97 MB |

**orders-5k**

| Method | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Ccf_Plain | 4.823 ms | 0.56 | 4.08 MB |
| Ccf_Dictionary | 2.625 ms | 0.30 | 2.85 MB |
| Json_SourceGen | 8.798 ms | 1.02 | 3.5 MB |
| Json_Reflection | 9.653 ms | 1.12 | 3.5 MB |
| Json_Gzip_SourceGen | 11.993 ms | 1.39 | 3.5 MB |
| Json_Gzip_Reflection | 12.409 ms | 1.44 | 3.5 MB |
| Json_Brotli_SourceGen | 11.019 ms | 1.28 | 3.5 MB |

## .NET encode (net10.0)

Ratios are against `Json_SourceGen`. Gzip/Brotli rows include the serialization.

**products-500**

| Method | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Ccf_Plain | 56.61 μs | 0.80 | 29296 B |
| Ccf_Dictionary | 60.16 μs | 0.85 | 43672 B |
| Json_SourceGen | 71.06 μs | 1.00 | - |
| Json_Reflection | 94.92 μs | 1.34 | 312 B |
| Json_SourceGen_Gzip | 291.06 μs | 4.11 | 10568 B |
| Json_SourceGen_Brotli | 199.42 μs | 2.82 | 10456 B |

**products-5k**

| Method | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Ccf_Plain | 626.8 μs | 0.83 | 332272 B |
| Ccf_Dictionary | 529.2 μs | 0.70 | 362888 B |
| Json_SourceGen | 756.8 μs | 1.00 | - |
| Json_Reflection | 950.6 μs | 1.26 | 312 B |
| Json_SourceGen_Gzip | 3,354.8 μs | 4.44 | 101461 B |
| Json_SourceGen_Brotli | 2,474.0 μs | 3.28 | 101360 B |

**unique-strings-5k**

| Method | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Ccf_Plain | 1,575.9 μs | 2.17 | 332272 B |
| Ccf_Dictionary | 2,170.2 μs | 2.99 | 1202944 B |
| Json_SourceGen | 727.2 μs | 1.00 | - |
| Json_Reflection | 1,053.0 μs | 1.45 | 312 B |
| Json_SourceGen_Gzip | 6,860.6 μs | 9.47 | 215188 B |
| Json_SourceGen_Brotli | 4,969.5 μs | 6.86 | 644634 B |

**orders-5k**

| Method | Mean | Ratio | Allocated |
| --- | ---: | ---: | ---: |
| Ccf_Plain | 3.774 ms | 1.35 | 1537304 B |
| Ccf_Dictionary | 3.006 ms | 1.07 | 1582851 B |
| Json_SourceGen | 2.805 ms | 1.00 | - |
| Json_Reflection | 5.001 ms | 1.79 | 312 B |
| Json_SourceGen_Gzip | 9.787 ms | 3.49 | 303016 B |
| Json_SourceGen_Brotli | 8.987 ms | 3.21 | 302908 B |

## JavaScript decode

**Node 22.22.0 (V8 12.4.254.21-node.33)** (median ms per decode, from wire bytes to JS values)

| Dataset | JSON.parse | gunzip + JSON.parse | gunzip alone | brotli + JSON.parse | CCF plain | CCF dictionary | JSON.parse ÷ CCF dict | gzip JSON ÷ CCF dict |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| products-500 | 0.160 | 0.914 | 0.644 | 0.240 | 0.155 | 0.109 | 1.47x | 8.40x |
| products-5k | 1.985 | 4.885 | 2.847 | 2.786 | 1.398 | 0.933 | 2.13x | 5.24x |
| unique-strings-5k | 2.343 | 9.753 | 6.290 | 5.078 | 3.466 | 3.187 | 0.74x | 3.06x |
| orders-5k | 9.523 | 16.356 | 7.594 | 10.093 | 9.035 | 4.010 | 2.37x | 4.08x |

**Chromium (Chrome/141.0.7390.37)** (median ms per decode, from wire bytes to JS values)

| Dataset | JSON.parse | gunzip + JSON.parse | gunzip alone | brotli + JSON.parse | CCF plain | CCF dictionary | JSON.parse ÷ CCF dict | gzip JSON ÷ CCF dict |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| products-500 | 0.102 | 1.756 | 1.619 | n/a | 0.182 | 0.110 | 0.93x | 15.97x |
| products-5k | 1.100 | 4.680 | 2.900 | n/a | 2.300 | 0.930 | 1.18x | 5.03x |
| unique-strings-5k | 1.709 | 7.800 | 5.980 | n/a | 5.875 | 5.200 | 0.33x | 1.50x |
| orders-5k | 6.050 | 12.533 | 6.340 | n/a | 10.200 | 3.267 | 1.85x | 3.84x |

## Reading the numbers against the go criteria

| Criterion (PLAN phase 4) | .NET | Node | Chromium |
| --- | --- | --- | --- |
| CCF decode faster than JSON parse + decompress, products-500 | 36.5 µs vs 250 µs gzip (6.9x) | 0.109 vs 0.914 ms (8.4x) | 0.110 vs 1.756 ms (16x) |
| same, products-5k | 309 µs vs 3,450 µs gzip (11x) | 0.93 vs 4.89 ms (5.2x) | 0.93 vs 4.68 ms (5.0x) |
| raw CCF dictionary size ≤ gzip JSON size | 5,266 ≤ 6,558 and 43,917 ≤ 64,197 | same bytes | same bytes |

Against plain JSON with no compression (`JSON.parse`/STJ only): .NET CCF dictionary is 5.4x (500 rows) and 6.1x (5k) faster than STJ source-gen; Node 1.47x and 2.13x; Chromium 0.93x (500 rows, within quantisation noise) and 1.18x (5k).

Things the numbers also show, not covered by the criteria:

- **unique-strings-5k is CCF's weak spot.** Size: CCF dictionary is 3.1x the gzip JSON size (62% of raw JSON). JS decode is slower than `JSON.parse` in Node (0.74x) and Chromium (0.33x) because UTF-8 string decoding dominates and `TextDecoder`/`JSON.parse` are native; against gzip JSON CCF still wins. .NET decode still wins (0.44x of STJ time). .NET encode is 2.2x–3.0x slower than STJ here.
- **.NET encode** is faster than STJ source-gen on the product sets (0.70x–0.85x time) and about equal on orders (1.07x dictionary, 1.35x plain), far cheaper than JSON+gzip/brotli (3x–4x STJ).
- **Allocations:** CCF decode allocates about the same as STJ (0.8x–1.3x).
- `JSON.parse` in Chromium is faster than in Node 22 on these inputs; CCF's lead over `JSON.parse` is therefore smaller there.

The go/no-go judgement is the project owner's; see the report in the session.
