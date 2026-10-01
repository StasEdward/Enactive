// The JS twin of PairingCodes.cs: what the browser shows when a person registers a computer (the
// connection code), what a trusted device shows to admit another (the invitation link), and the MAC
// with which a new device proves which public key the invitation was for. The pairing secret never
// reaches the gateway: the code is carried to the desktop app by hand and the link keeps it in the
// URL fragment, which a browser does not send. tests/vectors pins the formats to the C# text.
import { b64url, fromB64url, utf8, equal } from './bytes.js';
import { canonicalBytes } from './canonical.js';
import { hmacSha256 } from './grants.js';
import { derive, INFO } from './hostkey.js';

const CODE_PREFIX = 'enactive-connect:';
const LINK_PATH = '/pair';
const ENROLL_VERSION = 'enactive-enroll-v1';
// Pinned to 2 rather than read from a protocol constant: these formats are the protocol-2 ones, and an old
// Host or panel must refuse them by version, not misread them.
const FORMAT_VERSION = 2;
const SECRET = 32;
const TOKEN = /^[0-9a-f]{64}$/;

/** The key that authenticates the first grant and the enrollment, derived from the secret in a code or link. */
export const derivePairKey = (secretBytes) => derive(secretBytes, INFO.pair);

export function formatConnectionCode({ gateway, hostId, token, deviceId, devicePublicRaw, secret }) {
  // Everything the desktop app would refuse is refused here, where the person can still be told to make a new code.
  const origin = gatewayOrigin(gateway);
  requireText(hostId, 'computer id');
  if (typeof token !== 'string' || !TOKEN.test(token)) throw new TypeError('The code\'s token must be 64 lowercase hex characters.');
  requireText(deviceId, 'device id');
  if (!(devicePublicRaw instanceof Uint8Array) || devicePublicRaw.length !== 65 || devicePublicRaw[0] !== 4) {
    throw new TypeError('The code\'s device key must be a 65-byte uncompressed P-256 public key.');
  }
  requireSecret(secret, 'code');
  // Member order is the C# writer's. JSON.stringify leaves non-ASCII text as it is where C# escapes it as \uXXXX;
  // both are the same JSON and the desktop app reads either.
  const json = JSON.stringify({
    v: FORMAT_VERSION, g: origin, h: hostId, t: token, d: deviceId, k: b64url(devicePublicRaw), p: b64url(secret)
  });
  return CODE_PREFIX + b64url(utf8(json));
}

export function formatInviteLink(origin, inviteId, secret) {
  requireText(inviteId, 'invitation id');
  requireSecret(secret, 'link');
  return `${gatewayOrigin(origin)}${LINK_PATH}#v=${FORMAT_VERSION}&i=${escapeData(inviteId)}&p=${b64url(secret)}`;
}

/**
 * Reads the part of an invitation link after the #. The messages are written for the person who pasted
 * the link, as the C# ones are: what is wrong and what to do.
 */
export function parseInviteFragment(hash) {
  if (typeof hash !== 'string') throw new TypeError('The link is not text - copy all of it again.');
  // Split by hand: the invitation id is escaped on the way out, so each value is unescaped only after the split.
  // A Map, so a member called __proto__ or constructor is only a member.
  const members = new Map();
  for (const part of hash.replace(/^#+/, '').split('&')) {
    const at = part.indexOf('=');
    // The first value wins, as Dictionary.TryAdd does in C#: a second "i" must not replace the one the person saw.
    if (at > 0 && !members.has(part.slice(0, at))) members.set(part.slice(0, at), part.slice(at + 1));
  }

  if (members.get('v') !== String(FORMAT_VERSION)) {
    throw new TypeError(`The link's version is missing or is not one this Enactive understands (version ${FORMAT_VERSION}) - update the desktop app and ask for a new link.`);
  }
  let inviteId;
  try {
    inviteId = decodeURIComponent(members.get('i') ?? '');
  } catch {
    throw new TypeError('The link\'s invitation id is damaged - copy all of it again.');
  }
  if (inviteId.length === 0) throw new TypeError('The link has no invitation id - copy all of it again.');
  if (!members.has('p')) throw new TypeError('The link has no pairing secret - copy all of it again, including the part after the #.');
  let secret;
  try {
    secret = fromB64url(decodeURIComponent(members.get('p')));
  } catch {
    throw new TypeError('The link\'s pairing secret is damaged - copy all of it again.');
  }
  if (secret.length !== SECRET) throw new TypeError('The link\'s pairing secret has the wrong length - copy all of it again.');
  return { inviteId, secret };
}

export async function enrollmentMac(pairKey, inviteId, deviceId, devicePublicRaw) {
  return b64url(await compute(pairKey, inviteId, deviceId, devicePublicRaw));
}

export async function verifyEnrollment(pairKey, inviteId, deviceId, devicePublicRaw, mac) {
  let given;
  try {
    given = fromB64url(mac);
  } catch {
    // The MAC comes from the gateway's relay, so a malformed one is an answer ("no"), not an exception that every caller must remember to catch.
    return false;
  }
  return equal(await compute(pairKey, inviteId, deviceId, devicePublicRaw), given);
}

const compute = (pairKey, inviteId, deviceId, devicePublicRaw) =>
  hmacSha256(pairKey, canonicalBytes(ENROLL_VERSION, inviteId, deviceId, b64url(devicePublicRaw)));

// The one rule for a gateway address in a code or link, as PairingOrigin in C#. Plain http to a remote host
// would carry the device token in the clear, so http is accepted only for a machine's own loopback (local development).
function gatewayOrigin(gateway) {
  let url;
  try {
    url = new URL(gateway);
  } catch {
    throw new TypeError(REFUSAL);
  }
  const loopback = url.hostname === 'localhost' || url.hostname === '[::1]' || /^127(\.\d{1,3}){3}$/.test(url.hostname);
  if (!(url.protocol === 'https:' || (url.protocol === 'http:' && loopback))
    || url.username !== '' || url.password !== '' || url.search !== '' || url.hash !== '' || url.pathname !== '/') {
    throw new TypeError(REFUSAL);
  }
  return url.origin;
}

const REFUSAL = 'The gateway address must be an https address (http is allowed only for localhost) with no password, path or query - check it and make a new code.';

// Uri.EscapeDataString also escapes ! ' ( ) *, which encodeURIComponent leaves alone; the two ends
// must write one spelling of an id, or the link a browser makes would not be the link the desktop app writes.
function escapeData(text) {
  return encodeURIComponent(text).replace(/[!'()*]/g, (c) => '%' + c.charCodeAt(0).toString(16).toUpperCase());
}

function requireText(value, what) {
  if (typeof value !== 'string' || value.length === 0) throw new TypeError(`The ${what} must be text and not empty.`);
}

function requireSecret(secret, what) {
  if (!(secret instanceof Uint8Array) || secret.length !== SECRET) throw new TypeError(`The ${what}'s pairing secret must be 32 bytes.`);
}
