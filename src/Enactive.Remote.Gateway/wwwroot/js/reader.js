// What this device can read of what its computers sealed: tasks, the steps of a run and its summary,
// notices, permission requests and workspace names, each opened with the host keys this browser holds.
// No DOM here, so `node --test` runs it.
//
// The gateway carries every envelope and writes every plaintext field around it. So each record is opened
// only under the associated data rebuilt from its own fields (sealed.js), and an envelope moved to another
// record - another run, another step, another workspace - does not open. Nothing opened is believed beyond
// that: a permission request is answerable only when the action it shows hashes to what the computer asked.
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
// records it opened would be kept for as long as that; this is more than the page keeps on screen at once
// (500 events, 200 notices, 200 tasks and runs, the approvals and workspaces), so nothing drawn is evicted.
const KEEP = 4000;

/**
 * A reader over `keystore` (keystore.js), or over nothing when this browser cannot keep keys: then nothing
 * opens, and everything says so. Each method resolves to `{text}`, `{json}` or `{unreadable: reason}`, and
 * to null when the record carries nothing sealed (a run still going has no summary). `openAction` adds
 * `verified`.
 *
 * Results are kept by the record's id, the data it opens under and its envelope, so a redraw every few
 * seconds does not open the same steps again. Only what opened is kept: an unreadable record is tried again
 * on the next call, which is how a grant that arrives between two polls opens what was waiting for it.
 */
export function createReader(keystore) {
  const opened = new Map();

  async function attempt(hostId, envelope, adBytes, read) {
    let epoch;
    try {
      epoch = epochOf(envelope);
    } catch (error) {
      if (error instanceof EnvelopeError) return { unreadable: ALTERED };
      throw error;
    }

    const secret = keystore ? (await keystore.hostKeys(hostId)).get(epoch) : undefined;
    if (!secret) return { unreadable: NOT_GIVEN };

    try {
      return await read(await hostKey(epoch, secret), envelope, adBytes);
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
    // that opened must not be handed back for the same id shown under another run or step.
    const name = `${id}\n${b64url(adBytes)}\n${envelope}`;
    const known = opened.get(name);
    if (known) return known;

    // The promise is kept while it runs, so a redraw during a slow open waits for it instead of opening
    // the record a second time; it is let go as soon as it turns out unreadable.
    const result = attempt(hostId, envelope, adBytes, read).then((value) => {
      if (value.unreadable) opened.delete(name);
      return value;
    }, (error) => {
      opened.delete(name);
      throw error;
    });

    opened.set(name, result);
    if (opened.size > KEEP) opened.delete(opened.keys().next().value);
    return result;
  }

  const text = async (key, envelope, adBytes) => ({ text: await key.openText(envelope, adBytes) });
  const json = async (key, envelope, adBytes) => ({ json: await openJson(key, envelope, adBytes) });

  return {
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
     * is the action the computer asked about. The card shows `fullText`, and the hash the computer checks
     * an answer against covers `argumentsJson`; both are sealed, so a computer - or a key holder - could
     * seal one that shows one command and hashes another. `verified` is false then, and the card offers
     * no answer.
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
