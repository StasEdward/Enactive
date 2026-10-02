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
