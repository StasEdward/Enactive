import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createGuard, emptyState, resetState, Stale } from '../../src/Enactive.Remote.Gateway/wwwroot/js/session-guard.js';
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
