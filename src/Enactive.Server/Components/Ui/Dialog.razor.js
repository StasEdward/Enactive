// A dialog's hold on the page (Dialog.razor): while it is open everything else is inert - out of reach of Tab, a
// pointer and a screen reader - and when it closes the keyboard is back where it was. Without it the dialog was drawn
// over the screen but not in charge of it: Tab walked the rows behind the scrim, and another row's Delete could be
// pressed while a question was open, replacing it.

// The dialogs open now, by key: what had the focus before each, and what each made inert.
const shown = new Map();

/**
 * Makes everything but the dialog inert, and remembers what had the focus. `home` (the dialog itself unless given) is
 * where the focus goes when a hold opened over this one ends and what opened that one is gone.
 */
export function hold(key, dialog, home) {
  // A dialog not on the page (closed before this ran) holds nothing: held for it, the page stayed inert for good.
  if (!(dialog instanceof Element) || !dialog.isConnected) return;
  // The connection's state stays in reach: its Reload is the one way back when the connection is lost (ReconnectState).
  const keep = [dialog, document.getElementById('components-reconnect-modal')].filter(Boolean);
  const inerted = [];
  const visit = (parent) => {
    for (const child of parent.children) {
      if (keep.includes(child)) continue;
      if (keep.some((k) => child.contains(k))) visit(child);
      else if (!child.inert) {
        child.inert = true;
        inerted.push(child);
      }
    }
  };
  visit(document.body);
  shown.set(key, { opener: document.activeElement, inerted, home: home instanceof HTMLElement ? home : dialog });
}

/** Gives the page back, and the focus to what had it - unless that is gone (the row a question deleted). */
export function release(key) {
  const held = shown.get(key);
  if (!held) return;
  shown.delete(key);
  for (const element of held.inerted) element.inert = false;
  // The body is nothing to go back to: it is where the focus is when what had it was taken off the page before the
  // dialog opened (Add folder… closes the switcher it was in).
  const opener = held.opener;
  if (opener instanceof HTMLElement && opener !== document.body && opener.isConnected && !opener.closest('[inert]')) {
    opener.focus();
    return;
  }
  // What opened it is gone: into the hold still open, the newest, rather than nowhere - left on the page's body, Escape
  // and Tab no longer reached the drawer under the dialog.
  const under = [...shown.values()].pop();
  if (under && under.home.isConnected) under.home.focus();
}
