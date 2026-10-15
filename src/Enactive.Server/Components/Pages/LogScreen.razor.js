// The log page's two hands in the browser (LogScreen.razor): following the tail, as the window's list scrolls the newest
// line into view, and Export, which hands the visible log to the person as a file - the window writes it into its own log
// folder, and on the service that folder is on a machine the person reading may never see.

/** Scrolls the list to its end, so the newest line is in view. */
export function follow(list) {
  if (list instanceof HTMLElement) list.scrollTop = list.scrollHeight;
}

/** Offers the text as a file of this name, as a download. */
export function download(name, text) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = name;
  document.body.appendChild(link);
  link.click();
  link.remove();
  // Revoked after the click has been handled: revoked at once, a browser may cancel the download before reading the file.
  setTimeout(() => URL.revokeObjectURL(url), 0);
}
