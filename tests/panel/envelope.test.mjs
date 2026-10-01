import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PREFIX, seal, open, epochOf, EnvelopeError } from '../../src/Enactive.Remote.Gateway/wwwroot/js/envelope.js';
import { derive, INFO, hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { b64url, fromB64url, utf8, fromUtf8 } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import { vectors } from './vectors.mjs';

const first = vectors.envelope[0];
const firstKey = fromB64url(first.key);
const firstAd = fromB64url(first.ad);

test('seal with the stored nonce reproduces every C# envelope', async () => {
  for (const v of vectors.envelope) {
    const sealed = await seal(fromB64url(v.key), v.epoch, utf8(v.plaintext), fromB64url(v.ad), fromB64url(v.nonce));
    assert.equal(sealed, v.sealed);
  }
});

test('open returns the plaintext of every C# envelope', async () => {
  for (const v of vectors.envelope) {
    const opened = await open(fromB64url(v.key), v.sealed, fromB64url(v.ad));
    assert.ok(opened instanceof Uint8Array);
    assert.equal(fromUtf8(opened), v.plaintext);
  }
});

test('epochOf reads the big-endian epoch, up to the largest uint32', () => {
  for (const v of vectors.envelope) assert.equal(epochOf(v.sealed), v.epoch);
  assert.ok(vectors.envelope.some((v) => v.epoch === 4294967295));
});

test('a sealed envelope starts with the e1 prefix and carries the version, epoch and nonce in the clear', async () => {
  assert.equal(PREFIX, 'e1:');
  const sealed = await seal(firstKey, 258, utf8('x'), firstAd);
  assert.ok(sealed.startsWith('e1:'));
  const blob = fromB64url(sealed.slice(3));
  assert.deepEqual([...blob.subarray(0, 5)], [1, 0, 0, 1, 2]);
  assert.equal(blob.length, 1 + 4 + 12 + 1 + 16);
});

test('seal without a nonce draws a fresh one each time, and the result opens', async () => {
  const a = await seal(firstKey, 1, utf8('same'), firstAd);
  const b = await seal(firstKey, 1, utf8('same'), firstAd);
  assert.notEqual(a, b);
  assert.equal(fromUtf8(await open(firstKey, a, firstAd)), 'same');
});

test('seal refuses a nonce that is not 12 bytes with TypeError', async () => {
  for (const length of [1, 11, 13, 16]) {
    await assert.rejects(seal(firstKey, 1, utf8('x'), firstAd, new Uint8Array(length)), TypeError);
  }
});

test('seal refuses an epoch that is not a uint32 with TypeError', async () => {
  // DataView would wrap 2^32 to 0 silently, sealing under an epoch the caller did not mean.
  for (const epoch of [-1, 4294967296, 1.5, NaN, '3', null]) {
    await assert.rejects(seal(firstKey, epoch, utf8('x'), firstAd), TypeError, String(epoch));
  }
});

test('the JS twin of Moved_envelope_does_not_open', async () => {
  const otherAd = fromB64url(vectors.envelope[3].ad);
  await assert.rejects(open(firstKey, first.sealed, otherAd), EnvelopeError);
});

test('the JS twin of A_changed_byte_does_not_open', async () => {
  const blob = fromB64url(first.sealed.slice(PREFIX.length));
  // The version, nonce, ciphertext and tag in turn: each is authenticated or is the version itself.
  for (const index of [0, 8, 17, blob.length - 1]) {
    const changed = blob.slice();
    changed[index] ^= 1;
    await assert.rejects(open(firstKey, PREFIX + b64url(changed), firstAd), EnvelopeError, `byte ${index}`);
  }
});

test('the epoch in the header is not authenticated by GCM; hostKey.openText is what refuses a foreign one', async () => {
  // The epoch is not in the associated data, as in C#. open still succeeds with the right key.
  const blob = fromB64url(first.sealed.slice(PREFIX.length));
  blob[4] ^= 1;
  const changed = PREFIX + b64url(blob);
  assert.equal(epochOf(changed), first.epoch ^ 1);
  assert.equal(fromUtf8(await open(firstKey, changed, firstAd)), first.plaintext);
});

test('another key does not open', async () => {
  const other = firstKey.slice();
  other[0] ^= 1;
  await assert.rejects(open(other, first.sealed, firstAd), EnvelopeError);
});

test('every malformed envelope fails with EnvelopeError, never another error type', async () => {
  const header = Uint8Array.of(1, 0, 0, 0, 0, ...new Uint8Array(12));
  const body = first.sealed.slice(PREFIX.length);
  const bad = [
    'e1:',
    'e2:AAAA',
    body,
    'E1:' + body,
    PREFIX + '!!!!',
    PREFIX + 'AQ==',
    PREFIX + body + '=',
    PREFIX + 'A',
    PREFIX + b64url(new Uint8Array(32)),
    PREFIX + b64url(header),
    PREFIX + b64url(Uint8Array.of(...header, ...new Uint8Array(15))),
    PREFIX + b64url(Uint8Array.of(2, ...header.subarray(1), ...new Uint8Array(16))),
    '',
    null,
    undefined,
    5
  ];
  for (const text of bad) {
    await assert.rejects(open(firstKey, text, firstAd), EnvelopeError, JSON.stringify(text));
    assert.throws(() => epochOf(text), EnvelopeError, JSON.stringify(text));
  }
});

test('an envelope of exactly header plus tag is an empty plaintext', async () => {
  const sealed = await seal(firstKey, 9, new Uint8Array(0), firstAd);
  assert.equal(fromB64url(sealed.slice(PREFIX.length)).length, 33);
  assert.equal((await open(firstKey, sealed, firstAd)).length, 0);
});

test('EnvelopeError is an Error with its own name', () => {
  const error = new EnvelopeError('x');
  assert.ok(error instanceof Error);
  assert.equal(error.name, 'EnvelopeError');
  assert.equal(error.message, 'x');
});

test('derive reproduces every C# key derivation', async () => {
  for (const k of vectors.kdf) {
    const out = await derive(fromB64url(k.ikm), fromB64url(k.info));
    assert.ok(out instanceof Uint8Array);
    assert.equal(out.length, 32);
    assert.equal(b64url(out), k.out);
  }
});

test('derive takes a text info as its UTF-8 bytes', async () => {
  for (const k of vectors.kdf.slice(0, 3)) {
    const info = fromUtf8(fromB64url(k.info));
    assert.equal(b64url(await derive(fromB64url(k.ikm), info)), k.out);
  }
});

test('the info strings are the C# constants and distinct purposes give distinct keys', async () => {
  assert.deepEqual(INFO, { message: 'enactive-msg-v1', grantAuth: 'enactive-grant-auth-v1', pair: 'enactive-pair-v1' });
  const ikm = new Uint8Array(32).fill(7);
  const keys = await Promise.all(Object.values(INFO).map((info) => derive(ikm, info)));
  assert.equal(new Set(keys.map(b64url)).size, 3);
});

test('hostKey reproduces the C# host key', async () => {
  const stored = vectors.hostKey;
  const key = await hostKey(stored.epoch, fromB64url(stored.secret));
  assert.equal(key.epoch, stored.epoch);
  assert.equal(b64url(key.secret), stored.secret);
  assert.equal(b64url(key.messageKey), stored.messageKey);
  assert.equal(b64url(key.grantAuthKey), stored.grantAuthKey);
});

test('hostKey refuses a secret that is not 32 bytes with TypeError', async () => {
  for (const length of [0, 16, 31, 33]) {
    await assert.rejects(hostKey(1, new Uint8Array(length)), TypeError);
  }
});

test('hostKey opens the C# envelope sealed with its message key, and seals text that opens', async () => {
  const stored = vectors.hostKey;
  const key = await hostKey(stored.epoch, fromB64url(stored.secret));
  // vectors.envelope[0] was sealed with this host key's message key under the same epoch.
  assert.equal(await key.openText(first.sealed, firstAd), first.plaintext);
  const sealed = await key.sealText('Привет 🙂', firstAd);
  assert.equal(epochOf(sealed), stored.epoch);
  assert.equal(await key.openText(sealed, firstAd), 'Привет 🙂');
});

test('hostKey.openText refuses an envelope of another epoch before decrypting', async () => {
  const stored = vectors.hostKey;
  const key = await hostKey(stored.epoch, fromB64url(stored.secret));
  const other = await hostKey(stored.epoch + 1, fromB64url(stored.secret));
  // Same key material, so decrypting would succeed: only the epoch check can refuse this.
  const sealed = await other.sealText('x', firstAd);
  await assert.rejects(key.openText(sealed, firstAd), EnvelopeError);
});

test('hostKey.openText fails with EnvelopeError for a moved envelope and for text that is not UTF-8', async () => {
  const stored = vectors.hostKey;
  const key = await hostKey(stored.epoch, fromB64url(stored.secret));
  await assert.rejects(key.openText(first.sealed, utf8('another record')), EnvelopeError);
  const notText = await seal(key.messageKey, stored.epoch, Uint8Array.of(0xff, 0xfe), firstAd);
  await assert.rejects(key.openText(notText, firstAd), EnvelopeError);
});
