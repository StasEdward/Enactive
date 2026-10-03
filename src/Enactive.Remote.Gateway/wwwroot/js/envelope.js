// The JS twin of Envelope.cs: "e1:" + base64url(0x01 | epoch u32 BE | nonce 12 | ciphertext | tag 16).
// The associated data is never inside: both ends rebuild it from the record the envelope sits in, so an
// envelope moved to another record fails to open. tests/vectors pins this module to the C# bytes.
import { b64url, fromB64url } from './bytes.js';

export const PREFIX = 'e1:';
const VERSION = 1;
const NONCE = 12;
const HEADER = 1 + 4 + NONCE;
const TAG = 16;

// One error family for every way an envelope can fail to open, as EnvelopeException is in C#: a caller
// that shows "could not open" must not also have to know DOMException, TypeError and RangeError.
export class EnvelopeError extends Error {
  constructor(message) {
    super(message);
    this.name = 'EnvelopeError';
  }
}

export async function seal(keyBytes, epoch, plaintextBytes, adBytes, nonce) {
  // DataView.setUint32 wraps silently, so an epoch of 2^32 would be sealed as epoch 0 without a word.
  if (!Number.isInteger(epoch) || epoch < 0 || epoch > 0xffffffff) throw new TypeError('An epoch is a uint32.');
  if (nonce !== undefined && nonce.length !== NONCE) throw new TypeError('A nonce is 12 bytes.');
  const iv = nonce ?? crypto.getRandomValues(new Uint8Array(NONCE));
  const key = await crypto.subtle.importKey('raw', keyBytes, 'AES-GCM', false, ['encrypt']);
  // WebCrypto returns ciphertext followed by the tag, which is the layout C# writes after the header.
  const encrypted = new Uint8Array(await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv, additionalData: adBytes, tagLength: TAG * 8 }, key, plaintextBytes));
  const blob = new Uint8Array(HEADER + encrypted.length);
  blob[0] = VERSION;
  new DataView(blob.buffer).setUint32(1, epoch);
  blob.set(iv, 5);
  blob.set(encrypted, HEADER);
  return PREFIX + b64url(blob);
}

export async function open(keyBytes, sealed, adBytes) {
  const blob = decode(sealed);
  const key = await crypto.subtle.importKey('raw', keyBytes, 'AES-GCM', false, ['decrypt']);
  try {
    return new Uint8Array(await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv: blob.subarray(5, HEADER), additionalData: adBytes, tagLength: TAG * 8 },
      key, blob.subarray(HEADER)));
  } catch {
    // Only the tag check fails here (the key was imported above), and it is the DOMException WebCrypto
    // gives for a wrong key, a moved record and a changed byte alike.
    throw new EnvelopeError('The envelope does not open with this key for this record.');
  }
}

export function epochOf(sealed) {
  const blob = decode(sealed);
  // DataView reads an unsigned 32-bit value; a bitwise shift would turn epochs from 2^31 negative.
  return new DataView(blob.buffer, blob.byteOffset, blob.byteLength).getUint32(1);
}

// A blob shorter than header plus tag is refused before any slicing, as in C#: otherwise "e1:" alone
// would reach decrypt with a truncated tag and fail for a reason that has nothing to do with the key.
function decode(sealed) {
  if (typeof sealed !== 'string' || !sealed.startsWith(PREFIX)) throw new EnvelopeError('Not an e1 envelope.');
  let blob;
  try {
    blob = fromB64url(sealed.slice(PREFIX.length));
  } catch {
    throw new EnvelopeError('An envelope is base64url.');
  }
  if (blob.length < HEADER + TAG || blob[0] !== VERSION) throw new EnvelopeError('Not an e1 envelope.');
  return blob;
}
