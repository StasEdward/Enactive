// The colour theme: the system's unless this browser chose light or dark with the top bar's button.
//
// Loaded in the head, before the page is drawn, and not deferred: set any later, a light page showed dark for a frame
// on every load. It only marks <html> (data-theme, which theme.css reads) - nothing on the service knows or keeps the
// choice: it is this browser's, and the service has nothing to do with how a person likes to read.
//
// The button is found by its data-theme-toggle attribute at the time of a click, not wired once: the pages are drawn by
// Blazor after this has run (no prerender, App.razor) and drawn again as their connection comes and goes, so a button
// wired at load would be a button that no longer exists.
(() => {
  const key = 'enactive-theme';
  const order = ['system', 'light', 'dark'];
  const names = { system: 'System', light: 'Light', dark: 'Dark' };
  const media = window.matchMedia('(prefers-color-scheme: light)');
  const root = document.documentElement;

  const chosen = () => {
    try {
      const saved = localStorage.getItem(key);
      return order.includes(saved) ? saved : 'system';
    } catch {
      // Storage refused (a locked-down browser): the system's theme, every load.
      return 'system';
    }
  };

  const label = (button, choice) => {
    const next = order[(order.indexOf(choice) + 1) % order.length];
    const text = `Colour theme: ${names[choice]}. Switch to ${names[next]}`;
    button.setAttribute('aria-label', text);
    button.setAttribute('title', text);
  };

  const apply = (choice) => {
    root.dataset.theme = choice === 'system' ? (media.matches ? 'light' : 'dark') : choice;
    root.dataset.themeChoice = choice;
    document.querySelectorAll('[data-theme-toggle]').forEach((b) => label(b, choice));
  };

  // Held here as well as stored: where storage is refused, a click must still move on from what the last one chose.
  let current = chosen();
  apply(current);
  // The system's theme changed (a laptop's evening switch) while the page is open: followed, unless a theme was chosen.
  media.addEventListener('change', () => apply(current));

  document.addEventListener('click', (event) => {
    const button = event.target instanceof Element ? event.target.closest('[data-theme-toggle]') : null;
    if (!button) return;
    current = order[(order.indexOf(current) + 1) % order.length];
    try {
      localStorage.setItem(key, current);
    } catch {
      // Not kept past this page; it still applies now.
    }
    apply(current);
  });

  // A button Blazor draws later gets its words when it appears: its aria-label says the theme, and Blazor's own markup
  // for it can only hold a fixed one.
  new MutationObserver(() => {
    document.querySelectorAll('[data-theme-toggle]:not([data-theme-labelled])').forEach((b) => {
      b.setAttribute('data-theme-labelled', '');
      label(b, current);
    });
  }).observe(root, { childList: true, subtree: true });
})();
