import { test } from 'node:test';
import assert from 'node:assert/strict';
import { canonicalText, canonicalBytes } from '../../src/Enactive.Remote.Gateway/wwwroot/js/canonical.js';
import { b64url, fromB64url, utf8, fromUtf8, equal, concat } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import { vectors } from './vectors.mjs';

test('canonical text matches the C# vectors', () => {
  for (const v of vectors.canonical) assert.equal(canonicalText(v.version, ...v.fields), v.text);
});

test('a missing field is an empty field', () => {
  assert.equal(canonicalText('v1', 'abc', null, 'é'), 'v1\n3:abc\n0:\n2:é\n');
  assert.equal(canonicalText('v1', undefined), 'v1\n0:\n');
});

test('the length counts UTF-8 bytes, not characters', () => {
  assert.equal(canonicalText('v1', '日本語'), 'v1\n9:日本語\n');
  assert.equal(canonicalText('v1', '\u{1F642}'), 'v1\n4:\u{1F642}\n');
});

test('canonical bytes are the UTF-8 of the canonical text', () => {
  for (const v of vectors.canonical) {
    assert.deepEqual(canonicalBytes(v.version, ...v.fields), new TextEncoder().encode(v.text));
  }
});

test('base64url round-trips every length without padding', () => {
  for (let n = 0; n < 40; n++) {
    const bytes = Uint8Array.from({ length: n }, (_, i) => (i * 37 + 250) & 255);
    const text = b64url(bytes);
    assert.match(text, /^[A-Za-z0-9_-]*$/);
    assert.deepEqual(fromB64url(text), bytes);
  }
});

test('base64url uses - and _ and never + / =', () => {
  assert.equal(b64url(Uint8Array.of(0xfb, 0xff, 0xfe)), '-__-');
  assert.deepEqual(fromB64url('-__-'), Uint8Array.of(0xfb, 0xff, 0xfe));
  assert.equal(b64url(Uint8Array.of(1)), 'AQ');
});

test('base64url encodes a large array without overflowing the stack', () => {
  const big = new Uint8Array(1 << 20).fill(0xab);
  assert.deepEqual(fromB64url(b64url(big)), big);
});

test('base64url input outside the alphabet throws TypeError', () => {
  for (const bad of ['AQ==', 'A+Q', 'A/Q', 'A Q', 'AQ\n', 'AAAAA', 'é', '=', null, undefined, 5, Uint8Array.of(1)]) {
    assert.throws(() => fromB64url(bad), TypeError, JSON.stringify(bad));
  }
});

test('base64url rejects a second spelling of the same bytes', () => {
  // "AR" decodes to the same byte as "AQ" when the spare bits are ignored; a MAC or id must have one spelling.
  assert.throws(() => fromB64url('AR'), TypeError);
  assert.throws(() => fromB64url('AAB'), TypeError);
});

test('base64url empty text is empty bytes', () => {
  assert.equal(b64url(new Uint8Array(0)), '');
  assert.deepEqual(fromB64url(''), new Uint8Array(0));
});

test('utf8 and fromUtf8 round-trip, and bad bytes throw', () => {
  const text = 'é 日本語 \u{1F642}';
  assert.equal(fromUtf8(utf8(text)), text);
  assert.throws(() => fromUtf8(Uint8Array.of(0xff, 0xfe)), TypeError);
});

test('fromUtf8 keeps a leading byte order mark', () => {
  assert.equal(fromUtf8(Uint8Array.of(0xef, 0xbb, 0xbf, 0x61)), '﻿a');
});

test('equal compares content and length', () => {
  assert.equal(equal(Uint8Array.of(1, 2, 3), Uint8Array.of(1, 2, 3)), true);
  assert.equal(equal(Uint8Array.of(1, 2, 3), Uint8Array.of(1, 2, 4)), false);
  assert.equal(equal(Uint8Array.of(0, 2, 3), Uint8Array.of(1, 2, 3)), false);
  assert.equal(equal(Uint8Array.of(1, 2), Uint8Array.of(1, 2, 3)), false);
  assert.equal(equal(new Uint8Array(0), new Uint8Array(0)), true);
});

test('concat joins arrays in order', () => {
  assert.deepEqual(concat(Uint8Array.of(1), new Uint8Array(0), Uint8Array.of(2, 3)), Uint8Array.of(1, 2, 3));
  assert.deepEqual(concat(), new Uint8Array(0));
});

test('base64url values in the vectors decode to their real length and re-encode unchanged', () => {
  for (const k of vectors.kdf) {
    assert.equal(fromB64url(k.ikm).length, 32);
    assert.equal(b64url(fromB64url(k.ikm)), k.ikm);
    assert.equal(b64url(fromB64url(k.out)), k.out);
  }
});
