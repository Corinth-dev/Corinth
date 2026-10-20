import { defineConfig } from "vitest/config";

// End-to-end run against the .NET sample API: needs the .NET SDK (the test builds the sample if necessary).
export default defineConfig({
  test: { include: ["test/e2e/**/*.test.ts"], testTimeout: 60_000, hookTimeout: 300_000 },
});
