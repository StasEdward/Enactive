# Enactive website

The contents of this directory are the static publish root. The landing page is `index.html`; the English documentation starts at `wiki/index.html`. Publish this directory as a unit so relative navigation, fonts, and scripts work under the same origin. No server-side rendering or browser CDN is needed for the wiki.

## Updating the wiki

Markdown lives in `../Docs/wiki/`. From this directory, with Node.js 22 or later:

```sh
npm install --ignore-scripts
npm run build:wiki
npm run check:wiki
```

Commit the generated `wiki/*.html`, `wiki/search-index.js`, synced assets, and `sitemap.xml` alongside the source changes. The checked-in HTML is ready to serve; deployment does not require Node or an npm install.

`scripts/build-wiki.mjs` preserves the chapters and rewrites Markdown links to local HTML. Implementation references resolve to the repository on GitHub, including links written before the source moved into `Docs/wiki/`. It copies the canonical brand tokens/logos and the existing licensed, locally bundled fonts. `_Sidebar.md` is represented by the shared navigation rather than emitted as a separate article.

The two existing Mermaid `flowchart TD` diagrams become native, accessible HTML dependency diagrams with every node and labelled connection. The generator deliberately rejects unsupported diagram syntax rather than dropping content. Extend the renderer if a source adds a different Mermaid construct.

Styles and interactions are maintained in `wiki/wiki.css`, `wiki/wiki.js`, and `wiki/theme.js`; generation does not overwrite these files. Search is a local section index and also works without fetch requests. Theme preference is saved when local storage is available. Without JavaScript, content, links, tables, and diagrams remain readable; enhanced search and copy require JavaScript.

## Preview

From this directory:

```sh
python -m http.server 5188 --bind 127.0.0.1
```

Open `http://127.0.0.1:5188/wiki/index.html`. Check desktop/mobile widths, both themes, keyboard navigation, search, and code copying. The `_headers` policy permits self-hosted fonts and scripts without allowing inline JavaScript. The landing page and wiki share local fonts through `wiki/assets/fonts.css`.
