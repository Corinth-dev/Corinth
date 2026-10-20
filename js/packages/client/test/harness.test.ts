import { readFileSync } from "node:fs";
import { describe, expect, it } from "vitest";
import { canonicalDiff, canonicalEqual } from "./support/canonicalJson.js";
import { pathOf, readExpected, readPayload, validVectors, vectors } from "./support/vectors.js";

// Tests of the harness itself. These are blocking: they pass in phase 0.

const j = (s: string): unknown => JSON.parse(s);

describe("manifest", () => {
  it("lists all 15 vectors and their files exist", () => {
    expect(vectors).toHaveLength(15);
    expect(vectors.filter((v) => v.kind === "valid")).toHaveLength(6);
    expect(vectors.filter((v) => v.kind === "invalid")).toHaveLength(9);
    expect(new Set(vectors.map((v) => v.name)).size).toBe(vectors.length);
    for (const v of vectors) {
      expect(() => readFileSync(pathOf(v.file)), v.file).not.toThrow();
      expect(() => readFileSync(pathOf(v.schema)), v.schema).not.toThrow();
      expect(v.expected !== undefined, `${v.name} expected`).toBe(v.kind === "valid");
      expect(v.expectedError !== undefined, `${v.name} expectedError`).toBe(v.kind === "invalid");
    }
  });

  it("has payload sizes matching the manifest", () => {
    for (const v of vectors) expect(readPayload(v).length, v.name).toBe(v.bytes);
  });

  it("compares every expected file equal to itself", () => {
    for (const v of validVectors) expect(canonicalDiff(readExpected(v), readExpected(v)), v.name).toBeNull();
  });
});

describe("canonical comparer", () => {
  it("ignores object key order", () => {
    expect(canonicalEqual(j('{"a":1,"b":[1,2]}'), j('{"b":[1,2],"a":1}'))).toBe(true);
  });

  it("respects array order", () => {
    expect(canonicalEqual(j("[1,2]"), j("[2,1]"))).toBe(false);
  });

  it("compares numbers by parsed value, not text", () => {
    expect(canonicalEqual(j("[100.0,0.0,1.7976931348623157e+308]"), j("[100,0,1.7976931348623157E308]"))).toBe(true);
  });

  it("keeps -0.0 distinct from 0.0", () => {
    expect(canonicalEqual(j("[0.0]"), j("[-0.0]"))).toBe(false);
    expect(canonicalEqual(j("[-0.0]"), j("[0]"))).toBe(false);
    expect(canonicalEqual(j("[-0.0]"), j("[-0]"))).toBe(true);
  });

  it("compares floats exactly, not approximately", () => {
    expect(canonicalEqual(j("[3.141592653589793]"), j("[3.1415926535897936]"))).toBe(false);
  });

  it("does not treat strings and numbers as interchangeable", () => {
    expect(canonicalEqual(j('{"id":"1001"}'), j('{"id":1001}'))).toBe(false);
    expect(canonicalEqual(j('["361.48"]'), j("[361.48]"))).toBe(false);
  });

  it("flags null, missing and extra properties", () => {
    expect(canonicalEqual(j('{"a":null}'), j('{"a":0}'))).toBe(false);
    expect(canonicalEqual(j('{"a":1}'), j("{}"))).toBe(false);
    expect(canonicalEqual(j("{}"), j('{"a":1}'))).toBe(false);
    expect(canonicalEqual(j("[]"), j("{}"))).toBe(false);
  });

  it("reports the path of the first difference", () => {
    expect(canonicalDiff(j('[{"lines":[{"qty":1}]},{"lines":[{"qty":2}]}]'), j('[{"lines":[{"qty":1}]},{"lines":[{"qty":3}]}]'))).toBe(
      "$[1].lines[0].qty: expected 2, got 3",
    );
  });
});
