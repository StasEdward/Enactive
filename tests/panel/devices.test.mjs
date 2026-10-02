import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openKeystore, memoryAdapter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';
import { createWriter, createSendCache } from '../../src/Enactive.Remote.Gateway/wwwroot/js/writer.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { ad, openJson } from '../../src/Enactive.Remote.Gateway/wwwroot/js/sealed.js';
import {
  revokeDevice, forgetThisDevice, forgottenSentence, createRemovalWatch, storeGone, revocationWarning, cannotTell,
  revokeKey, cardActions, deleteDeviceKeys, toldUnder, toldAgainUnder, waitingForKey, NOT_CONFIRMED, MAX_RESENDS,
  KEYS_KEPT, KEY_NEVER_RECEIVED, GATEWAY_UNREACHED, KEY_WAIT_MS
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/devices.js';

const ALICE = '0123456789abcdef0123456789abcdef';
const PHONE = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1';
const TABLET = 'b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2';
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
    api, writer, sends: createSendCache(() => NOW), hosts, keystore: store, deviceId: PHONE
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

test('a removal says it was told under the computer\'s current key, and no more', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1], [LAPTOP]: [3] });
  const watch = createRemovalWatch();
  watch.record(PHONE, [
    { hostId: STUDIO, commandId: 'c-studio', epoch: 1 },
    { hostId: LAPTOP, commandId: 'c-laptop', epoch: 3 }
  ]);
  const hosts = [host(STUDIO, 'S', 1), host(LAPTOP, 'L', 3)];

  // What the panel can see: the command was sealed under the key the computer uses now. Whether the computer has
  // acted on it, and whose removal a later key change was for, nothing the panel receives says.
  assert.equal(await watch.step({
    hosts, keystore: store, api: fakeApi(), writer: spyWriter(store), sends: createSendCache(), now: () => NOW
  }), false);
  assert.deepEqual(watch.lines(PHONE, hosts),
    [{ hostId: STUDIO, status: toldUnder(1) }, { hostId: LAPTOP, status: toldUnder(3) }]);
  assert.equal(toldUnder(1), 'told under key 1');
  assert.ok(!/rotated/.test(watch.lines(PHONE, hosts).map(({ status }) => status).join(' ')));
});

test('a rotation by the computer itself has the removal told again under the new key', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1] });
  const sends = createSendCache(() => NOW);
  const api = fakeApi();
  const writer = spyWriter(store);
  const watch = createRemovalWatch();
  watch.record(PHONE, (await revokeDevice({
    api, writer, sends, hosts: [host(STUDIO, 'Studio PC', 1)], keystore: store, deviceId: PHONE
  })).sentTo);
  const [first] = commandsOf(api);
  api.calls.length = 0;
  const step = (keyEpoch) => watch.step({
    hosts: [host(STUDIO, 'Studio PC', keyEpoch)], keystore: store, api, writer, sends, now: () => NOW
  });

  // The desktop removed another device and moved to epoch 2 before running ours, and refused ours as sealed before
  // a device was removed: from here that looks exactly like our own removal done. Until this device holds epoch 2
  // nothing is sent - sealed under epoch 1 again, it would be refused again.
  assert.equal(await step(2), false);
  assert.deepEqual(watch.lines(PHONE, [host(STUDIO, 'Studio PC', 2)], NOW),
    [{ hostId: STUDIO, status: waitingForKey(2) }]);
  assert.equal(api.calls.length, 0);

  // Epoch 2 arrives: told again once, under it and a new command id.
  const newer = crypto.getRandomValues(new Uint8Array(32));
  await store.addHostKey(STUDIO, 2, newer);
  assert.equal(await step(2), true);
  assert.equal(await step(2), false);
  const [again] = commandsOf(api);
  assert.equal(api.calls.length, 1);
  assert.notEqual(again.body.commandId, first.body.commandId);
  const opened = await openJson(await hostKey(2, newer), again.body.sealed,
    ad.command(STUDIO, again.body.commandId, 'RevokeDevice'));
  assert.equal(opened.deviceId, PHONE);
  assert.deepEqual(watch.lines(PHONE, [host(STUDIO, 'Studio PC', 2)]), [{ hostId: STUDIO, status: toldAgainUnder(2) }]);
  assert.equal(toldAgainUnder(2), 'key changed - told again under key 2');
});

test('a removal that waits for a key this device never gets says what to do', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1] });
  const sends = createSendCache(() => NOW);
  const api = fakeApi();
  const writer = spyWriter(store);
  const watch = createRemovalWatch();
  watch.record(PHONE, (await revokeDevice({
    api, writer, sends, hosts: [host(STUDIO, 'Studio PC', 1)], keystore: store, deviceId: PHONE
  })).sentTo);
  const atKey2 = [host(STUDIO, 'Studio PC', 2)];
  const step = (at) => watch.step({ hosts: atKey2, keystore: store, api, writer, sends, now: () => at });

  // The computer moved to key 2, and its grant never reaches this device: waiting is said for a while, and then
  // what to do instead - the line used to wait for good.
  await step(NOW);
  await step(NOW + KEY_WAIT_MS - 1);
  assert.deepEqual(watch.lines(PHONE, atKey2, NOW + KEY_WAIT_MS - 1), [{ hostId: STUDIO, status: waitingForKey(2) }]);
  await step(NOW + KEY_WAIT_MS);
  assert.deepEqual(watch.lines(PHONE, atKey2, NOW + KEY_WAIT_MS), [{ hostId: STUDIO, status: KEY_NEVER_RECEIVED }]);
  assert.equal(KEY_NEVER_RECEIVED, "not confirmed - this device never received the computer's new key; do it from "
    + 'a device that holds its key, or from the computer');
});

test('a removal whose new command does not reach the gateway says so, and is sent on the next poll', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1, 2] });
  const sends = createSendCache(() => NOW);
  let reachable = false;
  const api = fakeApi({ refuse: () => reachable ? null : new Error('The gateway could not be reached.') });
  const writer = spyWriter(store);
  const watch = createRemovalWatch();
  watch.record(PHONE, [{ hostId: STUDIO, commandId: 'c-studio', epoch: 1 }]);
  const atKey2 = [host(STUDIO, 'Studio PC', 2)];
  const step = () => watch.step({ hosts: atKey2, keystore: store, api, writer, sends, now: () => NOW });

  // The key is held: it is not "waiting for key", it is the gateway that was not reached.
  assert.equal(await step(), true);
  assert.deepEqual(watch.lines(PHONE, atKey2, NOW), [{ hostId: STUDIO, status: GATEWAY_UNREACHED }]);
  assert.equal(GATEWAY_UNREACHED, 'could not reach the gateway - will try again');

  reachable = true;
  await step();
  assert.deepEqual(watch.lines(PHONE, atKey2, NOW), [{ hostId: STUDIO, status: toldAgainUnder(2) }]);
});

test('removals sealed under one key are all told again under the next', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1] });
  const sends = createSendCache(() => NOW);
  const api = fakeApi();
  const writer = spyWriter(store);
  const watch = createRemovalWatch();

  // The phone and then the tablet, both sealed under epoch 1 while the computer was asleep. It removed one, moved to
  // epoch 2 and granted it to the other, still trusted, then refused the other's command. Which one, nothing says.
  for (const deviceId of [PHONE, TABLET]) {
    watch.record(deviceId, (await revokeDevice({
      api, writer, sends, hosts: [host(STUDIO, 'Studio PC', 1)], keystore: store, deviceId
    })).sentTo);
  }
  // Told again with the same command, as "Tell the computers again" does: still the one command.
  watch.record(PHONE, (await revokeDevice({
    api, writer, sends, hosts: [host(STUDIO, 'Studio PC', 1)], keystore: store, deviceId: PHONE
  })).sentTo);
  api.calls.length = 0;

  const newer = crypto.getRandomValues(new Uint8Array(32));
  await store.addHostKey(STUDIO, 2, newer);
  await watch.step({ hosts: [host(STUDIO, 'Studio PC', 2)], keystore: store, api, writer, sends, now: () => NOW });

  // Both are told again under epoch 2; the computer returns without a key change for the one it removed already.
  const told = await Promise.all(commandsOf(api).map(async ({ body }) => (await openJson(await hostKey(2, newer),
    body.sealed, ad.command(STUDIO, body.commandId, 'RevokeDevice'))).deviceId));
  assert.deepEqual(told.sort(), [PHONE, TABLET].sort());
  for (const deviceId of [PHONE, TABLET]) {
    assert.deepEqual(watch.lines(deviceId, [host(STUDIO, 'Studio PC', 2)]),
      [{ hostId: STUDIO, status: toldAgainUnder(2) }]);
  }
});

test('a removal told again after every key change is not confirmed after the last', async () => {
  const { store } = await storeHolding({ [STUDIO]: [1] });
  const sends = createSendCache(() => NOW);
  const api = fakeApi();
  const writer = spyWriter(store);
  const watch = createRemovalWatch();
  watch.record(PHONE, (await revokeDevice({
    api, writer, sends, hosts: [host(STUDIO, 'Studio PC', 1)], keystore: store, deviceId: PHONE
  })).sentTo);
  api.calls.length = 0;

  // Each key change is told again, at most MAX_RESENDS times: a computer that goes on changing keys - other
  // removals, from other tabs - is not told for ever.
  for (let epoch = 2; epoch <= 2 + MAX_RESENDS; epoch += 1) {
    await store.addHostKey(STUDIO, epoch, crypto.getRandomValues(new Uint8Array(32)));
    await watch.step({
      hosts: [host(STUDIO, 'Studio PC', epoch)], keystore: store, api, writer, sends, now: () => NOW
    });
  }

  assert.equal(commandsOf(api).length, MAX_RESENDS);
  assert.deepEqual(watch.lines(PHONE, [host(STUDIO, 'Studio PC', 2 + MAX_RESENDS)]),
    [{ hostId: STUDIO, status: NOT_CONFIRMED }]);
  assert.equal(NOT_CONFIRMED, 'not confirmed - tell the computers again');
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

  // Another tab's forget() closes this tab's connection, and the browser then answers InvalidStateError.
  const closedUnder = new Error('The database connection is closing.');
  closedUnder.name = 'InvalidStateError';
  assert.equal(await storeGone({ device: async () => { throw closedUnder; } }), true);

  // Any other failure to read is not a forgotten store: the page would say this device was removed when it was not.
  assert.equal(await storeGone({ device: async () => { throw new Error('UnknownError'); } }), false);
});

test('a key store that cannot be deleted still signs out, and says the keys stay', async () => {
  const adapter = memoryAdapter();
  const broken = { ...adapter, deleteDatabase: async () => { throw new Error('UnknownError'); } };
  const store = await openKeystore(ALICE, broken, () => NOW);
  await store.addHostKey(STUDIO, 1, crypto.getRandomValues(new Uint8Array(32)));
  await store.createDevice();
  await store.setDeviceId(PHONE);
  let told = null;

  const result = await forgetThisDevice({
    api: fakeApi(), writer: spyWriter(store), sends: createSendCache(), keystore: store,
    hosts: [host(STUDIO, 'Studio PC', 1)],
    signOut: async (answer) => { told = answer; }
  });

  // Removed everywhere, and signed out: what is left is for the person to clear, and they are told how.
  assert.equal(told, result);
  assert.equal(result.keysKept, true);
  assert.equal(KEYS_KEPT,
    "The keys could not be deleted from this browser; clear this site's data in the browser settings.");
  assert.ok(forgottenSentence(result).includes(KEYS_KEPT));
  assert.ok(!forgottenSentence(result).includes('its keys are deleted'));
});

test('the removed page deletes this device\'s keys for the signed-in account', async () => {
  const adapter = memoryAdapter();
  const store = await openKeystore(ALICE, adapter);
  await store.createDevice();
  await store.addHostKey(STUDIO, 1, crypto.getRandomValues(new Uint8Array(32)));
  store.close();

  await deleteDeviceKeys({ authenticated: true, user: { id: ALICE } }, (userId) => openKeystore(userId, adapter));

  assert.deepEqual(adapter.databases(), []);
  await assert.rejects(deleteDeviceKeys({ authenticated: false }, (userId) => openKeystore(userId, adapter)),
    /Sign in again/);
});

test('a device card offers what can be done to that device', () => {
  const none = new Set();
  assert.deepEqual(cardActions({ id: PHONE, revoked: false }, PHONE, none), [{ kind: 'forget', busy: false }]);
  assert.deepEqual(cardActions({ id: TABLET, revoked: false }, PHONE, none), [{ kind: 'remove', busy: false }]);
  // A removed device can always be told about again - after a reload, or from another device, a computer that
  // missed it is otherwise never told - and the gateway and every computer take it again as nothing new.
  assert.deepEqual(cardActions({ id: TABLET, revoked: true }, PHONE, none), [{ kind: 'tell-again', busy: false }]);
  // A removal in flight stays disabled through every redraw until it ends.
  assert.deepEqual(cardActions({ id: TABLET, revoked: false }, PHONE, new Set([TABLET])),
    [{ kind: 'remove', busy: true }]);
  assert.deepEqual(cardActions({ id: PHONE, revoked: true }, PHONE, none), []);
});

test('the removal is said plainly', () => {
  assert.equal(revocationWarning('Phone'),
    'Phone can still read what it has already opened. It will not read anything new.');
});
