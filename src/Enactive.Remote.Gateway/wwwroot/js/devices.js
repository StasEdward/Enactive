// Removing a browser from the account (spec §5.4), from this one: the gateway is told to refuse it and delete its
// grants, and every computer is sent a sealed RevokeDevice, on which it distrusts the device and moves to a new
// key that it grants to every device still trusted. The gateway alone cannot do it: it holds no key, so a device
// it refuses still opens whatever it is shown of the old key, and only the computer's rotation makes what it
// sends afterwards unreadable to the device removed. No DOM here, so `node --test` runs it; app.js draws the list.
import { keyStanding } from './trust.js';
import { KeystoreClosedError } from './keystore.js';

/** What a removal does and does not do, said before it is made. */
export const revocationWarning = (label) =>
  `${label} can still read what it has already opened. It will not read anything new.`;

/**
 * Why a computer was not told of a removal from here. A computer acts only on what is sealed under its current
 * key, so this device - not paired with it, or not yet given its newest key - cannot tell it, and the removed
 * device stays trusted there until a device that can, or the computer itself, removes it.
 */
export const cannotTell = (hostLabel) =>
  `Cannot tell ${hostLabel} from this device - do it from a device that holds its key, or from the computer.`;

/** Where a computer stands after it was sent a removal. */
export const SENT = 'sent';
export const ROTATED = 'rotated';
export const NOT_ROTATED_YET = 'not rotated yet';
export const NOT_CONFIRMED = 'not confirmed - tell the computers again';

// How many times a removal is told again before it is called not confirmed. Each one waits for a key change, so a
// computer that never rotates for it - another tab's removal of the same device got there first - is not told for
// ever; three covers removals made one after another, which is how they come.
export const MAX_RESENDS = 3;

// How long a computer's rotation is waited for. A computer that is switched off takes the command when it next
// connects, which may be days away: watched for that long, the line would say "sent" for good and read as a
// removal still under way. After this it says it has not happened yet, which stays true until it does.
export const ROTATION_WATCH_MS = 10 * 60 * 1000;

/** The send-cache key of one removal to one computer (writer.js createSendCache). */
export const revokeKey = (deviceId, hostId) => `revoke:${deviceId}:${hostId}`;

/**
 * Removes `deviceId`: at the gateway, then on every computer of `hosts` (the snapshot's) that is not revoked
 * and whose current key this device holds. Resolves to `{sentTo, skipped, failed}`: `sentTo` the computers
 * whose command the gateway took, each `{hostId, commandId, epoch, at}` with the epoch it was sealed under;
 * `skipped` those this device cannot tell (cannotTell); `failed` those whose command did not reach the gateway,
 * each with the reason. Pressed again, it sends each computer the command and envelope it sent before (`sends`,
 * keyed by revokeKey): the gateway takes the same id with the same envelope as the same command, refuses the
 * same id with another envelope, and would queue a new id as a second removal. A refusal of the gateway's own
 * revocation throws, and no computer is told: a computer told first would rotate while the gateway went on
 * serving the device.
 */
export async function revokeDevice({ api, writer, sends, hosts, keystore, deviceId, now = () => Date.now() }) {
  const skipped = [];
  const failed = [];
  const sealed = [];

  // Every envelope is sealed before the gateway is told. Removing this very device, the gateway refuses its
  // calls from then on, and the first call this page makes as the device puts up "This device was removed"
  // and closes the key store - the computers not yet sealed for would never have been told.
  for (const host of hosts.filter((one) => !one.revoked)) {
    if (await keyStanding(keystore, host) !== null) {
      skipped.push({ hostId: host.id, reason: cannotTell(host.label) });
      continue;
    }

    try {
      const newest = await keystore.newestEpoch(host.id);
      const command = await sends.once(revokeKey(deviceId, host.id), newest,
        (commandId) => writer.sealRevocation(host.id, commandId, deviceId));
      sealed.push({ host, command });
    } catch (error) {
      failed.push({ hostId: host.id, reason: `${host.label}: ${error.message}` });
    }
  }

  await api.post(`/api/devices/${encodeURIComponent(deviceId)}/revoke`, {});

  const sentTo = [];
  for (const { host, command } of sealed) {
    try {
      await api.post(`/api/hosts/${encodeURIComponent(host.id)}/device-commands`,
        { commandId: command.id, kind: 'RevokeDevice', sealed: command.sealed });
      sentTo.push({ hostId: host.id, commandId: command.id, epoch: command.epoch, at: now() });
    } catch (error) {
      // One computer out of reach does not keep the others from being told; pressed again, it is sent the same.
      failed.push({ hostId: host.id, reason: `${host.label}: ${error.message}` });
    }
  }

  return { sentTo, skipped, failed };
}

/**
 * "Forget this device": removed like any other device (revokeDevice), then its key store deleted, then signed
 * out (`signOut`, given revokeDevice's answer to say on the sign-in page - forgottenSentence).
 *
 * A computer it could not reach keeps the store and throws: the keys deleted, nothing in this browser could seal
 * that computer's command any more, and the device would stay trusted there - pressed again, the same commands
 * are sent. A computer this device cannot tell at all does not hold it back; nothing here could change that.
 *
 * A store that cannot be deleted - the browser's storage failing, or another tab keeping it open (BlockedError) -
 * still signs out, with `keysKept` set on the answer. The device is removed by then, and a page left signed in
 * with its store closed drew everything unreadable and could not be forgotten from again; the person is told to
 * clear the site's data instead (KEYS_KEPT), and the removed page can delete the keys later (deleteDeviceKeys).
 * Resolves to revokeDevice's answer.
 */
export async function forgetThisDevice({ api, writer, sends, keystore, hosts, signOut, now }) {
  const deviceId = (await keystore.device())?.id ?? null;
  // Never registered with the gateway: no computer was ever told of it either, and there is nothing to remove.
  const result = deviceId === null
    ? { sentTo: [], skipped: [], failed: [] }
    : await revokeDevice({ api, writer, sends, hosts, keystore, deviceId, now });

  if (result.failed.length > 0) {
    throw new Error(`${result.failed.map(({ reason }) => reason).join(' ')} This browser's keys were kept, so `
      + 'Forget this device can be pressed again.');
  }

  try {
    await keystore.forget();
  } catch {
    result.keysKept = true;
  }

  await signOut(result);
  return result;
}

/** What is said when this device's keys could not be deleted from the browser. */
export const KEYS_KEPT =
  "The keys could not be deleted from this browser; clear this site's data in the browser settings.";

/** What the sign-in page says after "Forget this device", with every computer that could not be told from here. */
export function forgottenSentence(result) {
  const first = result.keysKept
    ? `This device was removed from your account. ${KEYS_KEPT}`
    : 'This device was forgotten: its keys are deleted from this browser, and it will not read anything new.';
  return [first, ...result.skipped.map(({ reason }) => reason)].join(' ');
}

/**
 * Deletes the key store of the account `view` (an /api/session answer) names: "Delete this device's keys" on the
 * page shown to a removed device. A Forget that stopped short - a computer out of reach, then a reload - left the
 * device removed at the gateway, and the removed page offered only Sign out: the keys stayed on disk for good.
 * `open` opens a store by account id (keystore.js openKeystore). Rejects with BlockedError while another tab
 * keeps the store open.
 */
export async function deleteDeviceKeys(view, open) {
  if (!view?.authenticated) throw new Error('Sign in again to delete this device\'s keys.');
  const store = await open(view.user.id);
  await store.forget();
}

/**
 * The buttons a device's card offers, `[{kind, busy}]`: `forget` on this device (`ownId`), `remove` on another,
 * and `tell-again` on a removed one - always, not only in the tab that removed it: after a reload, or from
 * another device, a computer the removal missed was otherwise never told, and the gateway and every computer take
 * the removal again as nothing new. `busy` while a removal of that device is under way (`removing`, a Set of
 * device ids): the poll redraws the cards every few seconds, and a redraw put back a live button that `act` had
 * disabled, so a second press ran a second removal over the first.
 */
export function cardActions(device, ownId, removing) {
  const busy = removing.has(device.id);
  if (device.id === ownId) return device.revoked ? [] : [{ kind: 'forget', busy }];
  return [{ kind: device.revoked ? 'tell-again' : 'remove', busy }];
}

/**
 * Watches the removals this tab sent until each computer has rotated for them, and tells a computer again when its
 * rotation cannot have been for this removal.
 *
 * A computer acts on a command sealed under its current key only. Two removals sealed under the same epoch e - a
 * phone and then a tablet, while the computer was asleep - meet one key change: the computer removes the phone,
 * moves to e+1 and grants it to every device still trusted, the tablet among them, then refuses the tablet's
 * command as sealed before a device was removed. Read as "the epoch rose", both were reported rotated, and the
 * tablet kept the new key. So a removal counts as rotated only when the computer moved exactly one epoch past the
 * one its latest command was sealed under, and that command was the first of this tab's sealed under that epoch
 * for that computer; otherwise it is sealed again under the newest key once this device holds it (a computer
 * returns without rotating for a device it has removed already, so this is harmless). At most MAX_RESENDS times:
 * after that it is NOT_CONFIRMED.
 *
 * `record(deviceId, sentTo)` takes revokeDevice's `sentTo`; `step(...)` runs on every poll and resolves to
 * whether anything changed; `lines(deviceId, now)` is `[{hostId, status}]` to draw.
 */
export function createRemovalWatch() {
  let watches = [];
  // Which command was sent first, for the first-sealed rule; a clock can give two sends one time.
  let order = 0;

  return {
    record(deviceId, sentTo) {
      for (const { hostId, epoch, at } of sentTo) {
        const kept = watches.find((one) => one.deviceId === deviceId && one.hostId === hostId);
        // Confirmed stays confirmed: told again later, the computer returns early and never rotates for it.
        if (kept?.state === ROTATED) continue;
        watches = watches.filter((one) => one !== kept);
        watches.push({ deviceId, hostId, epoch, at, resends: 0, state: null, order: order++ });
      }
    },

    async step({ hosts, keystore, api, writer, sends, now = () => Date.now() }) {
      let changed = false;

      for (const watch of watches) {
        if (watch.state !== null) continue;
        const host = hosts.find((one) => one.id === watch.hostId);
        if (!Number.isInteger(host?.keyEpoch) || host.keyEpoch <= watch.epoch) continue;

        const first = watches
          .filter((one) => one.hostId === watch.hostId && one.epoch === watch.epoch)
          .reduce((earliest, one) => (one.order < earliest.order ? one : earliest));
        if (first === watch && host.keyEpoch === watch.epoch + 1) {
          watch.state = ROTATED;
          changed = true;
          continue;
        }

        if (watch.resends >= MAX_RESENDS) {
          watch.state = NOT_CONFIRMED;
          changed = true;
          continue;
        }

        // Sealed under the key before, it would be refused again: wait for the grant of the newest one.
        const newest = keystore ? await keystore.newestEpoch(watch.hostId) : null;
        if (newest === null || newest < host.keyEpoch) continue;

        try {
          const command = await sends.once(revokeKey(watch.deviceId, watch.hostId), newest,
            (commandId) => writer.sealRevocation(watch.hostId, commandId, watch.deviceId));
          await api.post(`/api/hosts/${encodeURIComponent(watch.hostId)}/device-commands`,
            { commandId: command.id, kind: 'RevokeDevice', sealed: command.sealed });
          Object.assign(watch, { epoch: command.epoch, at: now(), resends: watch.resends + 1, order: order++ });
          changed = true;
        } catch {
          // Out of reach: the next poll tries again.
        }
      }

      return changed;
    },

    lines(deviceId, now = Date.now()) {
      return watches.filter((one) => one.deviceId === deviceId).map(({ hostId, state, at }) => ({
        hostId,
        status: state ?? (now - at >= ROTATION_WATCH_MS ? NOT_ROTATED_YET : SENT)
      }));
    },

    clear() {
      watches = [];
    }
  };
}

/**
 * Whether `keystore` can no longer be read: closed, or deleted by "Forget this device" in another tab. That tab's
 * forget() closes this tab's connection to the database, and every call after it rejects with the browser's own
 * InvalidStateError - which, caught as a failure to reach the gateway, left this tab polling with every record
 * drawn unreadable and no word of why. Only those two errors: any other failure to read is not a store that is
 * gone, and the page would have said this device was removed when it was not.
 */
export async function storeGone(keystore) {
  if (!keystore) return true;
  try {
    await keystore.device();
    return false;
  } catch (error) {
    return error instanceof KeystoreClosedError || error?.name === 'InvalidStateError';
  }
}
