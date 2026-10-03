// The JS twin of SealedRecords.cs: the associated data for each sealed record, and JSON sealed under a
// host key. AES-GCM authenticates the associated data without hiding it, so a ciphertext only opens under
// the ids it was sealed for: a gateway cannot move a sealed task to another workspace or a sealed start to
// another command. Each kind has its own version string so one kind's data is never read as another's.
// tests/vectors pins every builder to the C# bytes.
import { canonicalBytes } from './canonical.js';
import { EnvelopeError } from './envelope.js';

export const ad = Object.freeze({
  task: (hostId, taskId, workspaceId) =>
    canonicalBytes('enactive-task-v1', ...texts({ hostId, taskId, workspaceId })),

  // `kind` is the C# enum member's name, as the Host writes it ("StartTask"). It is part of the data so a
  // sealed start cannot be replayed as a cancel under the same command id.
  command: (hostId, commandId, kind) =>
    canonicalBytes('enactive-cmd-v1', ...texts({ hostId, commandId, kind })),

  event: (hostId, runId, sequence, kind) =>
    canonicalBytes('enactive-event-v1', ...texts({ hostId, runId }), integer(sequence), ...texts({ kind })),

  approval: (hostId, runId, approvalId, toolCallId, actionHash, remoteDecidable) => {
    // A truthy string or number would be sealed as "1": data that says "the owner may decide this from a phone" for a value that never said so.
    if (typeof remoteDecidable !== 'boolean') throw new TypeError('remoteDecidable is true or false.');
    return canonicalBytes('enactive-approval-v1',
      ...texts({ hostId, runId, approvalId, toolCallId, actionHash }), remoteDecidable ? '1' : '0');
  },

  workspace: (hostId, workspaceId) =>
    canonicalBytes('enactive-workspace-v1', ...texts({ hostId, workspaceId }))
});

export function sealJson(key, value, adBytes) {
  const text = JSON.stringify(value);
  // JSON.stringify answers undefined (not text) for undefined, a function and a symbol, which would seal the word "undefined".
  if (text === undefined) throw new TypeError('A value to seal must be JSON.');
  return key.sealText(text, adBytes);
}

export async function openJson(key, sealed, adBytes) {
  const text = await key.openText(sealed, adBytes);
  try {
    return JSON.parse(text);
  } catch {
    // Authentic text that is not JSON: still an envelope that does not hold what the caller expects.
    throw new EnvelopeError('The envelope does not hold JSON.');
  }
}

// An id that is missing would be written as an empty field, and the record would seal and open under
// data no other record shares. C# cannot make that mistake (its strings are not nullable), so JS refuses it.
function texts(fields) {
  return Object.entries(fields).map(([name, value]) => {
    if (typeof value !== 'string') throw new TypeError(`${name} is text.`);
    return value;
  });
}

// A number beyond 2^53 has lost digits, so it is refused rather than written as a different sequence; a
// bigint is how a caller holds the rest of a C# long. String() never localises, as the C# side's invariant culture.
function integer(value) {
  if (typeof value === 'bigint') {
    if (value < -(2n ** 63n) || value >= 2n ** 63n) throw new TypeError('A sequence is an int64.');
    return String(value);
  }
  if (!Number.isSafeInteger(value)) throw new TypeError('A sequence is an integer.');
  return String(value);
}
