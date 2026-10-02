// "Download my data": the gateway's export of everything it stores for the account (GET /api/export), opened on
// this device and offered as a file. No DOM here, so `node --test` runs it; app.js clicks the link.
//
// The gateway cannot open a single envelope, so its export alone is a file of ciphertext. What makes it readable
// is this browser's keys, and the readable copy is put together here and handed to the browser as a download:
// nothing of it is sent anywhere. The only request is the GET that fetches the export.
import { NOT_GIVEN } from './reader.js';

/** Said when the gateway refuses a second export within its hour. */
export const ONCE_AN_HOUR = 'You can download your data once an hour';

/**
 * The sealed field of each table the reader can open, and how. Each row's sealed field is replaced by what the
 * reader made of it - `{text}`, `{json}` (with `verified` for a permission request) or `{unreadable: reason}` -
 * under the same name, so the file reads like the export with the envelopes opened.
 *
 * Two kinds of envelope are left as the gateway stored them. A command's payload is an instruction this
 * person's browsers sealed for a computer; it carries nothing the task, run and approval rows do not already
 * open. A grant is a computer's key, sealed to one browser: opened, it would put that key in clear into a file
 * that gets mailed, synced and left in a downloads folder, and anyone holding the file could read everything
 * the computer ever sealed.
 */
const SEALED = [
  ['tasks', 'sealed', (reader, row) => reader.openTask(row)],
  ['runs', 'sealedSummary', (reader, row) => reader.openSummary(row)],
  ['events', 'sealedDetail', (reader, row) => reader.openEvent(row)],
  ['notices', 'sealedDetail', (reader, row) => reader.openNotice(row)],
  ['approvals', 'sealedAction', (reader, row) => reader.openAction(row)],
  ['hostWorkspaces', 'sealedName',
    (reader, row) => reader.openWorkspaceName(row.hostId, { id: row.workspaceId, sealedName: row.sealedName })]
];

/**
 * A copy of `data` (the gateway's export) with every envelope `reader` (reader.js) can open opened, and every
 * one it cannot replaced by the reason. `data` itself is not changed. A row with nothing sealed - a run still
 * going has no summary - is kept as it is.
 */
export async function exportOpened(data, reader) {
  const tables = { ...data.tables };

  await Promise.all(SEALED.map(async ([table, field, open]) => {
    if (!Array.isArray(tables[table])) return;

    tables[table] = await Promise.all(tables[table].map(async (row) => {
      if (row[field] === null || row[field] === undefined) return row;

      let opened;
      try {
        opened = await open(reader, row);
      } catch {
        // The key store could not be read - closed by a sign-out meanwhile, or the browser's storage failing.
        // The record says this device could not reach a key for it, as on screen (app.js openContent), rather
        // than the whole file failing over one record.
        opened = { unreadable: NOT_GIVEN };
      }

      return { ...row, [field]: opened };
    }));
  }));

  return { ...data, tables };
}

/**
 * The opened export as a file: JSON, indented to be read, named for the day of the export as the gateway names
 * its own (`enactive-export-<date>.json`).
 */
export function exportFile(opened) {
  return {
    name: `enactive-export-${String(opened.exportedAt).slice(0, 10)}.json`,
    blob: new Blob([JSON.stringify(opened, null, 2)], { type: 'application/json' })
  };
}

/**
 * Fetches the export with `api.get` (api.js), opens it with `reader`, and resolves to the file `{name, blob}`.
 * Refusals are thrown as api.js throws them; `exportRefusal` says what they mean. The GET carries the antiforgery
 * token: the gateway refuses an export without it (or the browser's word that the page asked), so that a link
 * elsewhere cannot download the account or spend its hour.
 */
export async function downloadMyData({ api, reader }) {
  const data = await api.get('/api/export', { antiforgery: true });
  return exportFile(await exportOpened(data, reader));
}

/**
 * What to tell the person when the export was refused. For the hour's limit, when the next one can be made, from
 * the gateway's Retry-After: "try again later" alone left them guessing, and pressing again only to be refused.
 * Anything else is the refusal's own sentence.
 */
export function exportRefusal(error, now = new Date()) {
  if (error?.code !== 'rate-limited') {
    return error?.message ?? 'The gateway could not be reached.';
  }

  if (typeof error.retryAfter !== 'number') {
    return `${ONCE_AN_HOUR}; try again later.`;
  }

  const at = new Date(now.getTime() + error.retryAfter * 1000);
  return `${ONCE_AN_HOUR}; try again at ${at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}.`;
}
