# @ccf/typegen

`ccf-typegen` turns a CCF schema (a file or a URL) into TypeScript interfaces for use with `@ccf/client`.

```bash
npx @ccf/typegen schema-Product.json -o product.ts   # installs the `ccf-typegen` command
```

It depends on `@ccf/client` to parse and verify the schema. Names: the root is the PascalCase schema name, nested objects are `<Parent><Field>`, object-list elements are `<Parent><Field>Item`. `int64`/`uint64` fields are typed `number` (the client's default; see its README for `bigint`).
