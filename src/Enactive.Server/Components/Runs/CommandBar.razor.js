// The command bar's keys (CommandBar.razor), as the window's (MainWindow.OnPreviewKeyDown): Enter in the box runs what it
// holds, Ctrl+Enter is a new line in it, and Ctrl+K anywhere on the page puts the focus in it. A browser's own Enter in a
// textarea is a new line and its Ctrl+Enter nothing, so without this the hint above the box said keys that did not work.
// Shift+Enter is left as the browser's new line - in a browser that is what it is everywhere else.

// The boxes attached now, with what each added to the page, to be taken off again.
const attached = new Map();

/** Attaches the keys to the box; Enter calls the bar's RunFromKeyboard with what the box holds. */
export function attach(box, bar) {
  if (!(box instanceof HTMLTextAreaElement) || attached.has(box)) return;

  const inBox = (e) => {
    if (e.key !== 'Enter' || e.isComposing || e.altKey || e.metaKey) return;
    if (e.ctrlKey) {
      e.preventDefault();
      box.setRangeText('\n', box.selectionStart, box.selectionEnd, 'end');
      // Told to the page as typing is, or the bar's copy of the text would not have the new line.
      box.dispatchEvent(new Event('input', { bubbles: true }));
      return;
    }
    if (e.shiftKey) return;
    e.preventDefault();
    // The text as it is now, sent with the call: the bar's copy arrives by its own event, and need not be there first.
    bar.invokeMethodAsync('RunFromKeyboard', box.value);
  };

  const onPage = (e) => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey || e.shiftKey || e.key.toLowerCase() !== 'k') return;
    // Not into a box that cannot be typed in, nor past an open dialog (Dialog.razor.js makes the rest inert).
    if (box.disabled || !box.isConnected || box.closest('[inert]')) return;
    e.preventDefault();
    box.focus();
    box.select();
  };

  box.addEventListener('keydown', inBox);
  document.addEventListener('keydown', onPage);
  attached.set(box, () => {
    box.removeEventListener('keydown', inBox);
    document.removeEventListener('keydown', onPage);
  });
}

/** Takes the keys off again - the bar has gone. */
export function detach(box) {
  const off = attached.get(box);
  if (!off) return;
  attached.delete(box);
  off();
}

/** The focus in the box with the caret after its text, to go on typing (a verb chip). */
export function focusEnd(box) {
  if (!(box instanceof HTMLTextAreaElement) || !box.isConnected) return;
  box.focus();
  box.setSelectionRange(box.value.length, box.value.length);
}
