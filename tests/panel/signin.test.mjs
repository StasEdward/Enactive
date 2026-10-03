import { test } from 'node:test';
import assert from 'node:assert/strict';
import { providerLinks, outcomeOf, createDevelopmentProbe } from '../../src/Enactive.Remote.Gateway/wwwroot/js/signin.js';

test('one link per provider the gateway lists, and no other', () => {
  assert.deepEqual(providerLinks(['github']), [{ href: '/auth/github/start', label: 'Continue with GitHub' }]);
  assert.deepEqual(providerLinks(['google', 'github']).map((link) => link.label),
    ['Continue with Google', 'Continue with GitHub']);
  assert.deepEqual(providerLinks([]), []);
});

test('a provider the page has no label for is offered under its own name', () => {
  assert.deepEqual(providerLinks(['gitlab']), [{ href: '/auth/gitlab/start', label: 'Continue with gitlab' }]);
});

test('the fragments a sign-in ends on each have a sentence, and nothing else does', () => {
  assert.equal(outcomeOf('#waiting'), 'Your sign-in is recorded; access is opened by hand during the beta.');

  for (const hash of ['#refused', '#disabled', '#failed']) {
    assert.equal(typeof outcomeOf(hash), 'string');
  }

  for (const hash of ['', '#', '#toString', '#constructor', '#pair', null]) {
    assert.equal(outcomeOf(hash), null);
  }
});

test('a provider name outside the expected shape is skipped, and said so', () => {
  const warnings = [];
  const links = providerLinks(['github', 'javascript:alert(1)', 'GitHub', '../x', '', 'a'.repeat(33), 'constructor'],
    (message) => warnings.push(message));

  assert.deepEqual(links.map((link) => link.href), ['/auth/github/start', '/auth/constructor/start']);
  assert.equal(warnings.length, 5);
});

test('a label is the page’s own, never something inherited', () => {
  assert.deepEqual(providerLinks(['constructor']).map((link) => link.label), ['Continue with constructor']);
  assert.deepEqual(providerLinks(['__proto__'], () => {}), []);
});

test('the development sign-in is asked for with a GET and remembered once answered', async () => {
  const asked = [];
  const probe = createDevelopmentProbe(async (path) => {
    asked.push(path);
    throw Object.assign(new Error('Method not allowed'), { status: 405 });
  });

  assert.equal(await probe(), true);
  assert.equal(await probe(), true);
  assert.deepEqual(asked, ['/api/dev/sign-in']);
});

test('a gateway without the development sign-in answers 404, and it is not offered', async () => {
  const probe = createDevelopmentProbe(async () => {
    throw Object.assign(new Error('Not found'), { status: 404 });
  });

  assert.equal(await probe(), false);
});

test('a probe that got no answer is asked again next time, not remembered as none', async () => {
  let calls = 0;
  const probe = createDevelopmentProbe(async () => {
    calls += 1;
    if (calls === 1) throw new Error('dropped by a reset');
    throw Object.assign(new Error('Method not allowed'), { status: 405 });
  });

  assert.equal(await probe(), false);
  assert.equal(await probe(), true);
  assert.equal(calls, 2);
});
