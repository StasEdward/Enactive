// Which signed-in session the page is working for, and what it forgets when that changes.
//
// A request is answered whenever the network gets round to it. One started while Alice was signed in
// can come back after she signed out and Bob signed in, and the page would draw Alice's runs on Bob's
// screen - or Alice's 401 would sign Bob out. So every request takes the generation it started under,
// and a reset (sign-out, an expired session, another account) moves the generation on: whatever comes
// back for an earlier one is dropped before anything reads it. No DOM here, so `node --test` runs it.

/** What a request started under an earlier session ends with. Callers drop it silently. */
export class Stale extends Error {
  constructor() {
    super('This answer belongs to a session that has ended.');
    this.name = 'Stale';
  }
}

export function createGuard() {
  let generation = 0;

  return {
    /** The generation a request starting now belongs to. */
    get generation() { return generation; },

    /** Ends the current generation: everything started under it is dropped from now on. */
    bump() { generation += 1; return generation; },

    isCurrent(started) { return started === generation; },

    /** Throws Stale unless `started` is still the current generation. */
    check(started) {
      if (started !== generation) throw new Stale();
    }
  };
}

// An empty screen: every array, the cursor, the unread count and the open run. A cursor kept across
// accounts would ask for the next account's history from the previous one's position, and an open run
// would go on redrawing a timeline that is not the new account's.
export function emptyState() {
  return {
    cursor: null,
    hosts: [],
    tasks: [],
    runs: [],
    approvals: [],
    notices: [],
    events: [],
    unread: 0,
    retention: null,
    live: false,
    openRun: null
  };
}

/** Puts `state` back to an empty screen in place, so every module holding it sees the reset. */
export function resetState(state) {
  return Object.assign(state, emptyState());
}

// What the page holds for the person outside `state`: what was typed into a dialog and not sent, a
// computer's credential shown once, a timeline, an error line, the last toast. Closing a dialog keeps
// its fields, so before this list the next person in the tab opened "New task" and found the previous
// one's title and prompt filled in, ready to send as their own, and the last computer's credential sat in
// a hidden field. Every field of every dialog is on it; tests/panel/session-guard.test.mjs checks the page.
export const FORGOTTEN = {
  values: ['task-title', 'task-prompt', 'host-name', 'host-code', 'invite-link'],
  contents: ['task-workspace', 'run-title', 'run-detail', 'task-error', 'host-status', 'host-error', 'invite-qr',
    'invite-status', 'invite-warning', 'invite-error', 'join-status', 'join-error', 'toast'],
  hidden: ['host-secret', 'invite-secret', 'toast']
};

/** Empties everything on FORGOTTEN and closes `openDialogs`. `byId` finds an element by its id. */
export function forgetScreen(byId, openDialogs) {
  for (const dialog of openDialogs) dialog.close();
  for (const id of FORGOTTEN.values) byId(id).value = '';
  for (const id of FORGOTTEN.contents) byId(id).replaceChildren();
  for (const id of FORGOTTEN.hidden) byId(id).hidden = true;
}

/**
 * One poll: the snapshot is drawn only if it names the account the page signed in as.
 *
 * The cookie belongs to the browser, not the tab. Bob signing in, in another window, changes whose state
 * Alice's open tab is polling for, and a tab that went on drawing whatever came back showed Bob's runs
 * and notices under Alice's name - appended to hers when her cursor happened to be valid on his line. A
 * snapshot that names nobody is treated the same way: it cannot be shown to be this account's.
 *
 * @returns {Promise<boolean>} whether it was drawn; `otherAccount` was called if not.
 */
export async function pollOnce({ read, accountId, apply, otherAccount }) {
  const snapshot = await read();

  if (!snapshot || snapshot.userId !== accountId) {
    otherAccount();
    return false;
  }

  apply(snapshot);
  return true;
}
