# SKM.Ccf.Client (.NET)

An `HttpClient` helper that asks for CCF, the Columnar Compact Format (`Accept: application/vnd.ccf; features=d`), decodes the response into your type, and falls back to JSON whenever anything goes wrong. It depends on `SKM.Ccf.Core`, `System.Net.Http.Json` and `Microsoft.Extensions.Logging.Abstractions`.

> CCF here is the *Columnar Compact Format* for API responses. It is not related to Microsoft's Confidential Consortium Framework, which shares the acronym.

```csharp
var order = await http.GetFromCcfAsync<Order>("/orders/7");          // extension method
var client = new CcfClient(http, new CcfClientOptions(), logger);    // or keep a client: it holds the schema cache
var products = await client.GetAsync<List<Product>>("/products");
```

Register a `CcfClient` once and reuse it: the schema cache and the single-flight schema fetch live inside it. `ILogger<CcfClient>` is optional (resolved from DI when you construct the client through the container, `NullLogger` otherwise).

## Behaviour you should know

- **`null` for `204 No Content` or an empty body.** `GetFromJsonAsync` throws on these; this client returns `null` (`default`) for both, whether the server answered in CCF or JSON.
- **When the request is repeated as JSON.** Only a CCF failure triggers the retry: a malformed payload (`CcfFormatException`), or a schema that cannot be fetched or does not verify (`CcfSchemaFetchException`). A warning is logged and the same request is sent once with `Accept: application/json`. If the retry fails, its error surfaces.
- **When it is _not_ repeated.** The retry happens only for requests that are safe to repeat: `GET` and `HEAD`, or any request `CcfClientOptions.IsIdempotent` approves, and never for a streamed body. For everything else the error surfaces unchanged. These also surface unchanged and are never retried:
  - HTTP error statuses (`4xx`, `5xx`): the server's answer is not a CCF problem;
  - usage errors, such as a target type that cannot receive the schema's fields.
- **Schemas are verified.** The schema text must hash to the id in the payload and be in canonical form; it is fetched from the same origin (a cross-origin redirect is refused) and kept in `ICcfSchemaCache` (in memory by default; the interface is async so you can persist it).

## Options

`CcfClientOptions`: `Features`, `SchemaPathPrefix` (default `/ccf/schemas`), `SchemaCache`, `JsonSerializerOptions` (JSON path and the naming policy used to match properties), `Limits`, `IsIdempotent`.
