// Shared by the Node and browser runners: times CCF decoding against JSON parsing (with and without decompression).
// Everything starts from the wire bytes (Uint8Array) and produces the same JavaScript values.

const median = (xs) => {
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.floor(s.length / 2)];
};

/** Time `fn` (sync or async): calibrate a batch to ~25 ms, run `samples` batches, return per-op milliseconds. */
async function time(fn, samples = 25) {
  let sink;
  for (let i = 0; i < 3; i++) sink = await fn();
  let n = 1;
  for (;;) {
    const t0 = performance.now();
    for (let i = 0; i < n; i++) sink = await fn();
    const dt = performance.now() - t0;
    if (dt >= 25 || n >= 1 << 16) break;
    n = Math.max(n * 2, Math.ceil((n * 25) / Math.max(dt, 0.01)));
  }
  const per = [];
  for (let s = 0; s < samples; s++) {
    const t0 = performance.now();
    for (let i = 0; i < n; i++) sink = await fn();
    per.push((performance.now() - t0) / n);
  }
  globalThis.__sink = sink;
  return { medianMs: median(per), minMs: Math.min(...per), batch: n, samples };
}

/**
 * @param {object} api       `{ decode, parseSchema }` from @skm/ccf-client
 * @param {Array}  datasets  `{ name, rows, schema, json, gz, plain, dict, br? }` (Uint8Array / string)
 * @param {object} env       `{ gunzip(bytes): Promise<Uint8Array>, brotli?(bytes): Uint8Array }`
 */
export async function runAll(api, datasets, env) {
  const td = new TextDecoder("utf-8");
  const results = [];
  for (const d of datasets) {
    const schema = await api.parseSchema(d.schema);
    const expected = JSON.parse(td.decode(d.json));
    for (const [label, bytes] of [["plain", d.plain], ["dict", d.dict]]) {
      if (JSON.stringify(api.decode(bytes, schema)) !== JSON.stringify(expected)) {
        throw new Error(`${d.name}: CCF ${label} does not decode to the JSON value`);
      }
    }
    const cases = {
      json_parse: () => JSON.parse(td.decode(d.json)),
      json_gunzip_parse: async () => JSON.parse(td.decode(await env.gunzip(d.gz))),
      gunzip_only: async () => (await env.gunzip(d.gz)).length,
      ccf_plain: () => api.decode(d.plain, schema),
      ccf_dict: () => api.decode(d.dict, schema),
    };
    if (env.brotli && d.br) {
      cases.json_brotli_parse = () => JSON.parse(td.decode(env.brotli(d.br)));
    }
    const row = { dataset: d.name, rows: expected.length };
    for (const [name, fn] of Object.entries(cases)) row[name] = await time(fn);
    results.push(row);
  }
  return results;
}

export function formatResults(results, title) {
  const ms = (c) => (c ? c.medianMs.toFixed(3) : "n/a");
  const lines = [`### ${title}`, "", "Median ms per decode (from wire bytes to JS values).", ""];
  lines.push("| Dataset | JSON.parse | gunzip + JSON.parse | (gunzip alone) | brotli + JSON.parse | CCF plain | CCF dictionary | dict vs JSON.parse | dict vs gzip JSON |");
  lines.push("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
  for (const r of results) {
    lines.push(
      `| ${r.dataset} | ${ms(r.json_parse)} | ${ms(r.json_gunzip_parse)} | ${ms(r.gunzip_only)} | ${ms(r.json_brotli_parse)} | ${ms(r.ccf_plain)} | ${ms(r.ccf_dict)} | ` +
        `${(r.json_parse.medianMs / r.ccf_dict.medianMs).toFixed(2)}x | ${(r.json_gunzip_parse.medianMs / r.ccf_dict.medianMs).toFixed(2)}x |`,
    );
  }
  lines.push("", "The last two columns are JSON time divided by CCF dictionary time: above 1.00x means CCF is faster.");
  return lines.join("\n");
}
