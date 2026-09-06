import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const root = new URL('../', import.meta.url);
const fonts = new URL('wwwroot/fonts/', root);
await mkdir(fonts, { recursive: true });
let css = '';
for (const [family, name, weights] of [['inter', 'Inter', [400, 500, 600]], ['space-grotesk', 'Space Grotesk', [500, 700]], ['jetbrains-mono', 'JetBrains Mono', [400]]]) {
  const packageRoot = new URL(`node_modules/@fontsource/${family}/`, root);
  await copyFile(new URL('LICENSE', packageRoot), new URL(`${family}-LICENSE.txt`, fonts));
  for (const weight of weights) {
    const filename = `${family}-latin-${weight}-normal.woff2`;
    await copyFile(new URL(`files/${filename}`, packageRoot), new URL(filename, fonts));
    css += `@font-face{font-family:'${name}';font-style:normal;font-weight:${weight};font-display:swap;src:url('/fonts/${filename}') format('woff2')}\n`;
  }
}
await writeFile(new URL('wwwroot/fonts.css', root), css);
// Brand assets are vendored so server remains movable and publishable on its own.
await mkdir(new URL('wwwroot/brand/', root), { recursive: true });
for (const name of ['brand.css', 'logos/mark-loop.svg', 'logos/wordmark.svg', 'logos/wordmark-light.svg']) {
  await copyFile(new URL(`../brand/${name}`, root), new URL(`wwwroot/brand/${name.split('/').at(-1)}`, root));
}
console.log('Bundled brand assets and licensed fonts.');
