// What this device sends its computers, sealed: a task's title and prompt, and the person's authorization
// for every command - start, cancel, an answer to a permission request, a device's removal or admission.
// The gateway carries each one and writes every plaintext field around it, so the computer acts only on what
// opens under its key for that command's id and kind (sealed.js), and compares it with the plaintext. No DOM
// here, so `node --test` runs it.
//
// The record shapes are the C# ones the Host reads (SealedRecords.cs) with RemoteJson: camelCase members,
// enums by name, and no member it does not know - one extra or misspelled member and the command is refused.
import { ad, sealJson } from './sealed.js';
import { hostKey } from './hostkey.js';
import { epochOf } from './envelope.js';
import { b64url } from './bytes.js';

/** Why the panel sends nothing to a computer this device holds no key for. */
export const NOT_PAIRED_TO_SEND = 'This device is not paired with this computer.';

/** Nothing is sealed for a computer this device holds no key for. */
export class NoKeyError extends Error {
  constructor(hostId) {
    super(NOT_PAIRED_TO_SEND);
    this.name = 'NoKeyError';
    this.hostId = hostId;
  }
}

/** Why it sends nothing to a computer that moved to a key whose grant has not reached this device yet. */
export const NOT_YET_GIVEN = "This device has not received this computer's newest key yet - it will in a moment";

/**
 * Whether the panel may seal for `host` (a HostView from the snapshot), given the newest epoch this device
 * holds for it: null when it may, the sentence to show when it may not. A computer that has rotated opens
 * commands under its current key only and refuses one sealed under the key before as "sealed before a device
 * was removed"; for a cancel or an answer the panel never hears of that refusal, so the click would have
 * looked accepted and done nothing. A snapshot older than the key store is no reason to refuse: the newest
 * key held is the computer's current one.
 */
export function sendRefusal(host, newest) {
  if (newest === null || newest === undefined) return NOT_PAIRED_TO_SEND;
  return Number.isInteger(host?.keyEpoch) && newest < host.keyEpoch ? NOT_YET_GIVEN : null;
}

const DECISIONS = Object.freeze(['Allow', 'Deny']);

/**
 * A writer over `keystore` (keystore.js), or over nothing when this browser cannot keep keys: then every
 * seal throws NoKeyError. Each method resolves to an envelope sealed under the newest epoch this device
 * holds for the computer. `now` is the clock `issuedAt` is read from; a test passes its own.
 */
export function createWriter(keystore, now = () => Date.now()) {
  async function newestKey(hostId) {
    const held = keystore ? await keystore.hostKeys(hostId) : new Map();
    if (held.size === 0) throw new NoKeyError(hostId);
    const epoch = Math.max(...held.keys());
    return hostKey(epoch, held.get(epoch));
  }

  // When the person sent it, as the Host parses a DateTimeOffset. The Host refuses a command older than the
  // lifetime a command may wait, so a gateway that kept one cannot carry it out a week later.
  const issuedAt = () => new Date(now()).toISOString();

  const command = async (hostId, commandId, kind, value) =>
    sealJson(await newestKey(hostId), value, ad.command(hostId, commandId, kind));

  return {
    async sealTask(hostId, taskId, workspaceId, { title, prompt } = {}) {
      const adBytes = ad.task(hostId, taskId, workspaceId);
      return sealJson(await newestKey(hostId), { title: text(title, 'title'), prompt: text(prompt, 'prompt') }, adBytes);
    },

    sealStart: async (hostId, commandId, taskId, workspaceId) => command(hostId, commandId, 'StartTask',
      { taskId: text(taskId, 'taskId'), workspaceId: text(workspaceId, 'workspaceId'), issuedAt: issuedAt() }),

    sealCancel: async (hostId, commandId, runId) => command(hostId, commandId, 'CancelRun',
      { runId: text(runId, 'runId'), issuedAt: issuedAt() }),

    /** `actionHash` exactly as the approval carried it: the computer runs only the action that hashes to it. */
    sealDecision: async (hostId, commandId, approvalId, actionHash, decision) => command(hostId, commandId, 'ResolveApproval', {
      approvalId: text(approvalId, 'approvalId'),
      actionHash: text(actionHash, 'actionHash'),
      decision: oneOf(decision, DECISIONS, 'decision'),
      issuedAt: issuedAt()
    }),

    sealRevocation: async (hostId, commandId, deviceId) => command(hostId, commandId, 'RevokeDevice',
      { deviceId: text(deviceId, 'deviceId'), issuedAt: issuedAt() }),

    /**
     * `devicePublic` is the new device's 65 raw bytes; it is sealed so a gateway cannot have its own key
     * endorsed in the device's place. `label` goes as given: the computer cleans it for display.
     */
    sealEndorsement: async (hostId, commandId, deviceId, devicePublic, label) => command(hostId, commandId, 'EndorseDevice', {
      deviceId: text(deviceId, 'deviceId'),
      devicePublic: publicKey(devicePublic),
      label: text(label, 'label'),
      issuedAt: issuedAt()
    })
  };
}

/**
 * What the panel sends for one logical action - a start of this task, a cancel of this run, Allow on this
 * request - kept across retries as `{id, sealed}`: the command id (or the task id) and the envelope.
 *
 * The gateway treats a repeated id as the same instruction only when what comes with it is the same, and
 * AES-GCM with a fresh nonce makes every seal a different envelope: a retry that sealed again under the same
 * id was refused as a different action, and one under a new id queued the action twice. So a retry sends back
 * exactly what the first attempt sent.
 *
 * Except across a key change. A computer that rotated refuses an envelope sealed under the key before, and for
 * a cancel or an answer the panel never hears of it, so a click after the rotation that resent the old envelope
 * would do nothing, every time. An action whose envelope is of another epoch than the newest held is sealed
 * again, under a new id - the gateway would refuse the old id with a new envelope. The person clicks again;
 * nothing is resent on its own.
 */
export function createSendCache() {
  const entries = new Map();

  return {
    /** The action's `{id, sealed}` for `epoch`, the newest epoch held; `seal(id)` makes the envelope if none fits. */
    async once(key, epoch, seal) {
      const kept = entries.get(key);
      if (kept) {
        const held = await kept.catch(() => null);
        if (held && held.epoch === epoch) return held;
        if (entries.get(key) === kept) entries.delete(key);
      }

      const made = (async () => {
        const id = crypto.randomUUID();
        const sealed = await seal(id);
        return { id, sealed, epoch: epochOf(sealed) };
      })();
      entries.set(key, made);
      // A seal that failed sent nothing, so there is nothing a retry must repeat.
      made.catch(() => { if (entries.get(key) === made) entries.delete(key); });
      return made;
    },

    /** Lets an action go once it is done with, so the next one like it is a new action. */
    forget(key) {
      entries.delete(key);
    }
  };
}

// A field that is not text would be sealed as null or left out of the JSON, and the computer would refuse
// the command for a reason only its log shows.
function text(value, name) {
  if (typeof value !== 'string') throw new TypeError(`${name} is text.`);
  return value;
}

// An enum is read by its member's name; anything else is not a decision the person made.
function oneOf(value, names, name) {
  if (!names.includes(value)) throw new TypeError(`${name} is one of ${names.join(', ')}.`);
  return value;
}

function publicKey(raw) {
  if (!(raw instanceof Uint8Array) || raw.length !== 65 || raw[0] !== 4) {
    throw new TypeError('A device public key is 65 bytes beginning with 0x04.');
  }
  return b64url(raw);
}
