import { type Browser, chromium } from "playwright-core";
import { afterAll, beforeAll, describe, expect, it } from "vitest";
import { type RunningSample, startSample } from "./sampleApi.js";

// The "CCF decode" toggle in the real Scalar UI (bundled in the sample), driven in a real Chromium. This guards the
// viewer script against changes in Scalar: if its page structure or `customFetch` option changes, this fails.

let sample: RunningSample;
let browser: Browser;

beforeAll(async () => {
  sample = await startSample();
  browser = await chromium.launch();
});

afterAll(async () => {
  await browser?.close();
  await sample?.stop();
});

describe("the CCF decode toggle in Scalar", () => {
  it("is off by default, can be switched while the Test Request window is open, and applies to the next request", async () => {
    const context = await browser.newContext({ viewport: { width: 1500, height: 1000 } });
    const page = await context.newPage();

    // Scalar offers "application/json, application/vnd.ccf" (JSON wins); ask for CCF only, as a user would by editing the header.
    await page.route("**/min/products", (route) =>
      route.continue({ headers: { ...route.request().headers(), accept: "application/vnd.ccf" } }),
    );
    const decodeCalls: string[] = [];
    page.on("request", (r) => {
      if (r.url().endsWith("/ccf/decode")) decodeCalls.push(r.method());
    });

    await page.goto(`${sample.origin}/scalar/v1#tag/minimal-apis/GET/min/products`, { waitUntil: "networkidle" });
    const toggle = page.locator("#ccf-decode-toggle");
    expect(await toggle.textContent()).toBe("CCF decode: OFF");

    await page.getByRole("button", { name: /Test Request/i }).first().click({ timeout: 15_000 });
    const send = page.getByRole("button", { name: /^Send/ }).first();

    // OFF: the response is shown as the server sent it.
    await send.click();
    await page.getByText("Binary file").waitFor({ timeout: 15_000 });
    expect(decodeCalls).toHaveLength(0);

    // Switch ON inside the open window; it applies to the next request.
    await toggle.click({ timeout: 5_000 });
    expect(await toggle.textContent()).toBe("CCF decode: ON");
    await send.click();
    await page.getByText("Steel Monitor").first().waitFor({ timeout: 15_000 });
    expect(decodeCalls).toEqual(["POST"]);

    // The choice is remembered across a reload.
    await page.reload({ waitUntil: "networkidle" });
    expect(await page.locator("#ccf-decode-toggle").textContent()).toBe("CCF decode: ON");

    // And OFF again.
    await page.locator("#ccf-decode-toggle").click();
    expect(await page.locator("#ccf-decode-toggle").textContent()).toBe("CCF decode: OFF");
    await context.close();
  });
});
