import { test } from 'node:test';
import assert from 'node:assert/strict';
import { openKeystore, memoryAdapter } from '../../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js';
import { createGrant } from '../../src/Enactive.Remote.Gateway/wwwroot/js/grants.js';
import { derivePairKey } from '../../src/Enactive.Remote.Gateway/wwwroot/js/pairing.js';
import { b64url, fromB64url } from '../../src/Enactive.Remote.Gateway/wwwroot/js/bytes.js';
import { canonicalBytes } from '../../src/Enactive.Remote.Gateway/wwwroot/js/canonical.js';
import {
  ensureDevice, collectGrants, deviceLabel, tamperingMessage, troubleFor, worthSaying, connectPendingId, invitePendingId,
  keyStanding, REJECTED, NO_SECRET, DEVICE_LIMIT, NOT_PAIRED, BEHIND
} from '../../src/Enactive.Remote.Gateway/wwwroot/js/trust.js';
import { vectors } from './vectors.mjs';

const ALICE = '0123456789abcdef0123456789abcdef';
const ECDH = { name: 'ECDH', namedCurve: 'P-256' };
const ECDSA = { name: 'ECDSA', namedCurve: 'P-256' };
const DAY = 24 * 60 * 60 * 1000;

const g = vectors.grant;
const r = vectors.rotationGrant;
const HOST = g.hostId;
const DEVICE = g.deviceId;
const devicePublicRaw = fromB64url(g.devicePublic);
// The connection code's secret is the one the vector grant's pair key is derived from.
const pairingSecret = fromB64url(vectors.connectionCode.pairingSecret);
const pinned = fromB64url(vectors.hostSigning.public);

// The vector keys carry only d, x and y; WebCrypto wants the key type and curve spelled out.
const jwkOf = (jwk) => ({ kty: 'EC', crv: 'P-256', d: jwk.d, x: jwk.x, y: jwk.y });
const devicePrivate = await crypto.subtle.importKey('jwk', jwkOf(g.devicePrivate), ECDH, false, ['deriveBits']);

/**
 * A key store holding the vector device, so the vector grants are for it. The key store makes its own device
 * key and has no import, so the record is written through the adapter, in the shape keystore.js keeps it.
 */
async function vectorStore(now = () => 0) {
  const adapter = memoryAdapter();
  const db = await adapter.open(`enactive-keys-${ALICE}`, ['device', 'hostKeys', 'hostSigning', 'pending']);
  await db.put('device', 'self', { id: DEVICE, privateKey: devicePrivate, publicRaw: devicePublicRaw });
  return openKeystore(ALICE, adapter, now);
}

/** A gateway serving `served` (the HostGrants list) from GET /api/grants, recording what it was asked. */
function gateway(served = []) {
  const api = {
    served,
    calls: [],
    async get(path, options) {
      api.calls.push({ path, options });
      if (path !== '/api/grants') throw new Error(`Unexpected GET ${path}`);
      return structuredClone(api.served);
    },
    async post(path) {
      throw new Error(`Unexpected POST ${path}`);
    }
  };
  return api;
}

const forHost = (...grants) => [{ hostId: HOST, keyEpoch: Math.max(...grants.map((grant) => grant.epoch)), grants }];

// A paired grant for the vector device made the way a computer or an inviting browser makes one.
async function pairedGrant(epoch, { pairingId = 'connect', secret = pairingSecret, signingPublic = pinned } = {}) {
  return createGrant({
    hostId: HOST, deviceId: DEVICE, devicePublicRaw,
    key: { epoch, secret: crypto.getRandomValues(new Uint8Array(32)) },
    pairingId, pairKey: await derivePairKey(secret), hostSigningPublic: signingPublic
  });
}

// The rotation grant's fields signed with any ECDSA key, as the computer - or someone else - would sign them.
async function signedBy(privateKey, fields) {
  const { mac: _, ...unsigned } = fields;
  const text = canonicalBytes('enactive-grant-sig-v1', unsigned.hostId, unsigned.deviceId, String(unsigned.epoch),
    unsigned.ephemeralPublic, b64url(devicePublicRaw), unsigned.nonce, unsigned.ciphertext, unsigned.authBy,
    unsigned.hostSigningPublic);
  const signature = await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, privateKey, text);
  return { ...unsigned, mac: b64url(new Uint8Array(signature)) };
}

async function otherSigner() {
  const pair = await crypto.subtle.generateKey(ECDSA, true, ['sign', 'verify']);
  return { privateKey: pair.privateKey, publicRaw: new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey)) };
}

/** A store paired with the vector computer at epoch 3, as the first grant of a connection code leaves it. */
async function pairedStore() {
  const store = await vectorStore();
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);
  await collectGrants(store, gateway(forHost(g.grant)), DEVICE);
  return store;
}

const nothing = { added: [], rejected: [] };

// ── collectGrants ───────────────────────────────────────────────────────────

test('a grant verified by the pending connection secret is stored', async () => {
  const store = await vectorStore();
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);
  const api = gateway(forHost(g.grant));

  const result = await collectGrants(store, api, DEVICE);

  assert.deepEqual(result, { added: [{ hostId: HOST, epoch: 3 }], rejected: [] });
  assert.equal(b64url((await store.hostKeys(HOST)).get(3)), vectors.hostKey.secret);
  assert.equal(b64url(await store.hostSigningKey(HOST)), vectors.hostSigning.public);
  // The grants are this device's: the call says which device it is.
  assert.equal(api.calls[0].options.headers['X-Enactive-Device'], DEVICE);
});

test('a grant with a changed mac is rejected and not stored', async () => {
  const store = await vectorStore();
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);
  const last = g.grant.mac.at(-1) === 'A' ? 'B' : 'A';
  const changed = { ...g.grant, mac: g.grant.mac.slice(0, -1) + last };

  const result = await collectGrants(store, gateway(forHost(changed)), DEVICE);

  assert.deepEqual(result, { added: [], rejected: [{ hostId: HOST, epoch: 3, code: 'unverified', reason: REJECTED }] });
  assert.equal((await store.hostKeys(HOST)).size, 0);
  assert.equal(await store.hostSigningKey(HOST), null);
  // Nothing verified, so the pairing is still waiting for its grant.
  assert.deepEqual(await store.pending(connectPendingId(HOST)), pairingSecret);
});

test('a rotation grant signed by the pinned key is stored', async () => {
  const store = await pairedStore();

  // The gateway keeps every grant it served, so the first one comes again beside the rotation.
  const result = await collectGrants(store, gateway(forHost(g.grant, r.grant)), DEVICE);

  assert.deepEqual(result, { added: [{ hostId: HOST, epoch: 4 }], rejected: [] });
  assert.equal(b64url((await store.hostKeys(HOST)).get(4)), r.secret);
  assert.equal(await store.newestEpoch(HOST), 4);
});

test('a rotation grant with another signing key is rejected', async () => {
  const store = await pairedStore();
  const other = await otherSigner();
  // It names the pinned key, so it is not a substituted identity - but the signature is not the computer's.
  const forged = await signedBy(other.privateKey, r.grant);

  const result = await collectGrants(store, gateway(forHost(forged)), DEVICE);

  assert.deepEqual(result, { added: [], rejected: [{ hostId: HOST, epoch: 4, code: 'unverified', reason: REJECTED }] });
  assert.equal(await store.newestEpoch(HOST), 3);
});

test('a rotation grant before any key is pinned is rejected', async () => {
  const store = await vectorStore();

  const result = await collectGrants(store, gateway(forHost(r.grant)), DEVICE);

  assert.deepEqual(result, { added: [], rejected: [{ hostId: HOST, epoch: 4, code: 'unverified', reason: REJECTED }] });
  assert.equal(await store.hostSigningKey(HOST), null);
});

test('a grant whose signing key differs from the pinned one is reported as tampering, not as rejected', async () => {
  const store = await pairedStore();
  const other = await otherSigner();
  const substituted = await signedBy(other.privateKey, { ...r.grant, hostSigningPublic: b64url(other.publicRaw) });

  // The genuine rotation comes after it; nothing more of this computer's is taken once it is in doubt.
  const result = await collectGrants(store, gateway(forHost(substituted, r.grant)), DEVICE);

  assert.deepEqual(result, { ...nothing, tampering: HOST });
  assert.equal(await store.newestEpoch(HOST), 3);
  assert.equal(b64url(await store.hostSigningKey(HOST)), vectors.hostSigning.public);
});

test('a paired grant naming another signing key than the pinned one is tampering too', async () => {
  const store = await pairedStore();
  const other = await otherSigner();
  await store.setPending(invitePendingId('invite-1'), pairingSecret, DAY);
  const invited = await pairedGrant(4, { pairingId: 'invite-1', signingPublic: other.publicRaw });

  const result = await collectGrants(store, gateway(forHost(invited)), DEVICE);

  assert.deepEqual(result, { ...nothing, tampering: HOST });
  assert.equal(await store.newestEpoch(HOST), 3);
});

test('an older or already-held epoch is not stored and the current epoch does not change', async () => {
  const store = await pairedStore();
  await collectGrants(store, gateway(forHost(r.grant)), DEVICE);
  const held = (await store.hostKeys(HOST)).get(4);
  // Authentic grants: a holder of the pairing secret made them. Only their epochs are wrong.
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);
  const older = await pairedGrant(2);
  const again = await pairedGrant(4);

  const result = await collectGrants(store, gateway(forHost(older, again)), DEVICE);

  assert.deepEqual(result, nothing);
  assert.deepEqual([...(await store.hostKeys(HOST)).keys()], [3, 4]);
  assert.deepEqual((await store.hostKeys(HOST)).get(4), held);
  assert.equal(await store.newestEpoch(HOST), 4);
});

test('the pending secret is dropped after the delivery it verified, and a later paired grant with it is then refused', async () => {
  const store = await pairedStore();
  assert.equal(await store.pending(connectPendingId(HOST)), null);
  const later = await pairedGrant(5);

  const result = await collectGrants(store, gateway(forHost(g.grant, later)), DEVICE);

  assert.deepEqual(result, { added: [], rejected: [{ hostId: HOST, epoch: 5, code: 'no-secret', reason: NO_SECRET }] });
  assert.equal(await store.newestEpoch(HOST), 3);
});

test('every key of one delivery is stored oldest first, and the invitation secret is dropped after it', async () => {
  const store = await vectorStore();
  await store.setPending(invitePendingId('invite-1'), pairingSecret, DAY);
  const grants = [await pairedGrant(3, { pairingId: 'invite-1' }), await pairedGrant(1, { pairingId: 'invite-1' }),
    await pairedGrant(2, { pairingId: 'invite-1' })];

  const result = await collectGrants(store, gateway(forHost(...grants)), DEVICE);

  assert.deepEqual(result.added, [1, 2, 3].map((epoch) => ({ hostId: HOST, epoch })));
  assert.deepEqual(result.rejected, []);
  assert.equal(await store.pending(invitePendingId('invite-1')), null);
});

test('a grant for another device id is rejected without opening', async () => {
  const store = await vectorStore();
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);

  // Wrapped to this device's key and authenticated: opened, it would be stored. It names another device.
  const result = await collectGrants(store, gateway(forHost(g.grant)), 'device-other');

  assert.deepEqual(result, { added: [], rejected: [{ hostId: HOST, epoch: 3, code: 'unverified', reason: REJECTED }] });
  assert.equal((await store.hostKeys(HOST)).size, 0);
  assert.deepEqual(await store.pending(connectPendingId(HOST)), pairingSecret);
});

test('a grant filed under another computer than its own is rejected', async () => {
  const store = await vectorStore();
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);

  const result = await collectGrants(store, gateway([{ hostId: 'host-other', keyEpoch: 3, grants: [g.grant] }]), DEVICE);

  assert.deepEqual(result, { added: [], rejected: [{ hostId: 'host-other', epoch: 3, code: 'unverified', reason: REJECTED }] });
  assert.equal((await store.hostKeys(HOST)).size, 0);
});

const HOST_B = 'host-b7c3e1';

// A paired grant for computer B, wrapped to the vector device and authenticated with the vector pair key.
async function grantForB(epoch, pairingId, signingPublic) {
  return createGrant({
    hostId: HOST_B, deviceId: DEVICE, devicePublicRaw,
    key: { epoch, secret: crypto.getRandomValues(new Uint8Array(32)) },
    pairingId, pairKey: await derivePairKey(pairingSecret), hostSigningPublic: signingPublic
  });
}

const forB = (...grants) => [{ hostId: HOST_B, keyEpoch: 1, grants }];

test('a connection secret for one computer does not vouch for another computer\'s grant', async () => {
  const store = await vectorStore();
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);
  const attacker = await otherSigner();
  // Named as an invitation whose id is A's computer id: before pending ids had namespaces, that found A's secret.
  const borrowed = await grantForB(1, HOST, attacker.publicRaw);
  const prefixed = await grantForB(1, `connect:${HOST}`, attacker.publicRaw);

  const result = await collectGrants(store, gateway(forB(borrowed, prefixed)), DEVICE);

  assert.deepEqual(result.added, []);
  assert.deepEqual(result.rejected.map((one) => one.code), ['no-secret', 'no-secret']);
  assert.equal(await store.hostSigningKey(HOST_B), null);
  assert.deepEqual(await store.pending(connectPendingId(HOST)), pairingSecret);
});

test('a connection grant for another computer is not checked with the secret of the code made for this one', async () => {
  const store = await vectorStore();
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);

  const result = await collectGrants(store, gateway(forB(await grantForB(1, 'connect', pinned))), DEVICE);

  assert.deepEqual(result, { added: [], rejected: [{ hostId: HOST_B, epoch: 1, code: 'no-secret', reason: NO_SECRET }] });
  assert.equal(await store.hostSigningKey(HOST_B), null);
  assert.deepEqual(await store.pending(connectPendingId(HOST)), pairingSecret);
});

test('a code answered after its secret expired is told apart from a grant that does not verify', async () => {
  let now = 0;
  const store = await vectorStore(() => now);
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);
  now = DAY;

  const result = await collectGrants(store, gateway(forHost(g.grant)), DEVICE);

  assert.deepEqual(result, { added: [], rejected: [{ hostId: HOST, epoch: 3, code: 'no-secret', reason: NO_SECRET }] });
  assert.equal(NO_SECRET, 'No code is waiting for this computer any more - make a new one from Computers.');
});

test('a grant under another signing key is tampering even when its epoch is older than the newest held', async () => {
  const store = await vectorStore();
  await store.setPending(connectPendingId(HOST), pairingSecret, DAY);
  await collectGrants(store, gateway(forHost(await pairedGrant(9))), DEVICE);
  const other = await otherSigner();
  const older = await signedBy(other.privateKey, { ...r.grant, epoch: 5, hostSigningPublic: b64url(other.publicRaw) });

  const result = await collectGrants(store, gateway(forHost(older)), DEVICE);

  assert.deepEqual(result, { ...nothing, tampering: HOST });
  assert.equal(await store.newestEpoch(HOST), 9);
});

test('the register dialog is told only of its own computer\'s trouble', () => {
  const result = {
    added: [],
    rejected: [{ hostId: 'host-y', epoch: 1, code: 'unverified', reason: REJECTED }],
    tampering: 'host-z'
  };

  assert.equal(troubleFor(result, 'host-x', 'Studio PC'), '');
  assert.equal(troubleFor(result, 'host-y', 'Laptop'), REJECTED);
  assert.equal(troubleFor(result, 'host-z', 'Office'), tamperingMessage('Office'));
});

test('a load tells of tampering and of grants that do not verify, not of codes that were never answered in time', () => {
  const label = (id) => `name of ${id}`;
  const expired = { hostId: 'host-a', epoch: 1, code: 'no-secret', reason: NO_SECRET };
  const forged = { hostId: 'host-b', epoch: 2, code: 'unverified', reason: REJECTED };

  assert.equal(worthSaying({ added: [], rejected: [expired] }, label), '');
  assert.equal(worthSaying({ added: [], rejected: [expired, forged] }, label), REJECTED);
  assert.equal(worthSaying({ added: [], rejected: [forged], tampering: 'host-c' }, label), tamperingMessage('name of host-c'));
});

test('a computer whose key this device does not hold, or holds only an older one, says so', async () => {
  const keystore = await openKeystore(ALICE, memoryAdapter());
  const host = { id: HOST, label: 'Studio PC', revoked: false, keyEpoch: 2 };

  // A computer registered here whose code was never answered in time: nothing else would say so, since
  // the gateway's refusal of its late grant is kept out of every toast (worthSaying).
  assert.equal(await keyStanding(keystore, host), NOT_PAIRED);
  assert.equal(await keyStanding(null, host), NOT_PAIRED);

  await keystore.addHostKey(HOST, 1, new Uint8Array(32).fill(1));
  assert.equal(await keyStanding(keystore, host), BEHIND);

  await keystore.addHostKey(HOST, 2, new Uint8Array(32).fill(2));
  assert.equal(await keyStanding(keystore, host), null);
  assert.equal(await keyStanding(keystore, { ...host, keyEpoch: 1 }), null);
});

test('the tampering sentence names the computer and says what to do', () => {
  assert.equal(tamperingMessage('Studio PC'),
    'Studio PC is presenting a different identity than the one this device trusts. Remove this device and add it '
    + 'again from the computer (desktop: Add a device).');
});

// ── ensureDevice ────────────────────────────────────────────────────────────

function registrar(answer = () => ({ id: 'device-1' })) {
  const api = {
    posts: [],
    async get(path) { throw new Error(`Unexpected GET ${path}`); },
    async post(path, body) {
      api.posts.push({ path, body });
      return answer(body);
    }
  };
  return api;
}

test('ensureDevice registers once and keeps the id', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());
  const api = registrar();

  assert.equal(await ensureDevice(store, api), 'device-1');
  assert.equal(await ensureDevice(store, api), 'device-1');

  assert.equal(api.posts.length, 1);
  const device = await store.device();
  assert.equal(device.id, 'device-1');
  assert.equal(api.posts[0].path, '/api/devices');
  assert.equal(api.posts[0].body.publicKey, b64url(device.publicRaw));
  assert.ok(api.posts[0].body.label.length > 0 && api.posts[0].body.label.length <= 80);
});

test('ensureDevice registers a device whose id was never stored with the key it already has', async () => {
  // A registration whose answer was lost: the key is there, the id is not.
  const store = await openKeystore(ALICE, memoryAdapter());
  const made = await store.createDevice();
  const api = registrar();

  assert.equal(await ensureDevice(store, api), 'device-1');

  assert.equal(api.posts[0].body.publicKey, b64url(made.publicRaw));
  assert.deepEqual((await store.device()).publicRaw, made.publicRaw);
});

test('a device the account has no room for is told where to make room', async () => {
  const store = await openKeystore(ALICE, memoryAdapter());
  const refused = Object.assign(new Error('The account already has 10 devices.'), { code: 'device-limit', status: 409 });
  const api = registrar(() => { throw refused; });

  await assert.rejects(ensureDevice(store, api), (error) => error.message === DEVICE_LIMIT && error.code === 'device-limit');
  assert.equal(DEVICE_LIMIT, 'This account has as many devices as it may; remove one in Devices.');
  assert.equal((await store.device()).id, null);
});

test('the device label names the browser and the system, or says Browser', () => {
  const edge = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Safari/537.36 Edg/129.0.0.0';
  const chrome = 'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/129.0.0.0 Mobile Safari/537.36';
  const safari = 'Mozilla/5.0 (iPhone; CPU iPhone OS 17_6 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Mobile/15E148 Safari/604.1';
  const firefox = 'Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0';
  const mac = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 14_6) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.6 Safari/605.1.15';

  assert.equal(deviceLabel(edge), 'Edge on Windows');
  assert.equal(deviceLabel(chrome), 'Chrome on Android');
  assert.equal(deviceLabel(safari), 'Safari on iPhone');
  assert.equal(deviceLabel(firefox), 'Firefox on Linux');
  assert.equal(deviceLabel(mac), 'Safari on Mac');
  assert.equal(deviceLabel('Node.js/22'), 'Browser');
  assert.equal(deviceLabel(undefined), 'Browser');
});
