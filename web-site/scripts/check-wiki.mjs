import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const site=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const wiki=path.join(site,'wiki');
const names=(await fs.readdir(wiki)).filter(n=>n.endsWith('.html'));
const errors=[]; let links=0, examples=0;
const escape=s=>s.replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const pages=new Map();
const descriptions=new Set();
for (const name of names) pages.set(path.join(wiki,name),await fs.readFile(path.join(wiki,name),'utf8'));
if (names.length!==13) errors.push(`Expected 13 article pages, found ${names.length}`);
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
  const description=html.match(/<meta name="description" content="([^"]+)">/);
  if (description) {
    if (descriptions.has(description[1])) errors.push(`${path.basename(file)}: duplicate meta description`);
    descriptions.add(description[1]);
  }
  for (const [label,pattern] of [
    ['description',/<meta name="description" content="[^"]+">/],
    ['indexing directive',/<meta name="robots" content="index,follow,max-image-preview:large">/],
    ['canonical URL',/<link rel="canonical" href="https:\/\/enactive\.dev\/wiki\/[^"]+">/],
    ['Open Graph metadata',/<meta property="og:title" content="[^"]+">/],
    ['structured article data',/<script type="application\/ld\+json">\{"@context":"https:\/\/schema\.org","@type":"TechArticle"/]
  ]) if (!pattern.test(html)) errors.push(`${path.basename(file)}: missing ${label}`);
  const jsonLd=html.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/);
  if (jsonLd) try { JSON.parse(jsonLd[1]); } catch { errors.push(`${path.basename(file)}: invalid structured data JSON`); }
  if (/href="(?!https?:)[^"#]*\.md(?:#|\")/.test(html)) errors.push(`${file}: unresolved Markdown navigation`);
  if (/<script(?![^>]*\bsrc=)(?![^>]*type="application\/ld\+json")[^>]*>|\sstyle=|\son\w+=/i.test(html)) errors.push(`${file}: inline content violates CSP`);
  for (const m of html.matchAll(/\b(?:href|src)="([^"]+)"/g)) await checkLink(file,m[1]);
  const source=html.match(/Markdown source ↗<\/a>/) && html.match(/href="https:\/\/github.com\/StasEdward\/Enactive\/blob\/master\/Docs\/wiki\/([^"#]+\.md)"/);
  if (!source) { errors.push(`${file}: no source link`); continue; }
  const markdown=await fs.readFile(path.join(site,'../Docs/wiki',source[1]),'utf8');
  for (const block of markdown.matchAll(/```[^\r\n]*\r?\n([\s\S]*?)\r?\n```/g)) {
    if (!html.includes(escape(block[1].replaceAll('\r\n','\n')))) errors.push(`${file}: changed code/diagram example`);
    examples++;
  }
}
const landing=await fs.readFile(path.join(site,'index.html'),'utf8');
if ((landing.match(/<h1[ >]/g)||[]).length!==1) errors.push('index.html: expected one H1');
if (!/<a class="skip-link" href="#main-content">/.test(landing) || !/<main id="main-content">/.test(landing)) errors.push('index.html: missing keyboard skip link or main landmark');
for (const match of landing.matchAll(/\b(?:href|src)="([^"]+)"/g)) await checkLink(path.join(site,'index.html'),match[1]);
for (const [label,pattern] of [
  ['title',/<title>[^<]+<\/title>/],
  ['description',/<meta name="description" content="[^"]+"\s*\/?\s*>/],
  ['WebSite structured data',/<script type="application\/ld\+json">[\s\S]*?"@type": "WebSite"/],
  ['canonical URL',/<link rel="canonical" href="https:\/\/enactive\.dev\/"\s*\/?\s*>/],
  ['SoftwareApplication structured data',/<script type="application\/ld\+json">[\s\S]*?"@type": "SoftwareApplication"/],
  ['Open Graph image',/<meta property="og:image" content="https:\/\/enactive\.dev\/assets\/og\.png"/]
]) if (!pattern.test(landing)) errors.push(`index.html: missing ${label}`);
const landingJsonLd=landing.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/);
if (landingJsonLd) try { JSON.parse(landingJsonLd[1]); } catch { errors.push('index.html: invalid structured data JSON'); }
else errors.push('index.html: missing structured data JSON');
const explainer=pages.get(path.join(wiki,'what-is-enactive.html'));
for (const phrase of ['What does Enactive do?', 'Which AI models and providers does it support?', 'Can I use GPT, Claude, or Gemini with Enactive?', 'Does a remote task run in the cloud?', 'Primary references']) {
  if (!explainer?.includes(phrase)) errors.push(`what-is-enactive.html: missing answer section ${phrase}`);
}
if (!landing.includes('href="wiki/what-is-enactive.html"')) errors.push('index.html: missing product explainer link');
const sitemap=await fs.readFile(path.join(site,'sitemap.xml'),'utf8');
for (const file of names) if (!sitemap.includes(`https://enactive.dev/wiki/${file}`)) errors.push(`sitemap.xml: missing ${file}`);
if (!sitemap.includes('https://enactive.dev/</loc>')) errors.push('sitemap.xml: missing landing page');
const robots=await fs.readFile(path.join(site,'robots.txt'),'utf8');
if (!robots.includes('Sitemap: https://enactive.dev/sitemap.xml')) errors.push('robots.txt: missing sitemap directive');
for (const agent of ['OAI-SearchBot','Claude-SearchBot','Claude-User','Googlebot','Google-Extended']) {
  const group=robots.match(new RegExp(`User-agent: ${agent}\\r?\\n([\\s\\S]*?)(?:\\r?\\n\\r?\\n|$)`));
  if (!group?.[1].includes('Allow: /')) errors.push(`robots.txt: ${agent} is not explicitly allowed`);
}
const search=JSON.parse((await fs.readFile(path.join(wiki,'search-index.js'),'utf8')).replace(/^window.enactiveWikiSearch = /,'').replace(/;\s*$/,''));
for (const entry of search) await checkLink(path.join(wiki,'index.html'),entry.url);
for (const css of ['wiki.css','assets/brand.css','assets/fonts.css']) {
  const file=path.join(wiki,css), text=await fs.readFile(file,'utf8');
  for (const m of text.matchAll(/url\(['"]?([^)'"\s]+)['"]?\)/g)) await checkLink(file,m[1]);
}
if (!(landing.match(/href="wiki\/index.html"/g)||[]).length) errors.push('Landing page has no Wiki link');
if (errors.length) { console.error(errors.join('\n')); process.exitCode=1; }
else console.log(`PASS: ${names.length} pages, ${links} local links/assets/search anchors, ${examples} preserved code/diagram examples, unique IDs, and CSP-safe markup.`);
