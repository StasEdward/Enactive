// Build committed, standalone HTML from Docs/wiki. No browser-side Markdown parser or CDN.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { marked } = await import(pathToFileURL(require.resolve('marked')).href);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const site = path.join(root, 'web-site');
const source = path.join(root, 'Docs/wiki');
const out = path.join(site, 'wiki');
const repo = 'https://github.com/StasEdward/Enactive/blob/master/';
const pages = [
  ['README', 'index', 'Wiki home', 'Start here'],
  ['Overview', 'overview', 'Application overview', 'Start here'],
  ['Getting-Started', 'getting-started', 'Getting started', 'Start here'],
  ['Running-Tasks', 'running-tasks', 'Running tasks', 'Workflows'],
  ['Console', 'console', 'Console & automation', 'Workflows'],
  ['Templates', 'templates', 'Templates', 'Workflows'],
  ['Remote-Access', 'remote-access', 'Remote access', 'Workflows'],
  ['Settings', 'settings', 'Settings', 'Configuration'],
  ['Models-and-Phases', 'models-and-phases', 'Models & Phases', 'Configuration'],
  ['Architecture', 'architecture', 'Architecture', 'Reference'],
  ['Operations', 'operations', 'Operations & troubleshooting', 'Reference']
].map(([file, slug, label, group]) => ({file, slug, label, group}));
const escape = text => String(text).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const plain = text => text.replace(/\[([^\]]+)\]\([^)]*\)/g, '$1').replace(/[*`_]/g, '').replace(/<[^>]*>/g, '');
const slugify = text => plain(text).toLowerCase().replace(/[^\p{L}\p{N}_ -]/gu, '').replace(/ /g, '-');
const hrefFor = target => {
  if (/^(https?:|mailto:|#)/.test(target)) return target;
  const [file, anchor] = target.split('#');
  const wiki = pages.find(p => `${p.file}.md` === file);
  if (wiki) return `${wiki.slug}.html${anchor ? '#' + anchor : ''}`;
  // Documents were moved from /wiki into /Docs/wiki; resolve old repository links too.
  let relative;
  if (/^\.\.\/(src|server|Docs)\//.test(file) || file === '../global.json') relative = file.slice(3);
  else relative = path.relative(root, path.resolve(source, file)).replaceAll('\\', '/');
  if (relative.startsWith('..')) throw new Error(`Link escapes repository: ${target}`);
  return repo + relative.split('/').map(encodeURIComponent).join('/') + (anchor ? '#' + anchor : '');
};

// The source contains two simple Mermaid flowchart TD graphs. Render every node/edge
// as an accessible, native HTML dependency diagram; fail if a new construct is added.
function diagram(text, number) {
  const nodes = new Map(); const edges = [];
  function node(raw) {
    const m = raw.trim().match(/^(\w+)(?:\[([^\]]+)\]|\{([^}]+)\})?$/);
    if (!m) throw new Error(`Unsupported diagram node: ${raw}`);
    if (m[2] || m[3]) nodes.set(m[1], {id:m[1], label:m[2] || m[3], decision:!!m[3]});
    else if (!nodes.has(m[1])) nodes.set(m[1], {id:m[1], label:m[1]});
    return m[1];
  }
  for (const line of text.split('\n').map(s=>s.trim()).filter(Boolean)) {
    if (line === 'flowchart TD') continue;
    const m = line.match(/^(.*?)\s+-->\s*(?:\|([^|]+)\|\s*)?(.+)$/);
    if (!m) throw new Error(`Unsupported diagram syntax: ${line}`);
    edges.push({from:node(m[1]), to:node(m[3]), label:m[2] || ''});
  }
  const ranks = new Map();
  for (let pass=0; pass<nodes.size; pass++) {
    for (const id of nodes.keys()) {
      if (ranks.has(id)) continue;
      const parents = edges.filter(e=>e.to===id);
      if (parents.every(e=>ranks.has(e.from))) ranks.set(id, parents.length ? Math.max(...parents.map(e=>ranks.get(e.from)))+1 : 0);
    }
  }
  if (ranks.size!==nodes.size) throw new Error('Diagram contains unresolved/cyclic edges');
  const levels = Array.from({length:Math.max(...ranks.values())+1},()=>[]);
  for (const n of nodes.values()) levels[ranks.get(n.id)].push(n);
  return `<figure class="diagram" aria-labelledby="diagram-${number}"><figcaption id="diagram-${number}">Flow from input to outcome</figcaption><ol class="diagram-levels">${levels.map(level=>`<li><div class="diagram-row">${level.map(n=>{
    const incoming = edges.filter(e=>e.to===n.id);
    return `<div class="diagram-node${n.decision?' decision':''}"><strong>${escape(n.label)}</strong>${incoming.length?`<ul>${incoming.map(e=>`<li>From ${escape(nodes.get(e.from).label)}${e.label?` <span>· ${escape(e.label)}</span>`:''}</li>`).join('')}</ul>`:'<small>Input</small>'}</div>`;
  }).join('')}</div></li>`).join('')}</ol><details><summary>View diagram source</summary><pre><code>${escape(text)}</code></pre></details></figure>`;
}

await fs.mkdir(out, {recursive:true});
await fs.mkdir(path.join(out,'assets/fonts'), {recursive:true});
await fs.copyFile(path.join(root,'brand/brand.css'), path.join(out,'assets/brand.css'));
for (const name of ['mark-loop.svg','wordmark.svg','wordmark-light.svg']) await fs.copyFile(path.join(root,'brand/logos',name),path.join(out,'assets',name));
// The fonts come from the gateway's wwwroot, which is where the shipped copies live. They used to
// be read from server/wwwroot - the prototype gateway - and that directory was removed when the
// real one replaced it, so this build had been failing since. The wiki was still being served from
// its last committed HTML, which is exactly the shape of failure that goes unnoticed.
const fonts = path.join(root,'src/Enactive.Remote.Gateway/wwwroot/fonts');
for (const name of await fs.readdir(fonts)) {
  if (/\.(woff2|txt)$/.test(name)) await fs.copyFile(path.join(fonts,name),path.join(out,'assets/fonts',name));
}
const search = [];
for (const [index, page] of pages.entries()) {
  // Normalised on read, so the output does not depend on which platform built it. The search
  // index splits on \n and kept whatever was left, which on a CRLF checkout is a trailing \r in
  // every extracted line — so a Windows build and a Linux build of the same source produced
  // different files. That is a small bug on its own and a fatal one for a CI step that rebuilds
  // and compares: the check would have been red on arrival, for a reason having nothing to do
  // with anybody's edit.
  const markdown = (await fs.readFile(path.join(source, page.file+'.md'),'utf8')).replace(/\r\n/g,'\n');
  const title = plain(markdown.match(/^# (.+)$/m)[1]);
  const minutes = Math.max(1,Math.ceil(markdown.split(/\s+/).length/220));
  const toc=[]; const used=new Map(); let diagramCount=0;
  const renderer = new marked.Renderer();
  renderer.heading = function(token) {
    const base=slugify(token.text), count=used.get(base)||0; used.set(base,count+1);
    const id=base+(count?'-'+count:'');
    if (token.depth===1) return '';
    if (token.depth===2 || token.depth===3) toc.push({id, title:plain(token.text),depth:token.depth});
    return `<h${token.depth} id="${id}">${this.parser.parseInline(token.tokens)}<a class="heading-anchor" href="#${id}" aria-label="Link to ${escape(plain(token.text))}">#</a></h${token.depth}>\n`;
  };
  renderer.link = function(token) {
    return `<a href="${escape(hrefFor(token.href))}"${token.title?` title="${escape(token.title)}"`:''}>${this.parser.parseInline(token.tokens)}</a>`;
  };
  renderer.code = function(token) {
    if (token.lang==='mermaid') return diagram(token.text,++diagramCount);
    const language=token.lang || 'text';
    return `<div class="code-block"><div class="code-toolbar"><span>${escape(language)}</span><button class="copy-button" type="button" aria-label="Copy ${escape(language)} example">Copy</button></div><pre tabindex="0"><code class="language-${escape(language)}">${escape(token.text)}</code></pre></div>\n`;
  };
  const baseTable = renderer.table;
  renderer.table = function(token) {
    if (page.file==='README' && token.header[0].text==='Page') {
      return `<div class="chapter-grid">${token.rows.map((row,i)=>`<a class="chapter-card" href="${escape(hrefFor(row[0].text.match(/\]\(([^)]+)\)/)[1]))}"><span class="chapter-number">${String(i+1).padStart(2,'0')}</span><h3>${escape(plain(row[0].text))}</h3><p>${escape(plain(row[1].text))}</p><span class="chapter-arrow" aria-hidden="true">↗</span></a>`).join('')}</div>`;
    }
    return `<div class="table-wrap" tabindex="0" role="region" aria-label="${escape(token.header.map(h=>plain(h.text)).join(' / '))}">${baseTable.call(this,token)}</div>`;
  };
  const content=marked.parse(markdown.replace(/^\[Wiki home\]\(README.md\)\s*$/m,''), {renderer,gfm:true});
  const nav = pages.map((p,i)=>`${i===0 || p.group!==pages[i-1].group?`<p class="nav-group">${p.group}</p>`:''}<a href="${p.slug}.html"${p===page?' aria-current="page"':''}>${p.label}</a>`).join('');
  const tocHtml=toc.filter(t=>t.depth===2).map(t=>`<a href="#${t.id}">${escape(t.title)}</a>`).join('');
  const previous=pages[index-1], next=pages[index+1];
  const description=page.file==='README'?'The product and engineering handbook for Enactive. Learn the application, run tasks, configure models, and build repeatable workflows.':`Enactive Wiki: ${title}. Practical workflows, configuration details, and implementation references.`;
  const html=`<!doctype html>
<html lang="en" data-theme="dark">
<head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escape(page.file==='README'?title:title+' · Enactive Wiki')}</title><meta name="description" content="${escape(description)}">
<link rel="canonical" href="https://enactive.dev/wiki/${page.slug}.html">
<link rel="icon" href="../assets/favicon.svg" type="image/svg+xml">
<link rel="stylesheet" href="assets/brand.css"><link rel="stylesheet" href="assets/fonts.css"><link rel="stylesheet" href="wiki.css">
<script src="theme.js"></script><script src="search-index.js" defer></script><script src="wiki.js" defer></script>
</head><body>
<a class="skip-link" href="#main">Skip to content</a>
<header class="wiki-header"><a class="brand" href="../index.html" aria-label="Enactive home"><img class="brand-mark" src="assets/mark-loop.svg" alt="" width="32" height="32"><img class="wordmark dark-mark" src="assets/wordmark.svg" alt="Enactive" width="144" height="18"><img class="wordmark light-mark" src="assets/wordmark-light.svg" alt="Enactive" width="144" height="18"></a><a class="wiki-label" href="index.html">Wiki</a><div class="header-actions"><button type="button" class="search-trigger" aria-haspopup="dialog">Search wiki <kbd>Ctrl K</kbd></button><button type="button" id="theme-toggle" aria-label="Switch to light theme">Light theme</button><button type="button" id="menu-toggle" aria-expanded="false" aria-controls="sidebar">Menu</button></div></header>
<div class="wiki-layout"><aside id="sidebar" class="sidebar"><nav aria-label="Wiki chapters">${nav}</nav><div class="sidebar-footer"><a href="../index.html">← Back to Enactive</a><span>Product & engineering handbook</span></div></aside>
<main id="main" class="${page.file==='README'?'home-page':''}" tabindex="-1"><div class="page-heading"><div class="breadcrumb"><a href="index.html">Documentation</a><span aria-hidden="true">/</span><span>${escape(page.label)}</span></div><p class="eyebrow">${escape(page.group)} · Enactive</p><h1>${escape(title)}</h1><div class="page-meta"><span>${minutes} min read</span><span>English</span><a href="${repo}Docs/wiki/${page.file}.md">Markdown source ↗</a></div></div><details class="mobile-toc"><summary>On this page</summary><nav aria-label="On this page">${tocHtml}</nav></details><article>${content}</article><nav class="pagination" aria-label="Adjacent chapters">${previous?`<a href="${previous.slug}.html"><small>← Previous</small><strong>${previous.label}</strong></a>`:'<span></span>'}${next?`<a href="${next.slug}.html"><small>Next →</small><strong>${next.label}</strong></a>`:'<a href="index.html"><small>Back to</small><strong>Wiki home →</strong></a>'}</nav><footer class="page-footer"><span>Enactive · Real, reviewable, recorded action.</span><a href="#main">Back to top ↑</a></footer></main>
<aside class="page-toc"><p class="nav-group">On this page</p><nav aria-label="Page sections">${tocHtml}</nav><a class="source-link" href="${repo}Docs/wiki/${page.file}.md">View Markdown source ↗</a></aside></div>
<dialog id="search-dialog" aria-labelledby="search-title"><div class="search-heading"><h2 id="search-title">Search the wiki</h2><button id="close-search" type="button" aria-label="Close search">Close <kbd>Esc</kbd></button></div><label for="search-input">Find a topic, setting, or command</label><input id="search-input" type="search" placeholder="Try templates, Review, or --workspace" autocomplete="off"><p id="search-status" role="status" aria-live="polite">Search across all chapters.</p><div id="search-results"></div></dialog><div id="copy-status" class="sr-only" role="status" aria-live="polite"></div>
</body></html>`;
  await fs.writeFile(path.join(out,page.slug+'.html'),html+'\n');
  let section={title, url:page.slug+'.html', page:page.label, text:''};
  for (const line of markdown.split('\n')) {
    if (/^#{2,3} /.test(line)) { search.push(section); const heading=line.replace(/^#+ /,''); section={title:plain(heading),url:page.slug+'.html#'+slugify(heading),page:page.label,text:''}; }
    else section.text+=' '+plain(line).replace(/\|/g,' ');
  }
  search.push(section);
}
await fs.writeFile(path.join(out,'search-index.js'),'window.enactiveWikiSearch = '+JSON.stringify(search).replaceAll('<','\\u003c')+';\n');
const sitemap=`<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n${['https://enactive.dev/',...pages.map(p=>'https://enactive.dev/wiki/'+p.slug+'.html')].map(url=>`  <url><loc>${url}</loc></url>`).join('\n')}\n</urlset>\n`;
await fs.writeFile(path.join(site,'sitemap.xml'),sitemap);
console.log(`Built ${pages.length} HTML pages and ${search.length} searchable sections from Docs/wiki.`);
