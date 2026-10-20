# CCF Wire Format Specification — version 1

Status: **frozen for implementation**. Changing anything in this file changes the wire format; do not edit it without the project owner's approval.

The key words MUST, MUST NOT, SHOULD and MAY are used as in RFC 2119.

---

## 1. Overview

CCF (Columnar Compact Format) is a binary, column-oriented encoding for API responses. A payload carries **no field names**: it names a schema by a 4-byte id, and the client decodes the columns using that schema, which it fetches once and caches forever.

A payload is:

```
Header  |  Column block 1  |  Column block 2  |  …  |  Column block N
```

One column block per schema field, in schema order. All values of one field across all rows are stored together.

---

## 2. Media type and content negotiation

### 2.1 Media type

```
application/vnd.ccf
```

Optional parameter `features`: the decoding features the **client can read**.

| Feature letter | Meaning |
| --- | --- |
| `d` | Dictionary mode for string columns (§8) |

Unknown feature letters MUST be ignored by servers. A list of more than one letter MUST be quoted, because a bare comma separates media ranges in `Accept`: `features="d,n"`. A single letter MAY be unquoted: `features=d`.

### 2.2 Request

Recommended client header:

```
Accept: application/vnd.ccf; features=d, application/json;q=0.5
```

### 2.3 Response

- If the request accepts `application/vnd.ccf` and the response type is supported (§10.3), the server SHOULD respond with CCF.
- `Content-Type` MUST be `application/vnd.ccf` when no feature was used, or `application/vnd.ccf; features=d` when at least one column anywhere in the payload uses dictionary mode. The same fact is in header flag bit 0; decoders MUST rely on the flags byte, not the header.
- The server MUST send `Vary: Accept` on every response from an endpoint that can serve CCF.
- In all other cases the server responds with JSON exactly as it would without CCF.

### 2.4 Schema endpoint

```
GET {prefix}/{schemaId}        default prefix: /ccf/schemas
```

- `schemaId` = 8 lowercase hex characters (§3.3).
- 200: body = the canonical schema JSON (§3.2), `Content-Type: application/json`, `Cache-Control: public, max-age=31536000, immutable`.
- 404: unknown id.
- Served from the same origin as the API. Clients MUST NOT fetch schemas from any other origin.

---

## 3. Schema

### 3.1 JSON form

```json
{"name":"Product","fields":[
  {"name":"id","type":"int64","delta":true},
  {"name":"name","type":"string"},
  {"name":"price","type":"decimal","unsigned":true,"scale":2},
  {"name":"status","type":"enum","values":["active","discontinued"]},
  {"name":"stock","type":"uint32"}]}
```

Root object: `name` (the type's name, informational) and `fields` (ordered array). Field object keys:

| Key | Type | Applies to | Default | Meaning |
| --- | --- | --- | --- | --- |
| `name` | string | all | required | Field name, matches `^[A-Za-z_][A-Za-z0-9_]*$`, or exactly `$value` (§7.3) |
| `type` | string | all | required | One of the types in §6 |
| `nullable` | bool | all | false | Column carries a presence bitmap (§7.1) |
| `delta` | bool | int32, int64, uint32, uint64, decimal, datetime | false | Values stored as differences (§6.4) |
| `unsigned` | bool | decimal | false | Scaled values are non-negative; stored without zigzag |
| `scale` | int 0–18 | decimal | required | Digits after the decimal point |
| `values` | string[] | enum | required | Enum member names in order, 1–65,536 entries, identifier pattern as `name` |
| `fields` | field[] | object, list | required | Child fields, at least 1 |

Field names MUST be unique within one `fields` array.

### 3.2 Canonical form

The canonical form is the exact text that is hashed and that the schema endpoint returns:

1. Root keys in the order `name`, `fields`.
2. Field keys in the order `name`, `type`, `nullable`, `delta`, `unsigned`, `scale`, `values`, `fields`.
3. Boolean keys whose value is `false` are omitted. Keys that do not apply to the type are omitted.
4. No whitespace anywhere. UTF-8. Strings use JSON escaping only where JSON requires it (names are ASCII identifiers, so in practice none).

The canonical form of every schema in `test-vectors/` is in its `schema-*.json` file; implementations MUST reproduce those files byte for byte.

### 3.3 Schema id

```
schemaId = first 4 bytes of SHA-256(canonical form as UTF-8)
```

Written in the payload as those 4 raw bytes; written in URLs and logs as 8 lowercase hex characters. Example: the Product schema above has id `c33a3ed0`.

Any change to a schema (field added, removed, renamed, reordered, retyped, any key changed) produces a new id. A server MUST refuse to start (or refuse to register the second type) if two different canonical schemas produce the same id.

---

## 4. Payload layout

### 4.1 Header

| Offset | Size | Field | Rules |
| --- | --- | --- | --- |
| 0 | 2 | Magic | MUST be `43 43` ("CC"), else `BadMagic` |
| 2 | 1 | Format version | MUST be `01`, else `UnsupportedVersion` |
| 3 | 1 | Flags | Bit 0 `D`: at least one dictionary column. Bit 1 `S`: root is a single object. Bits 2–7 reserved, MUST be 0, else `ReservedFlags` |
| 4 | 4 | Schema id | Raw bytes (§3.3). If the decoder was handed a schema whose id differs: `SchemaMismatch` |
| 8 | uvarint | Row count `n` | Rows in the root table. If flag `S` is set, MUST be 1, else `SingleObjectRowCount` |

### 4.2 Table

A table with `n` rows over a field list is the concatenation of one column block per field, in field order. The root table follows the header directly. After the root table's last block the payload MUST end, else `TrailingData`.

### 4.3 Column block

| Part | Size | Rules |
| --- | --- | --- |
| Mode | 1 byte | `00` plain, `01` dictionary. `01` is valid only for `string` fields. Anything else: `InvalidMode` |
| Block length `L` | uvarint | Number of bytes that follow, to the end of this block |
| Body | `L` bytes | §7.1 presence bitmap (nullable only), then the values (§6–§8) |

A decoder MUST verify that parsing the body consumes exactly `L` bytes, else `BlockLengthMismatch`. A length that runs past the end of the payload is `Truncated`; a length that runs past the end of an enclosing block is `LengthOverrun`.

Block lengths let a decoder skip a whole column (including nested tables) without understanding it.

---

## 5. Primitive encodings

### 5.1 uvarint

Unsigned LEB128: 7 bits per byte, least significant group first, high bit set on every byte except the last. At most 10 bytes; a value that does not fit in 64 bits, or an 11th byte, is `VarintOverflow`. Encoders MUST write the shortest form.

Examples: `0 → 00`, `127 → 7f`, `128 → 80 01`, `500 → f4 03`, `16384 → 80 80 01`.

### 5.2 zigzag

Maps signed 64-bit to unsigned so small magnitudes stay small: `zz(v) = (v << 1) XOR (v >> 63)` (arithmetic shift); inverse `v = (u >> 1) XOR -(u AND 1)`. Then written as uvarint.

Examples: `0 → 0`, `-1 → 1`, `1 → 2`, `1001 → 2002 → d2 0f`.

### 5.3 Bit packing

A sequence of `k`-bit values is packed **LSB-first**: value `i` occupies stream bits `i·k … i·k+k−1`, value bit 0 first; stream bit `b` is bit `b mod 8` (bit 0 = least significant) of byte `⌊b/8⌋`. The packed run occupies `⌈count·k / 8⌉` bytes; unused high bits of the last byte MUST be written as 0 and MAY be ignored by decoders.

---

## 6. Types and value encodings

Within a column block, values are written only for **present** rows (all rows for non-nullable fields). `p` below is the number of present values.

### 6.1 Type table

| Type | Range / meaning | Encoding of the `p` values |
| --- | --- | --- |
| `bool` | true / false | Bit-packed, 1 bit each, 1 = true |
| `int32` | −2³¹ … 2³¹−1 | zigzag uvarint each |
| `int64` | −2⁶³ … 2⁶³−1 | zigzag uvarint each |
| `uint32` | 0 … 2³²−1 | uvarint each |
| `uint64` | 0 … 2⁶⁴−1 | uvarint each |
| `float64` | IEEE 754 binary64 | 8 bytes each, little-endian |
| `decimal` | value × 10^scale as int64 | zigzag uvarint each; uvarint if `unsigned` |
| `enum` | index into `values` | Bit-packed, width `w` = smallest `w ≥ 1` with 2^w ≥ count of `values` |
| `string` | UTF-8 text | Plain (§6.2) or dictionary (§8) |
| `datetime` | UTC milliseconds since 1970-01-01T00:00:00Z, int64 | zigzag uvarint each |
| `guid` | 128-bit UUID | 16 bytes each, RFC 9562 byte order (the order of the hex digits in the text form) |
| `object` | nested record | Child table (§7.2) |
| `list` | sequence | Counts + child table (§7.3) |

Decoders MUST reject an int32/uint32 value outside its range, and an enum index ≥ the number of values, with `ValueOutOfRange` / `IndexOutOfRange` respectively.

### 6.2 Plain strings

```
p × uvarint length (bytes, not characters)   then   p × UTF-8 bytes, concatenated
```

All lengths first, then all bytes. Each length MUST be ≤ the string limit (§9), else `LimitExceeded`. Bytes MUST be valid UTF-8, else `InvalidUtf8`.

### 6.3 Decimal

The encoder multiplies by 10^scale; the result MUST be an exact integer that fits in int64, else the **encoder** fails (§10.2). An `unsigned` decimal MUST NOT be negative. Decoders produce `stored × 10^−scale` exactly (never via binary floating point in .NET).

### 6.4 Delta

For a field with `delta: true`, the encoder writes, for present values `v₀, v₁, …` in row order:

```
zigzag(v₀ − 0), zigzag(v₁ − v₀), zigzag(v₂ − v₁), …
```

Always zigzag, even for unsigned types and unsigned decimals (differences can be negative). Differences are computed on the stored integer (scaled decimal, epoch milliseconds) and MUST fit in int64. Nulls are skipped: the difference is to the previous **present** value.

### 6.5 Datetime

Encoders convert to UTC and truncate to whole milliseconds. Decoders produce a UTC instant. Values before 1970 are negative and valid.

---

## 7. Nulls and nesting

### 7.1 Presence bitmap

A `nullable` field's block body starts with a bitmap of `⌈n/8⌉` bytes (`n` = rows of the table this column belongs to), bit-packed per §5.3, 1 = present. Values (or nested tables) follow for present rows only. Non-nullable fields carry no bitmap; an encoder given a null for one MUST fail (§10.2).

### 7.2 object

A field of type `object` holds a nested record. Its block body is:

```
[presence bitmap, if nullable]   then   a table of m rows over the object's `fields`
```

`m` = number of present objects (= `n` when not nullable). The child table is a sequence of column blocks (§4.3), with no row count of its own.

### 7.3 list

A field of type `list` holds a sequence per row. Its block body is:

```
[presence bitmap, if nullable]
p × uvarint count            (elements in each present row's list; 0 = empty list, not null)
uvarint c                    (total elements; MUST equal the sum of the counts, else CountMismatch)
a table of c rows over the list's `fields`
```

Child rows are in parent order: the first row's elements, then the second's, and so on. A decoder slices them with a running sum of the counts.

**Lists of scalars** use exactly one field named `$value`, which holds the element:

```json
{"name":"tags","type":"list","nullable":true,"fields":[{"name":"$value","type":"string"}]}
```

`$value` is reserved and cannot clash with a real field name. Elements of a `$value` list MAY be nullable via the field's own `nullable` key. Lists of lists are expressed the same way (`$value` of type `list`).

### 7.4 Depth

The root table is depth 1; each `object` or `list` level adds 1. Depth MUST NOT exceed 8 (`LimitExceeded`).

---

## 8. Dictionary mode (feature `d`)

Valid only for `string` fields, signalled by mode byte `01`. Block body:

```
[presence bitmap, if nullable]
uvarint k                       distinct values, 1 … 65,536
k × (uvarint length + UTF-8 bytes)   the entries
p × index                       1 byte each if k ≤ 256, else 2 bytes little-endian
```

Each index MUST be < `k`, else `IndexOutOfRange`. `k` > 65,536 is `LimitExceeded`. When any column (at any depth) uses mode `01`, header flag `D` MUST be set.

### 8.1 Encoder rule (normative for conformance)

An encoder uses dictionary mode for a string column if and only if:

1. the request allowed feature `d`, and
2. `p > 0`, and
3. `2·k < p` (fewer than 50% of present values are distinct), and
4. `k ≤ 65,536`.

Entries are written in **order of first appearance**. Following this rule exactly is what makes encoders byte-identical to the golden vectors. Servers MAY expose the 50% threshold as an option, but conformance tests run with the default.

---

## 9. Limits

| Limit | Default | Error |
| --- | --- | --- |
| Payload size | 32 MiB | `LimitExceeded` |
| Rows in any one table (root or child) | 100,000 | `LimitExceeded` |
| Single string or dictionary entry | 1 MiB | `LimitExceeded` |
| Dictionary entries | 65,536 (fixed) | `LimitExceeded` |
| Nesting depth | 8 (fixed) | `LimitExceeded` |
| uvarint length | 10 bytes (fixed) | `VarintOverflow` |

Defaults MAY be raised by configuration on both sides. Decoders MUST NOT allocate based on a count before checking it against the limit and against the bytes remaining (each value needs at least 1 bit, most at least 1 byte).

---

## 10. Encoder rules

### 10.1 Determinism

Given the same schema, data and requested features, an encoder MUST produce identical bytes: shortest uvarints, zero padding bits, dictionary rule and entry order per §8.1.

### 10.2 Failures

An encoder MUST fail with an encode error (not a format error) when: a non-nullable value is null, a decimal has more digits than its scale or overflows int64, an unsigned decimal is negative, a delta difference overflows int64, or any limit is exceeded. Encoders MUST encode the whole payload into a buffer before writing anything to the network, so a failure can still fall back to JSON (§10.3).

### 10.3 JSON fallback

The server serves JSON instead of CCF when: the client did not accept CCF; the response type cannot be expressed (no schema can be built, see ARCHITECTURE.md §5); the value is null or a scalar; or encoding fails. Fallback MUST be invisible to the client except for the `Content-Type`.

---

## 11. Decoder error codes

Every decoder failure is reported as a single error type carrying one of these codes. Nothing partial is returned.

| Code | Raised when |
| --- | --- |
| `BadMagic` | Bytes 0–1 are not `43 43` |
| `UnsupportedVersion` | Byte 2 is not `01` |
| `ReservedFlags` | Any of flag bits 2–7 is set |
| `SchemaMismatch` | Header schema id differs from the schema supplied |
| `SingleObjectRowCount` | Flag `S` set and row count ≠ 1 |
| `Truncated` | A read runs past the end of the payload |
| `LengthOverrun` | A length runs past the end of its enclosing block |
| `BlockLengthMismatch` | A block body was not consumed exactly |
| `TrailingData` | Bytes remain after the root table |
| `VarintOverflow` | uvarint longer than 10 bytes or above 2⁶⁴−1 |
| `InvalidMode` | Mode byte not `00`/`01`, or `01` on a non-string field |
| `IndexOutOfRange` | Enum or dictionary index out of range |
| `ValueOutOfRange` | int32 / uint32 value outside its range |
| `CountMismatch` | List total `c` ≠ sum of counts |
| `InvalidUtf8` | String bytes are not valid UTF-8 |
| `LimitExceeded` | Any limit in §9 |

Client-side conditions that are not format errors (schema fetch failed, unknown schema id) are reported separately and trigger the JSON retry described in ARCHITECTURE.md §6.

---

## 12. Worked example (real bytes)

The first 3 of the 500 rows in `test-vectors/v01-products-plain.ccf`:

```
1001 | Steel Monitor  | 361.48 | active | 142
1002 | Deluxe Monitor | 715.82 | active | 432
1003 | Steel Gadget   | 287.57 | active | 616
```

### 12.1 Plain mode (`v01`, 9,317 bytes)

```
HEADER
43 43             magic "CC"
01                version 1
00                flags: none
c3 3a 3e d0       schema id c33a3ed0 (Product)
f4 03             500 rows

id       int64, delta              (offset 10)
00 f5 03          plain, L = 501
d2 0f             zz(1001 − 0)    = 2002
02 02 08 …        zz(+1), zz(+1), zz(+4) …

name     string                    (offset 514)
00 e6 31          plain, L = 6,374
0d 0e 0c …        lengths 13, 14, 12 … (500 uvarints)
53 74 65 65 6c 20 4d 6f 6e 69 74 6f 72 …   "Steel Monitor", "Deluxe Monitor", …

price    decimal(2), unsigned      (offset 6,891)
00 8f 0b          plain, L = 1,423
b4 9a 02          36148 → 361.48
9e af 04 …        71582 → 715.82 …

status   enum(active, discontinued)  (offset 8,317)
00 3f             plain, L = 63 (500 bits)
…                 1 bit per row, LSB-first; 0 = active

stock    uint32                    (offset 8,382)
00 a4 07          plain, L = 932
8e 01 b0 03 e8 04 …   142, 432, 616 …
```

### 12.2 Dictionary mode (`v02`, 5,266 bytes)

Header identical except flags `01`. Only the name block changes:

```
name     string, dictionary        (offset 514)
01 93 12          dictionary, L = 2,323
8e 01             k = 142 entries
0d 53 74 65 65 6c 20 4d 6f 6e 69 74 6f 72      entry 0: "Steel Monitor"
0e 44 65 6c 75 78 65 …                         entry 1: "Deluxe Monitor"
…                                               (order of first appearance)
00 01 02 …        500 indices, 1 byte each (k ≤ 256)
```

---

## 13. Conformance

An implementation conforms when, for every vector in `test-vectors/manifest.json`:

- **valid** vectors: the decoder produces exactly the `.expected.json` value (comparison rules in `test-vectors/README.md`), and the encoder, given that value, the schema and `requestedFeatures`, produces the `.ccf` file byte for byte;
- **invalid** vectors: the decoder fails with exactly `expectedError`.

---

## 14. Clarifications (v1.0.1)

These rules resolve points the sections above leave open. None of them changes the wire format.

1. **Length errors.** Reading past the end of the current block body is `LengthOverrun`. A block length larger than the remaining payload is `Truncated`. A block length larger than the enclosing block is `LengthOverrun`. `BlockLengthMismatch` is raised only when a body ends with bytes left unconsumed.
2. **Delta decoding.** The running value MUST fit the column's type range (int32, uint32, int64, uint64, or int64 for scaled decimals and datetime); otherwise `ValueOutOfRange`. int64 overflow MUST be detected with checked arithmetic.
3. **Invalid schemas.** A schema with a bad name, a `scale` outside 0–18, an inapplicable key, duplicate names, or an unknown type or key is rejected with `CcfSchemaException` (.NET) / `CcfSchemaError` (TypeScript). Inapplicable keys are rejected, not dropped. This error is separate from the format error codes in §11.
4. **float64.** The encoder fails on NaN, +Infinity and −Infinity (so the response falls back to JSON) and preserves −0.0. Decoders accept any bit pattern.
5. **Dictionary blocks.** Decoders validate structure only (k ≤ 65,536, every index < k), not the encoder rule of §8.1. k = 0 with p = 0 is valid; k = 0 with p > 0 fails with `IndexOutOfRange` on the first index.
6. **Row limit.** The row limit of §9 applies to every table separately, including list child tables. It is configurable on both sides; the default stays 100,000.
7. **Float comparison in expected files.** Floats in `*.expected.json` are compared numerically (parsed double equality, with −0.0 distinct from 0.0), not as text.
8. **Datetime range.** Decoders MAY reject datetime values outside the platform's representable range with `ValueOutOfRange`; every platform must accept at least 0001-01-01 to 9999-12-31 UTC.
