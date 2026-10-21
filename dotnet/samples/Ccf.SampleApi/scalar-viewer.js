// Scalar configuration module (loaded with WithJavaScriptConfiguration): makes a CCF response readable in Scalar.
//
// Scalar sends its test requests through `customFetch`. A small "CCF decode" toggle (bottom right of the page,
// remembered in the browser, OFF by default) decides what Scalar receives for an application/vnd.ccf response:
//   OFF  the response exactly as the server sent it (binary; Scalar shows "Binary file" and a download link);
//   ON   the payload is posted to this API's POST /ccf/decode and Scalar gets the decoded JSON instead, plus
//        x-ccf-decoded: true, x-ccf-payload-bytes, x-ccf-schema-id and x-ccf-original-content-type headers.
// The toggle applies to the next request you send. If decoding is not possible (no /ccf/decode on that server,
// unknown schema), the original response is shown even when the toggle is ON.

const CCF = "application/vnd.ccf";
const STORAGE_KEY = "ccf-scalar-decode";
const TOGGLE_ID = "ccf-decode-toggle";

function decodeEnabled() {
  try {
    return globalThis.localStorage?.getItem(STORAGE_KEY) === "on";
  } catch {
    return false; // storage blocked: stay raw
  }
}

function storeDecodeEnabled(on) {
  try {
    globalThis.localStorage?.setItem(STORAGE_KEY, on ? "on" : "off");
  } catch {
    // not remembered; the toggle still works for this page view
  }
}

async function ccfAwareFetch(input, init) {
  const response = await fetch(input, init);
  if (!decodeEnabled()) return response;
  if (!(response.headers.get("content-type") ?? "").toLowerCase().startsWith(CCF)) return response;

  try {
    const url = new URL(typeof input === "string" ? input : (input.url ?? String(input)), globalThis.location?.href);
    const payload = await response.clone().arrayBuffer();
    const decoded = await fetch(new URL("/ccf/decode", url.origin), {
      method: "POST",
      headers: { "content-type": CCF },
      body: payload,
    });
    if (!decoded.ok) {
      console.warn("[ccf] /ccf/decode answered", decoded.status, await decoded.text());
      return response;
    }

    const headers = new Headers(response.headers);
    headers.delete("content-length");
    headers.delete("content-encoding");
    headers.set("content-type", "application/json; charset=utf-8");
    headers.set("x-ccf-decoded", "true");
    headers.set("x-ccf-payload-bytes", String(payload.byteLength));
    headers.set("x-ccf-schema-id", decoded.headers.get("x-ccf-schema-id") ?? "");
    headers.set("x-ccf-original-content-type", response.headers.get("content-type") ?? CCF);
    return new Response(await decoded.text(), { status: response.status, statusText: response.statusText, headers });
  } catch (error) {
    console.warn("[ccf] could not decode the CCF response; showing it as received", error);
    return response;
  }
}

function mountToggle() {
  if (typeof document === "undefined" || document.getElementById(TOGGLE_ID)) return;

  const button = document.createElement("button");
  button.id = TOGGLE_ID;
  button.type = "button";
  button.setAttribute("role", "switch");
  Object.assign(button.style, {
    position: "fixed", right: "16px", bottom: "16px", zIndex: "2147483647", padding: "8px 14px",
    border: "1px solid rgba(255,255,255,.35)", borderRadius: "999px", background: "#1f2937", color: "#fff",
    font: "600 13px/1 system-ui, sans-serif", cursor: "pointer", boxShadow: "0 2px 8px rgba(0,0,0,.35)",
  });

  const render = () => {
    const on = decodeEnabled();
    button.textContent = on ? "CCF decode: ON" : "CCF decode: OFF";
    button.setAttribute("aria-checked", String(on));
    button.style.background = on ? "#047857" : "#1f2937";
    button.title = on
      ? "ON: a CCF response is decoded to JSON (applies to the next request you send)."
      : "OFF: a CCF response is shown as the server sent it, binary (applies to the next request you send).";
  };
  button.addEventListener("click", () => {
    storeDecodeEnabled(!decodeEnabled());
    render();
  });
  render();
  document.body.appendChild(button);
}

mountToggle();

export default { customFetch: ccfAwareFetch };
