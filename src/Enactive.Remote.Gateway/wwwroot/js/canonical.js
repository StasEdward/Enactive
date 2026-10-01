// The JS twin of Canonical.cs: each field preceded by its UTF-8 byte count, so no value can be
// arranged to read as a different list of fields. tests/vectors pins it to the C# bytes.
const encoder = new TextEncoder();

export function canonicalText(version, ...fields) {
  // The version is a fixed protocol constant, never from data, so it is not length-prefixed.
  let text = version + '\n';
  for (const field of fields) {
    const value = field ?? '';
    // The count is UTF-8 bytes, not string length: the hash and MAC run over bytes, and the two differ for non-ASCII text.
    text += encoder.encode(value).length + ':' + value + '\n';
  }
  return text;
}

export const canonicalBytes = (version, ...fields) => encoder.encode(canonicalText(version, ...fields));
