import { describe, expect, it } from "vitest";
import { canonicalDiff } from "./support/canonicalJson.js";
import { canonicalize, decodeToCanonical, errorCodeOf, schemaIdHex } from "./support/subject.js";
import { invalidVectors, readExpected, readPayload, readSchemaText, validVectors } from "./support/vectors.js";

// Golden-vector tests, one case per manifest entry. They are part of `npm test`; `npm run test:conformance` runs only these.

describe("valid vectors: schema canonical form and id", () => {
  it.each(validVectors.map((v) => [v.name, v] as const))("%s", async (_name, v) => {
    const text = readSchemaText(v);
    expect(await canonicalize(text)).toBe(text);
    expect(await schemaIdHex(text)).toBe(v.schemaId);
  });
});

describe("valid vectors: decode", () => {
  it.each(validVectors.map((v) => [v.name, v] as const))("%s", async (_name, v) => {
    const actual = await decodeToCanonical(readPayload(v), readSchemaText(v));
    expect(canonicalDiff(readExpected(v), actual)).toBeNull();
  });
});

describe("invalid vectors: decode fails with the expected error", () => {
  it.each(invalidVectors.map((v) => [v.name, v] as const))("%s", async (_name, v) => {
    const error = await decodeToCanonical(readPayload(v), readSchemaText(v)).then(
      () => undefined,
      (e: unknown) => e,
    );
    expect(error).toBeDefined();
    expect(errorCodeOf(error)).toBe(v.expectedError);
  });
});
