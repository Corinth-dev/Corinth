// node bench/node.mjs [dataDir]  (after `npm run build` and `dotnet run ... -- export`)
import { readFile, writeFile } from "node:fs/promises";
import { brotliDecompressSync } from "node:zlib";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { decode, parseSchema } from "../packages/client/dist/index.js";
import { formatResults, runAll } from "./harness.mjs";

const dir = resolve(process.argv[2] ?? join(fileURLToPath(new URL(".", import.meta.url)), "..", "..", "bench-data"));
const manifest = JSON.parse(await readFile(join(dir, "manifest.json"), "utf8"));
const read = async (f) => new Uint8Array(await readFile(join(dir, f)));
const datasets = [];
for (const m of manifest) {
  datasets.push({
    name: m.name,
    schema: await readFile(join(dir, `${m.name}.schema.json`), "utf8"),
    json: await read(`${m.name}.json`),
    gz: await read(`${m.name}.json.gz`),
    br: await read(`${m.name}.json.br`),
    plain: await read(`${m.name}.plain.ccf`),
    dict: await read(`${m.name}.dict.ccf`),
  });
}

const gunzip = async (bytes) =>
  new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"))).arrayBuffer());
const results = await runAll({ decode, parseSchema }, datasets, { gunzip, brotli: (b) => brotliDecompressSync(b) });
const title = `Node ${process.versions.node} (V8 ${process.versions.v8})`;
await writeFile(join(dir, "results-node.json"), JSON.stringify({ title, results }, null, 1));
console.log(formatResults(results, title));
