import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

/** One entry of `test-vectors/manifest.json`. */
export interface VectorEntry {
  name: string;
  kind: "valid" | "invalid";
  description: string;
  file: string;
  schema: string;
  schemaId: string;
  requestedFeatures?: string;
  expectedFlags?: number;
  expectedContentType?: string;
  expected?: string;
  expectedError?: string;
  bytes: number;
}

const root = findRoot();

function findRoot(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = resolve(dir, "test-vectors");
    try {
      readFileSync(resolve(candidate, "manifest.json"));
      return candidate;
    } catch {
      const parent = dirname(dir);
      if (parent === dir) throw new Error("test-vectors/manifest.json not found");
      dir = parent;
    }
  }
}

/** All vectors, in manifest order. Tests are driven from this list, never from hard-coded file names. */
export const vectors: readonly VectorEntry[] = (
  JSON.parse(readFileSync(resolve(root, "manifest.json"), "utf8")) as { vectors: VectorEntry[] }
).vectors;

export const validVectors = vectors.filter((v) => v.kind === "valid");
export const invalidVectors = vectors.filter((v) => v.kind === "invalid");

export function pathOf(file: string): string {
  return resolve(root, file);
}

export function readPayload(v: VectorEntry): Uint8Array {
  return new Uint8Array(readFileSync(pathOf(v.file)));
}

/** The canonical schema text exactly as stored (no trailing newline). */
export function readSchemaText(v: VectorEntry): string {
  return readFileSync(pathOf(v.schema), "utf8");
}

export function readExpected(v: VectorEntry): unknown {
  if (v.expected === undefined) throw new Error(`${v.name} has no expected file`);
  return JSON.parse(readFileSync(pathOf(v.expected), "utf8"));
}
