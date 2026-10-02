import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openKeystore, memoryAdapter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { ad, openJson } from '../../src/Enactive.Remote.Gateway/wwwroot/js/sealed.js';
import { epochOf } from '../../src/Enactive.Remote.Gateway/wwwroot/js/envelope.js';
import { fromB64url } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import {
  createWriter, createSendCache, sendRefusal, NoKeyError, NOT_PAIRED_TO_SEND, NOT_YET_GIVEN
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/writer.js';
import { vectors } from './vectors.mjs';

const ALICE = '0123456789abcdef0123456789abcdef';
const HOST = 'host-7f3a9c';
const EPOCH = vectors.hostKey.epoch;
const secret = fromB64url(vectors.hostKey.secret);
const newerSecret = new Uint8Array(32).fill(7);
// The computer's keys, as it opens what the browser sealed.
const computer = await hostKey(EPOCH, secret);
const computerNewer = await hostKey(EPOCH + 1, newerSecret);

const NOW = Date.UTC(2026, 9, 2, 10, 30, 15, 250);
const ISSUED = '2026-10-02T10:30:15.250Z';

async function store({ epochs = [EPOCH] } = {}) {
  const keystore = await openKeystore(ALICE, memoryAdapter());
  for (const epoch of epochs) await keystore.addHostKey(HOST, epoch, epoch === EPOCH ? secret : newerSecret);
  return keystore;
}

const writer = async (options) => createWriter(await store(options), () => NOW);
const vector = (builder) => fromB64url(vectors.ad.find((entry) => entry.builder === builder).bytes);

/** What the computer reads: the JSON, and exactly which members it carries (RemoteJson refuses any other). */
async function opened(key, sealed, adBytes) {
  const json = await openJson(key, sealed, adBytes);
  return { json, members: Object.keys(json).sort() };
}

test('a sealed start opens with the C# associated data', async () => {
  const { args } = vectors.ad.find((entry) => entry.builder === 'command');
  assert.equal(args.kind, 'StartTask');

  const sealed = await (await writer()).sealStart(args.hostId, args.commandId, 'task-0042', 'ws-main');

  // Opened under the bytes the C# Ad.Command wrote for the same ids, not under the JS builder's.
  const { json, members } = await opened(computer, sealed, vector('command'));
  assert.deepEqual(members, ['issuedAt', 'taskId', 'workspaceId']);
  assert.deepEqual(json, { taskId: 'task-0042', workspaceId: 'ws-main', issuedAt: ISSUED });
});

test('a sealed task opens with the C# associated data', async () => {
  const { args } = vectors.ad.find((entry) => entry.builder === 'task');
  const sealed = await (await writer()).sealTask(args.hostId, args.taskId, args.workspaceId,
    { title: 'Disk report', prompt: 'How full are the disks?' });

  const { json, members } = await opened(computer, sealed, vector('task'));
  assert.deepEqual(members, ['prompt', 'title']);
  assert.deepEqual(json, { title: 'Disk report', prompt: 'How full are the disks?' });
});

test('every command opens under its own kind with exactly the members the computer reads', async () => {
  const write = await writer();
  const devicePublic = new Uint8Array(65).fill(9);
  devicePublic[0] = 4;

  const cases = [
    ['CancelRun', await write.sealCancel(HOST, 'cmd-1', 'run-1'),
      { runId: 'run-1', issuedAt: ISSUED }],
    ['ResolveApproval', await write.sealDecision(HOST, 'cmd-1', 'appr-1', 'ab'.repeat(32), 'Deny'),
      { approvalId: 'appr-1', actionHash: 'ab'.repeat(32), decision: 'Deny', issuedAt: ISSUED }],
    ['RevokeDevice', await write.sealRevocation(HOST, 'cmd-1', 'device-2'),
      { deviceId: 'device-2', issuedAt: ISSUED }],
    ['EndorseDevice', await write.sealEndorsement(HOST, 'cmd-1', 'device-3', devicePublic, 'Phone'),
      // The key travels as the computer decodes it: base64url of the 65 raw bytes.
      { deviceId: 'device-3', devicePublic: 'BAkJ' + 'CQkJ'.repeat(20) + 'CQk', label: 'Phone', issuedAt: ISSUED }]
  ];

  for (const [kind, sealed, expected] of cases) {
    const { json, members } = await opened(computer, sealed, ad.command(HOST, 'cmd-1', kind));
    assert.deepEqual(json, expected, kind);
    assert.deepEqual(members, Object.keys(expected).sort(), kind);

    // Under any other kind it does not open: a cancel is never read as a revocation.
    for (const other of cases.map(([name]) => name).filter((name) => name !== kind)) {
      await assert.rejects(computer.openText(sealed, ad.command(HOST, 'cmd-1', other)), kind + ' as ' + other);
    }
  }
  assert.equal(fromB64url(cases[3][2].devicePublic).length, 65);
});

test('a decision is the enum name the computer reads, and nothing else is sealed as one', async () => {
  const write = await writer();
  const allow = await write.sealDecision(HOST, 'cmd-2', 'appr-1', 'ab'.repeat(32), 'Allow');
  assert.equal((await openJson(computer, allow, ad.command(HOST, 'cmd-2', 'ResolveApproval'))).decision, 'Allow');

  for (const wrong of ['allow', 'yes', true, undefined]) {
    await assert.rejects(write.sealDecision(HOST, 'cmd-2', 'appr-1', 'ab'.repeat(32), wrong), TypeError, String(wrong));
  }
  // A field missing from the body would be sealed as an absent member, and refused on the computer for a reason nobody sees.
  await assert.rejects(write.sealCancel(HOST, 'cmd-3', undefined), TypeError);
  await assert.rejects(write.sealEndorsement(HOST, 'cmd-3', 'device-3', new Uint8Array(32), 'Phone'), TypeError);
});

test('issuedAt is the injected clock in ISO-8601 UTC', async () => {
  let now = NOW;
  const write = createWriter(await store(), () => now);

  const first = await openJson(computer, await write.sealCancel(HOST, 'cmd-4', 'run-1'), ad.command(HOST, 'cmd-4', 'CancelRun'));
  now += 60_000;
  const later = await openJson(computer, await write.sealCancel(HOST, 'cmd-4', 'run-1'), ad.command(HOST, 'cmd-4', 'CancelRun'));

  assert.equal(first.issuedAt, ISSUED);
  assert.equal(later.issuedAt, '2026-10-02T10:31:15.250Z');
});

test('everything is sealed under the newest epoch this device holds', async () => {
  const write = await writer({ epochs: [EPOCH, EPOCH + 1] });
  const sealed = await write.sealCancel(HOST, 'cmd-5', 'run-1');

  // The computer opens commands under its current key only: one sealed under an older epoch is refused.
  assert.equal(epochOf(sealed), EPOCH + 1);
  assert.equal((await openJson(computerNewer, sealed, ad.command(HOST, 'cmd-5', 'CancelRun'))).runId, 'run-1');
});

test('sending is refused when the host has a newer epoch than this device holds', async () => {
  const host = { id: HOST, keyEpoch: EPOCH + 1 };

  assert.equal(sendRefusal(host, EPOCH), NOT_YET_GIVEN);
  assert.equal(NOT_YET_GIVEN, "This device has not received this computer's newest key yet - it will in a moment");
  assert.equal(sendRefusal(host, null), NOT_PAIRED_TO_SEND);
  assert.equal(sendRefusal(host, EPOCH + 1), null);
  // A snapshot older than the key store is no reason to refuse: the newest key held is the one to seal with.
  assert.equal(sendRefusal(host, EPOCH + 2), null);

  // And the writer itself, holding nothing for the computer, seals nothing.
  const empty = await writer({ epochs: [] });
  await assert.rejects(empty.sealStart(HOST, 'cmd-6', 'task-1', 'ws-main'), NoKeyError);
  await assert.rejects(createWriter(null).sealCancel(HOST, 'cmd-6', 'run-1'), NoKeyError);
});

test('a retry resends the identical envelope', async () => {
  const cache = createSendCache();
  const write = await writer();
  let sealedCount = 0;
  const seal = (id) => {
    sealedCount += 1;
    return write.sealCancel(HOST, id, 'run-1');
  };

  const first = await cache.once('cancel:run-1', EPOCH, seal);
  const again = await cache.once('cancel:run-1', EPOCH, seal);

  // A fresh nonce makes every seal a different envelope, and the gateway would take a second envelope under
  // the same command id for a different command.
  assert.deepEqual(again, first);
  assert.equal(sealedCount, 1);
  assert.equal(typeof first.id, 'string');
  assert.notEqual((await cache.once('cancel:run-2', EPOCH, seal)).id, first.id);
});

test('a key newer than the one a cached command was sealed under seals it again under a new id', async () => {
  const cache = createSendCache();
  let write = await writer();
  const seal = (id) => write.sealCancel(HOST, id, 'run-1');

  const old = await cache.once('cancel:run-1', EPOCH, seal);
  write = await writer({ epochs: [EPOCH, EPOCH + 1] });
  const renewed = await cache.once('cancel:run-1', EPOCH + 1, seal);

  // The computer refuses the old one as sealed before a device was removed; the same id with another
  // envelope would be refused by the gateway as a different command.
  assert.notEqual(renewed.id, old.id);
  assert.equal(epochOf(renewed.sealed), EPOCH + 1);
  assert.deepEqual(await cache.once('cancel:run-1', EPOCH + 1, seal), renewed);
});

test('a seal that failed is not kept, and a forgotten action starts over', async () => {
  const cache = createSendCache();
  const empty = await writer({ epochs: [] });
  await assert.rejects(cache.once('cancel:run-1', EPOCH, (id) => empty.sealCancel(HOST, id, 'run-1')), NoKeyError);

  const write = await writer();
  const made = await cache.once('cancel:run-1', EPOCH, (id) => write.sealCancel(HOST, id, 'run-1'));
  assert.equal(epochOf(made.sealed), EPOCH);

  cache.forget('cancel:run-1');
  assert.notEqual((await cache.once('cancel:run-1', EPOCH, (id) => write.sealCancel(HOST, id, 'run-1'))).id, made.id);
});
