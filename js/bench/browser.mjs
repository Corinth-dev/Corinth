// node bench/browser.mjs [dataDir]: the same benchmark inside a real Chromium (playwright-core), page served from localhost.
import { createServer } from "node:http";
import { readFile, writeFile } from "node:fs/promises";
import { extname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { chromium } from "playwright-core";
import { formatResults } from "./harness.mjs";

const here = fileURLToPath(new URL(".", import.meta.url));
const dir = resolve(process.argv[2] ?? join(here, "..", "..", "bench-data"));
const dist = join(here, "..", "packages", "client", "dist");
const types = { ".js": "text/javascript", ".mjs": "text/javascript", ".html": "text/html", ".json": "application/json" };

const page = `<!doctype html><meta charset="utf-8"><script type="module">
import { decode, parseSchema } from "/dist/index.js";
import { runAll } from "/harness.mjs";
const manifest = await (await fetch("/data/manifest.json")).json();
const get = async (f) => new Uint8Array(await (await fetch("/data/" + f)).arrayBuffer());
const datasets = [];
for (const m of manifest) {
  datasets.push({ name: m.name, schema: await (await fetch("/data/" + m.name + ".schema.json")).text(),
    json: await get(m.name + ".json"), gz: await get(m.name + ".json.gz"), plain: await get(m.name + ".plain.ccf"), dict: await get(m.name + ".dict.ccf") });
}
const gunzip = async (bytes) => new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"))).arrayBuffer());
window.__result = { results: await runAll({ decode, parseSchema }, datasets, { gunzip }), ua: navigator.userAgent };
</script>`;

const server = createServer(async (req, res) => {
  const url = new URL(req.url, "http://x").pathname;
  try {
    let file;
    if (url === "/") { res.setHeader("content-type", "text/html"); res.end(page); return; }
    if (url.startsWith("/dist/")) file = join(dist, url.slice(6));
    else if (url.startsWith("/data/")) file = join(dir, url.slice(6));
    else if (url === "/harness.mjs") file = join(here, "harness.mjs");
    else { res.statusCode = 404; res.end(); return; }
    res.setHeader("content-type", types[extname(file)] ?? "application/octet-stream");
    res.end(await readFile(file));
  } catch { res.statusCode = 404; res.end(); }
}).listen(0, "127.0.0.1");
await new Promise((r) => server.once("listening", r));

const browser = await chromium.launch({ executablePath: process.env.CCF_CHROMIUM || undefined });
try {
  const p = await browser.newPage();
  p.on("pageerror", (e) => console.error("page error:", e.message));
  await p.goto(`http://127.0.0.1:${server.address().port}/`);
  await p.waitForFunction(() => window.__result, null, { timeout: 15 * 60_000 });
  const { results, ua } = await p.evaluate(() => window.__result);
  const title = `Chromium (${ua.match(/Chrome\/[\d.]+/)?.[0] ?? ua})`;
  await writeFile(join(dir, "results-chromium.json"), JSON.stringify({ title, results }, null, 1));
  console.log(formatResults(results, title));
} finally {
  await browser.close();
  server.close();
}
