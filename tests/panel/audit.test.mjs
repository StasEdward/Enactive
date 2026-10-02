import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, newestFirst } from '../../src/Enactive.Remote.Gateway/wwwroot/js/audit.js';
import { FORGOTTEN } from '../../src/Enactive.Remote.Gateway/wwwroot/js/session-guard.js';

const ME = 'user:0123456789abcdef0123456789abcdef';
const COMPUTER = 'host:fedcba9876543210fedcba9876543210';

const row = (action, actor = ME, at = '2026-10-02T10:00:00.000Z') => ({ at, actor, action, target: null });

test('every action the gateway writes reads as a sentence of its own', () => {
  const sentences = {
    'signin.github': 'Signed in with GitHub',
    'signin.google': 'Signed in with Google',
    'signin.dev': 'Signed in by name (development sign-in)',
    'signout.everywhere': 'Signed out everywhere',
    'device.registered': 'Device added',
    'device.enrolled': 'Device added with an invitation',
    'device.revoked': 'Device removed',
    'invite.created': 'Invitation to add a device made',
    'host.registered': 'Computer registered',
    'host.revoked': 'Computer removed'
  };

  for (const [action, sentence] of Object.entries(sentences)) {
    assert.equal(describe(row(action)), sentence, action);
  }
});

test('who did it is said when it was not the person', () => {
  assert.equal(describe(row('account.disabled', 'operator')), 'Account disabled by the operator');
  assert.equal(describe(row('account.enabled', 'operator')), 'Account enabled again by the operator');
  assert.equal(describe(row('sessions.revoked', 'operator')), 'Signed out everywhere by the operator');
  assert.equal(describe(row('device.revoked', COMPUTER)), 'Device removed by one of your computers');
  assert.equal(describe(row('invite.created', COMPUTER)), 'Invitation to add a device made by one of your computers');
});

test('an action this page does not know is shown as its own name', () => {
  assert.equal(describe(row('quota.raised')), 'quota.raised');
  assert.equal(describe(row('quota.raised', 'operator')), 'quota.raised');

  // Own properties only: an inherited name would otherwise find Object's function and print its source.
  for (const action of ['constructor', 'toString', '__proto__', 'hasOwnProperty']) {
    assert.equal(describe(row(action)), action);
  }
});

test('the rows are drawn newest first, whatever order they came in', () => {
  const rows = [
    row('device.registered', ME, '2026-10-01T09:00:00.000Z'),
    row('signin.github', ME, '2026-10-02T09:00:00.000Z'),
    row('device.revoked', ME, '2026-09-30T09:00:00.000Z'),
    row('signout.everywhere', ME, '2026-10-02T09:00:00.500Z')
  ];

  assert.deepEqual(newestFirst(rows).map((each) => each.action),
    ['signout.everywhere', 'signin.github', 'device.registered', 'device.revoked']);

  // A copy: the answer the caller holds is not reordered under it.
  assert.equal(rows[0].action, 'device.registered');
});

test('a row whose time cannot be read goes last instead of scrambling the rest', () => {
  const rows = [
    row('device.revoked', ME, 'not a time'),
    row('device.registered', ME, '2026-10-01T09:00:00.000Z'),
    row('signin.github', ME, '2026-10-02T09:00:00.000Z')
  ];

  assert.deepEqual(newestFirst(rows).map((each) => each.action), ['signin.github', 'device.registered', 'device.revoked']);
});

test('one account’s log does not outlive a reset, and the page has its dialog', () => {
  assert.ok(FORGOTTEN.contents.includes('audit-list'));
  assert.ok(FORGOTTEN.contents.includes('audit-error'));

  const page = readFileSync(new URL('../../src/Enactive.Remote.Gateway/wwwroot/index.html', import.meta.url), 'utf8');
  assert.match(page, /<dialog id="audit-dialog"/);
  assert.match(page, /id="security-log"/);
});
