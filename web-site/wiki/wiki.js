const themeToggle = document.querySelector('#theme-toggle');
function themeLabel() {
  const next = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  themeToggle.textContent = `${next === 'light' ? 'Light' : 'Dark'} theme`;
  themeToggle.setAttribute('aria-label', `Switch to ${next} theme`);
}
themeLabel();
themeToggle.addEventListener('click', () => {
  document.documentElement.dataset.theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  try { localStorage.setItem('enactive-wiki-theme', document.documentElement.dataset.theme); } catch {}
  themeLabel();
});
const menu = document.querySelector('#menu-toggle');
menu.addEventListener('click', () => {
  const expanded = menu.getAttribute('aria-expanded') !== 'true';
  menu.setAttribute('aria-expanded', String(expanded));
  document.querySelector('#sidebar').classList.toggle('is-open', expanded);
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && menu.getAttribute('aria-expanded') === 'true') {
    menu.click(); menu.focus();
  }
});

const dialog = document.querySelector('#search-dialog');
const input = document.querySelector('#search-input');
const results = document.querySelector('#search-results');
const status = document.querySelector('#search-status');
function openSearch() { dialog.showModal(); input.focus(); }
document.querySelector('.search-trigger').addEventListener('click', openSearch);
document.querySelector('#close-search').addEventListener('click', () => dialog.close());
dialog.addEventListener('click', event => {
  const box = dialog.getBoundingClientRect();
  if (event.target === dialog && (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom)) dialog.close();
});
document.addEventListener('keydown', event => {
  if (event.key === 'Escape' && dialog.open) { event.preventDefault(); dialog.close(); }
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault(); if (!dialog.open) openSearch();
  }
});
input.addEventListener('input', () => {
  const query = input.value.trim().toLowerCase();
  results.replaceChildren();
  if (!query) { status.textContent = 'Search across all chapters.'; return; }
  const terms = query.split(/\s+/);
  const matches = (window.enactiveWikiSearch || []).map(section => {
    const heading = `${section.page} ${section.title}`.toLowerCase();
    const text = section.text.toLowerCase();
    const score = terms.every(t => (heading+' '+text).includes(t))
      ? terms.reduce((n,t) => n + (heading.includes(t) ? 10 : 1),0) : 0;
    return {section,score};
  }).filter(m => m.score).sort((a,b)=>b.score-a.score).slice(0,16);
  status.textContent = matches.length ? `${matches.length} matching sections${matches.length===16?' (top results)':''}. Tab to a result to open it.` : 'No matching sections. Try a shorter query or a setting name.';
  for (const {section} of matches) {
    const a = document.createElement('a'); a.href = section.url; a.className = 'search-result';
    const eyebrow = document.createElement('small'); eyebrow.textContent = section.page;
    const title = document.createElement('strong'); title.textContent = section.title;
    const snippet = document.createElement('p');
    const at = Math.max(0,section.text.toLowerCase().indexOf(terms[0])-60);
    snippet.textContent = (at?'…':'')+section.text.slice(at,at+220).trim()+'…';
    a.append(eyebrow,title,snippet); a.addEventListener('click',()=>dialog.close()); results.append(a);
  }
});
document.querySelectorAll('.copy-button').forEach(button => {
  button.addEventListener('click', async () => {
    const code = button.closest('.code-block').querySelector('code');
    try {
      await navigator.clipboard.writeText(code.textContent);
      button.textContent = 'Copied'; document.querySelector('#copy-status').textContent = 'Code copied to clipboard.';
    } catch {
      const range = document.createRange(); range.selectNodeContents(code);
      const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range);
      button.textContent = 'Selected'; document.querySelector('#copy-status').textContent = 'Clipboard unavailable. Code selected; press Control C or Command C to copy.';
    }
    setTimeout(()=>button.textContent='Copy',2000);
  });
});
const tocLinks = [...document.querySelectorAll('.page-toc nav a')];
if ('IntersectionObserver' in window) {
  const observer = new IntersectionObserver(entries => {
    for (const entry of entries) if (entry.isIntersecting) {
      tocLinks.forEach(link => {
        const current = link.hash.slice(1)===entry.target.id;
        if (current) link.setAttribute('aria-current','location'); else link.removeAttribute('aria-current');
      });
    }
  },{rootMargin:'-90px 0px -65% 0px'});
  document.querySelectorAll('article h2').forEach(h=>observer.observe(h));
}
