// Adding a device by an invitation link (spec §5.3), both ends of it. A trusted browser makes an invitation -
// an id the gateway keeps and a pairing secret it never sees - and shows the link; the new device opens it,
// posts an enrollment with a MAC over its own public key, and waits for its grants. The inviter reads the
// enrollment, checks the MAC, so the gateway cannot have put a key of its own in the new device's place, and
// grants it every computer key it holds, each authenticated with the pair key, then asks each computer to
// trust the device too. No DOM here, so `node --test` runs it; app.js draws the dialogs.
import { qrcode } from '../vendor/qrcode.mjs';
import { b64url, fromB64url } from './bytes.js';
import { authByPairing, createGrant } from './grants.js';
import { derivePairKey, enrollmentMac, parseInviteFragment, verifyEnrollment } from './pairing.js';
import { BEHIND, DEVICE_HEADER, invitePendingId, keyStanding } from './trust.js';

/** How long an invitation can be answered: the gateway's InviteLifetime. */
export const INVITE_LIFETIME_MS = 10 * 60 * 1000;

/** DeviceService.MaxGrantsPerCall: a call with more is refused whole. */
export const GRANTS_PER_CALL = 50;

/** What the inviting browser says when the enrollment's MAC does not verify. */
export const SWAPPED_KEY = 'Someone other than your new device answered this invitation. Nothing was shared.';

/**
 * Why a computer the inviter is behind on was not asked to trust the new device. Not the writer's "it will in a
 * moment": the new device was given only the keys before the computer's current one, the computer was never
 * told of it, and nothing on either side will put that right by waiting.
 */
export const behindReason = (label) =>
  `${label}: this device does not hold its newest key; add the new device from that computer, or again from here `
  + 'once this device has caught up.';

/** Said before the link is shown, so the person can catch up first rather than spend the invitation. */
export const behindWarning = (hosts) => hosts.length === 0
  ? ''
  : `Keys for ${hosts.map((host) => host.label).join(', ')} will not be shared until this device catches up.`;

const LINK_PATH = '/pair';
const KEPT = 'enactive.invite';

/** An invitation id as the gateway takes them: 32 lowercase hex characters, from the browser's CSPRNG. */
export const newInviteId = () => crypto.randomUUID().replaceAll('-', '');

/**
 * Reads the invitation out of the address the new device opened, taking it out of the address first. The
 * fragment holds the pairing secret: left there, it stays in the tab's history, in a bookmark or a synced
 * session, readable long after it was used. Replaced before it is parsed, so a link too damaged to use goes
 * too - its secret may be whole when the rest is not. `expiresAt` is ten minutes from now: no invitation lives
 * longer, so nothing this page keeps of one needs to.
 */
export function openInviteLink(hash, history, now = Date.now()) {
  history.replaceState(null, '', LINK_PATH);
  return { ...parseInviteFragment(hash), expiresAt: now + INVITE_LIFETIME_MS };
}

/**
 * Keeps an invitation for the sign-in round trip. A provider's sign-in leaves the page and comes back to
 * another address, and the invitation opened signed out would be lost with the fragment it came in. Session
 * storage: this tab only, and gone with it. Failing storage (a private window, storage turned off) keeps
 * nothing, and the person opens the link again once signed in.
 */
export function keepInvite(storage, { inviteId, secret, expiresAt }) {
  try {
    storage.setItem(KEPT, JSON.stringify({ inviteId, secret: b64url(secret), expiresAt }));
  } catch {
    // Nothing kept; see above.
  }
}

/**
 * The invitation kept across a sign-in, taken out of storage as it is read: one use. Null when none, and when
 * it is past its expiry: a tab left on the sign-in page kept the secret for as long as the tab lived, and a
 * sign-in hours later answered an invitation long dead.
 */
export function takeKeptInvite(storage, now) {
  let text;
  try {
    text = storage.getItem(KEPT);
    storage.removeItem(KEPT);
  } catch {
    return null;
  }
  if (text === null) return null;
  try {
    const { inviteId, secret, expiresAt } = JSON.parse(text);
    if (typeof inviteId !== 'string' || inviteId.length === 0) return null;
    if (!Number.isFinite(expiresAt) || now >= expiresAt) return null;
    return { inviteId, secret: fromB64url(secret), expiresAt };
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
 * One turn of the new device's wait: 'collecting' after asking for its grants (`collect`), or 'expired' once
 * the invitation's secret is past its deadline. Asked for until then, a first delivery or not: the secret is
 * kept until it expires (trust.js collectGrants), and the inviter may still have calls of grants to send.
 */
export async function inviteJoinStep({ keystore, inviteId, deadline, now, collect }) {
  if (now >= deadline || await keystore.pending(invitePendingId(inviteId)) === null) return 'expired';
  await collect();
  return 'collecting';
}

/** How many keys a delivery (collectGrants' result) added under this invitation. */
export const countInviteGrants = (result, inviteId) =>
  (result?.added ?? []).filter((one) => one.authBy === authByPairing(inviteId)).length;

/**
 * The computers of `hosts` (the snapshot's) whose current key this device holds: what it can read now. One it
 * holds only older keys of is left out - what that computer sends now is sealed under a key it does not have.
 */
export async function readableHosts(keystore, hosts) {
  return standing(keystore, hosts, (one) => one === null);
}

/** The computers of `hosts` that have moved to a key this device does not hold yet (trust.js BEHIND). */
export async function behindHosts(keystore, hosts) {
  return standing(keystore, hosts, (one) => one === BEHIND);
}

async function standing(keystore, hosts, wanted) {
  const found = [];
  for (const host of hosts) {
    if (!host.revoked && wanted(await keyStanding(keystore, host))) found.push(host);
  }
  return found;
}

/**
 * What an answer has already done, kept by the caller across retries of one answer: how many of each computer's
 * keys landed (oldest first, so always a prefix of them), and which computers were told. A retry after a call
 * that failed part way - a dropped connection, the account's limit - starts after what landed. Started over
 * instead, every grant was made and sent again, and each call that had landed came back a 409 and was sent again
 * grant by grant, which spent the account's limit on keys the new device already had.
 */
export const newAnswerProgress = () => ({ landed: new Map(), endorsed: new Set() });

/**
 * The inviting browser's answer to an enrollment. Returns `{granted, endorsed, notEndorsed}`: how many grants
 * the gateway holds for the new device now, the computers told to trust it, and `{hostId, reason}` (a sentence
 * to show) for each that was not. `refused: true` when the enrollment's MAC does not verify, and then nothing
 * at all is sent: the key in it is not the one the person's new device made, and a grant to it would hand every
 * computer's key to whoever made it.
 *
 * `hosts` is the snapshot's computers. Only those listed and not revoked are granted and told: the key store
 * keeps the keys of a computer removed since, and the gateway refuses a whole call that names one.
 */
export async function answerEnrollment({
  keystore, api, writer, deviceId, hosts, pairKey, inviteId, enrollment, progress = newAnswerProgress()
}) {
  const devicePublicRaw = publicKey(enrollment?.publicKey);
  if (devicePublicRaw === null || typeof enrollment.deviceId !== 'string' || typeof enrollment.mac !== 'string'
    || !await verifyEnrollment(pairKey, inviteId, enrollment.deviceId, devicePublicRaw, enrollment.mac)) {
    return { refused: true, granted: 0, endorsed: [], notEndorsed: [] };
  }

  const held = new Set(await keystore.hosts());
  const live = hosts.filter((host) => !host.revoked && held.has(host.id));
  let granted = 0;

  for (const host of live) {
    const signing = await keystore.hostSigningKey(host.id);
    // A key held with no signing key pinned did not come from a verified grant, and the gateway refuses a grant
    // naming no pinned key: one such computer would refuse the whole call.
    if (signing === null) continue;

    // One computer per call, its keys oldest first. The new device may take its grants between two calls, and
    // it passes over any epoch at or below the newest it holds (trust.js): sent newest first, every older key
    // in a later call was passed over for good. Sent oldest first, each call only extends its run upwards.
    const keys = [...await keystore.hostKeys(host.id)];
    for (let at = progress.landed.get(host.id) ?? 0; at < keys.length; at += GRANTS_PER_CALL) {
      const part = keys.slice(at, at + GRANTS_PER_CALL);
      const grants = [];
      for (const [epoch, secret] of part) {
        grants.push(await createGrant({
          hostId: host.id, deviceId: enrollment.deviceId, devicePublicRaw, key: { epoch, secret },
          pairingId: inviteId, pairKey, hostSigningPublic: signing
        }));
      }
      await publish(api, deviceId, grants);
      progress.landed.set(host.id, at + part.length);
    }
    granted += keys.length;
  }

  const endorsed = [];
  const notEndorsed = [];
  const label = typeof enrollment.label === 'string' ? enrollment.label : '';
  for (const host of live) {
    if (progress.endorsed.has(host.id)) {
      endorsed.push(host.id);
      continue;
    }
    // A computer that moved to a key this device has not been given yet refuses an endorsement sealed under the
    // one before, and says so only on its own screen: the new device would never be given its next key.
    if (await keyStanding(keystore, host) === BEHIND) {
      notEndorsed.push({ hostId: host.id, reason: behindReason(host.label) });
      continue;
    }
    try {
      const commandId = crypto.randomUUID();
      const sealed = await writer.sealEndorsement(host.id, commandId, enrollment.deviceId, devicePublicRaw, label);
      await api.post(`/api/hosts/${encodeURIComponent(host.id)}/device-commands`, { commandId, kind: 'EndorseDevice', sealed });
      progress.endorsed.add(host.id);
      endorsed.push(host.id);
    } catch (error) {
      // One computer out of reach does not keep the others from being told.
      notEndorsed.push({ hostId: host.id, reason: `${host.label} was not asked to trust it: ${error.message}` });
    }
  }

  return { granted, endorsed, notEndorsed };
}

/**
 * One call of grants. The gateway never replaces a grant a browser made, and refuses the whole call with a 409
 * when one of its grants is held already - a call that landed but whose answer was lost. The new device has
 * that grant, so each grant of the call is then sent alone and a 409 passed over; stopping there would leave it
 * without the rest.
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

/**
 * The inviting dialog's wait, one `check()` per tick: 'waiting', 'retrying' (with `error`), or one of the ends,
 * 'answered' (with `result`), 'expired' and 'failed' (with `error`), which every later check returns as it is.
 *
 * - The enrollment is read until there is one, and once more at the deadline: an enrollment posted in time can
 *   be read after it, and a dialog that stopped at its deadline without asking left a new device that had
 *   answered waiting ten minutes for keys that never came.
 * - Read once, it is not read again: the invitation is used (`onEnrollment` takes the link down), and what is
 *   retried is the answer, with the progress it keeps. A retry is made until the deadline; one that fails after
 *   it ends the wait, where it once went on every three seconds for as long as the dialog stayed open.
 */
export function createInviteWatch({ read, answer, onEnrollment = () => {}, deadline, now }) {
  let enrollment = null;
  let ended = null;

  return {
    async check() {
      if (ended) return ended;

      if (enrollment === null) {
        let found;
        try {
          found = await read();
        } catch (error) {
          if (now() >= deadline) return (ended = { state: 'failed', error });
          return { state: 'waiting', error };
        }
        if (!found) return now() >= deadline ? (ended = { state: 'expired' }) : { state: 'waiting' };
        enrollment = found;
        onEnrollment(enrollment);
      }

      try {
        return (ended = { state: 'answered', enrollment, result: await answer(enrollment) });
      } catch (error) {
        if (now() >= deadline) return (ended = { state: 'failed', enrollment, error });
        return { state: 'retrying', enrollment, error };
      }
    }
  };
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
