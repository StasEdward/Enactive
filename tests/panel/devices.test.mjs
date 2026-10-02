import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openKeystore, memoryAdapter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';
import { createWriter, createSendCache } from '../../src/Enactive.Remote.Gateway/wwwroot/js/writer.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { ad, openJson } from '../../src/Enactive.Remote.Gateway/wwwroot/js/sealed.js';
import {
  revokeDevice, forgetThisDevice, forgottenSentence, rotationWatch, storeGone, revocationWarning, cannotTell, revokeKey,
  SENT, ROTATED, NOT_ROTATED_YET, ROTATION_WATCH_MS
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/devices.js';

const ALICE = '0123456789abcdef0123456789abcdef';
const PHONE = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1';
const STUDIO = 'd4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4';
const LAPTOP = 'e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5';
const NAS = 'f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6';
const OLD = 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3';
const NOW = Date.UTC(2026, 9, 2, 12, 0, 0);

const host = (id, label, keyEpoch, revoked = false) => ({ id, label, keyEpoch, revoked, workspaces: [] });

/** A key store holding `epochsByHost`, and each computer's key per epoch, to open what was sealed for it. */
async function storeHolding(epochsByHost) {
  const store = await openKeystore(ALICE, memoryAdapter(), () => NOW);
  const computers = new Map();
  for (const [hostId, epochs] of Object.entries(epochsByHost)) {
    for (const epoch of epochs) {
      const secret = crypto.getRandomValues(new Uint8Array(32));
      await store.addHostKey(hostId, epoch, secret);
      computers.set(`${hostId}:${epoch}`, await hostKey(epoch, secret));
    }
  }
  return { store, computers };
}

/** The gateway: every call in order, and a refusal for the paths `refuse` names. */
function fakeApi({ refuse = () => null } = {}) {
  const calls = [];
  return {
    calls,
    async post(path, body) {
      calls.push({ path, body });
      const refusal = refuse(path, calls);
      if (refusal) throw refusal;
      return {};
    }
  };
}

/** The real writer, with every seal it is asked for counted. */
function spyWriter(store) {
  const real = createWriter(store, () => NOW);
  const seals = [];
  return {
    seals,
    async sealRevocation(hostId, commandId, deviceId) {
      seals.push({ hostId, commandId, deviceId });
      return real.sealRevocation(hostId, commandId, deviceId);
    }
  };
}

const commandsOf = (api) => api.calls.filter((call) => call.path.endsWith('/device-commands'));

test('revocation sends one sealed command per computer', async () => {
  const { store, computers } = await storeHolding({ [STUDIO]: [1], [LAPTOP]: [3], [NAS]: [2] });
  const api = fakeApi();
  const writer = spyWriter(store);
  const hosts = [host(STUDIO, 'Studio PC', 1), host(LAPTOP, 'Laptop', 3), host(NAS, 'NAS', 2)];

  const result = await revokeDevice({
    api, writer, sends: createSendCache(() => NOW), hosts, keystore: store, deviceId: PHONE, now: () => NOW
  });

  // The gateway first: from then on it refuses the device and has deleted its grants.
  assert.equal(api.calls[0].path, `/api/devices/${PHONE}/revoke`);
  const commands = commandsOf(api);
  assert.equal(api.calls.length, 4);
  assert.deepEqual(commands.map((call) => call.path),
    [STUDIO, LAPTOP, NAS].map((id) => `/api/hosts/${id}/device-commands`));
  assert.equal(new Set(commands.map((call) => call.body.commandId)).size, 3);

  for (const [index, hostId] of [STUDIO, LAPTOP, NAS].entries()) {
    const { body } = commands[index];
    assert.equal(body.kind, 'RevokeDevice');
    // Each opens on its own computer, under that command's id and kind, and names the device removed.
    const epoch = hosts[index].keyEpoch;
    const opened = await openJson(computers.get(`${hostId}:${epoch}`), body.sealed,
      ad.command(hostId, body.commandId, 'RevokeDevice'));
    assert.equal(opened.deviceId, PHONE);
  }

  assert.deepEqual(result.sentTo.map(({ hostId, epoch }) => [hostId, epoch]), [[STUDIO, 1], [LAPTOP, 3], [NAS, 2]]);
  assert.deepEqual(result.sentTo.map(({ commandId }) => commandId), commands.map((call) => call.body.commandId));
  assert.deepEqual(result.skipped, []);
  assert.deepEqual(result.failed, []);
});

test('a computer whose key this device does not hold is skipped with a reason', async () => {
  // Studio is held; the laptop moved to epoch 4 while this device holds 3; the NAS was never paired here; the
  // old computer is revoked and is not told anything.
  const { store } = await storeHolding({ [STUDIO]: [1], [LAPTOP]: [3] });
  const api = fakeApi();
  const hosts = [host(STUDIO, 'Studio PC', 1), host(LAPTOP, 'Laptop', 4), host(NAS, 'NAS', 2), host(OLD, 'Old PC', 1, true)];

  const result = await revokeDevice({
    api, writer: spyWriter(store), sends: createSendCache(() => NOW), hosts, keystore: store, deviceId: PHONE
  });

  assert.deepEqual(commandsOf(api).map((call) => call.path), [`/api/hosts/${STUDIO}/device-commands`]);
  assert.deepEqual(result.sentTo.map(({ hostId }) => hostId), [STUDIO]);
  assert.deepEqual(result.skipped, [
    { hostId: LAPTOP, reason: cannotTell('Laptop') },
    { hostId: NAS, reason: cannotTell('NAS') }
  ]);
  assert.match(cannotTell('NAS'), /^Cannot tell NAS from this device - do it from a device that holds its key, or from the computer/);
});

test('a retry reuses the same command ids and envelopes', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1], [LAPTOP]: [3] });
  const sends = createSendCache(() => NOW);
  const hosts = [host(STUDIO, 'Studio PC', 1), host(LAPTOP, 'Laptop', 3)];

  // The laptop's command was not taken the first time: one computer out of reach does not keep the other from
  // being told, and is said.
  const first = fakeApi({
    refuse: (path) => path === `/api/hosts/${LAPTOP}/device-commands` ? new Error('The gateway could not be reached.') : null
  });
  const tried = await revokeDevice({ api: first, writer: spyWriter(store), sends, hosts, keystore: store, deviceId: PHONE });
  assert.deepEqual(tried.sentTo.map(({ hostId }) => hostId), [STUDIO]);
  assert.deepEqual(tried.failed, [{ hostId: LAPTOP, reason: 'Laptop: The gateway could not be reached.' }]);

  const again = fakeApi();
  const writer = spyWriter(store);
  const retried = await revokeDevice({ api: again, writer, sends, hosts, keystore: store, deviceId: PHONE });

  // The gateway takes the same id with the same envelope as the same command; a fresh seal under the same id
  // would be refused as a different one, and a new id would queue the removal twice.
  assert.deepEqual(commandsOf(again).map((call) => call.body), commandsOf(first).map((call) => call.body));
  assert.equal(writer.seals.length, 0);
  assert.deepEqual(retried.sentTo.map(({ hostId }) => hostId), [STUDIO, LAPTOP]);
  assert.equal(again.calls[0].path, `/api/devices/${PHONE}/revoke`);
  assert.equal(revokeKey(PHONE, STUDIO), `revoke:${PHONE}:${STUDIO}`);
});

test('a refused gateway revocation tells no computer', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1] });
  const api = fakeApi({ refuse: (path) => path.startsWith('/api/devices/') ? new Error('No such device.') : null });

  await assert.rejects(revokeDevice({
    api, writer: spyWriter(store), sends: createSendCache(), hosts: [host(STUDIO, 'Studio PC', 1)], keystore: store,
    deviceId: PHONE
  }), /No such device/);
  assert.equal(commandsOf(api).length, 0);
});

test('rotation is reported once the computer\'s epoch rises', () => {
  const sent = [
    { hostId: STUDIO, epoch: 1, at: NOW },
    { hostId: LAPTOP, epoch: 3, at: NOW },
    { hostId: NAS, epoch: 2, at: NOW }
  ];

  // Nothing moved yet.
  assert.deepEqual(rotationWatch(sent, [host(STUDIO, 'S', 1), host(LAPTOP, 'L', 3), host(NAS, 'N', 2)], NOW + 1000),
    [{ hostId: STUDIO, status: SENT }, { hostId: LAPTOP, status: SENT }, { hostId: NAS, status: SENT }]);

  // Studio rotated; the laptop is still at the epoch the command was sealed under.
  assert.deepEqual(rotationWatch(sent, [host(STUDIO, 'S', 2), host(LAPTOP, 'L', 3), host(NAS, 'N', 2)], NOW + 5000),
    [{ hostId: STUDIO, status: ROTATED }, { hostId: LAPTOP, status: SENT }, { hostId: NAS, status: SENT }]);

  // Ten minutes on, a computer that has not moved is no longer watched for; one that rotated stays rotated, and
  // one missing from the snapshot is not called rotated.
  assert.deepEqual(rotationWatch(sent, [host(STUDIO, 'S', 2), host(LAPTOP, 'L', 3)], NOW + ROTATION_WATCH_MS),
    [{ hostId: STUDIO, status: ROTATED }, { hostId: LAPTOP, status: NOT_ROTATED_YET }, { hostId: NAS, status: NOT_ROTATED_YET }]);
});

test('forgetting this device revokes it everywhere, forgets the store and signs out', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1], [LAPTOP]: [3] });
  await store.createDevice();
  await store.setDeviceId(PHONE);
  const order = [];
  let told = null;
  const api = fakeApi();
  const recorded = {
    post: async (path, body) => { order.push(`post ${path}`); return api.post(path, body); }
  };
  const keystore = new Proxy(store, {
    get: (target, name) => name === 'forget'
      ? async () => { order.push('forget'); await target.forget(); }
      : target[name]
  });

  const result = await forgetThisDevice({
    api: recorded, writer: spyWriter(store), sends: createSendCache(), keystore,
    hosts: [host(STUDIO, 'Studio PC', 1), host(LAPTOP, 'Laptop', 3), host(NAS, 'NAS', 2)],
    signOut: async (answer) => { order.push('sign out'); told = answer; }
  });

  assert.deepEqual(order, [
    `post /api/devices/${PHONE}/revoke`,
    `post /api/hosts/${STUDIO}/device-commands`,
    `post /api/hosts/${LAPTOP}/device-commands`,
    'forget',
    'sign out'
  ]);
  assert.deepEqual(result.sentTo.map(({ hostId }) => hostId), [STUDIO, LAPTOP]);
  assert.equal(await storeGone(store), true);
  // A computer this device was never paired with cannot be told from here, and the sign-in page says so.
  assert.equal(told, result);
  assert.equal(forgottenSentence(result), 'This device was forgotten: its keys are deleted from this browser, and it '
    + 'will not read anything new. ' + cannotTell('NAS'));
});

test('a computer not reached keeps this device\'s keys, so forgetting can be pressed again', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1] });
  await store.createDevice();
  await store.setDeviceId(PHONE);
  let signedOut = false;
  const api = fakeApi({ refuse: (path) => path.endsWith('/device-commands') ? new Error('The gateway could not be reached.') : null });

  await assert.rejects(forgetThisDevice({
    api, writer: spyWriter(store), sends: createSendCache(), keystore: store, hosts: [host(STUDIO, 'Studio PC', 1)],
    signOut: async () => { signedOut = true; }
  }), /Studio PC: The gateway could not be reached/);

  assert.equal(signedOut, false);
  assert.equal(await storeGone(store), false);
  assert.equal((await store.hostKeys(STUDIO)).size, 1);
});

test('a key store forgotten or closed is gone; an open one is not', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1] });
  assert.equal(await storeGone(store), false);
  store.close();
  assert.equal(await storeGone(store), true);
  assert.equal(await storeGone(null), true);
});

test('the removal is said plainly', () => {
  assert.equal(revocationWarning('Phone'),
    'Phone can still read what it has already opened. It will not read anything new.');
});
