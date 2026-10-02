import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openKeystore, memoryAdapter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';
import { openGrant } from '../../src/Enactive.Remote.Gateway/wwwroot/js/grants.js';
import { derivePairKey, enrollmentMac, verifyEnrollment } from '../../src/Enactive.Remote.Gateway/wwwroot/js/pairing.js';
import { invitePendingId } from '../../src/Enactive.Remote.Gateway/wwwroot/js/trust.js';
import { createWriter, NOT_YET_GIVEN } from '../../src/Enactive.Remote.Gateway/wwwroot/js/writer.js';
import { hostKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/hostkey.js';
import { ad, openJson } from '../../src/Enactive.Remote.Gateway/wwwroot/js/sealed.js';
import { b64url, fromB64url } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import {
  openInviteLink, keepInvite, takeKeptInvite, newInviteId, enrollThisDevice, inviteAnswered, answerEnrollment,
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
function gateway() {
  const stored = new Set();
  const api = {
    grantCalls: [],
    commands: [],
    async get(path) {
      throw new Error(`Unexpected GET ${path}`);
    },
    async post(path, body, options) {
      if (path === '/api/grants') {
        api.grantCalls.push({ body, options });
        const keys = body.map((grant) => `${grant.hostId}|${grant.deviceId}|${grant.epoch}`);
        if (keys.some((key) => stored.has(key))) {
          throw Object.assign(new Error('That device already holds a grant of this key.'), { code: 'conflict', status: 409 });
        }
        keys.forEach((key) => stored.add(key));
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

  const invite = openInviteLink('#' + vectors.inviteLink.text.split('#')[1], history);

  assert.deepEqual(history.calls, [[null, '', '/pair']]);
  assert.equal(invite.inviteId, vectors.inviteLink.inviteId);
  assert.equal(b64url(invite.secret), vectors.inviteLink.secret);

  // Taken out of the address before it is read, so a link too damaged to use does not stay there either: its
  // secret may be whole when the rest is not.
  const damaged = fakeHistory();
  assert.throws(() => openInviteLink(`#v=2&i=${INVITE}&p=too-short`, damaged), TypeError);
  assert.deepEqual(damaged.calls, [[null, '', '/pair']]);
});

test('an invitation kept across a sign-in is read back once, and a storage that fails keeps nothing', () => {
  const storage = fakeStorage();

  keepInvite(storage, { inviteId: INVITE, secret });
  const kept = takeKeptInvite(storage);

  assert.equal(kept.inviteId, INVITE);
  assert.deepEqual(kept.secret, secret);
  assert.equal(takeKeptInvite(storage), null);
  assert.equal(storage.items.size, 0);

  const broken = { getItem() { throw new Error('denied'); }, setItem() { throw new Error('denied'); }, removeItem() {} };
  assert.doesNotThrow(() => keepInvite(broken, { inviteId: INVITE, secret }));
  assert.equal(takeKeptInvite(broken), null);
  storage.setItem('enactive.invite', '{"inviteId":5}');
  assert.equal(takeKeptInvite(storage), null);
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

test('an invitation is answered once a grant spent its secret, here or in another tab, and expires after its ten minutes', async () => {
  let clock = NOW;
  const store = await openKeystore(ALICE, memoryAdapter(), () => clock);
  const deadline = NOW + INVITE_LIFETIME_MS;
  await store.setPending(invitePendingId(INVITE), secret, deadline);
  let collected = 0;
  const nothingYet = async () => { collected += 1; return { added: [], rejected: [] }; };

  assert.equal(await inviteAnswered({ keystore: store, inviteId: INVITE, deadline, now: clock, collect: nothingYet }), 'waiting');
  assert.equal(collected, 1);

  // collectGrants drops the secret at the end of the delivery a grant of the invitation verified in.
  const spends = async () => { await store.dropPending(invitePendingId(INVITE)); return { added: [{ hostId: STUDIO, epoch: 1 }], rejected: [] }; };
  assert.equal(await inviteAnswered({ keystore: store, inviteId: INVITE, deadline, now: clock, collect: spends }), 'answered');

  // Another tab's delivery spent it first: nothing is asked for, and it is answered all the same.
  collected = 0;
  assert.equal(await inviteAnswered({ keystore: store, inviteId: INVITE, deadline, now: clock, collect: nothingYet }), 'answered');
  assert.equal(collected, 0);

  await store.setPending(invitePendingId(INVITE), secret, deadline);
  clock = deadline;
  assert.equal(await inviteAnswered({ keystore: store, inviteId: INVITE, deadline, now: clock, collect: nothingYet }), 'expired');
  assert.equal(collected, 0);
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
  assert.deepEqual(result.notEndorsed, [{ hostId: LAPTOP, reason: NOT_YET_GIVEN }]);
  assert.equal(api.commands.length, 0);
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
  assert.deepEqual(api.grantCalls.map((call) => call.body.length), [50, 50, 10]);
  assert.equal(result.granted, 110);
  assert.equal(new Set(api.grantCalls.flatMap((call) => call.body).map((grant) => `${grant.hostId}:${grant.epoch}`)).size, 110);
  // Every computer's newest key goes in the first call: should the new device take the first call before the
  // rest arrive, it can read what each computer sends now (see answerEnrollment).
  const first = api.grantCalls[0].body.map((grant) => `${grant.hostId}:${grant.epoch}`);
  assert.ok(first.includes(`${STUDIO}:70`));
  assert.ok(first.includes(`${LAPTOP}:40`));
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
