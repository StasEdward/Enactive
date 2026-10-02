import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  deleteAccount, DELETE_QUESTION, ACCOUNT_DELETED, SIGN_IN_TO_DELETE
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/account.js';
import { KEYS_KEPT } from '../../src/Enactive.Remote.Gateway/wwwroot/js/devices.js';

/** A refusal as api.js makes one: a code and the gateway's sentence. */
const refusal = (code, message) => Object.assign(new Error(message), { code });

/** The steps, in the order they were taken. */
function recorder({ refuse, forgetFails = false } = {}) {
  const steps = [];
  return {
    steps,
    remove: async (path) => {
      steps.push(`DELETE ${path}`);
      if (refuse) throw refuse;
    },
    keystore: {
      forget: async () => {
        steps.push('forget');
        if (forgetFails) throw new Error('blocked');
      }
    }
  };
}

test('the question and the answers say what happens, in the words the person reads', () => {
  assert.equal(DELETE_QUESTION,
    'This deletes your account and everything stored for it on the service. Your computers stop. Continue?');
  assert.equal(ACCOUNT_DELETED,
    'Your account and everything stored for it on the service are gone. Backups are kept for 30 days and then removed.');
  assert.equal(SIGN_IN_TO_DELETE, 'Sign in again, then delete the account from the menu.');
});

test('the account is deleted first, and only then this browser\'s keys of it', async () => {
  const { steps, remove, keystore } = recorder();

  const result = await deleteAccount({ remove, keystore });

  assert.deepEqual(steps, ['DELETE /api/account', 'forget']);
  assert.deepEqual(result, { deleted: true, sentence: ACCOUNT_DELETED });
});

test('keys that cannot be deleted do not undo the deletion, and the person is told to clear them', async () => {
  const { steps, remove, keystore } = recorder({ forgetFails: true });

  const result = await deleteAccount({ remove, keystore });

  assert.deepEqual(steps, ['DELETE /api/account', 'forget']);
  assert.deepEqual(result, { deleted: true, sentence: `${ACCOUNT_DELETED} ${KEYS_KEPT}` });
});

test('a browser that holds no keys for the account has nothing to forget', async () => {
  const { steps, remove } = recorder();

  const result = await deleteAccount({ remove, keystore: null });

  assert.deepEqual(steps, ['DELETE /api/account']);
  assert.equal(result.sentence, ACCOUNT_DELETED);
});

test('a session too old to delete keeps the keys and asks for a fresh sign-in', async () => {
  const { steps, remove, keystore } = recorder({
    refuse: refusal('reauthenticate', 'Sign in again to delete the account.')
  });

  const result = await deleteAccount({ remove, keystore });

  // The keys stay: the account is still there, and they are what reads it after the person signs in again.
  assert.deepEqual(steps, ['DELETE /api/account']);
  assert.deepEqual(result, { deleted: false, sentence: SIGN_IN_TO_DELETE });
});

test('any other refusal is passed on, and nothing of this browser is touched', async () => {
  const { steps, remove, keystore } = recorder({ refuse: refusal('rate-limited', 'Too many requests.') });

  await assert.rejects(deleteAccount({ remove, keystore }), { code: 'rate-limited' });
  assert.deepEqual(steps, ['DELETE /api/account']);
});
