// The JS twin of Grants.cs: a host key wrapped for one device. ECDH from a fresh ephemeral key to the
// device's public key, HKDF to a key-encryption key, AES-GCM over the 32-byte host key. The device's public
// key is public, so anyone - the gateway included - can wrap a key of their own to it; what makes a grant
// trustworthy is its authentication, by something the gateway never has: the pair key from an out-of-band
// code (an HMAC), or the computer's signing key (an ECDSA signature, on rotation). Rotation grants were once
// authenticated with a key derived from the previous epoch key, but the device being revoked holds that key,
// so with the gateway's help it could hand everyone else a next key of its own choosing; no epoch key can
// authenticate a grant now. The MAC or signature is checked before anything is decrypted. tests/vectors pins
// this to the C# bytes.
import { b64url, fromB64url, equal } from './bytes.js';
import { canonicalBytes } from './canonical.js';
import { EnvelopeError } from './envelope.js';
import { derive, hostKey } from './hostkey.js';

const WRAP_VERSION = 'enactive-grant-v1';
const MAC_VERSION = 'enactive-grant-mac-v1';
const SIGNATURE_VERSION = 'enactive-grant-sig-v1';
const PAIRING_PREFIX = 'pair:';
const NONCE = 12;
const SECRET = 32;
const TAG = 16;
// ECDSA over P-256 in IEEE P1363 form, r and s of 32 bytes each: what WebCrypto verifies and .NET's SignData writes.
const SIGNATURE = 64;
const ECDH = { name: 'ECDH', namedCurve: 'P-256' };
const ECDSA = { name: 'ECDSA', namedCurve: 'P-256' };
const ECDSA_SHA256 = { name: 'ECDSA', hash: 'SHA-256' };

export const AUTH_BY_HOST = 'host';

export const authByPairing = (pairingId) => `${PAIRING_PREFIX}${pairingId}`;

// Shared with pairing.js, whose enrollment MAC is the same HMAC under another key and message.
export async function hmacSha256(keyBytes, messageBytes) {
  const key = await crypto.subtle.importKey('raw', keyBytes, { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  return new Uint8Array(await crypto.subtle.sign('HMAC', key, messageBytes));
}

/**
 * Wraps a host key for a device, authenticated with a pair key. Meant for a trusted browser admitting a new
 * device by invitation: browsers never make rotation grants, which only the computer can sign, and the Host
 * makes its own grants in C#. `hostSigningPublic` is the computer's signing key as this browser pinned it, so
 * the new device pins the same one. `ephemeral` (a CryptoKeyPair) and `nonce` exist so a test can reproduce a
 * fixed vector; without them a fresh pair and a random nonce are drawn, as they must be in production.
 */
export async function createGrant({ hostId, deviceId, devicePublicRaw, key, pairingId, pairKey, hostSigningPublic, ephemeral, nonce }) {
  requireUncompressedPoint(devicePublicRaw, 'The device public key');
  requireAuthKey(pairKey);
  requireUncompressedPoint(hostSigningPublic, 'The computer signing key');
  // A grant naming a key that is not a point would be pinned by the new device and make every later rotation fail.
  if (await importSigningPublic(hostSigningPublic) === null) throw new TypeError('The computer signing key is not a point on the P-256 curve.');
  if (typeof pairingId !== 'string') throw new TypeError('A pairing id is text.');
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
  const info = canonicalBytes(WRAP_VERSION, hostId, deviceId, String(key.epoch), ephemeralPublic, devicePublicText);
  const kek = await keyEncryptionKey(pair.privateKey, devicePublic, info, ['encrypt']);
  const iv = nonce ?? crypto.getRandomValues(new Uint8Array(NONCE));
  // WebCrypto returns ciphertext followed by the tag, the layout Grants.cs writes.
  const ciphertext = b64url(new Uint8Array(await crypto.subtle.encrypt(
    { name: 'AES-GCM', iv, additionalData: info, tagLength: TAG * 8 }, kek, key.secret)));
  const fields = {
    hostId, deviceId, epoch: key.epoch, ephemeralPublic, nonce: b64url(iv), ciphertext,
    authBy: authByPairing(pairingId), hostSigningPublic: b64url(hostSigningPublic)
  };
  const mac = b64url(await hmacSha256(pairKey, authenticatedText(MAC_VERSION, fields, devicePublicText)));
  return { ...fields, mac };
}

/**
 * Opens a grant made for this device and returns `{ key, hostSigningPublic }`: the host key and the computer's
 * signing public key (65 raw bytes). A paired grant is checked with `pairKey`; a grant the computer signed with
 * `pinnedHostSigningPublic`, the key this device pinned for that computer, and is refused when nothing is
 * pinned - the only other key to check it with is the one in the grant, which anyone can put there. When a key
 * is pinned, a grant that carries another one is refused in both modes.
 *
 * Pinning is the caller's job: it stores the returned key for the grant's computer at its first verified grant
 * and passes it from then on. So are the checks that the grant's hostId, deviceId and epoch are the ones it
 * expects, as in Grants.Open: this function proves who made the grant and for which device key, not that it
 * answers the question the caller asked. `devicePublicRaw` comes from the caller, never from the grant: it is
 * inside the MAC and the signature, so a grant made for another key does not authenticate.
 *
 * Every way the grant can be wrong - not authenticated, not for this device, a field that is not base64url, a
 * length the cipher does not take, a key that is not a point on the curve - ends as an EnvelopeError, so the
 * caller can read it as "this grant is not for me / not trusted" without also having to know DOMException and
 * TypeError.
 */
export async function openGrant(grant, devicePrivateKey, devicePublicRaw, { pairKey, pinnedHostSigningPublic } = {}) {
  requireUncompressedPoint(devicePublicRaw, 'The device public key');
  if (pairKey != null) requireAuthKey(pairKey);
  if (pinnedHostSigningPublic != null) requireUncompressedPoint(pinnedHostSigningPublic, 'The pinned signing key');
  const g = read(grant);
  const devicePublicText = b64url(devicePublicRaw);

  // Base64url has one spelling per byte string, so comparing the text compares the keys without decoding an unauthenticated field.
  if (pinnedHostSigningPublic != null && b64url(pinnedHostSigningPublic) !== g.hostSigningPublic) {
    throw new EnvelopeError('The grant carries another signing key than the one pinned for this computer.');
  }
  if (g.authBy === AUTH_BY_HOST) {
    if (pinnedHostSigningPublic == null) {
      throw new EnvelopeError('A grant signed by the computer cannot be checked before the computer\'s signing key is pinned.');
    }
    const signature = decode(g.mac);
    if (signature.length !== SIGNATURE) throw new EnvelopeError('A grant signature is 64 bytes.');
    const verifier = await importSigningPublic(pinnedHostSigningPublic);
    // The pin comes from the caller's own store, where only a key that imported was ever put: a bad one is a bug there.
    if (verifier === null) throw new TypeError('The pinned signing key is not a point on the P-256 curve.');
    if (!await crypto.subtle.verify(ECDSA_SHA256, verifier, signature, authenticatedText(SIGNATURE_VERSION, g, devicePublicText))) {
      throw new EnvelopeError('The grant is not signed by the computer this device pinned.');
    }
  } else if (g.authBy.startsWith(PAIRING_PREFIX)) {
    if (pairKey == null) throw new EnvelopeError('A paired grant arrived but this device has no pairing in progress.');
    const expected = await hmacSha256(pairKey, authenticatedText(MAC_VERSION, g, devicePublicText));
    if (!equal(expected, decode(g.mac))) throw new EnvelopeError('The grant is not authenticated by a key this device trusts.');
    // The caller pins this key; one that is not a point would make every later rotation grant unverifiable.
    if (await importSigningPublic(decode(g.hostSigningPublic)) === null) {
      throw new EnvelopeError('The grant\'s signing key is not a 65-byte uncompressed P-256 point.');
    }
  } else {
    throw new EnvelopeError('The grant names an authentication this device does not know.');
  }

  const info = canonicalBytes(WRAP_VERSION, g.hostId, g.deviceId, String(g.epoch), g.ephemeralPublic, devicePublicText);
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
  return { key: await hostKey(g.epoch, secret), hostSigningPublic: fromB64url(g.hostSigningPublic) };
}

// What the MAC or the signature covers: every field of the grant but the MAC itself, with the device key the
// caller holds in place of one read from the wire. The version differs between the two so an HMAC text can
// never be presented as a signed one, or the reverse.
function authenticatedText(version, g, devicePublicText) {
  return canonicalBytes(version, g.hostId, g.deviceId, String(g.epoch), g.ephemeralPublic, devicePublicText,
    g.nonce, g.ciphertext, g.authBy, g.hostSigningPublic);
}

// Null rather than a throw, so each caller decides whether a bad key is its own bug (TypeError) or a bad grant (EnvelopeError).
// The 65-byte uncompressed form is required first because WebCrypto would also import a compressed point, which Grants.cs refuses.
async function importSigningPublic(raw) {
  if (raw.length !== 65 || raw[0] !== 4) return null;
  try {
    return await crypto.subtle.importKey('raw', raw, ECDSA, false, ['verify']);
  } catch {
    return null;
  }
}

async function keyEncryptionKey(privateKey, otherPublic, info, usages) {
  const shared = new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: otherPublic }, privateKey, 256));
  return crypto.subtle.importKey('raw', await derive(shared, info), 'AES-GCM', false, usages);
}

// A grant read off the wire may lack a field or carry one of another type; either is as untrustworthy as one that is not base64url.
function read(grant) {
  for (const name of ['hostId', 'deviceId', 'ephemeralPublic', 'nonce', 'ciphertext', 'authBy', 'hostSigningPublic', 'mac']) {
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
  if (!(bytes instanceof Uint8Array) || bytes.length !== 32) throw new TypeError('A pair key is 32 bytes.');
}

function requireUncompressedPoint(bytes, what) {
  if (!(bytes instanceof Uint8Array) || bytes.length !== 65 || bytes[0] !== 4) {
    throw new TypeError(`${what} is 65 bytes beginning with 0x04.`);
  }
}
