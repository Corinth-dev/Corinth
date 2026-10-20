import { describe, expect, it } from "vitest";
import { CcfFormatError, type CcfSchema, decode, parseSchema, readSchemaId } from "../src/index.js";
import { readPayload, readSchemaText, vectors } from "./support/vectors.js";

/** Small deterministic PRNG so a failing run can be reproduced from its seed. */
function mulberry32(seed: number): () => number {
  let a = seed >>> 0;
  return () => {
    a = (a + 0x6d2b79f5) >>> 0;
    let t = a;
    t = Math.imul(t ^ (t >>> 15), t | 1);
    t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const INTERESTING = [0, 1, 2, 3, 0x7f, 0x80, 0x81, 0xfe, 0xff];
const VARINTS = [
  [0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x01],
  [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01],
  [0xff, 0xff, 0xff, 0x7f],
  [0x80, 0x80, 0x04],
  [0xa0, 0x8d, 0x06],
  [0xa1, 0x8d, 0x06],
];

interface Seed {
  readonly name: string;
  readonly payload: Uint8Array;
  readonly schema: CcfSchema;
}

function mutate(rand: () => number, source: Uint8Array, seeds: readonly Seed[]): Uint8Array {
  let data = Array.from(source);
  const int = (n: number) => Math.floor(rand() * n);
  for (let m = 1 + int(4); m > 0; m--) {
    if (data.length === 0) {
      data.push(int(256));
      continue;
    }
    switch (int(12)) {
      case 0: data[int(data.length)]! ^= 1 << int(8); break;
      case 1: data[int(data.length)] = int(256); break;
      case 2: data[int(data.length)] = INTERESTING[int(INTERESTING.length)]!; break;
      case 3: data[int(Math.min(data.length, 24))] = INTERESTING[int(INTERESTING.length)]!; break;
      case 4: data = data.slice(0, int(data.length + 1)); break;
      case 5: data.splice(int(data.length), 1 + int(11)); break;
      case 6: data.splice(int(data.length + 1), 0, ...Array.from({ length: 1 + int(7) }, () => int(256))); break;
      case 7: {
        const v = VARINTS[int(VARINTS.length)]!;
        const pos = int(data.length);
        for (let i = 0; i < v.length && pos + i < data.length; i++) data[pos + i] = v[i]!;
        break;
      }
      case 8: {
        const from = int(data.length);
        data.splice(int(data.length + 1), 0, ...data.slice(from, from + 1 + int(40)));
        break;
      }
      case 9: {
        const other = seeds[int(seeds.length)]!.payload;
        const o = int(other.length);
        const dest = int(data.length);
        const len = 1 + int(64);
        for (let i = 0; i < len && o + i < other.length && dest + i < data.length; i++) data[dest + i] = other[o + i]!;
        break;
      }
      case 10: data[int(Math.min(data.length, 64))] = int(4); break;
      default: data.push(...Array.from({ length: 1 + int(5) }, () => int(256)));
    }
  }
  return Uint8Array.from(data);
}

describe("mutation fuzz", () => {
  it("only ever raises CcfFormatError, within bounded time and memory", async () => {
    const seconds = Number(process.env["CCF_FUZZ_SECONDS"] ?? 3);
    const seed = Number(process.env["CCF_FUZZ_SEED"] ?? Date.now() % 1_000_000);
    const rand = mulberry32(seed);
    const seeds: Seed[] = [];
    for (const v of vectors) seeds.push({ name: v.name, payload: readPayload(v), schema: await parseSchema(readSchemaText(v)) });

    const counts = new Map<string, number>();
    const failures: string[] = [];
    let iterations = 0;
    let ok = 0;
    let slowest = 0;
    const deadline = Date.now() + seconds * 1000;
    while (Date.now() < deadline && failures.length < 10) {
      const s = seeds[Math.floor(rand() * seeds.length)]!;
      const payload = mutate(rand, s.payload, seeds);
      const started = performance.now();
      try {
        // bigint keeps the int64 option from raising its (documented) RangeError.
        decode(payload, s.schema, { int64: "bigint", decimal: "string" });
        ok++;
      } catch (e) {
        if (e instanceof CcfFormatError) counts.set(e.code, (counts.get(e.code) ?? 0) + 1);
        else failures.push(`${(e as Error).name}: ${(e as Error).message} | seed=${s.name} payload=${Buffer.from(payload).toString("hex")}`);
      }
      try {
        readSchemaId(payload);
      } catch (e) {
        if (!(e instanceof CcfFormatError)) failures.push(`readSchemaId: ${(e as Error).message} | ${Buffer.from(payload).toString("hex")}`);
      }
      const elapsed = performance.now() - started;
      slowest = Math.max(slowest, elapsed);
      if (elapsed > 2000) failures.push(`decode took ${elapsed.toFixed(0)} ms | seed=${s.name} payload=${Buffer.from(payload).toString("hex")}`);
      iterations++;
    }

    console.log(`fuzz seed=${seed} iterations=${iterations} decodedOk=${ok} slowest=${slowest.toFixed(1)}ms`, Object.fromEntries([...counts].sort()));
    expect(failures).toEqual([]);
    expect(iterations).toBeGreaterThan(100);
  }, 36_000_000);
});
