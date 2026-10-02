import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  createGuard, emptyState, resetState, Stale, FORGOTTEN, forgetScreen, pollOnce
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/session-guard.js';
import {
  get, endSession, onUnauthenticated, onDeviceRevoked, Refused
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/api.js';

// A fetch whose answer the test releases by hand, after whatever it wants to happen first. It ignores the
// abort signal on purpose: an answer that arrives anyway is the case the generation exists for.
function heldFetch(status, body) {
  let release;
  const gate = new Promise((resolve) => { release = resolve; });
  globalThis.fetch = async () => {
    await gate;
    return new Response(body === undefined ? '' : JSON.stringify(body), { status });
  };
  return release;
}

test('a guard drops what started under an earlier generation', () => {
  const guard = createGuard();
  const started = guard.generation;

  assert.equal(guard.isCurrent(started), true);
  guard.bump();
  assert.equal(guard.isCurrent(started), false);
  assert.throws(() => guard.check(started), Stale);
});

test('a result from an earlier generation is ignored', async () => {
  const release = heldFetch(200, { runs: ['alice-run'] });
  const pending = get('/api/state');

  endSession();
  release();

  await assert.rejects(pending, Stale);
});

test('a 401 from an earlier generation does not sign out the session after it', async () => {
  let signedOut = 0;
  onUnauthenticated(() => { signedOut += 1; });

  const release = heldFetch(401);
  const pending = get('/api/state');

  endSession();
  release();

  await assert.rejects(pending, Stale);
  assert.equal(signedOut, 0);
});

test('a 401 of the current session is reported once and refused', async () => {
  let signedOut = 0;
  onUnauthenticated(() => { signedOut += 1; });

  heldFetch(401)();

  await assert.rejects(get('/api/state'), (error) => error instanceof Refused && error.code === 'unauthenticated');
  assert.equal(signedOut, 1);
});

test('only a device-revoked 403 is reported as this device removed', async () => {
  let removed = 0;
  onDeviceRevoked(() => { removed += 1; });

  heldFetch(403, { code: 'device-header', error: 'No device.' })();
  await assert.rejects(get('/api/devices'), (error) => error.code === 'device-header' && error.status === 403);
  assert.equal(removed, 0);

  heldFetch(403, { code: 'device-revoked', error: 'Removed.' })();
  await assert.rejects(get('/api/devices'), (error) => error.code === 'device-revoked');
  assert.equal(removed, 1);
});

test('a refusal that is not JSON keeps its status', async () => {
  globalThis.fetch = async () => new Response('Not Found', { status: 404 });

  await assert.rejects(get('/api/dev/sign-in'), (error) => error instanceof Refused && error.status === 404);
});

test('reset clears cursor, arrays, unread and the open run', () => {
  const state = emptyState();
  Object.assign(state, {
    cursor: '1.42',
    hosts: [{ id: 'h' }],
    tasks: [{ id: 't' }],
    runs: [{ id: 'r' }],
    approvals: [{ id: 'a' }],
    notices: [{ id: 'n' }],
    events: [{ id: 'e' }],
    unread: 3,
    retention: { days: 30 },
    live: true,
    openRun: 'r'
  });

  const same = resetState(state);

  assert.equal(same, state);
  assert.deepEqual(state, emptyState());
});

// What the page holds for the person in its fields rather than in `state`, as a fake element records it.
function element(id) {
  return {
    id, value: 'alice', hidden: false, cleared: false,
    replaceChildren(...children) { this.cleared = children.length === 0; }
  };
}

test('a reset forgets what was typed or shown in every field, and closes the dialogs', () => {
  const elements = new Map();
  const byId = (id) => {
    if (!elements.has(id)) elements.set(id, element(id));
    return elements.get(id);
  };
  let closed = 0;
  const dialogs = [{ close() { closed += 1; } }, { close() { closed += 1; } }];

  forgetScreen(byId, dialogs);

  for (const id of FORGOTTEN.values) assert.equal(byId(id).value, '', id);
  for (const id of FORGOTTEN.contents) assert.equal(byId(id).cleared, true, id);
  for (const id of FORGOTTEN.hidden) assert.equal(byId(id).hidden, true, id);
  assert.equal(closed, 2);

  // The named ones, by name: an unsent task and a computer's credential are what the next person
  // in this tab must not find.
  for (const id of ['task-title', 'task-prompt', 'host-id', 'host-token']) assert.ok(FORGOTTEN.values.includes(id), id);
  for (const id of ['run-detail', 'task-error', 'host-error', 'toast']) assert.ok(FORGOTTEN.contents.includes(id), id);
});

test('every field of every dialog in the page is on the reset list', () => {
  const page = readFileSync(new URL('../../src/Enactive.Remote.Gateway/wwwroot/index.html', import.meta.url), 'utf8');
  const listed = [...FORGOTTEN.values, ...FORGOTTEN.contents, ...FORGOTTEN.hidden];

  for (const id of listed) assert.match(page, new RegExp(`id="${id}"`), `${id} is not in the page`);

  const dialogs = page.match(/<dialog[\s\S]*?<\/dialog>/g) ?? [];
  assert.ok(dialogs.length > 0);

  for (const dialog of dialogs) {
    for (const [, id] of dialog.matchAll(/<(?:input|textarea|select)[^>]*\bid="([^"]+)"/g)) {
      assert.ok(FORGOTTEN.values.includes(id) || FORGOTTEN.contents.includes(id), `${id} outlives a reset`);
    }
  }
});

test('a snapshot of another account is not drawn, and resets the page', async () => {
  const applied = [];
  let others = 0;

  const drawn = await pollOnce({
    read: async () => ({ userId: 'bob', runs: ['bob-run'] }),
    accountId: 'alice',
    apply: (snapshot) => applied.push(snapshot),
    otherAccount: () => { others += 1; }
  });

  assert.equal(drawn, false);
  assert.deepEqual(applied, []);
  assert.equal(others, 1);
});

test('a snapshot of the signed-in account is drawn', async () => {
  const applied = [];
  let others = 0;

  const drawn = await pollOnce({
    read: async () => ({ userId: 'alice', runs: ['alice-run'] }),
    accountId: 'alice',
    apply: (snapshot) => applied.push(snapshot),
    otherAccount: () => { others += 1; }
  });

  assert.equal(drawn, true);
  assert.deepEqual(applied.map((s) => s.runs), [['alice-run']]);
  assert.equal(others, 0);
});

test('a snapshot that names no account is not drawn either', async () => {
  let others = 0;

  const drawn = await pollOnce({
    read: async () => ({ runs: [] }),
    accountId: 'alice',
    apply: () => assert.fail('drawn'),
    otherAccount: () => { others += 1; }
  });

  assert.equal(drawn, false);
  assert.equal(others, 1);
});
