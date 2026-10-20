import { type ChildProcess, execFileSync, spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../../../..");
const dotnetDir = resolve(repoRoot, "dotnet");

export interface RunningSample {
  readonly origin: string;
  stop(): Promise<void>;
}

/** Builds (if needed) and starts the sample API on a free port. `CCF_SAMPLE_DLL` overrides the assembly path. */
export async function startSample(): Promise<RunningSample> {
  const dll =
    process.env["CCF_SAMPLE_DLL"] ??
    ["Release", "Debug"].map((c) => resolve(dotnetDir, `samples/Ccf.SampleApi/bin/${c}/net10.0/Ccf.SampleApi.dll`)).find(existsSync) ??
    build();

  const child: ChildProcess = spawn("dotnet", [dll], {
    cwd: dirname(dll),
    env: { ...process.env, ASPNETCORE_URLS: "http://127.0.0.1:0", ASPNETCORE_ENVIRONMENT: "Production", DOTNET_NOLOGO: "1" },
    stdio: ["ignore", "pipe", "pipe"],
  });

  const origin = await new Promise<string>((resolveOrigin, reject) => {
    let output = "";
    const timer = setTimeout(() => reject(new Error(`The sample API did not start in 60 s:\n${output}`)), 60_000);
    const onData = (chunk: Buffer) => {
      output += chunk.toString();
      const m = /Now listening on: (http:\/\/[^\s]+)/.exec(output);
      if (m) {
        clearTimeout(timer);
        resolveOrigin(m[1]!);
      }
    };
    child.stdout!.on("data", onData);
    child.stderr!.on("data", onData);
    child.on("exit", (code) => reject(new Error(`The sample API exited with ${code}:\n${output}`)));
  });

  return {
    origin,
    stop: () =>
      new Promise<void>((r) => {
        child.removeAllListeners("exit");
        child.once("exit", () => r());
        child.kill();
      }),
  };
}

function build(): string {
  execFileSync("dotnet", ["build", "-c", "Release", "samples/Ccf.SampleApi", "-f", "net10.0"], { cwd: dotnetDir, stdio: "inherit" });
  return resolve(dotnetDir, "samples/Ccf.SampleApi/bin/Release/net10.0/Ccf.SampleApi.dll");
}
