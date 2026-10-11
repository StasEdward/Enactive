// The service's pages in a real browser (server plan 1.7): a run that outlives its page, a question two pages and two
// people see, a run that waits for its folder, and a role taken away while its page is open.
import { test, expect } from '@playwright/test';
import { spawnSync } from 'node:child_process';
import path from 'node:path';

const password = 'e2e password 1234';
const base = () => process.env.E2E_BASE_URL;
const folder = () => path.join(process.env.E2E_DATA, 'project');

/** Signs in on a page of its own browser context: each person is their own browser. */
async function signedIn(browser, name) {
  const context = await browser.newContext({ baseURL: base(), ignoreHTTPSErrors: true });
  const page = await context.newPage();
  await page.goto('/');
  await expect(page).toHaveURL(/\/signin/);
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(page.getByRole('heading', { name: 'Runs' })).toBeVisible();
  return { context, page };
}

/** Starts a run from the Runs page and waits for its own page. */
async function startRun(page, task) {
  await page.getByLabel('Workspace').selectOption(folder());
  await page.getByLabel('Task').fill(task);
  await page.getByRole('button', { name: 'Run' }).click();
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);
  return page.url();
}

/** The service's own command line, on its data folder - as an administrator on the server would use it. */
function setRole(name, role) {
  const done = spawnSync('dotnet', [process.env.E2E_SERVER_DLL, '--data', process.env.E2E_DATA, '--set-role', name, role],
    { encoding: 'utf8' });
  if (done.status !== 0) throw new Error(`--set-role failed: ${done.stderr}`);
}

/**
 * Ends the run a test left waiting, so the next one finds its folder free. Waits for the question: the run page is
 * live and drawn only once connected, and a check made at once found no button and left the run holding the folder.
 */
async function answerWhateverWaits(page) {
  const deny = page.getByRole('button', { name: 'Deny' });
  try {
    await deny.waitFor({ timeout: 10_000 });
    await deny.click();
    await expect(page.getByRole('button', { name: 'Cancel the run' })).toBeHidden();
  } catch {
    // Nothing was waiting.
  }
}

test('a run outlives its page, and its question waits for whoever comes back', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  const run = await startRun(page, 'write the digest');
  await expect(page.getByRole('heading', { name: 'Approve write_file' })).toBeVisible();

  // The browser tab is closed. The run is the service's: it goes on waiting.
  await page.close();

  const back = await context.newPage();
  await back.goto('/');
  await back.getByRole('link', { name: /write the digest/i }).first().click();
  await expect(back).toHaveURL(run);
  await expect(back.getByText('Waiting for an answer')).toBeVisible();

  await back.getByRole('button', { name: 'Allow', exact: true }).click();
  await expect(back.getByRole('heading', { name: 'Approve write_file' })).toBeHidden();
  await expect(back.getByRole('button', { name: 'Cancel the run' })).toBeHidden();
  await context.close();
});

test('two pages on one run see one question, and an answer in one takes it from the other', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  const run = await startRun(page, 'two tabs');
  const second = await context.newPage();
  await second.goto(run);
  await expect(page.getByRole('heading', { name: 'Approve write_file' })).toBeVisible();
  await expect(second.getByRole('heading', { name: 'Approve write_file' })).toBeVisible();

  await second.getByRole('button', { name: 'Deny' }).click();

  await expect(page.getByRole('heading', { name: 'Approve write_file' })).toBeHidden();
  await context.close();
});

test('another operator answers a run, but cannot leave an approval in its owner\'s name', async ({ browser }) => {
  const alice = await signedIn(browser, 'alice');
  const run = await startRun(alice.page, 'answered by bob');
  const bob = await signedIn(browser, 'bob');
  await bob.page.goto(run);
  await expect(bob.page.getByRole('heading', { name: 'Approve write_file' })).toBeVisible();

  await bob.page.getByRole('button', { name: 'Allow (workspace)', exact: true }).click();
  await expect(bob.page.getByText('Only alice, who started the run')).toBeVisible();

  await bob.page.getByRole('button', { name: 'Deny' }).click();
  await expect(alice.page.getByRole('heading', { name: 'Approve write_file' })).toBeHidden();
  await alice.context.close();
  await bob.context.close();
});

test('a second run in the same folder waits, says why, and starts when the first ends', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  const first = await startRun(page, 'first in the folder');
  await page.goto('/');
  await startRun(page, 'second in the folder');
  await expect(page.getByText(/Another run in this folder/)).toBeVisible();

  const firstPage = await context.newPage();
  await firstPage.goto(first);
  await firstPage.getByRole('button', { name: 'Deny' }).click();

  await expect(page.getByRole('heading', { name: 'Approve write_file' })).toBeVisible();
  await answerWhateverWaits(page);
  await context.close();
});

test('a viewer reads runs and is offered nothing to do to them', async ({ browser }) => {
  const alice = await signedIn(browser, 'alice');
  const run = await startRun(alice.page, 'watched by vera');
  const vera = await signedIn(browser, 'vera');

  await expect(vera.page.getByLabel('Task')).toHaveCount(0);
  await vera.page.goto(run);
  await expect(vera.page.getByRole('heading', { name: 'Approve write_file' })).toBeVisible();
  await expect(vera.page.getByRole('button', { name: 'Cancel the run' })).toHaveCount(0);

  await vera.page.getByRole('button', { name: 'Deny' }).click();
  await expect(vera.page.getByText('role does not include')).toBeVisible();

  await answerWhateverWaits(alice.page);
  await alice.context.close();
  await vera.context.close();
});

test('a role lowered while its page is open cannot act from that page', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await startRun(page, 'role taken away');
  await expect(page.getByRole('heading', { name: 'Approve write_file' })).toBeVisible();

  setRole('alice', 'Viewer');
  try {
    await page.getByRole('button', { name: 'Cancel the run' }).click();
    await expect(page.getByText('Your session has ended')).toBeVisible();
  } finally {
    setRole('alice', 'Operator');
  }

  // Back to Operator: signed in again, the run that was left is answered so the folder is free.
  const again = await signedIn(browser, 'alice');
  await again.page.getByRole('link', { name: /role taken away/i }).first().click();
  await answerWhateverWaits(again.page);
  await context.close();
  await again.context.close();
});

test('a past run is tried again from its page, and deleted after its question', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await startRun(page, 'kept in the history');
  await page.getByRole('button', { name: 'Allow', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Cancel the run' })).toBeHidden();

  await page.getByRole('link', { name: 'Workspaces' }).click();
  await page.getByRole('link', { name: 'project' }).click();
  await page.getByRole('link', { name: 'kept in the history' }).first().click();
  await expect(page.getByRole('heading', { name: 'kept in the history' })).toBeVisible();
  const past = page.url();

  // Try again: a run of the same task starts, and asks its question again.
  await page.getByRole('button', { name: 'Try again' }).click();
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);
  await page.getByRole('button', { name: 'Deny' }).click();
  await expect(page.getByRole('button', { name: 'Cancel the run' })).toBeHidden();

  // Delete: the window's question, on a page, then the record goes.
  await page.goto(past);
  await page.getByRole('link', { name: 'Delete this run' }).click();
  await expect(page.getByRole('heading', { name: /Delete the run/ })).toBeVisible();
  await page.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(page.getByText(/record is deleted/)).toBeVisible();
  await context.close();
});

test('an admin adds an account on the accounts page, and it signs in', async ({ browser }) => {
  const ada = await signedIn(browser, 'ada');
  await ada.page.getByRole('link', { name: 'Accounts' }).click();
  await expect(ada.page.getByText('trust each other')).toBeVisible();

  const add = ada.page.locator('form.start');
  await add.getByLabel('Name').fill('carl');
  await add.getByLabel('Role').selectOption('Viewer');
  await add.getByLabel('Password (12 characters or more)').fill(password);
  await add.getByLabel('Again').fill(password);
  await add.getByRole('button', { name: 'Add' }).click();
  await expect(ada.page.getByText('Added carl (Viewer).')).toBeVisible();

  const carl = await signedIn(browser, 'carl');
  await expect(carl.page.getByRole('link', { name: 'Accounts' })).toHaveCount(0);
  await carl.page.goto('/admin/users');
  await expect(carl.page.getByRole('heading', { name: 'Accounts' })).toHaveCount(0);

  await ada.page.getByRole('link', { name: 'Audit' }).click();
  await expect(ada.page.getByText('account add').first()).toBeVisible();
  await ada.context.close();
  await carl.context.close();
});

test('an operator is offered no accounts page and is refused it', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await expect(page.getByRole('link', { name: 'Accounts' })).toHaveCount(0);
  await page.goto('/admin/users');
  await expect(page.getByRole('heading', { name: 'Accounts' })).toHaveCount(0);
  await context.close();
});

test('a folder under the roots is added as a workspace by browsing, and nothing outside is offered', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await page.goto('/workspaces');
  await page.getByRole('link', { name: 'Add a folder…' }).click();
  await expect(page.getByRole('link', { name: process.env.E2E_DATA })).toBeVisible();
  await page.getByRole('link', { name: process.env.E2E_DATA }).click();
  await page.getByRole('link', { name: 'reports', exact: true }).click();
  await page.getByRole('button', { name: 'Make this folder a workspace' }).click();
  await expect(page.getByText('reports is a workspace of the service now.')).toBeVisible();

  // Not under a root: refused, whatever the address says.
  await page.goto('/workspaces/browse?path=' + encodeURIComponent('C:\Windows'));
  await expect(page.getByText(/is not under the folders the service runs tasks in/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Make this folder a workspace' })).toHaveCount(0);
  await context.close();
});

