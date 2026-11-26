import { readFileSync, writeFileSync } from "node:fs";
import { CcfSchemaError, CcfSchemaFetchError, parseSchema, verifySchema } from "@corinth/client";
import { type TypegenOptions, generateTypes } from "./generate.js";

/** Where the CLI reads and writes; replaceable in tests. */
export interface CliIo {
  stdout(text: string): void;
  stderr(text: string): void;
  readFile(path: string): string;
  readStdin(): string;
  writeFile(path: string, text: string): void;
  fetchText(url: string): Promise<string>;
}

export const USAGE = `Usage: corinth-typegen <schema> [options]

  <schema>              a schema JSON file, "-" for stdin, or an http(s) URL such as
                        https://api.example.com/ccf/schemas/c33a3ed0
Options:
  -o, --out <file>      write the interfaces to <file> instead of stdout
  --name <Name>         name of the root interface (default: the schema name)
  --int64 <number|bigint>    type of int64/uint64 fields (default number)
  --decimal <number|string>  type of decimal fields (default number)
  -h, --help            show this help
`;

export const nodeIo: CliIo = {
  stdout: (t) => void process.stdout.write(t),
  stderr: (t) => void process.stderr.write(t),
  readFile: (p) => readFileSync(p, "utf8"),
  readStdin: () => readFileSync(0, "utf8"),
  writeFile: (p, t) => writeFileSync(p, t),
  fetchText: async (url) => {
    const response = await fetch(url, { headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error(`GET ${url} failed: HTTP ${response.status}`);
    return response.text();
  },
};

/** Runs the CLI and returns the process exit code. */
export async function main(argv: readonly string[], io: CliIo = nodeIo): Promise<number> {
  let input: string | undefined;
  let out: string | undefined;
  const options: TypegenOptions = {};

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i]!;
    const value = (): string => {
      const v = argv[++i];
      if (v === undefined) throw new UsageError(`${arg} needs a value`);
      return v;
    };
    try {
      switch (arg) {
        case "-h": case "--help": io.stdout(USAGE); return 0;
        case "-o": case "--out": out = value(); break;
        case "--name": options.rootName = value(); break;
        case "--int64": options.int64 = oneOf(arg, value(), ["number", "bigint"] as const); break;
        case "--decimal": options.decimal = oneOf(arg, value(), ["number", "string"] as const); break;
        default:
          if (arg.startsWith("-") && arg !== "-") throw new UsageError(`unknown option ${arg}`);
          if (input !== undefined) throw new UsageError("only one schema can be given");
          input = arg;
      }
    } catch (e) {
      if (!(e instanceof UsageError)) throw e;
      io.stderr(`corinth-typegen: ${e.message}\n\n${USAGE}`);
      return 2;
    }
  }
  if (input === undefined) {
    io.stderr(`corinth-typegen: no schema given\n\n${USAGE}`);
    return 2;
  }

  try {
    const text = /^https?:\/\//i.test(input) ? await io.fetchText(input) : input === "-" ? io.readStdin() : io.readFile(input);
    // A schema URL ends with its id: check the content against it, like a client does.
    const urlId = /^https?:\/\/.*\/([0-9a-f]{8})$/i.exec(input)?.[1]?.toLowerCase();
    const schema = urlId !== undefined ? await verifySchema(urlId, text) : await parseSchema(text);
    const output = generateTypes(schema, options);
    if (out !== undefined) io.writeFile(out, output);
    else io.stdout(output);
    return 0;
  } catch (e) {
    const known = e instanceof CcfSchemaError || e instanceof CcfSchemaFetchError;
    io.stderr(`corinth-typegen: ${known ? "invalid schema: " : ""}${(e as Error).message}\n`);
    return 1;
  }
}

class UsageError extends Error {}

function oneOf<T extends string>(option: string, value: string, allowed: readonly T[]): T {
  if (!(allowed as readonly string[]).includes(value)) throw new UsageError(`${option} must be one of: ${allowed.join(", ")}`);
  return value as T;
}
