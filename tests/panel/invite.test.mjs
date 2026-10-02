import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openKeystore, memoryAdapter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';
import { openGrant } from '../../src/Enactive.Remote.Gateway/wwwroot/js/grants.js';
import { derivePairKey, enrollmentMac, verifyEnrollment } from '../../src/Enactive.Remote.Gateway/wwwroot/js/pairing.js';
import { invitePendingId, collectGrants } from '../../src/Enactive.Remote.Gateway/wwwroot/js/trust.js';
import { createWriter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/writer.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { ad, openJson } from '../../src/Enactive.Remote.Gateway/wwwroot/js/sealed.js';
import { b64url, fromB64url } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import {
  openInviteLink, keepInvite, takeKeptInvite, newInviteId, enrollThisDevice, inviteJoinStep, countInviteGrants,
  readableHosts, behindHosts, behindReason, behindWarning, answerEnrollment, newAnswerProgress, createInviteWatch,
  inviteQrSvg, INVITE_LIFETIME_MS, GRANTS_PER_CALL, SWAPPED_KEY
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/invite.js';
import { vectors } from './vectors.mjs';

const ALICE = '0123456789abcdef0123456789abcdef';
const INVITER = 'a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1';
const NEWCOMER = 'b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2';
const INVITE = 'c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3c3';
const STUDIO = 'd4d4d4d4d4d4d4d4d4d4d4d4d4d4d4d4';
const LAPTOP = 'e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5e5';
const NOW = Date.UTC(2026, 9, 2, 12, 0, 0);
const ECDH = { name: 'ECDH', namedCurve: 'P-256' };
const ECDSA = { name: 'ECDSA', namedCurve: 'P-256' };

const secret = fromB64url(vectors.inviteLink.secret);
const pairKey = await derivePairKey(secret);

function fakeHistory() {
  return { calls: [], replaceState(...args) { this.calls.push(args); } };
}

function fakeStorage() {
  const items = new Map();
  return {
    getItem: (key) => items.get(key) ?? null,
    setItem: (key, value) => { items.set(key, String(value)); },
    removeItem: (key) => { items.delete(key); },
    items
  };
}

const signingPublic = async () => new Uint8Array(await crypto.subtle.exportKey('raw',
  (await crypto.subtle.generateKey(ECDSA, true, ['sign', 'verify'])).publicKey));

/**
 * The inviting browser's key store: for each computer, a key per epoch and the signing key pinned for it, as
 * collectGrants leaves them. Returns the store and what it holds, to compare the new device's keys with.
 */
async function inviterStore(epochsByHost) {
  const store = await openKeystore(ALICE, memoryAdapter(), () => NOW);
  const held = new Map();
  for (const [hostId, epochs] of Object.entries(epochsByHost)) {
    const signing = await signingPublic();
    await store.pinHostSigningKey(hostId, signing);
    const keys = new Map();
    for (const epoch of epochs) {
      const key = crypto.getRandomValues(new Uint8Array(32));
      await store.addHostKey(hostId, epoch, key);
      keys.set(epoch, key);
    }
    held.set(hostId, { signing, keys });
  }
  return { store, held };
}

/** The new device: its key pair, and the enrollment the gateway relays for it (the MAC over its own key). */
async function newcomer(label = 'Safari on iPhone') {
  const pair = await crypto.subtle.generateKey(ECDH, false, ['deriveBits']);
  const publicRaw = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey));
  const enrollment = {
    deviceId: NEWCOMER, publicKey: b64url(publicRaw), label,
    mac: await enrollmentMac(pairKey, INVITE, NEWCOMER, publicRaw)
  };
  return { privateKey: pair.privateKey, publicRaw, enrollment };
}

/**
 * A gateway that takes grants and device commands and records them. Like the real one it refuses, with a
 * 409 and the whole call, a grant for a (computer, device, epoch) it already holds.
 */
function gateway({ afterGrants = async () => {}, failGrantCall = () => false } = {}) {
  const stored = new Set();
  const grants = [];
  const api = {
    grantCalls: [],
    commands: [],
    // GET /api/grants for the new device: what is stored so far, filed under each computer.
    async get(path) {
      if (path !== '/api/grants') throw new Error(`Unexpected GET ${path}`);
      const byHost = new Map();
      for (const grant of grants) byHost.set(grant.hostId, [...(byHost.get(grant.hostId) ?? []), grant]);
      return [...byHost].map(([hostId, list]) => ({ hostId, keyEpoch: Math.max(...list.map((one) => one.epoch)), grants: list }));
    },
    async post(path, body, options) {
      if (path === '/api/grants') {
        api.grantCalls.push({ body, options });
        if (failGrantCall(api.grantCalls.length)) throw new TypeError('Failed to fetch');
        const keys = body.map((grant) => `${grant.hostId}|${grant.deviceId}|${grant.epoch}`);
        if (keys.some((key) => stored.has(key))) {
          throw Object.assign(new Error('That device already holds a grant of this key.'), { code: 'conflict', status: 409 });
        }
        keys.forEach((key) => stored.add(key));
        grants.push(...structuredClone(body));
        await afterGrants(api.grantCalls.length);
        return null;
      }
      const command = /^\/api\/hosts\/([^/]+)\/device-commands$/.exec(path);
      if (command) {
        api.commands.push({ hostId: command[1], body });
        return {};
      }
      throw new Error(`Unexpected POST ${path}`);
    },
    stored
  };
  return api;
}

const hostsOf = (...ids) => ids.map((id) => ({ id, label: id, keyEpoch: 1, revoked: false }));

// ── the new device ──────────────────────────────────────────────────────────

test('a fragment is parsed and the secret is not kept in the url', () => {
  const history = fakeHistory();

  const invite = openInviteLink('#' + vectors.inviteLink.text.split('#')[1], history, NOW);

  assert.deepEqual(history.calls, [[null, '', '/pair']]);
  assert.equal(invite.inviteId, vectors.inviteLink.inviteId);
  assert.equal(b64url(invite.secret), vectors.inviteLink.secret);
  // No invitation lives longer than ten minutes, so neither does what this page keeps of one.
  assert.equal(invite.expiresAt, NOW + INVITE_LIFETIME_MS);

  // Taken out of the address before it is read, so a link too damaged to use does not stay there either: its
  // secret may be whole when the rest is not.
  const damaged = fakeHistory();
  assert.throws(() => openInviteLink(`#v=2&i=${INVITE}&p=too-short`, damaged), TypeError);
  assert.deepEqual(damaged.calls, [[null, '', '/pair']]);
});

test('an invitation kept across a sign-in is read back once, and a storage that fails keeps nothing', () => {
  const storage = fakeStorage();
  const expiresAt = NOW + INVITE_LIFETIME_MS;

  keepInvite(storage, { inviteId: INVITE, secret, expiresAt });
  const kept = takeKeptInvite(storage, NOW);

  assert.equal(kept.inviteId, INVITE);
  assert.deepEqual(kept.secret, secret);
  assert.equal(kept.expiresAt, expiresAt);
  assert.equal(takeKeptInvite(storage, NOW), null);
  assert.equal(storage.items.size, 0);

  const broken = { getItem() { throw new Error('denied'); }, setItem() { throw new Error('denied'); }, removeItem() {} };
  assert.doesNotThrow(() => keepInvite(broken, { inviteId: INVITE, secret, expiresAt }));
  assert.equal(takeKeptInvite(broken, NOW), null);
  assert.equal(takeKeptInvite(null, NOW), null);
  storage.setItem('enactive.invite', '{"inviteId":5}');
  assert.equal(takeKeptInvite(storage, NOW), null);
});

test('a kept invitation past its ten minutes is thrown away, secret and all', () => {
  const storage = fakeStorage();
  keepInvite(storage, { inviteId: INVITE, secret, expiresAt: NOW + INVITE_LIFETIME_MS });

  assert.equal(takeKeptInvite(storage, NOW + INVITE_LIFETIME_MS), null);
  assert.equal(storage.items.size, 0);

  // One kept with no expiry is as stale as any: its age cannot be told.
  storage.setItem('enactive.invite', JSON.stringify({ inviteId: INVITE, secret: b64url(secret) }));
  assert.equal(takeKeptInvite(storage, NOW), null);
});

test('the new device enrolls with a mac over its own key and keeps the secret under the invitation for ten minutes', async () => {
  const store = await openKeystore(ALICE, memoryAdapter(), () => NOW);
  await store.createDevice();
  await store.setDeviceId(NEWCOMER);
  const posts = [];
  const api = { async post(path, body) { posts.push({ path, body }); return null; } };

  const deadline = await enrollThisDevice({ keystore: store, api, deviceId: NEWCOMER, inviteId: INVITE, secret, now: NOW });

  assert.equal(deadline, NOW + INVITE_LIFETIME_MS);
  assert.equal(INVITE_LIFETIME_MS, 10 * 60 * 1000);
  assert.deepEqual(await store.pending(invitePendingId(INVITE)), secret);
  assert.equal(posts.length, 1);
  assert.equal(posts[0].path, '/api/enrollments');
  assert.deepEqual(Object.keys(posts[0].body).sort(), ['deviceId', 'inviteId', 'mac']);
  assert.equal(posts[0].body.inviteId, INVITE);
  assert.equal(posts[0].body.deviceId, NEWCOMER);
  assert.ok(await verifyEnrollment(pairKey, INVITE, NEWCOMER, (await store.device()).publicRaw, posts[0].body.mac));
});

test('the new device asks for its grants for as long as the secret is good, after a first delivery too', async () => {
  let clock = NOW;
  const store = await openKeystore(ALICE, memoryAdapter(), () => clock);
  const deadline = NOW + INVITE_LIFETIME_MS;
  await store.setPending(invitePendingId(INVITE), secret, deadline);
  let collected = 0;
  const collect = async () => { collected += 1; };

  assert.equal(await inviteJoinStep({ keystore: store, inviteId: INVITE, deadline, now: clock, collect }), 'collecting');
  // Keys arrived meanwhile, and the secret is still there: the inviter may have more calls of grants to send.
  clock = deadline - 1;
  assert.equal(await inviteJoinStep({ keystore: store, inviteId: INVITE, deadline, now: clock, collect }), 'collecting');
  assert.equal(collected, 2);

  clock = deadline;
  assert.equal(await inviteJoinStep({ keystore: store, inviteId: INVITE, deadline, now: clock, collect }), 'expired');
  assert.equal(collected, 2);
});

test('only grants of this invitation count as its answer', () => {
  const result = {
    added: [
      { hostId: STUDIO, epoch: 1, authBy: `pair:${INVITE}` }, { hostId: STUDIO, epoch: 2, authBy: `pair:${INVITE}` },
      { hostId: LAPTOP, epoch: 4, authBy: 'host' }, { hostId: LAPTOP, epoch: 1, authBy: 'pair:f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6' }
    ],
    rejected: []
  };

  assert.equal(countInviteGrants(result, INVITE), 2);
  assert.equal(countInviteGrants(null, INVITE), 0);
});

test('the new device names as readable only the computers whose current key it holds', async () => {
  const { store } = await inviterStore({ [STUDIO]: [1, 2], [LAPTOP]: [1] });
  const hosts = [
    { id: STUDIO, label: 'Studio PC', keyEpoch: 2, revoked: false },
    // Behind: what this computer sends now is sealed under epoch 3, which this device does not hold.
    { id: LAPTOP, label: 'Laptop', keyEpoch: 3, revoked: false },
    { id: 'f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7', label: 'Never paired', keyEpoch: 1, revoked: false }
  ];

  assert.deepEqual((await readableHosts(store, hosts)).map((host) => host.label), ['Studio PC']);
  assert.deepEqual(await readableHosts(store, [{ ...hosts[0], revoked: true }]), []);
  assert.deepEqual(await readableHosts(null, hosts), []);
});

// ── the inviting device ─────────────────────────────────────────────────────

test('an enrollment with a swapped key is refused and nothing is granted', async () => {
  const { store } = await inviterStore({ [STUDIO]: [1, 2] });
  const device = await newcomer();
  // The gateway puts a key of its own in the new device's place, keeping the device's MAC.
  const swapped = await newcomer();
  const api = gateway();

  const result = await answerEnrollment({
    keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER, hosts: hostsOf(STUDIO),
    pairKey, inviteId: INVITE, enrollment: { ...device.enrollment, publicKey: swapped.enrollment.publicKey }
  });

  assert.equal(result.refused, true);
  assert.equal(result.granted, 0);
  assert.deepEqual(result.endorsed, []);
  assert.equal(api.grantCalls.length, 0);
  assert.equal(api.commands.length, 0);
  assert.match(SWAPPED_KEY, /^Someone other than your new device answered this invitation\. Nothing was shared\.$/);

  // Nor a key that is not one, or a MAC from another invitation.
  for (const enrollment of [{ ...device.enrollment, publicKey: 'not a key' }, { ...device.enrollment, publicKey: undefined }]) {
    const refused = await answerEnrollment({
      keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER, hosts: hostsOf(STUDIO),
      pairKey, inviteId: INVITE, enrollment
    });
    assert.equal(refused.refused, true);
  }
  const otherInvite = await answerEnrollment({
    keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER, hosts: hostsOf(STUDIO),
    pairKey, inviteId: 'f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6f6', enrollment: device.enrollment
  });
  assert.equal(otherInvite.refused, true);
  assert.equal(api.grantCalls.length + api.commands.length, 0);
});

test('every held epoch of every host is granted and each computer endorsed', async () => {
  const { store, held } = await inviterStore({ [STUDIO]: [1, 2, 3], [LAPTOP]: [1] });
  const device = await newcomer('Safari on iPhone');
  const api = gateway();

  const result = await answerEnrollment({
    keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER,
    hosts: [{ id: STUDIO, label: 'Studio PC', keyEpoch: 3, revoked: false }, { id: LAPTOP, label: 'Laptop', keyEpoch: 1, revoked: false }],
    pairKey, inviteId: INVITE, enrollment: device.enrollment
  });

  assert.equal(result.refused, undefined);
  assert.equal(result.granted, 4);
  assert.deepEqual(result.endorsed.sort(), [LAPTOP, STUDIO].sort());
  assert.deepEqual(result.notEndorsed, []);

  // Made as this device: the gateway takes grants only from a live device of the account.
  assert.ok(api.grantCalls.every((call) => call.options.headers['X-Enactive-Device'] === INVITER));
  const grants = api.grantCalls.flatMap((call) => call.body);
  assert.deepEqual(grants.map((grant) => `${grant.hostId}:${grant.epoch}`).sort(),
    [`${LAPTOP}:1`, `${STUDIO}:1`, `${STUDIO}:2`, `${STUDIO}:3`].sort());

  // Each opens for the new device, under the pair key, holding the very key this device holds, and names the
  // signing key this device pinned, so the new device pins the same one.
  for (const grant of grants) {
    assert.equal(grant.deviceId, NEWCOMER);
    assert.equal(grant.authBy, `pair:${INVITE}`);
    const opened = await openGrant(grant, device.privateKey, device.publicRaw, { pairKey });
    assert.deepEqual(opened.key.secret, held.get(grant.hostId).keys.get(grant.epoch));
    assert.deepEqual(opened.hostSigningPublic, held.get(grant.hostId).signing);
  }

  // One endorsement per computer, sealed under its newest key, naming the device and the key the MAC vouched for.
  assert.deepEqual(api.commands.map((command) => command.hostId).sort(), [LAPTOP, STUDIO].sort());
  for (const { hostId, body } of api.commands) {
    assert.equal(body.kind, 'EndorseDevice');
    const keys = held.get(hostId).keys;
    const newest = Math.max(...keys.keys());
    const key = await hostKey(newest, keys.get(newest));
    const endorsement = await openJson(key, body.sealed, ad.command(hostId, body.commandId, 'EndorseDevice'));
    assert.deepEqual(endorsement, {
      deviceId: NEWCOMER, devicePublic: b64url(device.publicRaw), label: 'Safari on iPhone', issuedAt: new Date(NOW).toISOString()
    });
  }
  assert.notEqual(api.commands[0].body.commandId, api.commands[1].body.commandId);
});

test('a computer that is revoked or no longer listed is not granted, and one this device is behind on is not endorsed', async () => {
  const gone = 'f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7';
  const { store } = await inviterStore({ [STUDIO]: [1], [LAPTOP]: [1, 2], [gone]: [1] });
  const device = await newcomer();
  const api = gateway();

  const result = await answerEnrollment({
    keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER,
    hosts: [
      { id: STUDIO, label: 'Studio PC', keyEpoch: 1, revoked: true },
      // Moved to epoch 3, whose grant has not reached this device: the computer would refuse an endorsement sealed
      // under epoch 2, so none is sent - but the keys this device holds are still worth having.
      { id: LAPTOP, label: 'Laptop', keyEpoch: 3, revoked: false }
    ],
    pairKey, inviteId: INVITE, enrollment: device.enrollment
  });

  const grants = api.grantCalls.flatMap((call) => call.body);
  assert.deepEqual(grants.map((grant) => `${grant.hostId}:${grant.epoch}`).sort(), [`${LAPTOP}:1`, `${LAPTOP}:2`]);
  assert.equal(result.granted, 2);
  assert.deepEqual(result.endorsed, []);
  assert.deepEqual(result.notEndorsed, [{ hostId: LAPTOP, reason: behindReason('Laptop') }]);
  assert.equal(behindReason('Laptop'), 'Laptop: this device does not hold its newest key; add the new device from that '
    + 'computer, or again from here once this device has caught up.');
  assert.equal(api.commands.length, 0);
});

test('the inviter is told before the link is shown which computers it is behind on', async () => {
  const { store } = await inviterStore({ [STUDIO]: [1, 2], [LAPTOP]: [1] });
  const hosts = [
    { id: STUDIO, label: 'Studio PC', keyEpoch: 2, revoked: false },
    { id: LAPTOP, label: 'Laptop', keyEpoch: 2, revoked: false },
    { id: 'f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7f7', label: 'Old PC', keyEpoch: 9, revoked: true }
  ];

  const behind = await behindHosts(store, hosts);

  assert.deepEqual(behind.map((host) => host.label), ['Laptop']);
  // What answerEnrollment does for such a computer (the test above): every key this device holds of it is shared,
  // the computer's current one is not, and the computer is not asked to trust the new device. Said before as "keys
  // for Laptop will not be shared", it told the person the older keys were kept back when they were not.
  assert.equal(behindWarning(behind), 'The current key of Laptop will not be shared, and those computers will not be '
    + 'asked to trust the new device, until this device catches up; older keys are shared.');
  assert.equal(behindWarning([{ label: 'A' }, { label: 'B' }]), 'The current key of A, B will not be shared, and those '
    + 'computers will not be asked to trust the new device, until this device catches up; older keys are shared.');
  assert.equal(behindWarning([]), '');
});

test('grants are chunked by 50', async () => {
  const many = Array.from({ length: 70 }, (_, i) => i + 1);
  const some = Array.from({ length: 40 }, (_, i) => i + 1);
  const { store } = await inviterStore({ [STUDIO]: many, [LAPTOP]: some });
  const device = await newcomer();
  const api = gateway();

  const result = await answerEnrollment({
    keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER,
    hosts: [{ id: STUDIO, label: 'Studio PC', keyEpoch: 70, revoked: false }, { id: LAPTOP, label: 'Laptop', keyEpoch: 40, revoked: false }],
    pairKey, inviteId: INVITE, enrollment: device.enrollment
  });

  assert.equal(GRANTS_PER_CALL, 50);
  // One computer per call, oldest first: each call only extends that computer's run of keys upwards, so a new
  // device that takes one call before the next never holds a newer key than one still to come.
  assert.deepEqual(api.grantCalls.map((call) => call.body.length), [50, 20, 40]);
  assert.equal(result.granted, 110);
  for (const call of api.grantCalls) {
    assert.equal(new Set(call.body.map((grant) => grant.hostId)).size, 1);
  }
  const epochs = (hostId) => api.grantCalls.flatMap((call) => call.body).filter((grant) => grant.hostId === hostId)
    .map((grant) => grant.epoch);
  assert.deepEqual(epochs(STUDIO), many);
  assert.deepEqual(epochs(LAPTOP), some);
});

test('a new device that takes its grants between two calls still ends up with every key', async () => {
  const epochs = Array.from({ length: 70 }, (_, i) => i + 1);
  const { store, held } = await inviterStore({ [STUDIO]: epochs });
  // The new device, its secret waiting under the invitation as enrollThisDevice leaves it.
  const joining = await openKeystore(ALICE, memoryAdapter(), () => NOW);
  const device = await joining.createDevice();
  await joining.setDeviceId(NEWCOMER);
  await joining.setPending(invitePendingId(INVITE), secret, NOW + INVITE_LIFETIME_MS);
  const enrollment = {
    deviceId: NEWCOMER, publicKey: b64url(device.publicRaw), label: 'Phone',
    mac: await enrollmentMac(pairKey, INVITE, NEWCOMER, device.publicRaw)
  };
  // It asks right after the first call has landed, before the second.
  const api = gateway({ afterGrants: async (call) => { if (call === 1) await collectGrants(joining, api, NEWCOMER); } });

  await answerEnrollment({
    keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER,
    hosts: [{ id: STUDIO, label: 'Studio PC', keyEpoch: 70, revoked: false }], pairKey, inviteId: INVITE, enrollment
  });
  assert.equal((await joining.hostKeys(STUDIO)).size, 50);
  const second = await collectGrants(joining, api, NEWCOMER);

  assert.equal(countInviteGrants(second, INVITE), 20);
  const keys = await joining.hostKeys(STUDIO);
  assert.equal(keys.size, 70);
  for (const [epoch, key] of held.get(STUDIO).keys) assert.deepEqual(keys.get(epoch), key);
});

test('an answer cut off part way resumes after the last call that landed, and tells each computer once', async () => {
  const epochs = Array.from({ length: 120 }, (_, i) => i + 1);
  const { store } = await inviterStore({ [STUDIO]: epochs, [LAPTOP]: [1] });
  const device = await newcomer();
  let failing = true;
  // The second call of grants is lost on the way.
  const api = gateway({ failGrantCall: (call) => failing && call === 2 });
  const progress = newAnswerProgress();
  const answer = () => answerEnrollment({
    keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER,
    hosts: [{ id: STUDIO, label: 'Studio PC', keyEpoch: 120, revoked: false }, { id: LAPTOP, label: 'Laptop', keyEpoch: 1, revoked: false }],
    pairKey, inviteId: INVITE, enrollment: device.enrollment, progress
  });

  await assert.rejects(answer(), TypeError);
  failing = false;
  const result = await answer();

  // Calls: 50 (landed), 50 (lost); then on the retry only what had not landed: 50, 20, and Laptop's 1.
  assert.deepEqual(api.grantCalls.map((call) => `${call.body[0].epoch}:${call.body.length}`),
    ['1:50', '51:50', '51:50', '101:20', '1:1']);
  assert.equal(result.granted, 121);
  assert.equal(api.stored.size, 121);
  assert.deepEqual(result.endorsed.sort(), [LAPTOP, STUDIO].sort());

  // Asked again once all is done, it sends nothing more: no grant, and no second endorsement.
  await answer();
  assert.equal(api.grantCalls.length, 5);
  assert.equal(api.commands.length, 2);
});

// ── the inviting dialog's watch ─────────────────────────────────────────────

/** A watch over a fake clock: `reads` answers each enrollment read in turn (null for none yet). */
function watch({ reads, answer = async () => ({ granted: 1 }), deadline = NOW + INVITE_LIFETIME_MS }) {
  const clock = { now: NOW };
  const log = { reads: 0, answers: 0, answering: [] };
  const queue = [...reads];
  const w = createInviteWatch({
    read: async () => { log.reads += 1; return queue.length > 1 ? queue.shift() : queue[0]; },
    answer: async (enrollment) => { log.answers += 1; return answer(enrollment); },
    onEnrollment: (enrollment) => log.answering.push(enrollment),
    deadline, now: () => clock.now
  });
  return { w, clock, log };
}

test('an enrollment that came in just before the deadline is still read and answered', async () => {
  const enrollment = { deviceId: NEWCOMER, label: 'Phone' };
  const { w, clock, log } = watch({ reads: [null, enrollment] });

  assert.equal((await w.check()).state, 'waiting');
  clock.now = NOW + INVITE_LIFETIME_MS;
  const last = await w.check();

  assert.equal(last.state, 'answered');
  assert.deepEqual(last.result, { granted: 1 });
  assert.equal(log.reads, 2);
  assert.deepEqual(log.answering, [enrollment]);
});

test('with no enrollment by the deadline the invitation has expired, and nothing is read after', async () => {
  const { w, clock, log } = watch({ reads: [null] });
  clock.now = NOW + INVITE_LIFETIME_MS;

  assert.equal((await w.check()).state, 'expired');
  assert.equal((await w.check()).state, 'expired');
  assert.equal(log.reads, 1);
});

test('an answer that fails is tried again until the deadline, then ends with its error', async () => {
  const enrollment = { deviceId: NEWCOMER, label: 'Phone' };
  let fail = true;
  const { w, clock, log } = watch({
    reads: [enrollment],
    answer: async () => { if (fail) throw new Error('Too many requests'); return { granted: 3 }; }
  });

  const first = await w.check();
  assert.equal(first.state, 'retrying');
  assert.equal(first.error.message, 'Too many requests');
  // Read once: the invitation is used, and its answer is what is retried.
  assert.equal((await w.check()).state, 'retrying');
  assert.equal(log.reads, 1);
  assert.equal(log.answering.length, 1);

  clock.now = NOW + INVITE_LIFETIME_MS;
  const failed = await w.check();
  assert.equal(failed.state, 'failed');
  assert.equal(failed.error.message, 'Too many requests');
  fail = false;
  assert.equal((await w.check()).state, 'failed');
  assert.equal(log.answers, 3);
});

test('an answer that succeeds on a retry ends the watch', async () => {
  let fail = true;
  const { w, log } = watch({
    reads: [{ deviceId: NEWCOMER, label: 'Phone' }],
    answer: async () => { if (fail) throw new Error('Failed to fetch'); return { granted: 2 }; }
  });

  assert.equal((await w.check()).state, 'retrying');
  fail = false;
  assert.equal((await w.check()).state, 'answered');
  assert.equal((await w.check()).state, 'answered');
  assert.equal(log.answers, 2);
});

test('a grant the new device already holds is passed over and the rest are still sent', async () => {
  const { store } = await inviterStore({ [STUDIO]: [1, 2, 3] });
  const device = await newcomer();
  const api = gateway();
  // An earlier answer to this invitation stored epoch 2 before its connection dropped.
  api.stored.add(`${STUDIO}|${NEWCOMER}|2`);

  const result = await answerEnrollment({
    keystore: store, api, writer: createWriter(store, () => NOW), deviceId: INVITER, hosts: hostsOf(STUDIO),
    pairKey, inviteId: INVITE, enrollment: device.enrollment
  });

  assert.deepEqual([...api.stored].sort(), [1, 2, 3].map((epoch) => `${STUDIO}|${NEWCOMER}|${epoch}`));
  assert.equal(result.granted, 3);
  assert.deepEqual(result.endorsed, [STUDIO]);
});

test('an invitation id is 32 lowercase hex characters, as the gateway takes them, and new each time', () => {
  const id = newInviteId();
  assert.match(id, /^[0-9a-f]{32}$/);
  assert.notEqual(newInviteId(), id);
});

test('the link is drawn as a QR code of the vendored generator', () => {
  const svg = inviteQrSvg(vectors.inviteLink.text);
  assert.match(svg, /^<svg [^>]*xmlns="http:\/\/www\.w3\.org\/2000\/svg"/);
  assert.match(svg, /<path d="M/);
  assert.ok(!svg.includes(vectors.inviteLink.secret), 'the secret is drawn as modules, never as text');
});
