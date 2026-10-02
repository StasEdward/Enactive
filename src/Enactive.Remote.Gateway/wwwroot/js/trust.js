// What this browser trusts, and how it comes to: its own device key registered with the gateway, and the
// computers' keys taken from grants only when something the gateway does not have vouches for them - the
// pairing secret the person carried by hand, or the computer's signing key pinned at its first grant.
//
// The gateway hands over the grants, so it decides which ones this page sees and in what order. Everything
// here is written against a gateway that lies: it may list another device's grant, file a grant under the
// wrong computer, replay an old grant, or offer a grant under another signing key. None of those is stored.
// No DOM here, so `node --test` runs it.
import { AUTH_BY_HOST, authByPairing, openGrant } from './grants.js';
import { EnvelopeError } from './envelope.js';
import { PinMismatchError } from './keystore.js';
import { derivePairKey } from './pairing.js';
import { b64url, equal } from './bytes.js';

/** The header every call made as this device carries (DeviceHeader.Name on the gateway). */
export const DEVICE_HEADER = 'X-Enactive-Device';

/** Why a grant went into `rejected`. One sentence for every cause: to the person they are all the same. */
export const REJECTED = 'This key was not sent by your computer or a device you trust; it was ignored.';

/** What a person is told when the gateway refuses to register this browser as one device too many. */
export const DEVICE_LIMIT = 'This account has as many devices as it may; remove one in Devices.';

/**
 * What the panel says when a computer's grant carries another signing key than the one pinned. Spec §2: a
 * device admitted by a browser that was already compromised trusts the key that browser named, and the
 * computer's real rotation grant is what shows it - so the way out is to be added again from the computer.
 */
export const tamperingMessage = (label) =>
  `${label} is presenting a different identity than the one this device trusts. Remove this device and add it `
  + 'again from the computer (desktop: Add a device).';

// The gateway's column is 80 characters and it refuses a longer label rather than cutting it.
const MAX_LABEL = 80;

const CONNECT = authByPairing('connect');
const PAIRING_PREFIX = authByPairing('');

// Most specific first: Edge and Opera also say Chrome and Safari, Chrome also says Safari.
const BROWSERS = [[/Edg(e|A|iOS)?\//, 'Edge'], [/OPR\/|Opera/, 'Opera'], [/Firefox\/|FxiOS\//, 'Firefox'],
  [/Chrome\/|CriOS\//, 'Chrome'], [/Safari\//, 'Safari']];
// iPhone and Android before Mac and Linux: an iPhone says "like Mac OS X", Android says Linux.
const SYSTEMS = [[/iPhone/, 'iPhone'], [/iPad/, 'iPad'], [/Android/, 'Android'], [/Windows/, 'Windows'],
  [/CrOS/, 'ChromeOS'], [/Mac OS X|Macintosh/, 'Mac'], [/Linux/, 'Linux']];

/**
 * A short name for this browser, for the person's list of devices: "Firefox on Windows". Only the browser and
 * the system, never the version: it is a label to recognise, not a fingerprint. Anything not recognised -
 * Node under test has a navigator that names itself "Node.js/22" - is "Browser".
 */
export function deviceLabel(userAgent = globalThis.navigator?.userAgent) {
  if (typeof userAgent !== 'string') return 'Browser';
  const browser = BROWSERS.find(([pattern]) => pattern.test(userAgent))?.[1];
  const system = SYSTEMS.find(([pattern]) => pattern.test(userAgent))?.[1];
  if (!browser && !system) return 'Browser';
  return (browser && system ? `${browser} on ${system}` : browser ?? `Browser on ${system}`).slice(0, MAX_LABEL);
}

/**
 * This browser's device id for the account, registering its key the first time. The key is made once and
 * kept (keystore.js); the id is stored as soon as the gateway gives it.
 *
 * A registration whose answer was lost - the tab closed, the network dropped - leaves a key without an id, and
 * the next call registers the same key again. The gateway answers a live key it already holds for this account
 * with the device it has, so that costs no second place in the account's allowance and the id is the one any
 * grant was made for.
 */
export async function ensureDevice(keystore, api) {
  const device = (await keystore.device()) ?? await keystore.createDevice();
  if (device.id) return device.id;

  let registered;
  try {
    registered = await api.post('/api/devices', { publicKey: b64url(device.publicRaw), label: deviceLabel() });
  } catch (error) {
    if (error?.code === 'device-limit') throw Object.assign(new Error(DEVICE_LIMIT), { code: error.code });
    throw error;
  }

  await keystore.setDeviceId(registered.id);
  return registered.id;
}

/**
 * Takes the grants the gateway holds for this device and stores the host keys it can verify.
 *
 * Returns `{added: [{hostId, epoch}], rejected: [{hostId, epoch, reason}]}`, and `tampering: hostId` when a
 * computer's grant carries another signing key than the one pinned for it. That is not "rejected": a forged
 * grant is ignored, but a substituted identity means this device may trust the wrong key, and only the person
 * can put that right.
 *
 * What `openGrant` leaves to its caller is checked here, before anything is opened: the grant is for this
 * device and filed under its own computer, and its epoch is newer than every key held for that computer. A
 * grant for an epoch already held, or an older one, is passed over without a word: the gateway keeps every
 * grant it ever served and lists them all on every call, and storing an old one could roll this device back
 * to a key a revoked device holds.
 */
export async function collectGrants(keystore, api, deviceId) {
  const device = await keystore.device();
  // Grants are wrapped to the device key; without one there is nothing they could be opened with.
  if (device === null) throw new Error('This browser has no device key yet: ensureDevice comes first.');
  const served = await api.get('/api/grants', { headers: { [DEVICE_HEADER]: deviceId } });
  const result = { added: [], rejected: [] };
  const newest = new Map();
  const doubted = new Set();
  // Pairing secrets that verified a grant in this delivery, dropped once it has been read (see below).
  const spent = new Set();

  for (const group of Array.isArray(served) ? served : []) {
    const hostId = group?.hostId;
    if (typeof hostId !== 'string' || hostId.length === 0 || doubted.has(hostId)) continue;
    if (!newest.has(hostId)) newest.set(hostId, await keystore.newestEpoch(hostId));

    // Oldest first, whatever order the gateway chose: a device admitted to a computer is sent every epoch it
    // has at once, and taken newest first the older ones would all look like a rollback. Stable, so two grants
    // of one epoch keep the gateway's order.
    const grants = (Array.isArray(group.grants) ? group.grants : [])
      .slice().sort((a, b) => epochOf(a) - epochOf(b));

    for (const grant of grants) {
      const outcome = await take(keystore, device, deviceId, hostId, grant, newest.get(hostId), spent);
      if (outcome === 'tampering') {
        result.tampering ??= hostId;
        doubted.add(hostId);
        break;
      }
      if (outcome === 'rejected') result.rejected.push({ hostId, epoch: grant?.epoch, reason: REJECTED });
      if (outcome === 'added') {
        result.added.push({ hostId, epoch: grant.epoch });
        newest.set(hostId, grant.epoch);
      }
    }
  }

  // A pairing secret authenticates one delivery: the first grant of a connection code, or every key an
  // invitation hands over, which the computer or the inviting browser publishes in one batch. Kept after that,
  // anyone who saw the code could go on adding "new" paired grants for as long as it had left to live. Dropped
  // at the end of the delivery rather than at its first grant, which would refuse the rest of that batch.
  for (const id of spent) await keystore.dropPending(id);

  return result;
}

const epochOf = (grant) => (Number.isSafeInteger(grant?.epoch) ? grant.epoch : 0);

/** One grant: 'added', 'rejected', 'tampering', or 'skipped' (an epoch this device has moved past). */
async function take(keystore, device, deviceId, hostId, grant, newest, spent) {
  // The gateway files grants under computers and devices; nothing in a grant for another device or computer is
  // this one's business, however well it is authenticated.
  if (grant?.deviceId !== deviceId || grant?.hostId !== hostId) return 'rejected';
  // Epoch 0 is a uint32 the grant format takes, but no computer has a key for it, and the key store refuses it.
  if (!Number.isSafeInteger(grant.epoch) || grant.epoch < 1) return 'rejected';
  if (newest !== null && grant.epoch <= newest) return 'skipped';

  const pinned = await keystore.hostSigningKey(hostId);
  // Compared before openGrant, which would refuse it too but as one more bad grant. Base64url has one spelling
  // per byte string, so the text compares the keys.
  if (pinned !== null && typeof grant.hostSigningPublic === 'string' && grant.hostSigningPublic !== b64url(pinned)) {
    return 'tampering';
  }

  // Which secret vouches for the grant is named by the grant, and only one this device holds can.
  let pairingId = null;
  let pairKey = null;
  if (grant.authBy === CONNECT) {
    pairingId = hostId;
  } else if (typeof grant.authBy === 'string' && grant.authBy.startsWith(PAIRING_PREFIX)) {
    pairingId = grant.authBy.slice(PAIRING_PREFIX.length);
  } else if (grant.authBy !== AUTH_BY_HOST) {
    return 'rejected';
  }
  if (pairingId !== null) {
    const secret = pairingId.length > 0 ? await keystore.pending(pairingId) : null;
    // Not pending: the pairing is over, or never happened here. Its secret is no longer anyone's say-so.
    if (secret === null) return 'rejected';
    pairKey = await derivePairKey(secret);
  }

  let opened;
  try {
    opened = await openGrant(grant, device.privateKey, device.publicRaw, { pairKey, pinnedHostSigningPublic: pinned });
  } catch (error) {
    if (error instanceof EnvelopeError) return 'rejected';
    throw error;
  }

  try {
    await keystore.pinHostSigningKey(hostId, opened.hostSigningPublic);
  } catch (error) {
    // Another tab pinned another key between the check above and here.
    if (error instanceof PinMismatchError) return 'tampering';
    throw error;
  }
  if (pairingId !== null) spent.add(pairingId);

  if (await keystore.addHostKey(hostId, grant.epoch, opened.key.secret)) return 'added';
  // Another tab stored this epoch first. The same key is the same grant taken twice; a different one for an
  // epoch already held would replace what that epoch was sealed with, and the first key stays.
  const held = (await keystore.hostKeys(hostId)).get(grant.epoch);
  return equal(held, opened.key.secret) ? 'skipped' : 'rejected';
}
