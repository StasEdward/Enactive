// The JS twin of ActionIdentity.cs: the hash that binds an approval to the exact action it approved. The
// browser recomputes it from the sealed action, so a gateway that attaches an "allow" to another action
// is caught on this side too. tests/vectors pins it to the C# hashes.
import { canonicalBytes } from './canonical.js';

export const VERSION = 'enactive-action-v1';

/**
 * The hash of one bound action, as lowercase hex. The arguments are the exact text the tool will be
 * handed: not re-serialised and not reformatted, because a tidied copy is a different string.
 */
export async function actionHash(runId, toolCallId, tool, workingDirectory, argumentsJson) {
  // Absent text would be hashed as "" and the owner would approve an action the Host never described;
  // C# has non-nullable strings here, and only the working directory may be empty or missing.
  for (const [name, value] of Object.entries({ runId, toolCallId, tool, argumentsJson })) {
    if (typeof value !== 'string') throw new TypeError(`The action's ${name} is text.`);
  }
  const bytes = canonicalBytes(VERSION, runId, toolCallId, tool, normaliseDirectory(workingDirectory), argumentsJson);
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', bytes));
  return Array.from(digest, (byte) => byte.toString(16).padStart(2, '0')).join('');
}

// One spelling for one folder: forward slashes, no trailing separator. A model writes C:\work\p, C:/work/p
// and C:\work\p\ for the same place across three calls, and an approval that stops matching because of a
// slash is an approval nobody can give. The same rule as ActionIdentity.NormaliseDirectory.
function normaliseDirectory(path) {
  if (path === undefined || path === null || path === '') return '';
  if (typeof path !== 'string') throw new TypeError("The action's working directory is text.");
  const normalised = path.replaceAll('\\', '/').replace(/\/+$/, '');
  // A drive root is the one place where the trailing separator is the path: "C:" is a drive-relative reference, not a folder.
  return normalised.length === 2 && normalised[1] === ':' ? normalised + '/' : normalised;
}
