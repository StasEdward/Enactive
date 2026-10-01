// The shared vectors are read relative to this file, so the tests run from any working directory.
import { readFileSync } from 'node:fs';

export const vectors = JSON.parse(
  readFileSync(new URL('../vectors/remote-crypto-v2.json', import.meta.url), 'utf8'));
