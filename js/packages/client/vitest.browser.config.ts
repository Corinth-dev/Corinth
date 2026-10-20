import { defineConfig } from "vitest/config";

// Browser run: needs `npm run build` first (it loads dist/ in a real Chromium via Playwright).
export default defineConfig({
  test: { include: ["test/browser/**/*.test.ts"], testTimeout: 60_000, hookTimeout: 60_000 },
});
