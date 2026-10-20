import { execFileSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, readdirSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { describe, expect, it } from "vitest";
import { readPayload, readSchemaText, vectors } from "./support/vectors.js";

const packageDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const manifest = JSON.parse(readFileSync(join(packageDir, "package.json"), "utf8")) as Record<string, unknown>;

describe("package", () => {
  it("has zero runtime dependencies", () => {
    for (const key of ["dependencies", "peerDependencies", "optionalDependencies", "bundledDependencies"]) {
      expect(manifest[key], key).toBeUndefined();
    }
  });

  it("imports only its own files, in every source file", () => {
    for (const file of readdirSync(join(packageDir, "src")).filter((f) => f.endsWith(".ts"))) {
      const source = readFileSync(join(packageDir, "src", file), "utf8");
      const specifiers = [...source.matchAll(/(?:from|import)\s*\(?\s*["']([^"']+)["']/g)].map((m) => m[1]!);
      for (const s of specifiers) expect(s.startsWith("./"), `${file} imports "${s}"`).toBe(true);
    }
  });

  it("is an ES module that exposes types", () => {
    expect(manifest["type"]).toBe("module");
    expect(manifest["types"]).toBe("./dist/index.d.ts");
    expect(JSON.stringify(manifest["exports"])).toContain('"import":"./dist/index.js"');
  });

  it("builds to ESM plus .d.ts and the build decodes a vector in plain Node", async () => {
    const out = mkdtempSync(join(tmpdir(), "ccf-build-"));
    const tsc = createRequire(import.meta.url).resolve("typescript/bin/tsc");
    execFileSync(process.execPath, [tsc, "-p", join(packageDir, "tsconfig.build.json"), "--outDir", out], { stdio: "pipe" });

    for (const f of ["index.js", "index.d.ts", "decoder.js", "decoder.d.ts", "client.d.ts"]) expect(existsSync(join(out, f)), f).toBe(true);
    const indexDts = readFileSync(join(out, "index.d.ts"), "utf8");
    for (const name of ["decode", "parseSchema", "createCcfClient", "IndexedDbSchemaCache", "CcfFormatError", "readSchemaId"]) {
      expect(indexDts, name).toContain(name);
    }

    // The emitted package imports without a bundler or any dependency: a copy of the vector decodes.
    const built = (await import(pathToFileURL(join(out, "index.js")).href)) as typeof import("../src/index.js");
    const v = vectors.find((x) => x.name === "v01-products-plain")!;
    const rows = built.decode(readPayload(v), await built.parseSchema(readSchemaText(v))) as unknown[];
    expect(rows).toHaveLength(500);
  }, 60_000);
});
