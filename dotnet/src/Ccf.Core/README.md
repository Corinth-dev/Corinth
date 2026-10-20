# Ccf.Core

The core of CCF (Columnar Compact Format), a compact binary, column-oriented format for API responses: the schema model, the encoder and the decoder. It depends on the .NET base class library only.

Most applications use [`Ccf.AspNetCore`](../Ccf.AspNetCore/README.md) on the server and [`Ccf.Client`](../Ccf.Client/README.md) on the client. Use `Ccf.Core` directly to encode or decode CCF yourself:

```csharp
CcfSchemaBuilder.TryBuild(typeof(List<Product>), null, out var schema, out var reason);

var buffer = new ArrayBufferWriter<byte>();
new CcfEncoder().Encode(products, schema!, CcfFeatures.Dictionary, buffer);

List<Product> back = CcfDecoder.Decode<List<Product>>(buffer.WrittenSpan, schema!);
```

- Every decode failure is a `CcfFormatException` with a `CcfErrorCode`; nothing else escapes the decoder. Encode failures are `CcfEncodeException`.
- Annotate properties with `[CcfDelta]` (delta-coded integers and datetimes) and `[CcfDecimal(scale)]`.
- `CcfLimits` bounds rows, payload size and string length (SPEC §9).
- The wire format is specified in `docs/SPEC.md` of the repository and validated against shared golden vectors by every implementation.
