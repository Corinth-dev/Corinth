/**
 * Deep comparison of two values in the canonical JSON representation (test-vectors/README.md, SPEC §14.7).
 * Objects compare irrespective of key order, arrays in order, strings exactly, numbers by parsed double
 * equality with -0 distinct from 0.
 *
 * Returns `null` when equal, otherwise a description of the first difference with its path.
 */
export function canonicalDiff(expected: unknown, actual: unknown, path = "$"): string | null {
  if (expected === null || actual === null) {
    return expected === actual ? null : `${path}: expected ${show(expected)}, got ${show(actual)}`;
  }
  if (typeof expected !== typeof actual || Array.isArray(expected) !== Array.isArray(actual)) {
    return `${path}: expected ${show(expected)}, got ${show(actual)}`;
  }
  switch (typeof expected) {
    case "number":
    case "string":
    case "boolean":
      return Object.is(expected, actual) ? null : `${path}: expected ${show(expected)}, got ${show(actual)}`;
    case "object": {
      if (Array.isArray(expected)) {
        const a = actual as unknown[];
        if (expected.length !== a.length) return `${path}: expected ${expected.length} elements, got ${a.length}`;
        for (let i = 0; i < expected.length; i++) {
          const d = canonicalDiff(expected[i], a[i], `${path}[${i}]`);
          if (d !== null) return d;
        }
        return null;
      }
      const e = expected as Record<string, unknown>;
      const a = actual as Record<string, unknown>;
      for (const key of Object.keys(e)) {
        if (!Object.hasOwn(a, key)) return `${path}: missing property "${key}"`;
        const d = canonicalDiff(e[key], a[key], `${path}.${key}`);
        if (d !== null) return d;
      }
      const extra = Object.keys(a).find((k) => !Object.hasOwn(e, k));
      return extra === undefined ? null : `${path}: unexpected property "${extra}"`;
    }
    default:
      return `${path}: unsupported type ${typeof expected}`;
  }
}

export function canonicalEqual(expected: unknown, actual: unknown): boolean {
  return canonicalDiff(expected, actual) === null;
}

function show(v: unknown): string {
  return Object.is(v, -0) ? "-0" : JSON.stringify(v);
}
