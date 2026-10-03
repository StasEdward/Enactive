import { test } from 'node:test';
import assert from 'node:assert/strict';
import { get, post, remove, useDevice, endSession } from '../../src/Enactive.Remote.Gateway/wwwroot/js/api.js';

// fetch replaced for one call, recording what the page would have sent.
async function sent(call) {
  const original = globalThis.fetch;
  let request;
  globalThis.fetch = async (path, init) => {
    request = { path, init };
    return new Response('{}', { status: 200 });
  };
  try {
    await call();
  } finally {
    globalThis.fetch = original;
  }
  return request;
}

// A device's calls name the device in a header; the headers every call needs must not be lost beside it.
test('a call can carry headers of its own beside the ones every call sends', async () => {
  const read = await sent(() => get('/api/grants', { headers: { 'X-Enactive-Device': 'device-1' } }));
  assert.equal(read.init.headers['X-Enactive-Device'], 'device-1');
  assert.equal(read.init.headers.accept, 'application/json');

  const written = await sent(() => post('/api/grants', [], { headers: { 'X-Enactive-Device': 'device-1' } }));
  assert.equal(written.init.headers['X-Enactive-Device'], 'device-1');
  assert.equal(written.init.headers['content-type'], 'application/json');
  assert.ok('X-CSRF-TOKEN' in written.init.headers);
  assert.equal(written.init.body, '[]');
});

// A refusal for asking too often says when asking again will work, as the gateway's Retry-After header does: the
// page can only tell the person when to come back if the refusal carries it.
test('a refusal carries the gateway\'s Retry-After, in seconds', async () => {
  const original = globalThis.fetch;
  const refusal = JSON.stringify({ code: 'rate-limited', error: 'Too many requests. Wait a moment and try again.' });

  try {
    globalThis.fetch = async () => new Response(refusal, { status: 429, headers: { 'Retry-After': '1800' } });
    const limited = await get('/api/export').catch((error) => error);
    assert.equal(limited.code, 'rate-limited');
    assert.equal(limited.status, 429);
    assert.equal(limited.retryAfter, 1800);

    globalThis.fetch = async () => new Response(refusal, { status: 429 });
    assert.equal((await get('/api/export').catch((error) => error)).retryAfter, undefined);
  } finally {
    globalThis.fetch = original;
  }
});

// A GET the gateway gives only to the panel (the data export) carries the antiforgery token, which no other site can
// read; every other GET is sent as before.
test('a GET carries the antiforgery token only when asked to', async () => {
  const asked = await sent(() => get('/api/export', { antiforgery: true }));
  assert.ok('X-CSRF-TOKEN' in asked.init.headers);
  assert.equal(asked.init.headers.accept, 'application/json');

  const plain = await sent(() => get('/api/state'));
  assert.ok(!('X-CSRF-TOKEN' in plain.init.headers));
});

// The gateway refuses every private call that does not name this browser's device (403 for a removed one, 400 for
// none), so once the device is known every call names it - not only the ones that hand out keys. Before it is
// known only registering it and signing out are asked, and those go without; after the session ends nothing does.
test('once the device is known every call names it, until the session ends', async () => {
  assert.ok(!('X-Enactive-Device' in (await sent(() => post('/api/devices', {}))).init.headers));

  useDevice('device-1');
  try {
    assert.equal((await sent(() => get('/api/state'))).init.headers['X-Enactive-Device'], 'device-1');
    assert.equal((await sent(() => post('/api/tasks', {}))).init.headers['X-Enactive-Device'], 'device-1');
    assert.equal((await sent(() => remove('/api/account'))).init.headers['X-Enactive-Device'], 'device-1');
    assert.equal((await sent(() => get('/api/export', { antiforgery: true }))).init.headers['X-Enactive-Device'], 'device-1');

    // A call naming a device itself is sent with that one.
    const named = await sent(() => get('/api/grants', { headers: { 'X-Enactive-Device': 'device-2' } }));
    assert.equal(named.init.headers['X-Enactive-Device'], 'device-2');
  } finally {
    endSession();
  }

  assert.ok(!('X-Enactive-Device' in (await sent(() => get('/api/state'))).init.headers));
});
