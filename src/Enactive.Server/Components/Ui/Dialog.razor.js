// A dialog's hold on the page (Dialog.razor): while it is open everything else is inert - out of reach of Tab, a
// pointer and a screen reader - and when it closes the keyboard is back where it was. Without it the dialog was drawn
// over the screen but not in charge of it: Tab walked the rows behind the scrim, and another row's Delete could be
// pressed while a question was open, replacing it.

// The dialogs open now, by key: what had the focus before each, and what each made inert.
const shown = new Map();

/** Makes everything but the dialog inert, and remembers what had the focus. */
export function hold(key, dialog) {
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
  shown.set(key, { opener: document.activeElement, inerted });
}

/** Gives the page back, and the focus to what had it - unless that is gone (the row a question deleted). */
export function release(key) {
  const held = shown.get(key);
  if (!held) return;
  shown.delete(key);
  for (const element of held.inerted) element.inert = false;
  if (held.opener instanceof HTMLElement && held.opener.isConnected && !held.opener.closest('[inert]')) held.opener.focus();
}
