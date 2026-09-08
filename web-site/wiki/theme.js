// Small synchronous file sets the saved theme before first paint; CSP needs no inline scripts.
try {
  const saved = localStorage.getItem('enactive-wiki-theme');
  document.documentElement.dataset.theme = saved === 'light' ? 'light' : 'dark';
} catch { /* Private/file contexts retain the accessible dark default. */ }
