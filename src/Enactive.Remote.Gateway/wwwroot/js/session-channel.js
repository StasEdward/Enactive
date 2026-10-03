// Tells the other tabs of this browser that the session changed, so they ask the gateway about it.
//
// The cookie belongs to the browser, not the tab. Without a word between tabs, Alice's tab shows her runs
// until it is next focused or its next poll, after Bob signed in or she signed out in another one. The
// word is only a hint: `{type: 'changed'}`, no task content, no tokens, no account id. A page of the same
// origin can post anything on this channel, so a received message is never believed - the tab asks the
// gateway's /api/session who is signed in, and that answer is what decides (revalidate, in app.js). A
// BroadcastChannel is a way to wake the other tabs, not a way to authorize anything.
//
// No DOM here, so `node --test` runs it with a fake channel.

const CHANNEL_NAME = 'enactive-session';
const CHANGED = 'changed';

/**
 * Opens the channel. `onChanged` is called with no arguments when another tab announced a change.
 *
 * Kept for the life of the tab, not closed on sign-out: the next account signed in in this tab needs it.
 * A browser without BroadcastChannel, or one that refuses it, gets a channel that does nothing - the tabs
 * then catch up on focus and on their next poll, as before.
 *
 * @returns {{announce: () => void, close: () => void}}
 */
export function openSessionChannel({ onChanged, channelFactory = (name) => new BroadcastChannel(name) }) {
  let channel = null;

  try {
    channel = channelFactory(CHANNEL_NAME);
  } catch {
    // No BroadcastChannel (ReferenceError) or storage blocked (SecurityError): nothing to listen to.
    return { announce() {}, close() {} };
  }

  channel.onmessage = (event) => {
    const data = event?.data;

    // Only the type is read. Whatever else a message carries is dropped here, so it cannot reach the page.
    if (data !== null && typeof data === 'object' && !Array.isArray(data) && data.type === CHANGED) {
      onChanged();
    }
  };

  return {
    /** Tells the other tabs, and only them: a channel does not deliver to the end that posted. */
    announce() {
      try {
        channel?.postMessage({ type: CHANGED });
      } catch {
        // Closed, or the browser refused: the other tabs still revalidate when focused.
      }
    },

    close() {
      if (channel) {
        channel.onmessage = null;
        channel.close();
        channel = null;
      }
    }
  };
}

/**
 * What a tab does when another one announced a change.
 * - With an account: asks the gateway whether it is still that account's (`revalidate`).
 * - At "this device was removed" (`view === 'removed'`): only checks the session (`toSignIn`, which moves to the
 *   sign-in view when there is none). It never boots. That view is shown with the gateway session still open, and
 *   entering the panel from it uses the removed device's keys again - or, after a forget, recreates the deleted key
 *   store and registers the browser as a new device, with nobody having asked.
 * - Otherwise (the sign-in view): had nothing to revalidate, so after another tab signed in it went on offering a
 *   sign-in for a session the browser already holds. It asks the gateway who is signed in instead (`boot`), which
 *   enters the panel if somebody is and stays at the sign-in view if not.
 */
export function onSessionSignal({ account, view, boot, revalidate, toSignIn }) {
  if (account) return revalidate();
  if (view === 'removed') return toSignIn();
  return boot();
}

/**
 * What a tab at "this device was removed" does with the gateway's /api/session answer after a signal: `'sign-in'`
 * when nobody is signed in any more, `'boot'` when another account is (the view is Alice's, the browser is Bob's:
 * the normal account change), `'stay'` when it is the same account - or when the view does not know which account
 * it was for, which cannot be shown to be another one. Never a boot into the panel for the removed account.
 */
export function removedViewAction(view, removedFor) {
  if (!view?.authenticated) return 'sign-in';
  if (removedFor && view.user.id !== removedFor) return 'boot';
  return 'stay';
}
