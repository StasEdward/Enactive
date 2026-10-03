// One run of a piece of async work at a time. No DOM here, so `node --test` runs it.
//
// Coming back to a tab fires both visibilitychange and focus, and each asked for a poll. Both polls
// carried the same cursor, both were answered with the same delta, and the panel appended it twice:
// every step and notice from while the tab was away showed up twice in the timeline and the inbox.
// A call made while the work is running now shares that run and its result instead of starting another.

/**
 * Wraps `work` so that calls made while it is running get the running promise. A call after it has
 * settled runs it again. `key` separates runs that must not be shared: a call whose key differs from the
 * running one's starts its own run (the poll keys by session generation, so a poll of a session that has
 * ended is never what the next session's first poll waits on).
 *
 * The returned function has `pending`: the running promise, or null.
 */
export function singleFlight(work, key = () => undefined) {
  let running = null;

  const once = (...args) => {
    const current = key();

    if (running && running.key === current) {
      return running.promise;
    }

    const promise = (async () => work(...args))();
    const run = { key: current, promise };
    running = run;

    const settle = () => {
      if (running === run) running = null;
    };
    promise.then(settle, settle);

    return promise;
  };

  Object.defineProperty(once, 'pending', { get: () => running?.promise ?? null });
  return once;
}
