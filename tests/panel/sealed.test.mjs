import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ad, sealJson, openJson } from '../../src/Enactive.Remote.Gateway/wwwroot/js/sealed.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { seal, EnvelopeError } from '../../src/Enactive.Remote.Gateway/wwwroot/js/envelope.js';
import { b64url, fromB64url } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import { vectors } from './vectors.mjs';

// The vectors name each builder and give its arguments by name; the builders take them in order.
const ORDER = {
  task: ['hostId', 'taskId', 'workspaceId'],
  command: ['hostId', 'commandId', 'kind'],
  event: ['hostId', 'runId', 'sequence', 'kind'],
  approval: ['hostId', 'runId', 'approvalId', 'toolCallId', 'actionHash', 'remoteDecidable'],
  workspace: ['hostId', 'workspaceId']
};

const build = (v) => ad[v.builder](...ORDER[v.builder].map((name) => v.args[name]));

test('every ad builder reproduces the C# bytes', () => {
  for (const v of vectors.ad) {
    const bytes = build(v);
    assert.ok(bytes instanceof Uint8Array);
    assert.equal(b64url(bytes), v.bytes, `${v.builder} ${JSON.stringify(v.args)}`);
  }
});

test('the vectors cover every builder and both values of remoteDecidable', () => {
  assert.deepEqual([...new Set(vectors.ad.map((v) => v.builder))].sort(), Object.keys(ad).sort());
  const decidable = vectors.ad.filter((v) => v.builder === 'approval').map((v) => v.args.remoteDecidable);
  assert.ok(decidable.includes(true) && decidable.includes(false));
});

test('an event sequence beyond 32 bits is written in full, as a number or as a bigint', () => {
  const v = vectors.ad.find((x) => x.builder === 'event');
  assert.ok(v.args.sequence > 2 ** 32);
  assert.equal(b64url(ad.event(v.args.hostId, v.args.runId, BigInt(v.args.sequence), v.args.kind)), v.bytes);
  assert.equal(b64url(ad.event(v.args.hostId, v.args.runId, v.args.sequence, v.args.kind)), v.bytes);
});

test('an event sequence that is not an exact integer is refused', () => {
  for (const sequence of [1.5, NaN, Infinity, 2 ** 53, '12', null, undefined, 2n ** 63n, -(2n ** 63n) - 1n]) {
    assert.throws(() => ad.event('h', 'r', sequence, 'Progress'), TypeError, String(sequence));
  }
  // The int64 limits themselves are valid, as C# long.
  assert.doesNotThrow(() => ad.event('h', 'r', 2n ** 63n - 1n, 'Progress'));
  assert.doesNotThrow(() => ad.event('h', 'r', -(2n ** 63n), 'Progress'));
  assert.doesNotThrow(() => ad.event('h', 'r', 0, 'Progress'));
});

test('an id that is not text is refused rather than sealed as an empty field', () => {
  for (const bad of [undefined, null, 5]) {
    assert.throws(() => ad.task(bad, 't', 'w'), TypeError, `task ${bad}`);
    assert.throws(() => ad.task('h', bad, 'w'), TypeError);
    assert.throws(() => ad.command('h', bad, 'StartTask'), TypeError);
    assert.throws(() => ad.command('h', 'c', bad), TypeError);
    assert.throws(() => ad.event('h', bad, 1, 'Progress'), TypeError);
    assert.throws(() => ad.approval('h', 'r', bad, 'c', 'x', true), TypeError);
    assert.throws(() => ad.workspace('h', bad), TypeError);
  }
});

test('remoteDecidable must be a boolean: a truthy value is not "true"', () => {
  for (const bad of ['1', 1, 'true', 0, null, undefined]) {
    assert.throws(() => ad.approval('h', 'r', 'a', 'c', 'x', bad), TypeError, String(bad));
  }
});

test('the kinds of record have different data, so one kind cannot be read as another', () => {
  const all = [
    ad.task('h', 'x', 'y'), ad.command('h', 'x', 'y'), ad.workspace('h', 'x'),
    ad.event('h', 'x', 1, 'y'), ad.approval('h', 'x', 'y', 'z', 'w', true)
  ].map(b64url);
  assert.equal(new Set(all).size, all.length);
});

const stored = vectors.hostKey;
const key = await hostKey(stored.epoch, fromB64url(stored.secret));
const taskAd = ad.task('host-1', 'task-1', 'ws-1');

test('sealJson seals JSON that openJson returns, including non-ASCII text', async () => {
  const value = { title: 'Отчёт 🙂', prompt: 'line one\nline two', nested: { list: [1, 2, null, true] } };
  const sealed = await sealJson(key, value, taskAd);
  assert.ok(sealed.startsWith('e1:'));
  assert.deepEqual(await openJson(key, sealed, taskAd), value);
});

test('sealJson writes the JSON text itself, readable by the C# side', async () => {
  const sealed = await sealJson(key, { title: 't', prompt: 'p' }, taskAd);
  assert.equal(await key.openText(sealed, taskAd), '{"title":"t","prompt":"p"}');
});

test('openJson reads JSON text sealed by hostKey.sealText', async () => {
  const sealed = await key.sealText('{"approvalId":"a1","decision":"Allow"}', taskAd);
  assert.deepEqual(await openJson(key, sealed, taskAd), { approvalId: 'a1', decision: 'Allow' });
});

test('openJson of a record moved to other data fails with EnvelopeError', async () => {
  const sealed = await sealJson(key, { a: 1 }, taskAd);
  await assert.rejects(openJson(key, sealed, ad.task('host-1', 'task-1', 'ws-other')), EnvelopeError);
  await assert.rejects(openJson(key, sealed, ad.workspace('host-1', 'ws-1')), EnvelopeError);
});

test('openJson of a record sealed under another epoch fails with EnvelopeError', async () => {
  const other = await hostKey(stored.epoch + 1, fromB64url(stored.secret));
  await assert.rejects(openJson(key, await sealJson(other, { a: 1 }, taskAd), taskAd), EnvelopeError);
});

test('openJson of authentic text that is not JSON fails with EnvelopeError, not SyntaxError', async () => {
  for (const text of ['not json', '', '{"a":', 'undefined']) {
    const sealed = await key.sealText(text, taskAd);
    await assert.rejects(openJson(key, sealed, taskAd), EnvelopeError, JSON.stringify(text));
  }
});

test('openJson of bytes that are not text fails with EnvelopeError', async () => {
  const sealed = await seal(key.messageKey, stored.epoch, Uint8Array.of(0xff, 0xfe), taskAd);
  await assert.rejects(openJson(key, sealed, taskAd), EnvelopeError);
});

test('sealJson refuses a value JSON cannot write, instead of sealing the word "undefined"', async () => {
  for (const value of [undefined, () => 1, Symbol('x')]) {
    assert.throws(() => sealJson(key, value, taskAd), TypeError);
  }
  // null and the other plain values are JSON.
  for (const value of [null, 0, '', false, []]) {
    assert.deepEqual(await openJson(key, await sealJson(key, value, taskAd), taskAd), value);
  }
});

test('a record sealed with an ad builder opens with the same builder and the vector bytes', async () => {
  const v = vectors.ad[0];
  const sealed = await sealJson(key, { ok: true }, build(v));
  assert.deepEqual(await openJson(key, sealed, fromB64url(v.bytes)), { ok: true });
});
