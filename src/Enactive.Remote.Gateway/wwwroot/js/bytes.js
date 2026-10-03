// Byte and text helpers for the panel's cryptography. Only globals a browser has (TextEncoder,
// btoa, Uint8Array), so the same module runs in the page and under `node --test`.
const encoder = new TextEncoder();
// Fatal, so malformed bytes throw instead of becoming U+FFFD: a decrypted value that silently
// changed would no longer match what was authenticated. ignoreBOM keeps a leading U+FEFF in the text.
const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });

const ALPHABET = /^[A-Za-z0-9_-]*$/;

export function b64url(bytes) {
  // Built in slices because String.fromCharCode(...bytes) overflows the call stack on a large array.
  let binary = '';
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
}

export function fromB64url(text) {
  // Anything but the unpadded base64url alphabet is refused (atob would skip whitespace and accept
  // '+', '/' and '='), so one byte string has exactly one spelling and cannot be smuggled in two.
  if (typeof text !== 'string' || !ALPHABET.test(text) || text.length % 4 === 1) {
    throw new TypeError('Not base64url text.');
  }
  // atob accepts unpadded input whose length is not 1 modulo 4, which was checked above.
  const binary = atob(text.replaceAll('-', '+').replaceAll('_', '/'));
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  // atob ignores the spare bits of the last character, so "AR" would decode like "AQ"; re-encoding catches that.
  if (b64url(bytes) !== text) throw new TypeError('Not canonical base64url text.');
  return bytes;
}

export const utf8 = (text) => encoder.encode(text);

export function fromUtf8(bytes) {
  return decoder.decode(bytes);
}

export function equal(a, b) {
  if (a.length !== b.length) return false;
  // No early return: the time must not reveal how many leading bytes matched when this compares MACs.
  let difference = 0;
  for (let i = 0; i < a.length; i++) difference |= a[i] ^ b[i];
  return difference === 0;
}

export function concat(...arrays) {
  const result = new Uint8Array(arrays.reduce((sum, array) => sum + array.length, 0));
  let offset = 0;
  for (const array of arrays) {
    result.set(array, offset);
    offset += array.length;
  }
  return result;
}
