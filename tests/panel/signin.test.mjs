import { test } from 'node:test';
import assert from 'node:assert/strict';
import { providerLinks, outcomeOf } from '../../src/Enactive.Remote.Gateway/wwwroot/js/signin.js';

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
