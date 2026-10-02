// What this device can read of what its computers sealed: tasks, the steps of a run and its summary,
// notices, permission requests and workspace names, each opened with the host keys this browser holds.
// No DOM here, so `node --test` runs it.
//
// The gateway carries every envelope and writes every plaintext field around it. So each record is opened
// only under the associated data rebuilt from its own fields (sealed.js), and an envelope moved to another
// record - another run, another step, another workspace - does not open. Nothing opened is believed beyond
// that: a permission request is answerable only when the tool, folder and arguments it carries hash to what
// the computer asked.
import { ad, openJson } from './sealed.js';
import { hostKey } from './hostkey.js';
import { epochOf, EnvelopeError } from './envelope.js';
import { actionHash } from './action-identity.js';
import { b64url } from './bytes.js';

// Why a record is unreadable, said on the page where its content would be. An empty line in its place read
// as "the computer said nothing", which is a different claim from "this device cannot read what it said".
//
// A third reason, a key this device once held and has dropped, is not told apart: the key store keeps no
// record of a key it forgot (dropHost forgets the whole computer), so such a record reads as NOT_GIVEN.
export const NOT_GIVEN = 'this device has not been given the key for this';
export const ALTERED = 'this does not open - it may have been altered';

// A tab left open for a day sees new events and notices for as long as it is open. Without a bound the
// records it opened would be kept for as long as that. This is more than the page keeps on screen at once
// (500 events, 200 notices, 200 tasks and runs, the approvals and workspaces), and a record found is moved
// to the back, so what is let go is what was drawn longest ago.
const KEEP = 4000;

/**
 * A reader over `keystore` (keystore.js), or over nothing when this browser cannot keep keys: then nothing
 * opens, and everything says so. Each method resolves to `{text}`, `{json}` or `{unreadable: reason}`, and
 * to null when the record carries nothing sealed (a run still going has no summary). `openAction` adds
 * `verified`.
 *
 * Results are kept by the record's id and the data it opens under, with its envelope beside them, so a
 * redraw every few seconds does not open the same steps again. An unreadable result is kept too, until
 * `keysChanged()` says this device was given keys: tried again on every call, the records of a computer
 * this device was never admitted to cost one key-store read each, on every poll, for as long as the tab
 * stayed open. `keep` is for a test; the page keeps the default.
 */
export function createReader(keystore, { keep = KEEP } = {}) {
  const opened = new Map();
  // Each computer's key for an epoch, derived once: a first load opens hundreds of records under one key,
  // and each one read the key store and ran the derivation again. Null is kept too - not held - until the
  // keys change.
  const derived = new Map();
  // Moved on by keysChanged(); an unreadable result kept from an earlier count is tried again.
  let keys = 0;

  function keyFor(hostId, epoch) {
    const name = `${hostId}
${epoch}`;
    if (!derived.has(name)) {
      const key = (async () => {
        const secret = keystore ? (await keystore.hostKeys(hostId)).get(epoch) : undefined;
        return secret ? hostKey(epoch, secret) : null;
      })();
      derived.set(name, key);
      // A key store that failed is asked again next time rather than taken to hold nothing.
      key.catch(() => { if (derived.get(name) === key) derived.delete(name); });
    }
    return derived.get(name);
  }

  async function attempt(hostId, envelope, adBytes, read) {
    let epoch;
    try {
      epoch = epochOf(envelope);
    } catch (error) {
      if (error instanceof EnvelopeError) return { unreadable: ALTERED };
      throw error;
    }

    const key = await keyFor(hostId, epoch);
    if (!key) return { unreadable: NOT_GIVEN };

    try {
      return await read(key, envelope, adBytes);
    } catch (error) {
      if (error instanceof EnvelopeError) return { unreadable: ALTERED };
      throw error;
    }
  }

  function open(id, hostId, envelope, data, read) {
    if (envelope === null || envelope === undefined) return Promise.resolve(null);

    let adBytes;
    try {
      adBytes = data();
    } catch (error) {
      // A field the data is built from that is not what the computer wrote - a sequence that is not a
      // whole number, an id that is not text - came from the gateway, and nothing sealed opens under it.
      if (error instanceof TypeError) return Promise.resolve({ unreadable: ALTERED });
      throw error;
    }

    // The data is in the name, not only the id: the gateway names records too, and the text of a record
    // that opened must not be handed back for the same id shown under another run or step. The envelope is
    // compared whole rather than hashed into the name: it is the string the snapshot already holds, so
    // keeping it costs a reference while the record is on screen, and no two envelopes can be mistaken.
    const name = `${id}
${b64url(adBytes)}`;
    const known = opened.get(name);
    if (known && known.envelope === envelope && (!known.unreadable || known.keys === keys)) {
      opened.delete(name);
      opened.set(name, known);
      return known.result;
    }

    // The promise is kept while it runs, so a redraw during a slow open waits for it instead of opening
    // the record a second time.
    const entry = { envelope, keys, unreadable: false };
    entry.result = attempt(hostId, envelope, adBytes, read).then((value) => {
      entry.unreadable = Boolean(value.unreadable);
      return value;
    }, (error) => {
      if (opened.get(name) === entry) opened.delete(name);
      throw error;
    });

    opened.delete(name);
    opened.set(name, entry);
    if (opened.size > keep) opened.delete(opened.keys().next().value);
    return entry.result;
  }

  const text = async (key, envelope, adBytes) => ({ text: await key.openText(envelope, adBytes) });
  const json = async (key, envelope, adBytes) => ({ json: await openJson(key, envelope, adBytes) });

  return {
    /**
     * Says the keys this browser holds changed: a delivery this tab took added some, or another tab stored
     * some in the key store they share (trust.js epochsChanged). What did not open before is tried again,
     * and what was not held is looked for again. Also for keys taken away, so nothing derived outlives them.
     */
    keysChanged() {
      keys += 1;
      derived.clear();
    },

    /** A task's `{title, prompt}`, as the browser that wrote it sealed it. */
    openTask: (task) => open(task.id, task.hostId, task.sealed,
      () => ad.task(task.hostId, task.id, task.workspaceId), json),

    /** A step's sentence. `hostId` is the computer whose key opens it; the event's own by default. */
    openEvent: (event, hostId = event.hostId) => open(event.id, hostId, event.sealedDetail,
      () => ad.event(hostId, event.runId, event.sequence, event.kind), text),

    /**
     * A run's summary: the terminal event's envelope, copied by the gateway, so it opens as that event -
     * at the sequence kept beside it, and of the kind the run's status names (the terminal statuses and
     * the terminal event kinds are the same words).
     */
    openSummary: (run) => open(`summary:${run.id}`, run.hostId, run.sealedSummary,
      () => ad.event(run.hostId, run.id, run.summarySequence, run.status), text),

    /** A notice's detail: the raising event's envelope, copied, with that event's sequence and kind. */
    openNotice: (notice) => open(notice.id, notice.hostId, notice.sealedDetail,
      () => ad.event(notice.hostId, notice.runId, notice.eventSequence, notice.eventKind), text),

    /**
     * A permission request's `{tool, argumentsJson, fullText, workingDirectory, topic}`, and whether it
     * is the action the computer asked about: `verified` recomputes the action hash from the sealed tool,
     * working directory and arguments and compares it with the approval's. That is what an answer is
     * bound to - the hash is in the associated data, so the gateway cannot swap it, and the computer runs
     * only the action that hashes to it. So a request whose sealed action does not match its hash (a fault
     * on the computer, or JS and C# hashing differently) is not offered an answer here.
     *
     * `fullText` is not covered. It is the computer's own rendering of the action for the card, sealed by
     * the computer like everything else, and a computer that rendered one command and ran another is a
     * compromised computer, which this protects nobody from (spec section 2).
     */
    openAction: (approval) => open(approval.id, approval.hostId, approval.sealedAction,
      () => ad.approval(approval.hostId, approval.runId, approval.id, approval.toolCallId,
        approval.actionHash, approval.remoteDecidable),
      async (key, envelope, adBytes) => {
        const { json: action } = await json(key, envelope, adBytes);
        return { json: action, verified: await verify(approval, action) };
      }),

    /** The name a computer gave one of its workspaces. */
    openWorkspaceName: (hostId, workspace) => open(`workspace:${hostId}:${workspace.id}`, hostId,
      workspace.sealedName, () => ad.workspace(hostId, workspace.id), text)
  };
}

/**
 * Whether Allow and Deny may be drawn for `approval`, given what `openAction` made of it: only for a request
 * that opened, that hashes to what the computer asked, and that the computer lets a phone answer. A button
 * on an unverified card would send an answer for the hash while the person read another command.
 */
export function answerable(approval, opened) {
  return approval.remoteDecidable === true && opened?.verified === true;
}

async function verify(approval, action) {
  try {
    const hash = await actionHash(approval.runId, approval.toolCallId, action?.tool, action?.workingDirectory,
      action?.argumentsJson);
    return hash === approval.actionHash;
  } catch (error) {
    // An action missing the fields its hash is made of is not the action anybody asked about.
    if (error instanceof TypeError) return false;
    throw error;
  }
}
