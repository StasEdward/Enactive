import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openKeystore, memoryAdapter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { ad } from '../../src/Enactive.Remote.Gateway/wwwroot/js/sealed.js';
import { actionHash } from '../../src/Enactive.Remote.Gateway/wwwroot/js/action-identity.js';
import { fromB64url } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import { createReader, answerable, NOT_GIVEN, ALTERED } from '../../src/Enactive.Remote.Gateway/wwwroot/js/reader.js';
import { vectors } from './vectors.mjs';

const ALICE = '0123456789abcdef0123456789abcdef';
const HOST = 'host-7f3a9c';
const EPOCH = vectors.hostKey.epoch;
const secret = fromB64url(vectors.hostKey.secret);
// The computer's key as it seals: the same epoch and secret this device is given.
const computer = await hostKey(EPOCH, secret);

/** A key store holding the computer's key for EPOCH, and a count of how often its keys were asked for. */
async function store({ holding = true } = {}) {
  const keystore = await openKeystore(ALICE, memoryAdapter());
  if (holding) await keystore.addHostKey(HOST, EPOCH, secret);
  const counted = { ...keystore, asked: 0 };
  counted.hostKeys = async (hostId) => {
    counted.asked += 1;
    return keystore.hostKeys(hostId);
  };
  return counted;
}

const event = async ({ id = 'event-1', runId = 'run-1', sequence = 7, kind = 'Progress', text = 'Read the disk.' } = {}) => ({
  id, runId, hostId: HOST, sequence, kind, ordinal: 1, at: '2026-10-02T10:00:00Z',
  sealedDetail: await computer.sealText(text, ad.event(HOST, runId, sequence, kind))
});

test('an event opens under its own record and not another\'s', async () => {
  const reader = createReader(await store());
  const own = await event();

  assert.deepEqual(await reader.openEvent(own, HOST), { text: 'Read the disk.' });

  // The gateway carries the envelope and writes every plaintext field around it: the same envelope
  // shown as another run's event, another step of this run or another kind of step does not open.
  for (const moved of [
    { ...own, id: 'event-2', runId: 'run-2' },
    { ...own, id: 'event-3', sequence: 8 },
    { ...own, id: 'event-4', kind: 'Completed' },
    // The same id as the record that opened: the cache does not hand its text to other data.
    { ...own, runId: 'run-2' }
  ]) {
    assert.deepEqual(await reader.openEvent(moved, HOST), { unreadable: ALTERED }, JSON.stringify(moved));
  }

  // Under another computer: that computer's key is not held, which is the honest reason.
  assert.deepEqual(await reader.openEvent({ ...own, hostId: 'host-other' }, 'host-other'), { unreadable: NOT_GIVEN });
});

const sealedAction = {
  tool: 'run_command',
  argumentsJson: '{"command":"dotnet test"}',
  fullText: 'dotnet test',
  workingDirectory: 'C:\\work\\project',
  topic: 'Run the tests?'
};

/** An approval as the computer seals it: its hash over `hashed`, and the action it shows in the envelope. */
async function approval({ hashed = sealedAction, shown = sealedAction, remoteDecidable = true } = {}) {
  const runId = 'run-1';
  const toolCallId = 'call_abc123';
  const hash = await actionHash(runId, toolCallId, hashed.tool, hashed.workingDirectory, hashed.argumentsJson);
  return {
    id: 'appr-1', hostId: HOST, runId, toolCallId, actionHash: hash, remoteDecidable, status: 'Pending',
    sealedAction: await computer.sealText(JSON.stringify(shown),
      ad.approval(HOST, runId, 'appr-1', toolCallId, hash, remoteDecidable))
  };
}

test('an approval whose arguments do not hash to its actionHash is unverified', async () => {
  const reader = createReader(await store());

  const honest = await reader.openAction(await approval());
  assert.deepEqual(honest, { json: sealedAction, verified: true });

  // Sealed properly, so it opens - but what it would show is not what the hash the Host checks covers.
  const shown = { ...sealedAction, argumentsJson: '{"command":"del /s /q C:\\\\"}' };
  const mismatched = await reader.openAction(await approval({ shown }));
  assert.deepEqual(mismatched, { json: shown, verified: false });

  // An action without the fields the hash is made of is not verified either, rather than an error.
  const { tool, ...noTool } = sealedAction;
  assert.equal((await reader.openAction(await approval({ shown: noTool }))).verified, false);
});

test('Allow and Deny are offered only for a verified request the computer lets a phone answer', async () => {
  const reader = createReader(await store());
  const decidable = await approval();
  const atComputer = await approval({ remoteDecidable: false });
  const mismatched = await approval({ shown: { ...sealedAction, fullText: 'something else', argumentsJson: '{}' } });

  assert.equal(answerable(decidable, await reader.openAction(decidable)), true);
  assert.equal(answerable(atComputer, await reader.openAction(atComputer)), false);
  assert.equal(answerable(mismatched, await reader.openAction(mismatched)), false);
  assert.equal(answerable(decidable, { unreadable: NOT_GIVEN }), false);
  assert.equal(answerable(decidable, undefined), false);
});

test('a missing epoch is unreadable, not empty', async () => {
  const newer = await hostKey(EPOCH + 1, secret);
  const record = { ...await event(), sealedDetail: await newer.sealText('Later.', ad.event(HOST, 'run-1', 7, 'Progress')) };

  // An epoch this device was not given, and a computer it holds nothing for.
  assert.deepEqual(await createReader(await store()).openEvent(record, HOST), { unreadable: NOT_GIVEN });
  const empty = createReader(await store({ holding: false }));
  assert.deepEqual(await empty.openEvent(await event(), HOST), { unreadable: NOT_GIVEN });
  assert.deepEqual(await empty.openAction(await approval()), { unreadable: NOT_GIVEN });

  // A browser that cannot keep keys has nothing to open with: the same reason, never an empty text.
  assert.deepEqual(await createReader(null).openEvent(await event(), HOST), { unreadable: NOT_GIVEN });

  // Text that is not an envelope at all was not sealed by anybody this device trusts.
  assert.deepEqual(await createReader(await store()).openEvent({ ...record, sealedDetail: 'plain words' }, HOST),
    { unreadable: ALTERED });
});

test('a summary opens under the terminal event\'s sequence and kind', async () => {
  const reader = createReader(await store());
  // The gateway copies the terminal event's envelope; the run's status is named like that event's kind.
  const sealedSummary = await computer.sealText('All tests pass.', ad.event(HOST, 'run-1', 9, 'Completed'));
  const run = { id: 'run-1', taskId: 'task-1', hostId: HOST, status: 'Completed', sealedSummary, summarySequence: 9 };

  assert.deepEqual(await reader.openSummary(run), { text: 'All tests pass.' });
  assert.deepEqual(await reader.openSummary({ ...run, summarySequence: 8 }), { unreadable: ALTERED });
  assert.deepEqual(await reader.openSummary({ ...run, status: 'Failed' }), { unreadable: ALTERED });

  // A run that has not ended, or one the computer never saw, has no summary: nothing, not an unreadable one.
  assert.equal(await reader.openSummary({ ...run, status: 'Running', sealedSummary: null, summarySequence: null }), null);
});

test('a notice opens under its event\'s sequence and kind', async () => {
  const reader = createReader(await store());
  const sealedDetail = await computer.sealText('Run the tests?', ad.event(HOST, 'run-1', 4, 'ApprovalRequested'));
  const notice = {
    id: 'notice-1', runId: 'run-1', hostId: HOST, kind: 'PermissionRequested', sealedDetail,
    eventSequence: 4, eventKind: 'ApprovalRequested', read: false, ordinal: 3
  };

  assert.deepEqual(await reader.openNotice(notice), { text: 'Run the tests?' });
  assert.deepEqual(await reader.openNotice({ ...notice, eventSequence: 5 }), { unreadable: ALTERED });
  assert.deepEqual(await reader.openNotice({ ...notice, eventKind: 'Progress' }), { unreadable: ALTERED });
  // The gateway's own notice of a run the computer never saw carries no detail.
  assert.equal(await reader.openNotice({ ...notice, kind: 'NotStarted', sealedDetail: null, eventSequence: null, eventKind: null }), null);
});

test('the cache returns the same result for the same record and envelope and retries an unreadable one after the key arrives', async () => {
  const keystore = await store({ holding: false });
  const reader = createReader(keystore);
  const record = await event();

  assert.deepEqual(await reader.openEvent(record, HOST), { unreadable: NOT_GIVEN });
  // Unreadable is remembered until the keys change: asked again on every poll, a device never admitted to
  // one computer read its key store once for each of that computer's records every three seconds.
  assert.deepEqual(await reader.openEvent(record, HOST), { unreadable: NOT_GIVEN });
  assert.equal(keystore.asked, 1, 'an unreadable record is not tried again before a key arrives');

  await keystore.addHostKey(HOST, EPOCH, secret);
  reader.keysChanged();
  // The grant that just arrived opens it.
  const opened = await reader.openEvent(record, HOST);
  assert.deepEqual(opened, { text: 'Read the disk.' });

  const asked = keystore.asked;
  assert.equal(await reader.openEvent(record, HOST), opened, 'an opened record is not opened again');
  assert.equal(keystore.asked, asked);

  // A new envelope under the same id is another record to open.
  const resealed = { ...record, sealedDetail: await computer.sealText('Changed.', ad.event(HOST, 'run-1', 7, 'Progress')) };
  assert.deepEqual(await reader.openEvent(resealed, HOST), { text: 'Changed.' });
});

test("a computer's key is read and derived once for all its records", async () => {
  const keystore = await store();
  const reader = createReader(keystore);

  for (let sequence = 1; sequence <= 5; sequence += 1) {
    assert.deepEqual(await reader.openEvent(await event({ id: `event-${sequence}`, sequence }), HOST), { text: 'Read the disk.' });
  }

  assert.equal(keystore.asked, 1);
});

test('the cache lets go of the record used longest ago, not of one still being drawn', async () => {
  const reader = createReader(await store(), { keep: 2 });
  const first = await event({ id: 'event-1', sequence: 1 });
  const second = await event({ id: 'event-2', sequence: 2 });
  const third = await event({ id: 'event-3', sequence: 3 });

  const opened = await reader.openEvent(first, HOST);
  const secondOpened = await reader.openEvent(second, HOST);
  // Drawn again on the next poll: the first record is the newest used, so the third evicts the second.
  assert.equal(await reader.openEvent(first, HOST), opened);
  await reader.openEvent(third, HOST);

  assert.equal(await reader.openEvent(first, HOST), opened);
  assert.notEqual(await reader.openEvent(second, HOST), secondOpened);
});

test('a task the C# side sealed opens, with its title and prompt', async () => {
  // The vector envelope was sealed by Envelope.cs under the vector host key's message key, for task-0042.
  const sealed = vectors.envelope.find((one) => one.epoch === EPOCH);
  const reader = createReader(await store());
  const task = { id: 'task-0042', hostId: HOST, workspaceId: 'ws-main', sealed: sealed.sealed, createdAt: '2026-10-02T10:00:00Z' };

  assert.deepEqual(await reader.openTask(task), { json: JSON.parse(sealed.plaintext) });
  assert.deepEqual(await reader.openTask({ ...task, workspaceId: 'ws-other' }), { unreadable: ALTERED });
});

test('a workspace name opens under its own computer and workspace', async () => {
  const reader = createReader(await store());
  const workspace = { id: 'ws-main', sealedName: await computer.sealText('Main', ad.workspace(HOST, 'ws-main')) };

  assert.deepEqual(await reader.openWorkspaceName(HOST, workspace), { text: 'Main' });
  assert.deepEqual(await reader.openWorkspaceName(HOST, { ...workspace, id: 'ws-2' }), { unreadable: ALTERED });
});
