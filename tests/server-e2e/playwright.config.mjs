import { defineConfig, devices } from '@playwright/test';

// One worker, in order: the scenarios share one service, and one of them changes an account's role.
export default defineConfig({
  testDir: '.',
  testMatch: 'server.spec.mjs',
  globalSetup: './global-setup.mjs',
  workers: 1,
  fullyParallel: false,
  forbidOnly: Boolean(process.env.CI),
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : [['list']],
  use: {
    ...devices['Desktop Chrome'],
    // The harness's certificate is made for the run and trusted by nobody; what is under test is the service's
    // pages, not the machine's certificate store.
    ignoreHTTPSErrors: true,
    trace: 'retain-on-failure'
  },
  projects: [{ name: 'chromium' }]
});
