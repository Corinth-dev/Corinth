/** Decoder error codes: exactly the codes of SPEC §11, with the same names as the .NET `CcfErrorCode`. */
export type CcfErrorCode =
  | "BadMagic"
  | "UnsupportedVersion"
  | "ReservedFlags"
  | "SchemaMismatch"
  | "SingleObjectRowCount"
  | "Truncated"
  | "LengthOverrun"
  | "BlockLengthMismatch"
  | "TrailingData"
  | "VarintOverflow"
  | "InvalidMode"
  | "IndexOutOfRange"
  | "ValueOutOfRange"
  | "CountMismatch"
  | "InvalidUtf8"
  | "LimitExceeded";

/** The only error the decoder raises for malformed input (apart from the documented `RangeError` of `int64: "number"`). */
export class CcfFormatError extends Error {
  readonly code: CcfErrorCode;

  constructor(code: CcfErrorCode, detail?: string) {
    super(detail === undefined ? code : `${code}: ${detail}`);
    this.name = "CcfFormatError";
    this.code = code;
  }
}

/** An invalid schema (SPEC §14.3). Separate from {@link CcfFormatError}. */
export class CcfSchemaError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "CcfSchemaError";
  }
}

/**
 * The schema for a CCF response could not be obtained or verified (request failed, unknown id, content does not
 * hash to the id, invalid schema). A client-side condition, not a format error; like one it makes the client repeat
 * a safe request as JSON.
 */
export class CcfSchemaFetchError extends Error {
  readonly schemaId: string;

  constructor(schemaId: string, message: string, options?: { cause?: unknown }) {
    super(message, options);
    this.name = "CcfSchemaFetchError";
    this.schemaId = schemaId;
  }
}

/** The server answered with an error status. */
export class CcfHttpError extends Error {
  readonly status: number;
  readonly response: Response;

  constructor(response: Response) {
    super(`HTTP ${response.status} ${response.statusText}`.trim());
    this.name = "CcfHttpError";
    this.status = response.status;
    this.response = response;
  }
}
