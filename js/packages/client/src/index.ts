/**
 * @corinth/client: decoder and client for CCF (Corinthian Columnar Format) payloads. Zero runtime dependencies; runs in
 * browsers and Node 20+.
 */
export { CcfFormatError, CcfSchemaError, CcfSchemaFetchError, CcfHttpError } from "./errors.js";
export type { CcfErrorCode } from "./errors.js";
export { parseSchema, createSchema, canonicalize } from "./schema.js";
export type { CcfField, CcfSchema, CcfType } from "./schema.js";
export { decode, readSchemaId, defaultLimits } from "./decoder.js";
export type { DecodeOptions, CcfLimits } from "./decoder.js";
export { createCcfClient, verifySchema } from "./client.js";
export type { CcfClient, CcfClientOptions, CcfRequestInit } from "./client.js";
export { MemorySchemaCache, IndexedDbSchemaCache } from "./schemaCache.js";
export type { CcfSchemaCache, IndexedDbSchemaCacheOptions } from "./schemaCache.js";
export { sha256, sha256Fallback } from "./sha256.js";
