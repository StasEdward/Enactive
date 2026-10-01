// The JS twin of Grants.cs: a host key wrapped for one device. ECDH from a fresh ephemeral key to the
// device's public key, HKDF to a key-encryption key, AES-GCM over the 32-byte host key. The device's public
// key is public, so anyone - the gateway included - can wrap a key of their own to it; what makes a grant
// trustworthy is the HMAC, with a key the gateway never has (the pairing key, or the previous epoch's
// grant-auth key). The MAC is checked before anything is decrypted. tests/vectors pins this to the C# bytes.
import { b64url, fromB64url, equal } from './bytes.js';
import { canonicalBytes } from './canonical.js';
import { EnvelopeError } from './envelope.js';
import { derive, hostKey } from './hostkey.js';

const WRAP_VERSION = 'enactive-grant-v1';
const MAC_VERSION = 'enactive-grant-mac-v1';
const NONCE = 12;
const SECRET = 32;
const TAG = 16;
const ECDH = { name: 'ECDH', namedCurve: 'P-256' };

export const authByPairing = (pairingId) => `pair:${pairingId}`;

export function authByEpoch(previous) {
  // The epoch is written into text that both ends hash, so "3.5" or "NaN" would be a grant nobody can verify.
  if (!Number.isInteger(previous) || previous < 0 || previous > 0xffffffff) throw new TypeError('An epoch is a uint32.');
  return `epoch:${previous}`;
}

// Shared with pairing.js, whose enrollment MAC is the same HMAC under another key and message.
export async function hmacSha256(keyBytes, messageBytes) {
  const key = await crypto.subtle.importKey('raw', keyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await crypto.subtle.sign('HMAC', key, messageBytes));
}

/**
 * Wraps a host key for a device. Meant for a trusted browser granting to a new device: the Host makes
 * grants itself, in C#. `ephemeral` (a CryptoKeyPair) and `nonce` exist so a test can reproduce a fixed
 * vector; without them a fresh pair and a random nonce are drawn, as they must be in production.
 */
export async function createGrant({ hostId, deviceId, devicePublicRaw, key, authBy, authKeyBytes, ephemeral, nonce }) {
  requireUncompressedPoint(devicePublicRaw, 'The device public key');
  requireAuthKey(authKeyBytes);
  if (nonce !== undefined && nonce.length !== NONCE) throw new TypeError('A nonce is 12 bytes.');
  let devicePublic;
  try {
    devicePublic = await crypto.subtle.importKey('raw', devicePublicRaw, ECDH, true, []);
  } catch {
    throw new TypeError('The device public key is not a point on the P-256 curve.');
  }
  const pair = ephemeral ?? await crypto.subtle.generateKey(ECDH, false, ['deriveBits']);
  const ephemeralPublic = b64url(new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey)));
  const devicePublicText = b64url(devicePublicRaw);
  const epoch = String(key.epoch);
  const info = canonicalBytes(WRAP_VERSION, hostId, deviceId, epoch, ephemeralPublic, devicePublicText);
  const kek = await keyEncryptionKey(pair.privateKey, devicePublic, info, ['encrypt']);
  const iv = nonce ?? crypto.getRandomValues(new Uint8Array(NONCE));
  // WebCrypto returns ciphertext followed by the tag, the layout Grants.cs writes.
  const ciphertext = b64url(new Uint8Array(await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv, additionalData: info, tagLength: TAG * 8 }, kek, key.secret)));
  const nonceText = b64url(iv);
  const mac = b64url(await hmacSha256(authKeyBytes,
    canonicalBytes(MAC_VERSION, hostId, deviceId, epoch, ephemeralPublic, devicePublicText, nonceText, ciphertext, authBy)));
  return { hostId, deviceId, epoch: key.epoch, ephemeralPublic, nonce: nonceText, ciphertext, authBy, mac };
}

/**
 * Opens a grant made for this device. Every way the grant can be wrong - not authenticated, not for this
 * device, a field that is not base64url, a length the cipher does not take, a key that is not a point on the
 * curve - ends as an EnvelopeError, so the caller can read it as "this grant is not for me / not trusted"
 * without also having to know DOMException and TypeError.
 *
 * `devicePublicRaw` comes from the caller, never from the grant: it is inside the MAC, so a grant made for
 * another key does not authenticate. Whether the grant's hostId, deviceId and epoch are the ones the caller
 * expects is the caller's check, as in Grants.Open: this function proves that the grant was made by a holder
 * of `authKeyBytes` for this device key, not that it answers the question the caller asked.
 */
export async function openGrant(grant, devicePrivateKey, devicePublicRaw, authKeyBytes) {
  requireUncompressedPoint(devicePublicRaw, 'The device public key');
  requireAuthKey(authKeyBytes);
  const g = read(grant);
  const devicePublicText = b64url(devicePublicRaw);
  const epoch = String(g.epoch);

  const expected = await hmacSha256(authKeyBytes, canonicalBytes(MAC_VERSION, g.hostId, g.deviceId, epoch,
    g.ephemeralPublic, devicePublicText, g.nonce, g.ciphertext, g.authBy));
  if (!equal(expected, decode(g.mac))) throw new EnvelopeError('The grant is not authenticated by a key this device trusts.');

  const info = canonicalBytes(WRAP_VERSION, g.hostId, g.deviceId, epoch, g.ephemeralPublic, devicePublicText);
  const ephemeralRaw = decode(g.ephemeralPublic);
  // WebCrypto would also import a compressed point; Grants.cs takes only the 65-byte uncompressed form, and the two must agree on what a grant is.
  if (ephemeralRaw.length !== 65 || ephemeralRaw[0] !== 4) throw new EnvelopeError('A grant ephemeral key is a 65-byte uncompressed P-256 point.');
  let ephemeralPublic;
  try {
    ephemeralPublic = await crypto.subtle.importKey('raw', ephemeralRaw, ECDH, true, []);
  } catch {
    throw new EnvelopeError('The grant was not made for this device or is malformed: the ephemeral key is not on the curve.');
  }
  const ciphertext = decode(g.ciphertext);
  if (ciphertext.length !== SECRET + TAG) throw new EnvelopeError('A grant wraps 32 bytes.');
  // AES-GCM takes any nonce length here, but a holder of the pairing key can sign a grant with another one, and C# refuses it.
  const nonce = decode(g.nonce);
  if (nonce.length !== NONCE) throw new EnvelopeError('A grant nonce is 12 bytes.');

  const kek = await keyEncryptionKey(devicePrivateKey, ephemeralPublic, info, ['decrypt']);
  let secret;
  try {
    secret = new Uint8Array(await crypto.subtle.decrypt(
      { name: 'AES-GCM', iv: nonce, additionalData: info, tagLength: TAG * 8 }, kek, ciphertext));
  } catch {
    // Only the tag check can fail here (the key is well formed): the grant was wrapped for another device.
    throw new EnvelopeError('The grant was not made for this device or is malformed.');
  }
  return hostKey(g.epoch, secret);
}

async function keyEncryptionKey(privateKey, otherPublic, info, usages) {
  const shared = new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: otherPublic }, privateKey, 256));
  return crypto.subtle.importKey('raw', await derive(shared, info), 'AES-GCM', false, usages);
}

// A grant read off the wire may lack a field or carry one of another type; either is as untrustworthy as one that is not base64url.
function read(grant) {
  for (const name of ['hostId', 'deviceId', 'ephemeralPublic', 'nonce', 'ciphertext', 'authBy', 'mac']) {
    if (typeof grant?.[name] !== 'string') throw new EnvelopeError(`A grant field is missing: ${name}.`);
  }
  if (!Number.isInteger(grant.epoch) || grant.epoch < 0 || grant.epoch > 0xffffffff) throw new EnvelopeError('A grant epoch is a uint32.');
  return grant;
}

function decode(text) {
  try {
    return fromB64url(text);
  } catch {
    throw new EnvelopeError('A grant field is not base64url.');
  }
}

// These two come from the caller's own code, not from the wire, so a wrong one is a bug (TypeError) and not a bad grant.
// An empty HMAC key would otherwise fail inside WebCrypto with a DOMException that says nothing about which argument was wrong.
function requireAuthKey(bytes) {
  if (!(bytes instanceof Uint8Array) || bytes.length !== 32) throw new TypeError('An authentication key is 32 bytes.');
}

function requireUncompressedPoint(bytes, what) {
  if (!(bytes instanceof Uint8Array) || bytes.length !== 65 || bytes[0] !== 4) {
    throw new TypeError(`${what} is 65 bytes beginning with 0x04.`);
  }
}
