# @skm/ccf-typegen

`ccf-typegen` turns a CCF schema (a file or a URL) into TypeScript interfaces for use with `@skm/ccf-client`.

> CCF here is the *Columnar Compact Format* for API responses. It is not related to Microsoft's Confidential Consortium Framework, which shares the acronym.

```bash
npx @skm/ccf-typegen schema-Product.json -o product.ts   # installs the `ccf-typegen` command
```

It depends on `@skm/ccf-client` to parse and verify the schema. Names: the root is the PascalCase schema name, nested objects are `<Parent><Field>`, object-list elements are `<Parent><Field>Item`. `int64`/`uint64` fields are typed `number` (the client's default; see its README for `bigint`).
