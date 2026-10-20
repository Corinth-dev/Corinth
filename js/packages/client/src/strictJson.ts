import { CcfSchemaError } from "./errors.js";

/** A parsed JSON value where objects keep their members in order and numbers keep their source text. */
export type JsonValue =
  | null
  | boolean
  | string
  | { number: string }
  | JsonValue[]
  | { members: [string, JsonValue][] };

/**
 * A small strict JSON parser for schema documents. Unlike `JSON.parse` it reports duplicate keys (which a schema
 * must reject, SPEC §14.3) and keeps number text so integers can be told from floats.
 */
export function parseStrictJson(text: string): JsonValue {
  let pos = 0;
  const fail = (what: string): never => {
    throw new CcfSchemaError(`Schema is not valid JSON: ${what} at position ${pos}.`);
  };
  const skipWhitespace = () => {
    while (pos < text.length && (text[pos] === " " || text[pos] === "\t" || text[pos] === "\n" || text[pos] === "\r")) pos++;
  };

  const parseString = (): string => {
    pos++; // opening quote
    let out = "";
    for (;;) {
      if (pos >= text.length) return fail("unterminated string");
      const c = text[pos++]!;
      if (c === '"') return out;
      if (c < " ") return fail("control character in string");
      if (c !== "\\") {
        out += c;
        continue;
      }
      const e = text[pos++];
      switch (e) {
        case '"': out += '"'; break;
        case "\\": out += "\\"; break;
        case "/": out += "/"; break;
        case "b": out += "\b"; break;
        case "f": out += "\f"; break;
        case "n": out += "\n"; break;
        case "r": out += "\r"; break;
        case "t": out += "\t"; break;
        case "u": {
          const hex = text.slice(pos, pos + 4);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) return fail("bad \\u escape");
          out += String.fromCharCode(parseInt(hex, 16));
          pos += 4;
          break;
        }
        default: return fail("bad escape");
      }
    }
  };

  const parseValue = (depth: number): JsonValue => {
    if (depth > 64) return fail("nesting too deep");
    skipWhitespace();
    const c = text[pos];
    if (c === "{") {
      pos++;
      const members: [string, JsonValue][] = [];
      const seen = new Set<string>();
      skipWhitespace();
      if (text[pos] === "}") {
        pos++;
        return { members };
      }
      for (;;) {
        skipWhitespace();
        if (text[pos] !== '"') return fail("expected a property name");
        const key = parseString();
        if (seen.has(key)) throw new CcfSchemaError(`Duplicate key "${key}" in schema.`);
        seen.add(key);
        skipWhitespace();
        if (text[pos++] !== ":") return fail("expected ':'");
        members.push([key, parseValue(depth + 1)]);
        skipWhitespace();
        const next = text[pos++];
        if (next === "}") return { members };
        if (next !== ",") return fail("expected ',' or '}'");
      }
    }
    if (c === "[") {
      pos++;
      const items: JsonValue[] = [];
      skipWhitespace();
      if (text[pos] === "]") {
        pos++;
        return items;
      }
      for (;;) {
        items.push(parseValue(depth + 1));
        skipWhitespace();
        const next = text[pos++];
        if (next === "]") return items;
        if (next !== ",") return fail("expected ',' or ']'");
      }
    }
    if (c === '"') return parseString();
    if (text.startsWith("true", pos)) { pos += 4; return true; }
    if (text.startsWith("false", pos)) { pos += 5; return false; }
    if (text.startsWith("null", pos)) { pos += 4; return null; }
    const m = /^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?/.exec(text.slice(pos, pos + 64));
    if (m === null) return fail("unexpected character");
    pos += m[0].length;
    return { number: m[0] };
  };

  const value = parseValue(0);
  skipWhitespace();
  if (pos !== text.length) fail("unexpected trailing content");
  return value;
}
