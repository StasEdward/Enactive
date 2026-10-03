import { defineConfig } from '@playwright/test';
export default defineConfig({
  testDir: '.', testMatch: 'admin.spec.mjs', workers: 1, retries: 0,
  use: { browserName: 'chromium' }, reporter: 'list'
});
