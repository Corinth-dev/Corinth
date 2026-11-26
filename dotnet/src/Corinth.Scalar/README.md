# Corinth.Scalar

Development tooling that makes [CCF (Corinthian Columnar Format)](https://github.com/Corinth-dev/Corinth) visible in the OpenAPI document and in the [Scalar](https://scalar.com) API reference, so nobody has to write this per project.

```csharp
builder.Services.AddCcf();
builder.Services.AddOpenApi(o => o.AddCcf());            // OpenAPI lists application/vnd.ccf per operation

var app = builder.Build();
app.MapCcfViewer();                                      // POST /ccf/decode + the script (Development only)
app.MapOpenApi();
app.MapScalarApiReference(o => o.WithCcfViewer());       // adds the "CCF decode" toggle to Scalar
```

## What you get

- **OpenAPI:** every operation whose 200 response type has a CCF schema gets an `application/vnd.ccf` (binary) response next to JSON, and its description names the schema id. A note on how to opt in is appended to the document description. Operations restricted with `[Produces]` are skipped; minimal APIs must declare their response type (`Produces<T>()`).
- **Scalar:** a "CCF decode" toggle (bottom right, off by default, remembered in the browser). Send a request with `accept: application/vnd.ccf`:
  - **OFF:** Scalar shows the response as the server sent it (binary, "Binary file" with a download link).
  - **ON:** Scalar shows the decoded JSON, with `x-ccf-decoded`, `x-ccf-payload-bytes`, `x-ccf-schema-id` and `x-ccf-original-content-type` response headers.
  - The toggle applies to the next request you send.

## Development only

`POST /ccf/decode` decodes any payload for a schema this server knows and has no authentication. `MapCcfViewer()` therefore maps it only in the Development environment; elsewhere the script is an empty module and no toggle appears. To enable it elsewhere: `MapCcfViewer(o => o.EnableOutsideDevelopment = true)`, and protect it, because the method returns the endpoint group: `MapCcfViewer(...).RequireAuthorization("Admin")`.

## Compatibility

Depends on `Scalar.AspNetCore` 2.x from 2.18.2 (tested with 2.18.2 and 2.21.0; the script relies on Scalar's `customFetch` and JavaScript-configuration options, so a Scalar 3.0 needs a new release of this package) and `Microsoft.AspNetCore.OpenApi`. Requires `AddCcf()` from `Corinth.AspNetCore`. You do not need to reference `Microsoft.AspNetCore.OpenApi` yourself (the package carries the build setting its source generator needs). A browser test in the repository's CI drives the toggle in the real Scalar UI.
