// Scalar configuration module (loaded with WithJavaScriptConfiguration): makes a CCF response readable in Scalar.
//
// Scalar sends its test requests through `customFetch`. When the response is application/vnd.ccf (binary), the
// payload is posted to this API's POST /ccf/decode and Scalar receives the decoded JSON instead, with these extra
// headers so you can still see what happened on the wire:
//   x-ccf-decoded: true           x-ccf-payload-bytes: size of the CCF payload
//   x-ccf-schema-id: schema id    x-ccf-original-content-type: the CCF content type
// If decoding is not possible (no /ccf/decode on that server, unknown schema), the original response is shown.

const CCF = "application/vnd.ccf";

async function ccfAwareFetch(input, init) {
  const response = await fetch(input, init);
  if (!(response.headers.get("content-type") ?? "").toLowerCase().startsWith(CCF)) return response;

  try {
    const url = new URL(typeof input === "string" ? input : (input.url ?? String(input)), location.href);
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

export default { customFetch: ccfAwareFetch };
