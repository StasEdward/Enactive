import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const site=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const wiki=path.join(site,'wiki');
const names=(await fs.readdir(wiki)).filter(n=>n.endsWith('.html'));
const errors=[]; let links=0, examples=0;
const escape=s=>s.replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const pages=new Map();
for (const name of names) pages.set(path.join(wiki,name),await fs.readFile(path.join(wiki,name),'utf8'));
if (names.length!==12) errors.push(`Expected 12 article pages, found ${names.length}`);
async function checkLink(from,target) {
  if (/^(?:https?:|mailto:|data:)/.test(target)) return;
  const [pathname,anchor]=target.split('#');
  const dest=path.resolve(path.dirname(from),decodeURIComponent(pathname || path.basename(from)));
  if (!dest.startsWith(site+path.sep)) { errors.push(`Link escapes site: ${target}`); return; }
  try { await fs.access(dest); } catch { errors.push(`${path.basename(from)}: missing ${target}`); return; }
  if (anchor && dest.endsWith('.html')) {
    const html=pages.get(dest) || await fs.readFile(dest,'utf8');
    if (!html.includes(`id="${decodeURIComponent(anchor)}"`)) errors.push(`${path.basename(from)}: missing anchor ${target}`);
  }
  links++;
}
for (const [file,html] of pages) {
  const ids=[...html.matchAll(/\bid="([^"]+)"/g)].map(m=>m[1]);
  if (new Set(ids).size!==ids.length) errors.push(`${file}: duplicate IDs`);
  if ((html.match(/<h1[ >]/g)||[]).length!==1) errors.push(`${file}: expected one H1`);
  if (/href="(?!https?:)[^"#]*\.md(?:#|\")/.test(html)) errors.push(`${file}: unresolved Markdown navigation`);
  if (/<script(?![^>]*\bsrc=)[^>]*>|\sstyle=|\son\w+=/i.test(html)) errors.push(`${file}: inline content violates CSP`);
  for (const m of html.matchAll(/\b(?:href|src)="([^"]+)"/g)) await checkLink(file,m[1]);
  const source=html.match(/Markdown source ↗<\/a>/) && html.match(/href="https:\/\/github.com\/StasEdward\/Enactive\/blob\/master\/Docs\/wiki\/([^"#]+\.md)"/);
  if (!source) { errors.push(`${file}: no source link`); continue; }
  const markdown=await fs.readFile(path.join(site,'../Docs/wiki',source[1]),'utf8');
  for (const block of markdown.matchAll(/```[^\r\n]*\r?\n([\s\S]*?)\r?\n```/g)) {
    if (!html.includes(escape(block[1].replaceAll('\r\n','\n')))) errors.push(`${file}: changed code/diagram example`);
    examples++;
  }
}
const search=JSON.parse((await fs.readFile(path.join(wiki,'search-index.js'),'utf8')).replace(/^window.enactiveWikiSearch = /,'').replace(/;\s*$/,''));
for (const entry of search) await checkLink(path.join(wiki,'index.html'),entry.url);
for (const css of ['wiki.css','assets/brand.css','assets/fonts.css']) {
  const file=path.join(wiki,css), text=await fs.readFile(file,'utf8');
  for (const m of text.matchAll(/url\(['"]?([^)'"\s]+)['"]?\)/g)) await checkLink(file,m[1]);
}
const landing=await fs.readFile(path.join(site,'index.html'),'utf8');
if (!(landing.match(/href="wiki\/index.html"/g)||[]).length) errors.push('Landing page has no Wiki link');
if (errors.length) { console.error(errors.join('\n')); process.exitCode=1; }
else console.log(`PASS: ${names.length} pages, ${links} local links/assets/search anchors, ${examples} preserved code/diagram examples, unique IDs, and CSP-safe markup.`);
