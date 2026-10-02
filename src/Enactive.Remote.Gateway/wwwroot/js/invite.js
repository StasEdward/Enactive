// Adding a device by an invitation link (spec §5.3), both ends of it. A trusted browser makes an invitation -
// an id the gateway keeps and a pairing secret it never sees - and shows the link; the new device opens it,
// posts an enrollment with a MAC over its own public key, and waits for its grants. The inviter reads the
// enrollment, checks the MAC, so the gateway cannot have put a key of its own in the new device's place, and
// grants it every computer key it holds, each authenticated with the pair key, then asks each computer to
// trust the device too. No DOM here, so `node --test` runs it; app.js draws the dialogs.
import { qrcode } from '../vendor/qrcode.mjs';
import { b64url, fromB64url } from './bytes.js';
import { createGrant } from './grants.js';
import { derivePairKey, enrollmentMac, parseInviteFragment, verifyEnrollment } from './pairing.js';
import { DEVICE_HEADER, invitePendingId } from './trust.js';
import { sendRefusal } from './writer.js';

/** How long an invitation can be answered: the gateway's InviteLifetime. */
export const INVITE_LIFETIME_MS = 10 * 60 * 1000;

/** DeviceService.MaxGrantsPerCall: a call with more is refused whole. */
export const GRANTS_PER_CALL = 50;

/** What the inviting browser says when the enrollment's MAC does not verify. */
export const SWAPPED_KEY = 'Someone other than your new device answered this invitation. Nothing was shared.';

const LINK_PATH = '/pair';
const KEPT = 'enactive.invite';

/** An invitation id as the gateway takes them: 32 lowercase hex characters, from the browser's CSPRNG. */
export const newInviteId = () => crypto.randomUUID().replaceAll('-', '');

/**
 * Reads the invitation out of the address the new device opened, taking it out of the address first. The
 * fragment holds the pairing secret: left there, it stays in the tab's history, in a bookmark or a synced
 * session, readable long after it was used. Replaced before it is parsed, so a link too damaged to use goes
 * too - its secret may be whole when the rest is not.
 */
export function openInviteLink(hash, history) {
  history.replaceState(null, '', LINK_PATH);
  return parseInviteFragment(hash);
}

/**
 * Keeps an invitation for the sign-in round trip. A provider's sign-in leaves the page and comes back to
 * another address, and the invitation opened signed out would be lost with the fragment it came in. Session
 * storage: this tab only, and gone with it. Failing storage (a private window, storage turned off) keeps
 * nothing, and the person opens the link again once signed in.
 */
export function keepInvite(storage, { inviteId, secret }) {
  try {
    storage.setItem(KEPT, JSON.stringify({ inviteId, secret: b64url(secret) }));
  } catch {
    // Nothing kept; see above.
  }
}

/** The invitation kept across a sign-in, taken out of storage as it is read: one use. Null when none. */
export function takeKeptInvite(storage) {
  let text;
  try {
    text = storage.getItem(KEPT);
    storage.removeItem(KEPT);
  } catch {
    return null;
  }
  if (text === null) return null;
  try {
    const { inviteId, secret } = JSON.parse(text);
    if (typeof inviteId !== 'string' || inviteId.length === 0) return null;
    return { inviteId, secret: fromB64url(secret) };
  } catch {
    return null;
  }
}

/**
 * The new device's answer: the secret is kept under the invitation's pending id (trust.js invitePendingId,
 * where collectGrants looks for it) for the invitation's ten minutes, and the enrollment is posted with the MAC
 * over this device's own key. Kept first: grants answering the enrollment can arrive as soon as it is posted,
 * and one taken before the secret was there is refused for good. Returns when the secret expires.
 */
export async function enrollThisDevice({ keystore, api, deviceId, inviteId, secret, now }) {
  const deadline = now + INVITE_LIFETIME_MS;
  await keystore.setPending(invitePendingId(inviteId), secret, deadline);
  const device = await keystore.device();
  const mac = await enrollmentMac(await derivePairKey(secret), inviteId, deviceId, device.publicRaw);
  await api.post('/api/enrollments', { inviteId, deviceId, mac });
  return deadline;
}

/**
 * Whether the invitation's grants have arrived: 'answered', 'waiting' or 'expired'. collectGrants drops an
 * invitation's secret at the end of the delivery in which one of its grants verified, so a secret gone before
 * its deadline was spent - by `collect` here, or by another tab of this browser, or by the page's own poll,
 * all of which take grants from the same store. Asked only while the secret is there: once it is gone the
 * grants that answer it cannot verify.
 */
export async function inviteAnswered({ keystore, inviteId, deadline, now, collect }) {
  const id = invitePendingId(inviteId);
  if (await keystore.pending(id) !== null) await collect();
  if (await keystore.pending(id) !== null) return 'waiting';
  return now < deadline ? 'answered' : 'expired';
}

/**
 * The inviting browser's answer to an enrollment. Returns `{granted, endorsed, notEndorsed}`: how many grants
 * the gateway holds for the new device now, the computers told to trust it, and `{hostId, reason}` for each that
 * was not. `refused: true` when the enrollment's MAC does not verify, and then nothing at all is sent: the key
 * in it is not the one the person's new device made, and a grant to it would hand every computer's key to
 * whoever made it.
 *
 * `hosts` is the snapshot's computers. Only those listed and not revoked are granted and told: the key store
 * keeps the keys of a computer removed since, and the gateway refuses a whole call that names one.
 */
export async function answerEnrollment({ keystore, api, writer, deviceId, hosts, pairKey, inviteId, enrollment }) {
  const devicePublicRaw = publicKey(enrollment?.publicKey);
  if (devicePublicRaw === null || typeof enrollment.deviceId !== 'string' || typeof enrollment.mac !== 'string'
    || !await verifyEnrollment(pairKey, inviteId, enrollment.deviceId, devicePublicRaw, enrollment.mac)) {
    return { refused: true, granted: 0, endorsed: [], notEndorsed: [] };
  }

  const held = new Set(await keystore.hosts());
  const live = hosts.filter((host) => !host.revoked && held.has(host.id));

  // Every computer's newest key first, then each one's next newest, and so on. A delivery spends the invitation's
  // secret (trust.js collectGrants), so a new device that asks between two calls takes the first and refuses the
  // rest. Ordered this way, what it took is at least every computer's current key, and it reads what they send
  // from now on; in the order the keys were made it would have taken old keys and been behind on every computer.
  const ranked = [];
  for (const host of live) {
    const signing = await keystore.hostSigningKey(host.id);
    // A key held with no signing key pinned did not come from a verified grant, and the gateway refuses a grant
    // naming no pinned key: one such computer would refuse the whole call.
    if (signing === null) continue;
    const keys = [...(await keystore.hostKeys(host.id))].reverse();
    for (const [rank, [epoch, secret]] of keys.entries()) {
      ranked.push({
        rank,
        grant: await createGrant({
          hostId: host.id, deviceId: enrollment.deviceId, devicePublicRaw, key: { epoch, secret },
          pairingId: inviteId, pairKey, hostSigningPublic: signing
        })
      });
    }
  }
  const grants = ranked.sort((a, b) => a.rank - b.rank).map((one) => one.grant);

  // Made first and then sent back to back, so the gap a new device can fall into is as short as it can be.
  for (let at = 0; at < grants.length; at += GRANTS_PER_CALL) {
    await publish(api, deviceId, grants.slice(at, at + GRANTS_PER_CALL));
  }

  const endorsed = [];
  const notEndorsed = [];
  const label = typeof enrollment.label === 'string' ? enrollment.label : '';
  for (const host of live) {
    // A computer that moved to a key this device has not been given yet refuses an endorsement sealed under the
    // one before, and says so only on its own screen: the new device would never be given its next key.
    const refusal = sendRefusal(host, await keystore.newestEpoch(host.id));
    if (refusal) {
      notEndorsed.push({ hostId: host.id, reason: refusal });
      continue;
    }
    try {
      const commandId = crypto.randomUUID();
      const sealed = await writer.sealEndorsement(host.id, commandId, enrollment.deviceId, devicePublicRaw, label);
      await api.post(`/api/hosts/${encodeURIComponent(host.id)}/device-commands`, { commandId, kind: 'EndorseDevice', sealed });
      endorsed.push(host.id);
    } catch (error) {
      // One computer out of reach does not keep the others from being told.
      notEndorsed.push({ hostId: host.id, reason: error.message });
    }
  }

  return { granted: grants.length, endorsed, notEndorsed };
}

/**
 * One call of grants. The gateway never replaces a grant a browser made, and refuses the whole call with a 409
 * when one of its grants is held already - an earlier answer to this invitation whose connection dropped
 * part-way. The new device has that grant, so each grant of the call is then sent alone and a 409 passed over;
 * stopping there would leave it without the rest. One call each, on this rare path only: every call of the
 * account counts against one limit.
 */
async function publish(api, deviceId, grants) {
  const options = { headers: { [DEVICE_HEADER]: deviceId } };
  try {
    await api.post('/api/grants', grants, options);
    return;
  } catch (error) {
    if (error?.status !== 409) throw error;
  }
  for (const grant of grants) {
    try {
      await api.post('/api/grants', [grant], options);
    } catch (error) {
      if (error?.status !== 409) throw error;
    }
  }
}

// The gateway relays the key; one that is not a 65-byte uncompressed point is not a device's, and is refused like a
// MAC that does not verify.
function publicKey(text) {
  try {
    const raw = fromB64url(text);
    return raw.length === 65 && raw[0] === 4 ? raw : null;
  } catch {
    return null;
  }
}

/**
 * The link as a QR code, SVG text from the vendored generator. Error correction M, the size chosen for the
 * link's length: a phone's camera reads it off a screen. Scalable, so the page sizes it.
 */
export function inviteQrSvg(link) {
  const code = qrcode(0, 'M');
  code.addData(link);
  code.make();
  return code.createSvgTag({ cellSize: 4, margin: 16, scalable: true, title: 'The invitation link as a QR code' });
}
