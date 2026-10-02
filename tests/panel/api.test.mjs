import { test } from 'node:test';
import assert from 'node:assert/strict';
import { get, post } from '../../src/Enactive.Remote.Gateway/wwwroot/js/api.js';

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
