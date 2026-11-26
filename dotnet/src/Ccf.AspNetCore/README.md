# SKM.Ccf.AspNetCore

Serves [CCF (Columnar Compact Format)](https://github.com/SamuraiKenshin/CCF) from ASP.NET Core. JSON stays the default; a client opts in with `Accept: application/vnd.ccf; features=d`. Anything CCF cannot serve falls back to JSON.

> CCF here is the *Columnar Compact Format* for API responses. It is not related to Microsoft's Confidential Consortium Framework, which shares the acronym.

```csharp
builder.Services.AddControllers();
builder.Services.AddCcf();          // options bind from the "Ccf" configuration section

var app = builder.Build();
app.MapControllers();               // controllers: negotiated like any other output format
app.MapCcfSchemas();                // GET /ccf/schemas/{id}
app.MapGet("/products", () => CcfResults.Ok(GetProducts()));   // minimal APIs opt in per endpoint
```

See `docs/GUIDE.md` in the repository for the opt-in guide (selecting endpoints, pre-registering schemas, configuration reference) and `docs/OBSERVABILITY.md` for the `Ccf` meter (`ccf.responses`, `ccf.bytes_written`, `ccf.encode_failures`).
