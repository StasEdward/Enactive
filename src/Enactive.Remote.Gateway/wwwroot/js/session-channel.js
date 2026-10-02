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
 * What a tab does when another one announced a change. A tab with an account asks the gateway whether it is still
 * that account's (`revalidate`). A tab with none - left at the sign-in view, or at "this device was removed" - had
 * nothing to revalidate, so after another tab signed in it went on offering a sign-in for a session the browser
 * already holds: it asks the gateway who is signed in instead (`boot`), which enters the panel if somebody is and
 * stays at the sign-in view if not.
 */
export function onSessionSignal({ account, boot, revalidate }) {
  return account ? revalidate() : boot();
}
