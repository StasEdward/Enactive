import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
const source = await readFile(new URL('../../src/Enactive.Remote.Gateway/wwwroot/admin/quotas.js', import.meta.url), 'utf8');
class Element {
  constructor() { this.children = []; this.value = ''; this.hidden = false; this.disabled = false; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; }
  addEventListener(event, handler) { this[event] = handler; }
  setAttribute() {}
  set innerHTML(value) { throw new Error('Markup insertion'); }
}
const view = { version: 2, defaultsVersion: 4, items: [{ key: 'SealedBytesPerUser', effective: '9223372036854775807', override: null,
  default: '9223372036854775807', startup: '209715200', source: 'default', usage: '250000000', overLimit: true }] };
const response = data => ({ ok: true, json: async () => data });
function setup(fetch) {
  const elements = new Map();
  const get = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
  const context = { document: { getElementById: get, createElement: () => new Element() }, fetch };
  runInNewContext(source, context);
  context.adminQuotas.setAuthenticated(true, 'csrf');
  return { get, quotas: context.adminQuotas, input: () => get('quota-rows').children[0].children[3].children[0] };
}
const submit = ui => ui.get('quota-form').submit({ preventDefault() {} });

test('quota edits preserve 64-bit strings, review scope and send revisions with CSRF', async () => {
  let post;
  const ui = setup(async (url, options) => {
    if (options.method === 'POST') { post = { url, options }; return response({ saved: true }); }
    return response(url.includes('/queues') ? { items: [{ hostId: 'a', name: '<img onerror=bad>', pending: 3, deviceChanges: 1 }], next: null } : view);
  });
  await ui.quotas.open('a'.repeat(32), '<img onerror=bad>');
  assert.match(ui.get('quota-title').textContent, /<img/);
  assert.match(ui.get('quota-queues').children[1].textContent, /<img/);
  assert.match(ui.get('quota-rows').children[0].children[1].textContent, /Over limit/);
  ui.input().value = '9223372036854775806'; ui.get('quota-reason').value = 'Capacity change';
  submit(ui); assert.equal(post, undefined);
  assert.match(ui.get('quota-description').textContent, /Existing resources are preserved/);
  await ui.get('quota-confirm').click();
  assert.equal(post.options.headers['X-CSRF-TOKEN'], 'csrf');
  assert.deepEqual(JSON.parse(post.options.body), { expectedVersion: 2, expectedDefaultsVersion: 4,
    values: { SealedBytesPerUser: '9223372036854775806' }, reason: 'Capacity change' });
  assert.match(ui.get('quota-status').textContent, /saved and reloaded/);
});

for (const status of [403, 409, 500, 'network']) test(`failed save ${status} preserves input and never retries`, async () => {
  let posts = 0;
  const ui = setup(async (url, options) => {
    if (options.method === 'POST') { posts++; if (status === 'network') throw new Error('Failed to fetch'); return { ok: false, status }; }
    return response(view);
  });
  await ui.quotas.open(); ui.input().value = '100'; ui.get('quota-reason').value = 'Reason'; submit(ui);
  await ui.get('quota-confirm').click();
  assert.equal(ui.input().value, '100'); assert.equal(ui.get('quota-reason').value, 'Reason');
  assert.equal(ui.get('quota-save').disabled, true); assert.equal(posts, 1);
  submit(ui); await ui.get('quota-confirm').click(); assert.equal(posts, 1);
  assert.doesNotMatch(ui.get('quota-status').textContent, /saved/);
  if (status === 403) assert.equal(ui.get('quota-reauth').hidden, false);
});

test('reset clears overrides for inheritance and cancel makes no request', async () => {
  let posts = 0;
  const ui = setup(async (_, options) => { if (options.method === 'POST') posts++; return response({ ...view, items: [{ ...view.items[0], override: '100' }] }); });
  await ui.quotas.open(); ui.get('quota-reset').click(); ui.get('quota-reason').value = 'Reset'; submit(ui);
  assert.match(ui.get('quota-description').textContent, /100 → inherited/);
  ui.get('quota-cancel').click(); await ui.get('quota-confirm').click(); assert.equal(posts, 0);
});

test('sign-out clears quota data and ignores late reads', async () => {
  let finish;
  const ui = setup(() => new Promise(resolve => { finish = resolve; }));
  const loading = ui.quotas.open(); ui.quotas.setAuthenticated(false); finish(response(view)); await loading;
  assert.equal(ui.get('quota-panel').hidden, true); assert.equal(ui.get('quota-rows').children.length, 0);
});

test('a refused value preserves the form for correction and zero never means unlimited', async () => {
  let posts = 0;
  const ui = setup(async (_, options) => { if (options.method === 'POST') { posts++; return { ok: false, status: 400 }; } return response(view); });
  await ui.quotas.open(); ui.input().value = '0'; ui.get('quota-reason').value = 'Reason'; submit(ui);
  assert.match(ui.get('quota-status').textContent, /positive whole numbers/); assert.equal(posts, 0);
  ui.input().value = '9999999999999999999'; submit(ui); await ui.get('quota-confirm').click();
  assert.equal(ui.get('quota-save').disabled, false); assert.equal(ui.input().value, '9999999999999999999');
});
