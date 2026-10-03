import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const source = await readFile(new URL('../../src/Enactive.Remote.Gateway/wwwroot/admin/admin.js', import.meta.url), 'utf8');
async function page(fetch, search = '') {
  const elements = Object.fromEntries(['status', 'error', 'signout', 'signin', 'session', 'expires'].map(id => [id, {
    textContent: '', hidden: true, disabled: false,
    addEventListener(event, listener) { this[event] = listener; }
  }]));
  runInNewContext(source, { document: { getElementById: id => elements[id] }, fetch, location: { search }, URLSearchParams });
  await new Promise(resolve => setImmediate(resolve));
  return elements;
}
const session = authenticated => ({ ok: true, json: async () => ({ authenticated, csrfToken: 'csrf', expiresAt: '2026-10-03T12:00:00Z' }) });

test('admin page reads server session and never displays query text as markup', async () => {
  const elements = await page(async () => session(false), '?error=<img src=x onerror=alert(1)>');
  assert.equal(elements.signin.hidden, false);
  assert.equal(elements.session.hidden, true);
  assert.equal(elements.error.hidden, false);
  assert.equal(elements.error.textContent.includes('<img'), false);
});
test('logout sends CSRF and reloads session only after server confirmation', async () => {
  let signedIn = true;
  const elements = await page(async (path, options) => {
    if (path.endsWith('/signout')) {
      assert.equal(options.method, 'POST');
      assert.equal(options.headers['X-CSRF-TOKEN'], 'csrf');
      signedIn = false;
      return { ok: true, status: 204 };
    }
    return session(signedIn);
  });
  assert.equal(elements.session.hidden, false);
  await elements.signout.click();
  assert.equal(elements.session.hidden, true);
  assert.equal(elements.signin.hidden, false);
  assert.equal(elements.signout.disabled, false);
});
test('failed logout leaves signed-in controls visible and allows retry', async () => {
  const elements = await page(async path => path.endsWith('/signout') ? { ok: false, status: 400 } : session(true));
  await elements.signout.click();
  assert.equal(elements.session.hidden, false);
  assert.equal(elements.error.hidden, false);
  assert.match(elements.error.textContent, /Sign out failed/);
  assert.equal(elements.signout.disabled, false);
});
test('failed initial session check does not claim that the user is signed in', async () => {
  const elements = await page(async () => ({ ok: false, status: 500 }));
  assert.equal(elements.session.hidden, true);
  assert.match(elements.error.textContent, /Session check failed/);
});
