// Removing a browser from the account (spec §5.4), from this one: the gateway is told to refuse it and delete its
// grants, and every computer is sent a sealed RevokeDevice, on which it distrusts the device and moves to a new
// key that it grants to every device still trusted. The gateway alone cannot do it: it holds no key, so a device
// it refuses still opens whatever it is shown of the old key, and only the computer's rotation makes what it
// sends afterwards unreadable to the device removed. No DOM here, so `node --test` runs it; app.js draws the list.
import { keyStanding } from './trust.js';

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
 * out (`signOut`, given revokeDevice's answer to say on the sign-in page - forgottenSentence). A computer it could not reach keeps the store and throws: the keys deleted, nothing in this
 * browser could seal that computer's command any more, and the device would stay trusted there - pressed again,
 * the same commands are sent. A computer this device cannot tell at all does not hold it back; nothing here
 * could change that. Resolves to revokeDevice's answer.
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

  await keystore.forget();
  await signOut(result);
  return result;
}

/** What the sign-in page says after "Forget this device", with every computer that could not be told from here. */
export function forgottenSentence(result) {
  return ['This device was forgotten: its keys are deleted from this browser, and it will not read anything new.',
    ...result.skipped.map(({ reason }) => reason)].join(' ');
}

/**
 * Where each computer of `sent` (revokeDevice's `sentTo`) stands in `snapshotHosts`: ROTATED once its key epoch
 * is above the one the command was sealed under - the computer has acted on it and granted every other device
 * the new key - SENT until then, and NOT_ROTATED_YET once ROTATION_WATCH_MS have passed without it.
 */
export function rotationWatch(sent, snapshotHosts, now = Date.now()) {
  return sent.map(({ hostId, epoch, at }) => {
    const host = snapshotHosts.find((one) => one.id === hostId);
    if (Number.isInteger(host?.keyEpoch) && host.keyEpoch > epoch) return { hostId, status: ROTATED };
    return { hostId, status: now - at >= ROTATION_WATCH_MS ? NOT_ROTATED_YET : SENT };
  });
}

/**
 * Whether `keystore` can no longer be read: closed, or deleted by "Forget this device" in another tab. That tab's
 * forget() closes this tab's connection to the database, and every call after it rejects with the browser's own
 * InvalidStateError - which, caught as a failure to reach the gateway, left this tab polling with every record
 * drawn unreadable and no word of why.
 */
export async function storeGone(keystore) {
  if (!keystore) return true;
  try {
    await keystore.device();
    return false;
  } catch {
    return true;
  }
}
