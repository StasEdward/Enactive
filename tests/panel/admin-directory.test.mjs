import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
const source = await readFile(new URL('../../src/Enactive.Remote.Gateway/wwwroot/admin/directory.js', import.meta.url), 'utf8');
class Element {
  constructor() { this.children = []; this.value = ''; this.hidden = false; this.disabled = false; this.textContent = ''; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; }
  addEventListener(event, handler) { this[event] = handler; }
  set innerHTML(value) { throw new Error('Untrusted markup was inserted'); }
}
function setup(fetch) {
  const elements = new Map();
  const get = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
  get('directory-kind').value = 'users';
  const context = { document: { getElementById: get, createElement: () => new Element() }, fetch, URLSearchParams };
  runInNewContext(source, context);
  return { get, directory: context.adminDirectory };
}
const response = data => ({ ok: true, json: async () => data });
const totals = { users: 2, disabledUsers: 1, waitingRegistrations: 3 };
const row = name => ({ id: 'a'.repeat(32), displayName: name, status: 'Active', sealedBytes: 10, createdAt: '2026-10-04T00:00:00Z' });
const submit = ui => ui.get('directory-filter').submit({ preventDefault() {} });

test('metadata is rendered as text and search is encoded; details use an explicit projection', async () => {
  const calls = [];
  const ui = setup(async (url, options) => {
    calls.push(url); assert.equal(options.cache, 'no-store');
    if (url.endsWith('/overview')) return response(totals);
    if (url.includes('/users/')) return response({ user: row('<img onerror=alert(1)>'), hosts: 2, devices: 1, tasks: 0, runs: 0, lastHostSeenAt: null });
    return response({ items: [row('<img onerror=alert(1)>')], next: null });
  });
  ui.get('directory-search').value = '&state=Disabled';
  await ui.directory.setAuthenticated(true);
  assert.equal(ui.get('directory-rows').children[0].children[0].textContent, '<img onerror=alert(1)>');
  assert.match(calls[0], /search=%26state%3DDisabled/);
  await ui.get('directory-rows').children[0].children[4].children[0].click();
  assert.equal(ui.get('user-details').hidden, false);
  assert.equal(ui.get('user-fields').children[1].textContent, '<img onerror=alert(1)>');
});

test('next and previous use server cursors and a new search resets pagination', async () => {
  const paths = [];
  const ui = setup(async url => {
    if (url.endsWith('/overview')) return response(totals);
    paths.push(new URL(url, 'https://admin.test').searchParams);
    return response({ items: [row('User')], next: paths.length === 1 ? 'a'.repeat(32) : null });
  });
  await ui.directory.setAuthenticated(true);
  await ui.get('directory-next').click();
  assert.equal(paths[1].get('after'), 'a'.repeat(32));
  assert.equal(ui.get('directory-next').disabled, true);
  await ui.get('directory-previous').click();
  assert.equal(paths[2].get('after'), '');
  ui.get('directory-search').value = 'New search';
  await submit(ui);
  assert.equal(paths[3].get('after'), '');
  assert.equal(ui.get('directory-previous').disabled, true);
});

test('registration view defaults to waiting and exposes no account mutation controls', async () => {
  let path;
  const ui = setup(async url => {
    if (url.endsWith('/overview')) return response(totals);
    path = url;
    return response({ items: [], next: null });
  });
  await ui.directory.setAuthenticated(true);
  ui.get('directory-kind').value = 'registrations';
  await ui.get('directory-kind').change();
  assert.match(path, /registrations.*state=Waiting/);
  assert.equal(ui.get('directory-state').children.length, 4);
  assert.equal(ui.get('directory-status').textContent, 'No matching records.');
});

test('sign-out clears metadata and a late response cannot bring it back', async () => {
  let finish;
  const ui = setup(url => url.endsWith('/overview') ? response(totals) : new Promise(resolve => { finish = resolve; }));
  const loading = ui.directory.setAuthenticated(true);
  ui.directory.setAuthenticated(false);
  finish(response({ items: [row('Private name')], next: null }));
  await loading;
  assert.equal(ui.get('directory-rows').children.length, 0);
  assert.equal(ui.get('overview').textContent, '');
  assert.equal(ui.get('session').hidden, true);
});

test('the latest search wins when an earlier response arrives later', async () => {
  let finish;
  let count = 0;
  const ui = setup(url => {
    if (url.endsWith('/overview')) return response(totals);
    if (++count === 1) return new Promise(resolve => { finish = resolve; });
    return response({ items: [row('New result')], next: null });
  });
  const first = ui.directory.setAuthenticated(true);
  ui.get('directory-search').value = 'New';
  await submit(ui);
  finish(response({ items: [row('Old result')], next: null }));
  await first;
  assert.equal(ui.get('directory-rows').children[0].children[0].textContent, 'New result');
});

test('failed requests retain filters and allow a successful retry', async () => {
  let failed = true;
  const ui = setup(url => url.endsWith('/overview') ? response(totals) : failed ? { ok: false, status: 500 } : response({ items: [], next: null }));
  ui.get('directory-search').value = 'Keep this';
  await ui.directory.setAuthenticated(true);
  assert.match(ui.get('directory-status').textContent, /Could not load/);
  assert.equal(ui.get('directory-search').value, 'Keep this');
  failed = false;
  await submit(ui);
  assert.equal(ui.get('directory-status').textContent, 'No matching records.');
});

test('expired sessions remove private metadata and offer sign-in', async () => {
  const ui = setup(() => ({ ok: false, status: 401 }));
  await ui.directory.setAuthenticated(true);
  assert.equal(ui.get('session').hidden, true);
  assert.equal(ui.get('signin').hidden, false);
  assert.equal(ui.get('directory-rows').children.length, 0);
});
