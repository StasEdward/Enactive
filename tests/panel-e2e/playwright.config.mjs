import { defineConfig, devices } from '@playwright/test';

// One worker, in order: the scenarios are one story - the device admitted in the second is the one removed in the
// fourth, and the seventh reads every request the others made - on one gateway and one database.
export default defineConfig({
  testDir: '.',
  testMatch: 'e2e.spec.mjs',
  globalSetup: './global-setup.mjs',
  workers: 1,
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  // A computer syncs every second and the panel polls every three; a step that waits on both takes seconds.
  timeout: 120_000,
  expect: { timeout: 30_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],
  use: {
    ...devices['Desktop Chrome'],
    trace: 'retain-on-failure'
  },
  projects: [{ name: 'chromium' }]
});
