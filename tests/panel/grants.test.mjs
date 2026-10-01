import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHmac } from 'node:crypto';
import { authByPairing, authByEpoch, createGrant, openGrant } from '../../src/Enactive.Remote.Gateway/wwwroot/js/grants.js';
import {
  formatConnectionCode, formatInviteLink, parseInviteFragment, enrollmentMac, verifyEnrollment, derivePairKey
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/pairing.js';
import { EnvelopeError } from '../../src/Enactive.Remote.Gateway/wwwroot/js/envelope.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { canonicalBytes } from '../../src/Enactive.Remote.Gateway/wwwroot/js/canonical.js';
import { b64url, fromB64url, concat } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import { vectors } from './vectors.mjs';

const ECDH = { name: 'ECDH', namedCurve: 'P-256' };
const g = vectors.grant;
const pairKey = fromB64url(g.pairKey);
const devicePublicRaw = fromB64url(g.devicePublic);

// The vector keys carry only d, x and y; WebCrypto wants the key type and curve spelled out.
const importPrivate = (jwk) => crypto.subtle.importKey(
  'jwk', { kty: 'EC', crv: 'P-256', d: jwk.d, x: jwk.x, y: jwk.y }, ECDH, false, ['deriveBits']);

const devicePrivate = await importPrivate(g.devicePrivate);

async function ephemeralPair() {
  const jwk = g.ephemeralPrivate;
  return {
    privateKey: await importPrivate(jwk),
    publicKey: await crypto.subtle.importKey('raw', concat(Uint8Array.of(4), fromB64url(jwk.x), fromB64url(jwk.y)), ECDH, true, [])
  };
}

async function freshDevice() {
  const pair = await crypto.subtle.generateKey(ECDH, false, ['deriveBits']);
  return { privateKey: pair.privateKey, publicRaw: new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey)) };
}

// An independent implementation of the MAC (node:crypto, not WebCrypto), so a grant with any chosen fields can be signed.
function signed(fields, key = pairKey, devicePublic = devicePublicRaw) {
  const mac = createHmac('sha256', key).update(canonicalBytes('enactive-grant-mac-v1', fields.hostId, fields.deviceId,
    String(fields.epoch), fields.ephemeralPublic, b64url(devicePublic), fields.nonce, fields.ciphertext, fields.authBy)).digest();
  return { ...fields, mac: b64url(new Uint8Array(mac)) };
}

const secretOf = (key) => b64url(key.secret);

test('openGrant opens the C# grant and yields the host key', async () => {
  const key = await openGrant(g.grant, devicePrivate, devicePublicRaw, pairKey);
  assert.equal(key.epoch, vectors.hostKey.epoch);
  assert.equal(secretOf(key), vectors.hostKey.secret);
  assert.equal(b64url(key.messageKey), vectors.hostKey.messageKey);
  assert.equal(b64url(key.grantAuthKey), vectors.hostKey.grantAuthKey);
});

test('createGrant with the stored ephemeral key and nonce reproduces the C# grant', async () => {
  const key = await hostKey(vectors.hostKey.epoch, fromB64url(vectors.hostKey.secret));
  const grant = await createGrant({
    hostId: g.hostId, deviceId: g.deviceId, devicePublicRaw, key, authBy: g.authBy, authKeyBytes: pairKey,
    ephemeral: await ephemeralPair(), nonce: fromB64url(g.nonce)
  });
  assert.deepEqual(grant, g.grant);
});

test('a grant made in JS opens in JS, and a second one differs from the first', async () => {
  const device = await freshDevice();
  const key = await hostKey(7, crypto.getRandomValues(new Uint8Array(32)));
  const make = () => createGrant({
    hostId: 'host-1', deviceId: 'dev-1', devicePublicRaw: device.publicRaw, key,
    authBy: authByEpoch(6), authKeyBytes: pairKey
  });
  const first = await make();
  const second = await make();
  // A fresh ephemeral key and nonce each time: a reused pair would wrap two keys under one key-encryption key and nonce.
  assert.notEqual(first.ephemeralPublic, second.ephemeralPublic);
  assert.notEqual(first.nonce, second.nonce);
  assert.equal(first.authBy, 'epoch:6');
  assert.equal(first.epoch, 7);
  const opened = await openGrant(first, device.privateKey, device.publicRaw, pairKey);
  assert.equal(opened.epoch, 7);
  assert.equal(secretOf(opened), secretOf(key));
});

test('a grant whose mac has one changed character is rejected', async () => {
  // The first character carries six real bits; the last carries only two, so changing it also breaks the canonical spelling - both must refuse.
  for (const index of [0, 20, g.grant.mac.length - 1]) {
    const mac = g.grant.mac;
    const other = mac[index] === 'A' ? 'B' : 'A';
    const changed = { ...g.grant, mac: mac.slice(0, index) + other + mac.slice(index + 1) };
    await assert.rejects(openGrant(changed, devicePrivate, devicePublicRaw, pairKey), EnvelopeError, `character ${index}`);
  }
});

test('a changed field of the grant is rejected by the mac', async () => {
  const swapped = { ...g.grant, hostId: 'host-other' };
  const other = { ...g.grant, ciphertext: g.grant.ciphertext.slice(0, -2) + (g.grant.ciphertext.endsWith('AA') ? 'BB' : 'AA') };
  for (const grant of [swapped, other, { ...g.grant, epoch: g.grant.epoch + 1 }, { ...g.grant, authBy: 'pair:other' }]) {
    await assert.rejects(openGrant(grant, devicePrivate, devicePublicRaw, pairKey), /not authenticated/);
  }
});

test('a grant does not open under another authentication key', async () => {
  const wrong = pairKey.slice();
  wrong[0] ^= 1;
  await assert.rejects(openGrant(g.grant, devicePrivate, devicePublicRaw, wrong), /not authenticated/);
});

test('a grant made for another device key does not authenticate for this one', async () => {
  const other = await freshDevice();
  await assert.rejects(openGrant(g.grant, other.privateKey, other.publicRaw, pairKey), /not authenticated/);
});

test('a grant signed for this device but wrapped to another device fails with EnvelopeError', async () => {
  const other = await freshDevice();
  const key = await hostKey(3, crypto.getRandomValues(new Uint8Array(32)));
  const forOther = await createGrant({
    hostId: 'h', deviceId: 'd', devicePublicRaw: other.publicRaw, key, authBy: authByPairing('p'), authKeyBytes: pairKey
  });
  // The pairing-key holder signs it with this device's key in the MAC, but the wrap is to the other key: the tag must refuse.
  const { mac, ...fields } = forOther;
  await assert.rejects(openGrant(signed(fields), devicePrivate, devicePublicRaw, pairKey), EnvelopeError);
});

test('the mac is checked before anything else is read from the grant', async () => {
  // An ephemeral key that is not base64url would fail on its own; a wrong mac must be reported first.
  const grant = { ...g.grant, ephemeralPublic: '!!!!', mac: 'AAAA' };
  await assert.rejects(openGrant(grant, devicePrivate, devicePublicRaw, pairKey), /not authenticated/);
});

test('every way a validly signed grant can be malformed fails with EnvelopeError', async () => {
  const base = { ...g.grant };
  delete base.mac;
  const onCurve = fromB64url(g.grant.ephemeralPublic);
  const offCurve = onCurve.slice();
  offCurve[64] ^= 1;
  const compressed = Uint8Array.of(2, ...onCurve.subarray(1, 33));
  const cases = {
    'ephemeral key not base64url': { ephemeralPublic: 'not base64url!' },
    'ephemeral key of the wrong length': { ephemeralPublic: b64url(onCurve.subarray(0, 64)) },
    'ephemeral key compressed': { ephemeralPublic: b64url(compressed) },
    'ephemeral key off the curve': { ephemeralPublic: b64url(offCurve) },
    'nonce not base64url': { nonce: '@@@@' },
    'nonce of 11 bytes': { nonce: b64url(new Uint8Array(11)) },
    'nonce of 16 bytes': { nonce: b64url(new Uint8Array(16)) },
    'ciphertext of 47 bytes': { ciphertext: b64url(new Uint8Array(47)) },
    'ciphertext of 49 bytes': { ciphertext: b64url(new Uint8Array(49)) },
    'ciphertext with a flipped byte': { ciphertext: b64url(flip(fromB64url(g.grant.ciphertext), 5)) },
    'ciphertext with a flipped tag byte': { ciphertext: b64url(flip(fromB64url(g.grant.ciphertext), 47)) }
  };
  for (const [name, change] of Object.entries(cases)) {
    await assert.rejects(openGrant(signed({ ...base, ...change }), devicePrivate, devicePublicRaw, pairKey), EnvelopeError, name);
  }
  // The control: the same signing with nothing changed opens, so the cases above fail for their own reason.
  assert.equal(secretOf(await openGrant(signed(base), devicePrivate, devicePublicRaw, pairKey)), vectors.hostKey.secret);
});

function flip(bytes, index) {
  const changed = bytes.slice();
  changed[index] ^= 1;
  return changed;
}

test('a grant with a missing, mistyped or out-of-range field fails with EnvelopeError', async () => {
  for (const name of ['hostId', 'deviceId', 'ephemeralPublic', 'nonce', 'ciphertext', 'authBy', 'mac']) {
    const grant = { ...g.grant };
    delete grant[name];
    await assert.rejects(openGrant(grant, devicePrivate, devicePublicRaw, pairKey), EnvelopeError, name);
    await assert.rejects(openGrant({ ...g.grant, [name]: 5 }, devicePrivate, devicePublicRaw, pairKey), EnvelopeError, `${name} as a number`);
  }
  for (const epoch of [undefined, -1, 4294967296, 1.5, '3', null]) {
    await assert.rejects(openGrant({ ...g.grant, epoch }, devicePrivate, devicePublicRaw, pairKey), EnvelopeError, String(epoch));
  }
  for (const grant of [null, undefined, 'grant', 5]) {
    await assert.rejects(openGrant(grant, devicePrivate, devicePublicRaw, pairKey), EnvelopeError, String(grant));
  }
});

test('openGrant does not compare the grant with what the caller expects: that is the caller\'s check', async () => {
  // The grant is for host-7f3a9c / device-b41e02 / epoch 3; openGrant has no parameters to say otherwise, and returns the key it carries.
  const key = await openGrant(g.grant, devicePrivate, devicePublicRaw, pairKey);
  assert.equal(key.epoch, 3);
});

test('arguments that are the caller\'s own mistakes fail with TypeError, not EnvelopeError', async () => {
  const short = new Uint8Array(31);
  await assert.rejects(openGrant(g.grant, devicePrivate, devicePublicRaw, short), TypeError);
  await assert.rejects(openGrant(g.grant, devicePrivate, devicePublicRaw, new Uint8Array(0)), TypeError);
  await assert.rejects(openGrant(g.grant, devicePrivate, devicePublicRaw.subarray(0, 64), pairKey), TypeError);
  await assert.rejects(openGrant(g.grant, devicePrivate, Uint8Array.of(2, ...devicePublicRaw.subarray(1, 33)), pairKey), TypeError);
});

test('createGrant refuses arguments that cannot make a grant, with TypeError', async () => {
  const key = await hostKey(1, new Uint8Array(32));
  const base = { hostId: 'h', deviceId: 'd', devicePublicRaw, key, authBy: 'pair:x', authKeyBytes: pairKey };
  const offCurve = devicePublicRaw.slice();
  offCurve[64] ^= 1;
  await assert.rejects(createGrant({ ...base, devicePublicRaw: offCurve }), TypeError);
  await assert.rejects(createGrant({ ...base, devicePublicRaw: devicePublicRaw.subarray(0, 64) }), TypeError);
  await assert.rejects(createGrant({ ...base, authKeyBytes: new Uint8Array(16) }), TypeError);
  await assert.rejects(createGrant({ ...base, nonce: new Uint8Array(11) }), TypeError);
});

test('authByPairing and authByEpoch write the text C# writes', () => {
  assert.equal(authByPairing('connect'), g.authBy);
  assert.equal(authByEpoch(0), 'epoch:0');
  assert.equal(authByEpoch(4294967295), 'epoch:4294967295');
  for (const bad of [-1, 4294967296, 1.5, NaN, '3', null]) assert.throws(() => authByEpoch(bad), TypeError, String(bad));
});

// ---- pairing -----------------------------------------------------------------------------------------------

const code = vectors.connectionCode;
const invite = vectors.inviteLink;
const connectionFields = () => ({
  gateway: code.gateway, hostId: code.hostId, token: code.token, deviceId: code.deviceId,
  devicePublicRaw: fromB64url(code.devicePublic), secret: fromB64url(code.pairingSecret)
});

test('formatConnectionCode produces the C# code exactly', () => {
  assert.equal(formatConnectionCode(connectionFields()), code.text);
});

test('formatConnectionCode accepts the gateway as a URL object and writes only its origin', () => {
  assert.equal(formatConnectionCode({ ...connectionFields(), gateway: new URL('https://remote.example.com/') }), code.text);
});

test('formatConnectionCode refuses a gateway that is not https, or is http outside loopback, or carries more than an origin', () => {
  for (const gateway of [
    'http://remote.example.com', 'ftp://remote.example.com', 'remote.example.com', '', 'https://user:pw@remote.example.com',
    'https://remote.example.com/path', 'https://remote.example.com/?q=1', 'https://remote.example.com/#x', 'http://localhost.example.com'
  ]) {
    assert.throws(() => formatConnectionCode({ ...connectionFields(), gateway }), TypeError, gateway);
  }
  for (const gateway of ['http://localhost:5000', 'http://127.0.0.1:5000', 'http://[::1]:5000']) {
    assert.doesNotThrow(() => formatConnectionCode({ ...connectionFields(), gateway }), gateway);
  }
});

test('formatConnectionCode refuses members the desktop app would refuse, with a TypeError that names them', () => {
  const fields = connectionFields();
  assert.throws(() => formatConnectionCode({ ...fields, token: 'ABC' }), /token/);
  assert.throws(() => formatConnectionCode({ ...fields, token: fields.token.toUpperCase() }), /token/);
  assert.throws(() => formatConnectionCode({ ...fields, secret: new Uint8Array(31) }), /pairing secret/);
  assert.throws(() => formatConnectionCode({ ...fields, devicePublicRaw: new Uint8Array(64) }), /device key/);
  assert.throws(() => formatConnectionCode({ ...fields, hostId: '' }), /computer id/);
  assert.throws(() => formatConnectionCode({ ...fields, deviceId: '' }), /device id/);
});

test('derivePairKey reproduces the C# pair key of the code and of the link', async () => {
  assert.equal(b64url(await derivePairKey(fromB64url(code.pairingSecret))), code.pairKey);
  assert.equal(b64url(await derivePairKey(fromB64url(invite.secret))), invite.pairKey);
});

test('formatInviteLink produces the C# link exactly, escaping the id as Uri.EscapeDataString does', () => {
  assert.equal(formatInviteLink(invite.gateway, invite.inviteId, fromB64url(invite.secret)), invite.text);
  // encodeURIComponent leaves these five alone and Uri.EscapeDataString does not.
  const link = formatInviteLink(invite.gateway, "a!b'c(d)e*f", fromB64url(invite.secret));
  assert.ok(link.includes('i=a%21b%27c%28d%29e%2Af&'), link);
});

test('formatInviteLink refuses a gateway that is not an origin and a secret that is not 32 bytes', () => {
  const secret = fromB64url(invite.secret);
  assert.throws(() => formatInviteLink('http://remote.example.com', 'id', secret), TypeError);
  assert.throws(() => formatInviteLink('https://remote.example.com/pair', 'id', secret), TypeError);
  assert.throws(() => formatInviteLink(invite.gateway, '', secret), /invitation id/);
  assert.throws(() => formatInviteLink(invite.gateway, 'id', secret.subarray(1)), /pairing secret/);
});

test('parseInviteFragment reads the C# link, with or without the leading #', () => {
  const fragment = invite.text.slice(invite.text.indexOf('#'));
  for (const hash of [fragment, fragment.slice(1)]) {
    const parsed = parseInviteFragment(hash);
    assert.equal(parsed.inviteId, invite.inviteId);
    assert.ok(parsed.secret instanceof Uint8Array);
    assert.equal(b64url(parsed.secret), invite.secret);
  }
});

test('a link written by formatInviteLink parses back, whatever the id holds', () => {
  const secret = fromB64url(invite.secret);
  for (const id of ['plain', 'with space', 'a&b=c', '100%', 'ü日本🙂', "!'()*", '#hash', '+plus']) {
    const link = formatInviteLink('https://gw.example.org', id, secret);
    const parsed = parseInviteFragment(link.slice(link.indexOf('#')));
    assert.equal(parsed.inviteId, id);
    assert.equal(b64url(parsed.secret), invite.secret);
  }
});

test('parseInviteFragment throws a TypeError with a sentence for each thing wrong with the link', () => {
  const p = invite.secret;
  const cases = [
    ['', /version/],
    ['#i=x&p=' + p, /version/],
    ['#v=1&i=x&p=' + p, /version/],
    ['#v=2&p=' + p, /invitation id/],
    ['#v=2&i=&p=' + p, /invitation id/],
    ['#v=2&i=x', /pairing secret/],
    ['#v=2&i=x&p=!!!', /pairing secret is damaged/],
    ['#v=2&i=x&p=' + p.slice(1), /pairing secret/],
    ['#v=2&i=x&p=' + b64url(new Uint8Array(31)), /wrong length/],
    ['#v=2&i=%E0%A4%A&p=' + p, /invitation id/],
    [undefined, /link/],
    [null, /link/],
    [5, /link/]
  ];
  for (const [hash, pattern] of cases) {
    assert.throws(() => parseInviteFragment(hash), (error) => {
      assert.ok(error instanceof TypeError, `${hash}: ${error}`);
      assert.match(error.message, pattern, String(hash));
      return true;
    }, String(hash));
  }
});

test('parseInviteFragment keeps the first value of a repeated member and ignores empty parts and members without a name', () => {
  const p = invite.secret;
  const parsed = parseInviteFragment(`#&&v=2&=junk&i=first&i=second&x&p=${p}&`);
  assert.equal(parsed.inviteId, 'first');
  // A member called __proto__ is just a member: it must not reach the result.
  assert.equal(parseInviteFragment(`#__proto__=x&v=2&i=a&p=${p}`).inviteId, 'a');
});

test('enrollmentMac reproduces the C# MAC and verifyEnrollment accepts it', async () => {
  const e = vectors.enrollment;
  const key = fromB64url(e.pairKey);
  const devicePublic = fromB64url(e.devicePublic);
  const mac = await enrollmentMac(key, e.inviteId, e.deviceId, devicePublic);
  assert.equal(mac, e.mac);
  assert.equal(await verifyEnrollment(key, e.inviteId, e.deviceId, devicePublic, e.mac), true);
});

test('verifyEnrollment refuses a MAC for another invite, device, key or pair key, and malformed text, as false', async () => {
  const e = vectors.enrollment;
  const key = fromB64url(e.pairKey);
  const devicePublic = fromB64url(e.devicePublic);
  const otherKey = key.slice();
  otherKey[0] ^= 1;
  const otherPublic = devicePublic.slice();
  otherPublic[64] ^= 1;
  assert.equal(await verifyEnrollment(key, 'another invite', e.deviceId, devicePublic, e.mac), false);
  assert.equal(await verifyEnrollment(key, e.inviteId, 'another-device', devicePublic, e.mac), false);
  assert.equal(await verifyEnrollment(key, e.inviteId, e.deviceId, otherPublic, e.mac), false);
  assert.equal(await verifyEnrollment(otherKey, e.inviteId, e.deviceId, devicePublic, e.mac), false);
  // The MAC comes from the gateway's relay, so a malformed one is an answer, not an exception.
  for (const bad of ['', 'not base64url!', e.mac + '=', e.mac.slice(1), e.mac.slice(0, -2), null, undefined, 5]) {
    assert.equal(await verifyEnrollment(key, e.inviteId, e.deviceId, devicePublic, bad), false, String(bad));
  }
});
