import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  openKeystore, memoryAdapter, indexedDbAdapter, BlockedError, KeystoreClosedError, BLOCKED_AFTER_MS
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';

// User ids in the gateway's shape: 32 hex characters.
const ALICE = '0123456789abcdef0123456789abcdef';
const BOB = 'fedcba9876543210fedcba9876543210';

const bytes = (fill, length = 32) => new Uint8Array(length).fill(fill);

// A real P-256 public key in raw form, as a computer's signing key arrives in a grant.
async function signingPublicRaw() {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
  return new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey));
}

test('pending secrets expire', async () => {
  let now = 1_000;
  const store = await openKeystore(ALICE, memoryAdapter(), () => now);

  await store.setPending('pairing-1', bytes(7), 2_000);
  assert.deepEqual(await store.pending('pairing-1'), bytes(7));

  now = 2_000;
  assert.equal(await store.pending('pairing-1'), null);

  // Dropped, not merely hidden: winding the clock back does not bring the secret back.
  now = 1_000;
  assert.equal(await store.pending('pairing-1'), null);
});

test('a pending secret can be dropped before it expires', async () => {
  const store = await openKeystore(ALICE, memoryAdapter(), () => 0);
  await store.setPending('pairing-1', bytes(7), 10_000);

  await store.dropPending('pairing-1');

  assert.equal(await store.pending('pairing-1'), null);
});

test('two user ids never share a store', async () => {
  const adapter = memoryAdapter();
  const alice = await openKeystore(ALICE, adapter);
  const bob = await openKeystore(BOB, adapter);

  await alice.createDevice();
  await alice.addHostKey('host-a', 1, bytes(1));
  await alice.pinHostSigningKey('host-a', await signingPublicRaw());
  await alice.setPending('pairing-1', bytes(2), Date.now() + 60_000);

  assert.equal(await bob.device(), null);
  assert.equal((await bob.hostKeys('host-a')).size, 0);
  assert.equal(await bob.hostSigningKey('host-a'), null);
  assert.equal(await bob.pending('pairing-1'), null);
  assert.deepEqual(await bob.hosts(), []);

  // And the other way round: Bob's device is his own, not Alice's.
  const bobDevice = await bob.createDevice();
  assert.notDeepEqual(bobDevice.publicRaw, (await alice.device()).publicRaw);
});

test('forget removes everything', async () => {
  const adapter = memoryAdapter();
  const store = await openKeystore(ALICE, adapter);
  await store.createDevice();
  await store.setDeviceId('device-1');
  await store.addHostKey('host-a', 1, bytes(1));
  await store.pinHostSigningKey('host-a', await signingPublicRaw());
  await store.setPending('pairing-1', bytes(2), Date.now() + 60_000);

  await store.forget();

  const reopened = await openKeystore(ALICE, adapter);
  assert.equal(await reopened.device(), null);
  assert.equal((await reopened.hostKeys('host-a')).size, 0);
  assert.equal(await reopened.hostSigningKey('host-a'), null);
  assert.equal(await reopened.pending('pairing-1'), null);
  assert.deepEqual(await reopened.hosts(), []);
});

test('a host key added twice for one epoch keeps one', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());

  await store.addHostKey('host-a', 1, bytes(1));
  await store.addHostKey('host-a', 1, bytes(9));
  await store.addHostKey('host-a', 2, bytes(2));

  const keys = await store.hostKeys('host-a');
  assert.deepEqual([...keys.keys()], [1, 2]);
  // The first key stays: a different key for an epoch already held would be a rollback.
  assert.deepEqual(keys.get(1), bytes(1));
  assert.deepEqual(keys.get(2), bytes(2));
});

test('host keys of one computer are not another computer\'s', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());
  await store.addHostKey('host-a', 1, bytes(1));
  await store.addHostKey('host-b', 1, bytes(2));

  assert.deepEqual((await store.hostKeys('host-a')).get(1), bytes(1));
  assert.deepEqual((await store.hostKeys('host-b')).get(1), bytes(2));
  assert.deepEqual((await store.hosts()).sort(), ['host-a', 'host-b']);

  await store.dropHost('host-a');

  assert.equal((await store.hostKeys('host-a')).size, 0);
  assert.deepEqual(await store.hosts(), ['host-b']);
});

test('the device private key is not extractable', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());
  const created = await store.createDevice();
  const device = await store.device();

  assert.equal(device.privateKey.extractable, false);
  await assert.rejects(crypto.subtle.exportKey('pkcs8', device.privateKey));
  await assert.rejects(crypto.subtle.exportKey('jwk', device.privateKey));
  assert.equal(device.publicRaw.length, 65);
  assert.equal(device.publicRaw[0], 4);
  assert.deepEqual(device.publicRaw, created.publicRaw);
  assert.equal(device.id, null);

  await store.setDeviceId('device-1');
  assert.equal((await store.device()).id, 'device-1');
});

test('a second device is never made over the first', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());
  const first = await store.createDevice();
  await store.setDeviceId('device-1');

  const second = await store.createDevice();

  assert.deepEqual(second.publicRaw, first.publicRaw);
  assert.equal((await store.device()).id, 'device-1');
});

test('a second pin with another signing key is refused and the first stays', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());
  const first = await signingPublicRaw();
  const other = await signingPublicRaw();

  await store.pinHostSigningKey('host-a', first);
  await store.pinHostSigningKey('host-a', first.slice());
  await assert.rejects(store.pinHostSigningKey('host-a', other), /signing key/);

  assert.deepEqual(await store.hostSigningKey('host-a'), first);
});

test('an invalid user id is refused', async () => {
  const adapter = memoryAdapter();
  for (const userId of ['', '../' + ALICE, ALICE + '/x', 'a b', 'é', 'a'.repeat(65), null, undefined, 42]) {
    await assert.rejects(openKeystore(userId, adapter), TypeError, `accepted ${JSON.stringify(userId)}`);
  }
  assert.deepEqual(adapter.databases(), []);
});

test('newestEpoch is the largest epoch held', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());
  assert.equal(await store.newestEpoch('host-a'), null);

  await store.addHostKey('host-a', 2, bytes(2));
  await store.addHostKey('host-a', 10, bytes(10));
  await store.addHostKey('host-a', 3, bytes(3));
  await store.addHostKey('host-b', 50, bytes(50));

  assert.equal(await store.newestEpoch('host-a'), 10);
});

test('the database is named for the account', async () => {
  const adapter = memoryAdapter();
  await openKeystore(ALICE, adapter);

  assert.deepEqual(adapter.databases(), [`enactive-keys-${ALICE}`]);
});

test('an abandoned pending secret is swept when the store opens', async () => {
  const adapter = memoryAdapter();
  const first = await openKeystore(ALICE, adapter, () => 0);
  await first.setPending('pairing-1', bytes(7), 100);
  first.close();

  // Opened after the expiry and never asked for the secret.
  (await openKeystore(ALICE, adapter, () => 200)).close();

  // A clock from before the expiry finds nothing: the secret was deleted, not merely past its time.
  const later = await openKeystore(ALICE, adapter, () => 50);
  assert.equal(await later.pending('pairing-1'), null);
});

test('a forgotten store cannot be used', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());
  await store.forget();

  await assert.rejects(store.addHostKey('host-a', 1, bytes(1)), KeystoreClosedError);
  await assert.rejects(store.device(), /This key store is closed/);
});

test('a deletion another tab blocks fails with BlockedError', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  // The browser's deleteDatabase: the request is held while another connection stays open, and onblocked fires.
  let request = null;
  const previous = globalThis.indexedDB;
  globalThis.indexedDB = { deleteDatabase: () => (request = {}) };

  try {
    let settled = false;
    const deleting = indexedDbAdapter.deleteDatabase('enactive-keys-x');
    deleting.catch(() => {}).finally(() => { settled = true; });
    request.onblocked();

    t.mock.timers.tick(BLOCKED_AFTER_MS - 1);
    await Promise.resolve();
    assert.equal(settled, false);

    t.mock.timers.tick(1);
    await assert.rejects(deleting, (error) => error instanceof BlockedError
      && error.message === 'Close the other tabs of this site and try again.');
  } finally {
    globalThis.indexedDB = previous;
  }
});
