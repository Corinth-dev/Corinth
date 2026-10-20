# Golden test vectors

These files are the conformance suite for every CCF implementation (.NET, TypeScript, and later Swift and Kotlin). They were produced by an independent reference encoder written from `docs/SPEC.md`, and each valid vector was decoded back and compared before being saved.

**Never edit or regenerate these files to make a failing test pass.** If an implementation disagrees with a vector, the implementation is wrong — or, if you are sure the vector contradicts the spec, stop and report it to the project owner.

## Files

| File | Purpose |
| --- | --- |
| `manifest.json` | Index of all vectors; drive tests from it rather than hard-coding names |
| `schema-<Name>.json` | Canonical schema text (SPEC §3.2). Hashing it must give the vector's `schemaId` |
| `vNN-*.ccf` | Valid payloads |
| `vNN-*.expected.json` | The decoded value in the canonical JSON representation below |
| `xNN-*.ccf` | Invalid payloads; decoding must fail with `expectedError` |

## Manifest entries

Valid:

```json
{"name": "v02-products-dict", "kind": "valid", "file": "v02-products-dict.ccf",
 "schema": "schema-Product.json", "schemaId": "c33a3ed0", "requestedFeatures": "d",
 "expectedFlags": 1, "expectedContentType": "application/vnd.ccf; features=d",
 "expected": "v02-products-dict.expected.json", "bytes": 5266, "description": "…"}
```

Invalid:

```json
{"name": "x05-dict-index-out-of-range", "kind": "invalid", "file": "x05-dict-index-out-of-range.ccf",
 "schema": "schema-Product.json", "schemaId": "c33a3ed0", "expectedError": "IndexOutOfRange", "bytes": 5266, "description": "…"}
```

## The vectors

| Vector | What it covers |
| --- | --- |
| `v01-products-plain` | 500 rows, plain mode, delta ids, unsigned decimal, 1-bit enum (9,317 bytes) |
| `v02-products-dict` | Same rows with `features=d`: name column in dictionary mode, 142 entries (5,266 bytes) |
| `v03-all-types` | Every scalar type, nulls in 5 columns, int64/uint64 extremes, unicode, a 300-byte string, pre-1970 datetime with delta; `text` stays plain despite `features=d` |
| `v04-orders-nested` | Nullable nested object, list of objects (its `sku` column uses dictionary mode), nullable `$value` list of strings, empty lists, null list |
| `v05-single-product` | Single-object root: flag `S` set, row count 1, decodes to an object not an array |
| `v06-empty` | 0 rows; every column block is `00 00`; no dictionary even though `d` was requested |
| `x01` … `x09` | One decoder error each: BadMagic, UnsupportedVersion, ReservedFlags, Truncated, IndexOutOfRange (dictionary), BlockLengthMismatch, IndexOutOfRange (enum), TrailingData, SchemaMismatch |

## Canonical JSON representation (`*.expected.json`)

Chosen so that every language can compare without precision loss:

| CCF type | JSON in expected files |
| --- | --- |
| `bool` | `true` / `false` |
| `int32`, `uint32` | number |
| `int64`, `uint64` | **string** of decimal digits, e.g. `"-9223372036854775808"` |
| `float64` | number (exact round-trip representation) |
| `decimal` | **string** with exactly `scale` digits after the point, e.g. `"361.48"`, `"0.0000"` |
| `enum` | member name string |
| `string` | string |
| `datetime` | string, ISO 8601 UTC with milliseconds, e.g. `"1969-12-31T23:59:59.999Z"` |
| `guid` | lowercase hyphenated string |
| `object` | object with every schema field present (nulls as `null`) |
| `list` | array; a `$value` list is an array of the element values |
| null | `null` |

A single-object root is a JSON object; every other root is an array.

## How tests must use them

For each **valid** vector, in each implementation:

1. **Schema**: build the canonical text from the schema JSON and check it equals the `schema-*.json` file byte for byte; check its id equals `schemaId`.
2. **Decode**: decode the `.ccf` with the schema, convert the result to the canonical representation above, and compare to `.expected.json` (deep equality; floats compared exactly).
3. **Encode** (.NET only, the encoder lives there): load `.expected.json` into values, encode with `requestedFeatures`, and compare to the `.ccf` byte for byte. Check flags and the Content-Type the server would send.

For each **invalid** vector: decoding must throw the CCF format error with code `expectedError`, and must not throw anything else (no index exceptions, no out-of-memory).
