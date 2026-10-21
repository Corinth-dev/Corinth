# Versioning policy

## Wire format

- The **format version stays 1** (the version byte of the header, SPEC §4.1). A reader that sees another version fails with `UnsupportedVersion`.
- Format evolution happens through **features**, advertised by the client in `Accept: application/vnd.ccf; features=<letters>` and recorded in the header flags only where SPEC says so. A new feature adds a new **letter** and new encodings that a server may use only when the client listed that letter. It never changes the meaning of an existing encoding, mode byte, flag, error code, the media type or the schema canonical form.
- A change that cannot be expressed as an opt-in feature would need format version 2 and is out of scope of this policy; it requires an owner decision and a new set of golden vectors.
- `docs/SPEC.md` is normative. Clarifications that do not change the wire format are appended to SPEC §14 and numbered (v1.0.1 added rules 1–8). The golden vectors in `test-vectors/` change only by adding new files for new features.
- A schema id is the first 4 bytes of SHA-256 of the canonical schema JSON, so it changes exactly when the schema changes; it is not a version number.

## Packages

All packages follow **SemVer 2.0**, and `SKM.Ccf.Core`, `SKM.Ccf.AspNetCore`, `SKM.Ccf.Client`, `SKM.Ccf.Scalar` (NuGet) and `@skm/ccf-client`, `@skm/ccf-typegen` (npm) are released together with one version number.

- `0.x`: the public API may still change in minor releases; each release notes the changes. Wire compatibility is not affected: any `0.x` reader and writer interoperate on format version 1, feature `d`.
- From `1.0.0`: breaking changes to the public API only in a major release; new features and options in minor releases; fixes in patch releases. `EnablePackageValidation` (NuGet) guards against accidental breaks once a baseline version exists.
- A package supports every format feature it lists in its README; a reader never needs to be newer than the server unless the server uses a feature the client advertised, which the client controls.
- Supported runtimes: .NET 10 (`net10.0`; net8.0 was dropped before the first release because it reaches end of support in November 2026); Node 20+ and current browsers. Dropping a runtime is a minor release in `0.x` and a major release afterwards.
- `@skm/ccf-typegen` depends on the exact same `@skm/ccf-client` version.
