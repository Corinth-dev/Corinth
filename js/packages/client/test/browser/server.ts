import { createServer, type IncomingMessage, type Server, type ServerResponse } from "node:http";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import type { AddressInfo } from "node:net";
import { pathOf, readPayload, readSchemaText, vectors } from "../support/vectors.js";

const dist = resolve(dirname(fileURLToPath(import.meta.url)), "../../dist");

const MIME: Record<string, string> = { ".js": "text/javascript", ".json": "application/json", ".html": "text/html" };

export interface TestServer {
  readonly origin: string;
  /** Requests received, as `METHOD path` strings. */
  readonly requests: string[];
  close(): Promise<void>;
}

/**
 * Serves the built package (`/dist/*`), the vector files (`/vectors/*`) and a small CCF API: `/api/products`
 * (v01, or v02 when the Accept header allows dictionaries) and `/ccf/schemas/{id}`.
 */
export async function startServer(): Promise<TestServer> {
  const requests: string[] = [];
  const product = vectors.find((v) => v.name === "v01-products-plain")!;
  const productDict = vectors.find((v) => v.name === "v02-products-dict")!;
  const schemaText = readSchemaText(product);

  const server: Server = createServer((req: IncomingMessage, res: ServerResponse) => {
    const url = new URL(req.url ?? "/", "http://localhost");
    requests.push(`${req.method} ${url.pathname}`);
    const send = (status: number, type: string, body: string | Uint8Array) => {
      res.writeHead(status, { "content-type": type, "cache-control": "no-store" });
      res.end(body);
    };

    if (url.pathname === "/") return send(200, "text/html", "<!doctype html><title>ccf</title>");
    if (url.pathname.startsWith("/dist/")) {
      const file = resolve(dist, url.pathname.slice("/dist/".length));
      if (!file.startsWith(dist)) return send(403, "text/plain", "no");
      try {
        return send(200, MIME[file.slice(file.lastIndexOf("."))] ?? "application/octet-stream", readFileSync(file));
      } catch {
        return send(404, "text/plain", "not found");
      }
    }
    if (url.pathname === "/vectors/manifest.json") return send(200, "application/json", readFileSync(pathOf("manifest.json")));
    if (url.pathname.startsWith("/vectors/")) {
      try {
        const name = url.pathname.slice("/vectors/".length);
        return send(200, name.endsWith(".json") ? "application/json" : "application/octet-stream", readFileSync(pathOf(name)));
      } catch {
        return send(404, "text/plain", "not found");
      }
    }
    if (url.pathname === "/ccf/schemas/c33a3ed0") return send(200, "application/json", schemaText);
    if (url.pathname === "/api/products") {
      const accept = req.headers.accept ?? "";
      if (!accept.includes("vnd.ccf")) return send(200, "application/json", JSON.stringify([{ id: 1, name: "json" }]));
      const dict = accept.includes("features=d");
      return send(200, dict ? "application/vnd.ccf; features=d" : "application/vnd.ccf", readPayload(dict ? productDict : product));
    }
    return send(404, "text/plain", "not found");
  });

  await new Promise<void>((r) => server.listen(0, "127.0.0.1", r));
  const origin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
  return { origin, requests, close: () => new Promise<void>((r) => server.close(() => r())) };
}
