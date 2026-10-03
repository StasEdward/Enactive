import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openKeystore, memoryAdapter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { ad, sealJson } from '../../src/Enactive.Remote.Gateway/wwwroot/js/sealed.js';
import { fromB64url } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import { createReader, NOT_GIVEN } from '../../src/Enactive.Remote.Gateway/wwwroot/js/reader.js';
import { actionHash } from '../../src/Enactive.Remote.Gateway/wwwroot/js/action-identity.js';
import {
  exportOpened, exportFile, downloadMyData, exportRefusal
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/export.js';
import { vectors } from './vectors.mjs';

const ALICE = '0123456789abcdef0123456789abcdef';
const HOST = 'host-7f3a9c';
const EPOCH = vectors.hostKey.epoch;
const secret = fromB64url(vectors.hostKey.secret);

/** A key store holding the computer's key for EPOCH, and nothing else. */
async function store() {
  const keystore = await openKeystore(ALICE, memoryAdapter());
  await keystore.addHostKey(HOST, EPOCH, secret);
  return keystore;
}

/**
 * The gateway's export, as it streams it: one task sealed under the key this device holds, and one step sealed
 * under the computer's next key, which this device was never given. Around them, what the gateway keeps in clear.
 */
async function gatewayExport() {
  const computer = await hostKey(EPOCH, secret);
  const newer = await hostKey(EPOCH + 1, secret);
  const task = { id: 'task-1', hostId: HOST, workspaceId: 'workspace-1', createdAt: '2026-10-03T09:00:00.000Z' };
  const event = { id: 'event-1', hostId: HOST, runId: 'run-1', sequence: 7, kind: 'Progress', ordinal: 1 };

  return {
    exportedAt: '2026-10-03T10:15:00.000Z',
    userId: ALICE,
    tables: {
      users: [{ id: ALICE, displayName: 'Alice' }],
      hosts: [{ id: HOST, label: 'Studio PC', keyEpoch: EPOCH + 1 }],
      tasks: [{
        ...task,
        sealed: await sealJson(computer, { title: 'Disk report', prompt: 'Say how full the disks are.' },
          ad.task(HOST, task.id, task.workspaceId))
      }],
      events: [{
        ...event,
        sealedDetail: await newer.sealText('Read the disk.', ad.event(HOST, event.runId, event.sequence, event.kind))
      }],
      runs: [{ id: 'run-1', taskId: 'task-1', hostId: HOST, status: 'Running', sealedSummary: null }]
    }
  };
}

const action = {
  tool: 'run_command', argumentsJson: '{"command":"df -h"}', fullText: 'df -h', workingDirectory: '/home/alice',
  topic: 'Read the disks?'
};

/**
 * One record of each other sealed kind, under the key this device holds, in the rows the gateway writes: its
 * columns in camelCase (Services/Export.cs), with every field the associated data is rebuilt from. A column
 * renamed on either side leaves a record that does not open, which these then fail on.
 */
async function everyKind() {
  const computer = await hostKey(EPOCH, secret);
  const runId = 'run-2';
  const hash = await actionHash(runId, 'call-1', action.tool, action.workingDirectory, action.argumentsJson);

  return {
    exportedAt: '2026-10-03T10:15:00.000Z',
    userId: ALICE,
    tables: {
      runs: [{
        id: runId, taskId: 'task-1', hostId: HOST, status: 'Completed', appliedSequence: 9,
        createdAt: '2026-10-03T09:00:00.000Z', endedAt: '2026-10-03T09:05:00.000Z', summarySequence: 9,
        sealedSummary: await computer.sealText('The disks are half full.', ad.event(HOST, runId, 9, 'Completed'))
      }],
      notices: [{
        id: 'notice-1', runId, hostId: HOST, kind: 'run-finished', eventSequence: 9, eventKind: 'Completed',
        at: '2026-10-03T09:05:00.000Z', isRead: false, ordinal: 3,
        sealedDetail: await computer.sealText('The disks are half full.', ad.event(HOST, runId, 9, 'Completed'))
      }],
      approvals: [{
        id: 'approval-1', hostId: HOST, runId, toolCallId: 'call-1', actionHash: hash, remoteDecidable: true,
        status: 'Approved', requestedDecision: 'Allow', createdAt: '2026-10-03T09:01:00.000Z',
        expiresAt: '2026-10-03T09:11:00.000Z',
        sealedAction: await sealJson(computer, action, ad.approval(HOST, runId, 'approval-1', 'call-1', hash, true))
      }],
      hostWorkspaces: [{
        hostId: HOST, workspaceId: 'workspace-1',
        sealedName: await computer.sealText('Home', ad.workspace(HOST, 'workspace-1'))
      }]
    }
  };
}

test('the panel export opens what it can and names what it cannot', async () => {
  const data = await gatewayExport();
  const original = structuredClone(data);

  const opened = await exportOpened(data, createReader(await store()));

  // The task opened with the key this device holds; the step is under a key it was never given, and says so
  // where its text would be rather than being left out or left as an envelope.
  assert.deepEqual(opened.tables.tasks[0].sealed, { json: { title: 'Disk report', prompt: 'Say how full the disks are.' } });
  assert.deepEqual(opened.tables.events[0].sealedDetail, { unreadable: NOT_GIVEN });

  // Everything around them is kept as the gateway sent it, and a run with nothing sealed stays without it.
  assert.equal(opened.tables.tasks[0].createdAt, data.tables.tasks[0].createdAt);
  assert.equal(opened.tables.events[0].sequence, 7);
  assert.equal(opened.tables.runs[0].sealedSummary, null);
  assert.deepEqual(opened.tables.hosts, data.tables.hosts);
  assert.equal(opened.userId, ALICE);

  // The export as fetched is not changed: the opened copy is a copy.
  assert.deepEqual(data, original);

  // The file: the opened copy, as JSON, named for the day of the export.
  const file = exportFile(opened);
  assert.equal(file.name, 'enactive-export-2026-10-03.json');
  assert.equal(file.blob.type, 'application/json');
  assert.deepEqual(JSON.parse(await file.blob.text()), opened);
});

test('a run summary, a notice, a permission request and a workspace name open from the gateway rows', async () => {
  const opened = (await exportOpened(await everyKind(), createReader(await store()))).tables;

  assert.deepEqual(opened.runs[0].sealedSummary, { text: 'The disks are half full.' });
  assert.deepEqual(opened.notices[0].sealedDetail, { text: 'The disks are half full.' });
  assert.deepEqual(opened.approvals[0].sealedAction, { json: action, verified: true });
  assert.deepEqual(opened.hostWorkspaces[0].sealedName, { text: 'Home' });
});

test('nothing is sent while exporting', async () => {
  const calls = [];
  const api = {
    // With the antiforgery token: the gateway gives the export only to the panel, never to a link (Export.cs).
    get: async (path, options) => {
      calls.push(`GET ${path}${options?.antiforgery === true ? ' with the token' : ''}`);
      return gatewayExport();
    },
    post: async (path) => { calls.push(`POST ${path}`); },
    remove: async (path) => { calls.push(`DELETE ${path}`); }
  };

  // And nothing behind the api's back.
  const fetched = [];
  const original = globalThis.fetch;
  globalThis.fetch = async (path) => { fetched.push(String(path)); return new Response('{}'); };

  let file;
  try {
    file = await downloadMyData({ api, reader: createReader(await store()) });
  } finally {
    globalThis.fetch = original;
  }

  assert.deepEqual(calls, ['GET /api/export with the token']);
  assert.deepEqual(fetched, []);
  assert.equal(JSON.parse(await file.blob.text()).tables.tasks[0].sealed.json.title, 'Disk report');
});

test('a second download within the hour says when the next one can be made', () => {
  const now = new Date('2026-10-03T10:15:00Z');
  const refused = Object.assign(new Error('Too many requests. Wait a moment and try again.'),
    { code: 'rate-limited', status: 429, retryAfter: 1800 });
  const at = new Date(now.getTime() + 1800 * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });

  assert.equal(exportRefusal(refused, now), `You can download your data once an hour; try again at ${at}.`);
  assert.equal(exportRefusal({ ...refused, retryAfter: undefined }, now),
    'You can download your data once an hour; try again later.');

  // Any other refusal is the gateway's own sentence.
  assert.equal(exportRefusal(Object.assign(new Error('Sign in again.'), { code: 'unauthenticated' }), now),
    'Sign in again.');
});
