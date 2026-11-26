# @corinth/typegen

`corinth-typegen` turns a CCF schema (a file or a URL) into TypeScript interfaces for use with `@corinth/client`.

```bash
npx @corinth/typegen schema-Product.json -o product.ts   # installs the `corinth-typegen` command
```

It depends on `@corinth/client` to parse and verify the schema. Names: the root is the PascalCase schema name, nested objects are `<Parent><Field>`, object-list elements are `<Parent><Field>Item`. `int64`/`uint64` fields are typed `number` (the client's default; see its README for `bigint`).
