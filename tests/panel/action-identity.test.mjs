import { test } from 'node:test';
import assert from 'node:assert/strict';
import { actionHash, VERSION } from '../../src/Enactive.Remote.Gateway/wwwroot/js/action-identity.js';
import { vectors } from './vectors.mjs';

const first = vectors.actionIdentity[0];
const hashOf = (a) => actionHash(a.runId, a.toolCallId, a.tool, a.workingDirectory, a.argumentsJson);

test('actionHash reproduces every C# hash', async () => {
  assert.ok(vectors.actionIdentity.length >= 5);
  for (const v of vectors.actionIdentity) assert.equal(await hashOf(v), v.hash, `${v.runId} ${v.workingDirectory}`);
});

test('the vectors cover the cases that matter: both spellings of a folder, a drive root, no folder, non-ASCII arguments', () => {
  const dirs = vectors.actionIdentity.map((v) => v.workingDirectory);
  assert.ok(dirs.includes('C:\\work\\project\\') && dirs.includes('C:/work/project') && dirs.includes('C:\\') && dirs.includes(''));
  assert.ok(vectors.actionIdentity.some((v) => /[^\x00-\x7f]/.test(v.argumentsJson)));
});

test('the version is the C# constant', () => {
  assert.equal(VERSION, 'enactive-action-v1');
});

test('a hash is 64 lowercase hex characters', async () => {
  assert.match(await hashOf(first), /^[0-9a-f]{64}$/);
});

test('the same folder spelled differently hashes the same, and a different folder does not', async () => {
  const hash = await hashOf(first);
  for (const dir of ['C:\\work\\project', 'C:/work/project/', 'C:\\work\\project\\\\', 'C:/work/project//']) {
    assert.equal(await actionHash(first.runId, first.toolCallId, first.tool, dir, first.argumentsJson), hash, dir);
  }
  assert.notEqual(await actionHash(first.runId, first.toolCallId, first.tool, 'C:/work/other', first.argumentsJson), hash);
});

test('a drive root keeps its separator: C: is written as C:/ and is not an empty folder', async () => {
  // Trimming every trailing slash would leave "C:", which the C# rule turns back into "C:/"; the two must agree on every spelling of the root.
  const root = await actionHash('r', 'c', 't', 'C:/', '{}');
  for (const dir of ['C:', 'C:\\', 'C:/', 'C://']) assert.equal(await actionHash('r', 'c', 't', dir, '{}'), root, dir);
  assert.notEqual(await actionHash('r', 'c', 't', 'D:\\', '{}'), root);
  assert.notEqual(await actionHash('r', 'c', 't', '', '{}'), root);
  // Only a two-character "X:" is a drive root; a longer path is trimmed as usual.
  assert.equal(await actionHash('r', 'c', 't', 'ab/', '{}'), await actionHash('r', 'c', 't', 'ab', '{}'));
});

test('a missing working directory hashes as an empty one', async () => {
  const empty = await actionHash('r', 'c', 't', '', '{}');
  assert.equal(await actionHash('r', 'c', 't', undefined, '{}'), empty);
  assert.equal(await actionHash('r', 'c', 't', null, '{}'), empty);
});

test('the arguments are hashed as written: not re-serialised, not trimmed', async () => {
  const a = await actionHash('r', 'c', 't', '', '{"a":1}');
  assert.notEqual(await actionHash('r', 'c', 't', '', '{ "a": 1 }'), a);
  assert.notEqual(await actionHash('r', 'c', 't', '', '{"a":1} '), a);
});

test('fields that would collide under a plain join do not collide', async () => {
  // With a separator, ("a:b", "") and ("a", "b:") could read alike; the byte counts keep them apart.
  assert.notEqual(await actionHash('a\n1:b', 'c', 't', '', '{}'), await actionHash('a', 'b', 't', '', '{}'));
  assert.notEqual(await actionHash('ab', 'c', 't', '', '{}'), await actionHash('a', 'bc', 't', '', '{}'));
  assert.notEqual(await actionHash('r', 'c', 't', '', '{}'), await actionHash('r', 'c2', 't', '', '{}'));
});

test('text that is not text is refused with TypeError rather than hashed as an empty field', async () => {
  for (const bad of [undefined, null, 5, {}]) {
    await assert.rejects(actionHash(bad, 'c', 't', '', '{}'), TypeError, `runId ${bad}`);
    await assert.rejects(actionHash('r', bad, 't', '', '{}'), TypeError, `toolCallId ${bad}`);
    await assert.rejects(actionHash('r', 'c', bad, '', '{}'), TypeError, `tool ${bad}`);
    await assert.rejects(actionHash('r', 'c', 't', '', bad), TypeError, `argumentsJson ${bad}`);
  }
  await assert.rejects(actionHash('r', 'c', 't', 5, '{}'), TypeError);
});
