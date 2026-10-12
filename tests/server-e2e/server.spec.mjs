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

/**
 * The Runs screen on the folder "project": the command bar runs in the workspace the column shows, which is the one used
 * last - another one once a test has added a folder - so it is picked in the switcher when it is not the one shown.
 */
async function onProject(page) {
  const card = page.getByRole('button', { name: /^Workspace:/ });
  if ((await card.getAttribute('aria-label')) === 'Workspace: project') return;
  await card.click();
  await page.locator('.switcher').getByRole('link', { name: /^project/ }).click();
  await expect(page.getByRole('button', { name: 'Workspace: project' })).toBeVisible();
}

/** Starts a run from the Runs screen's command bar and waits for its own page. */
async function startRun(page, task) {
  await onProject(page);
  // Exact: the rail's icons are named by their tooltips, and getByLabel reads those too ("Templates · a saved task").
  await page.getByLabel('Task', { exact: true }).fill(task);
  // Exact: the bar's "Run in background" and the history's "Delete this run from the history" are buttons beside it.
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);
  return page.url();
}

/** The service's own command line, on its data folder - as an administrator on the server would use it. */
function setRole(name, role) {
  const done = spawnSync('dotnet', [process.env.E2E_SERVER_DLL, '--data', process.env.E2E_DATA, '--set-role', name, role],
    { encoding: 'utf8' });
  if (done.status !== 0) throw new Error(`--set-role failed: ${done.stderr}`);
}

/** The question the open run waits on: the status column's USER DECISION REQUIRED card. */
const question = (page) => page.getByRole('region', { name: 'User decision required' });

/** The command bar's Stop, which is the open run's: offered while it goes, greyed once it has ended. */
const stop = (page) => page.getByRole('button', { name: 'Stop', exact: true });

/** Opens a run going now from the Runs column's RUNNING list, by what it was asked to do. */
async function openRunning(page, task) {
  await page.getByRole('complementary', { name: 'Runs column' }).getByRole('listitem').filter({ hasText: task })
    .getByRole('link', { name: 'Open' }).first().click();
}

/**
 * Ends the run a test left waiting, so the next one finds its folder free. Waits for the question: the run is live and
 * drawn only once connected, and a check made at once found no button and left the run holding the folder.
 */
async function answerWhateverWaits(page) {
  const deny = question(page).getByRole('button', { name: 'Deny' });
  try {
    await deny.waitFor({ timeout: 10_000 });
    await deny.click();
    await expect(stop(page)).toBeDisabled();
  } catch {
    // Nothing was waiting.
  }
}

test('a run outlives its page, and its question waits for whoever comes back', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  const run = await startRun(page, 'write the digest');
  await expect(question(page)).toContainText('Approve write_file');

  // The browser tab is closed. The run is the service's: it goes on waiting.
  await page.close();

  const back = await context.newPage();
  await back.goto('/');
  await openRunning(back, 'write the digest');
  await expect(back).toHaveURL(run);
  await expect(back.getByText('Waiting for an answer')).toBeVisible();

  await question(back).getByRole('button', { name: 'Allow', exact: true }).click();
  await expect(question(back)).toBeHidden();
  await expect(stop(back)).toBeDisabled();
  await context.close();
});

test('two pages on one run see one question, and an answer in one takes it from the other', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  const run = await startRun(page, 'two tabs');
  const second = await context.newPage();
  await second.goto(run);
  await expect(question(page)).toContainText('Approve write_file');
  await expect(question(second)).toContainText('Approve write_file');

  await question(second).getByRole('button', { name: 'Deny' }).click();

  await expect(question(page)).toBeHidden();
  await context.close();
});

test('another operator answers a run, and is offered no approval to leave in its owner\'s name', async ({ browser }) => {
  const alice = await signedIn(browser, 'alice');
  const run = await startRun(alice.page, 'answered by bob');
  const bob = await signedIn(browser, 'bob');
  await bob.page.goto(run);
  await expect(question(bob.page)).toContainText('Approve write_file');
  // Who else has the run open, named on each page.
  await expect(alice.page.getByText('also watching: bob')).toBeVisible();

  // What is remembered binds the person it is remembered for: only the owner is offered it.
  await expect(question(alice.page).getByRole('button', { name: 'Allow (workspace)', exact: true })).toBeVisible();
  await expect(question(bob.page).getByRole('button', { name: 'Allow (workspace)', exact: true })).toHaveCount(0);

  await question(bob.page).getByRole('button', { name: 'Deny' }).click();
  await expect(question(alice.page)).toBeHidden();
  await alice.context.close();
  await bob.context.close();
});

test('a second run in the same folder waits, says why, and starts when the first ends', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  const first = await startRun(page, 'first in the folder');
  await page.goto('/');
  await startRun(page, 'second in the folder');
  await expect(page.getByRole('region', { name: 'Current action' })).toContainText(/Another run in this folder/);

  const firstPage = await context.newPage();
  await firstPage.goto(first);
  await question(firstPage).getByRole('button', { name: 'Deny' }).click();

  await expect(question(page)).toContainText('Approve write_file');
  await answerWhateverWaits(page);
  await context.close();
});

test('the command bar takes the window\'s keys: Ctrl+K to focus, Ctrl+Enter a new line, Enter to run', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await onProject(page);
  const box = page.getByLabel('Task', { exact: true });
  // From nowhere in particular: Ctrl+K is the page's, not the box's.
  await page.evaluate(() => document.activeElement?.blur());
  await expect(box).not.toBeFocused();
  await page.keyboard.press('Control+k');
  await expect(box).toBeFocused();

  await page.keyboard.type('first line');
  await page.keyboard.press('Control+Enter');
  await page.keyboard.type('second line');
  await expect(box).toHaveValue('first line\nsecond line');

  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);
  await answerWhateverWaits(page);
  await context.close();
});

test('a viewer reads runs and is offered nothing to do to them', async ({ browser }) => {
  const alice = await signedIn(browser, 'alice');
  const run = await startRun(alice.page, 'watched by vera');
  const vera = await signedIn(browser, 'vera');

  await expect(vera.page.getByLabel('Task', { exact: true })).toHaveCount(0);
  await expect(vera.page.getByRole('button', { name: 'Run in background' })).toHaveCount(0);
  await vera.page.goto(run);
  await expect(question(vera.page)).toContainText('Approve write_file');
  // The full action is hers to read; no answer, and no Stop, is hers to give.
  await expect(question(vera.page).getByRole('button')).toHaveCount(0);
  await expect(stop(vera.page)).toHaveCount(0);

  await answerWhateverWaits(alice.page);
  await alice.context.close();
  await vera.context.close();
});

test('a role lowered while its page is open cannot act from that page', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await startRun(page, 'role taken away');
  await expect(question(page)).toContainText('Approve write_file');

  setRole('alice', 'Viewer');
  try {
    await stop(page).click();
    await expect(page.getByText('Your session has ended')).toBeVisible();
  } finally {
    setRole('alice', 'Operator');
  }

  // Back to Operator: signed in again, the run that was left is answered so the folder is free.
  const again = await signedIn(browser, 'alice');
  await openRunning(again.page, 'role taken away');
  await answerWhateverWaits(again.page);
  await context.close();
  await again.context.close();
});

test('a past run is tried again from its page, and deleted after its question', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await startRun(page, 'kept in the history');
  await question(page).getByRole('button', { name: 'Allow', exact: true }).click();
  await expect(stop(page)).toBeDisabled();

  // The Runs screen's column lists the workspace's history; the run it just ran opens on the same screen, at its own
  // address, in the run's place.
  await page.goto('/');
  await page.getByRole('complementary', { name: 'Runs column' }).getByRole('link', { name: /kept in the history/ }).first().click();
  await expect(page).toHaveURL(/\/history\/[0-9a-f-]{36}\/[0-9a-f-]{36}$/);
  const past = page.getByRole('region', { name: 'Past run' });
  await expect(past.getByRole('heading', { name: 'kept in the history' })).toBeVisible();
  const address = page.url();

  // Retry: a run of the same task starts, opens at its own address, and asks its question again.
  await past.getByRole('button', { name: 'Retry' }).click();
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);
  await question(page).getByRole('button', { name: 'Deny' }).click();
  await expect(stop(page)).toBeDisabled();

  // Delete the file it wrote: the window's question, in a dialog on the screen, then the card says what became of it.
  await page.goto(address);
  await past.getByRole('tab', { name: /^Artifacts/ }).click();
  const file = past.getByRole('listitem').filter({ hasText: 'digest.md' });
  await file.getByRole('button', { name: 'Delete' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByRole('heading', { name: 'Delete “digest.md”?' })).toBeVisible();
  await dialog.getByRole('button', { name: 'Delete', exact: true }).click();
  await expect(dialog).toBeHidden();
  await expect(file.getByText('deleted', { exact: true })).toBeVisible();
  await context.close();
});

test('a question in the runs column holds the keyboard until it is answered, then gives it back', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  const opener = page.getByRole('complementary', { name: 'Runs column' })
    .getByRole('button', { name: 'Delete this run from the history' }).first();
  await opener.focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog');
  await expect(dialog).toBeFocused();

  // Tab never reaches the rows behind the scrim: the page is inert while the question is open. (Past the dialog's last
  // button it may leave the page for the browser's own controls, as a native modal dialog's does.)
  const outsideTheDialog = () => page.evaluate(() => {
    const at = document.activeElement;
    return at !== document.body && !at.closest('[role=dialog]') && !at.closest('#components-reconnect-modal');
  });
  for (let i = 0; i < 6; i++) {
    await page.keyboard.press('Tab');
    expect(await outsideTheDialog()).toBe(false);
  }

  await dialog.focus();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(opener).toBeFocused();
  await context.close();
});

test('an admin adds an account on the accounts page, and it signs in', async ({ browser }) => {
  const ada = await signedIn(browser, 'ada');
  await ada.page.getByRole('link', { name: 'Accounts' }).click();
  await expect(ada.page).toHaveURL(/\/accounts$/);
  await expect(ada.page.getByText('trust each other')).toBeVisible();

  // The last administrator: Disable is offered, and the service refuses it.
  await ada.page.getByRole('button', { name: 'Edit ada' }).click();
  let dialog = ada.page.getByRole('dialog', { name: 'ada' });
  await dialog.getByRole('button', { name: 'Disable' }).click();
  await expect(dialog.getByRole('alert')).toHaveText('That is the last administrator; make another one first.');
  await dialog.getByRole('button', { name: 'Close' }).click();
  await expect(dialog).toBeHidden();

  await ada.page.getByRole('button', { name: 'Add', exact: true }).click();
  dialog = ada.page.getByRole('dialog', { name: 'Add an account' });
  await dialog.getByLabel('Name').fill('carl');
  await dialog.getByLabel('Role').selectOption('Viewer');
  const first = dialog.getByLabel('Password (12 characters or more)');
  const again = dialog.getByLabel('Again');
  // Typed differently: refused, and both boxes are empty again - the name and the role stay for the next try.
  await first.fill(password);
  await again.fill(password + '!');
  await dialog.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(dialog.getByRole('alert')).toHaveText('The two passwords did not match; nothing was changed.');
  await expect(first).toHaveValue('');
  await expect(again).toHaveValue('');
  await expect(dialog.getByLabel('Name')).toHaveValue('carl');
  await expect(first).toHaveAttribute('autocomplete', 'new-password');

  await first.fill(password);
  await again.fill(password);
  await dialog.getByRole('button', { name: 'Add', exact: true }).click();
  await expect(ada.page.getByRole('main').getByRole('status')).toHaveText('Added carl (Viewer).');
  await expect(dialog).toBeHidden();
  await expect(ada.page.getByRole('list', { name: 'Accounts' }).getByText('carl', { exact: true })).toBeVisible();

  const carl = await signedIn(browser, 'carl');
  await expect(carl.page.getByRole('link', { name: 'Accounts' })).toHaveCount(0);
  // The old address leads to the new one, which refuses a Viewer as every Admin page does.
  await carl.page.goto('/admin/users');
  await expect(carl.page).toHaveURL(/\/denied\?ReturnUrl=%2Faccounts$/);
  await expect(carl.page.getByText("Your account's role does not include this page.")).toBeVisible();
  await expect(carl.page.getByRole('heading', { name: 'Accounts' })).toHaveCount(0);

  await ada.page.getByRole('link', { name: 'Audit' }).click();
  await expect(ada.page).toHaveURL(/\/audit$/);
  const added = ada.page.getByRole('row').filter({ hasText: 'account add' }).first();
  await expect(added.getByRole('cell', { name: 'carl (Viewer)' })).toBeVisible();
  await ada.context.close();
  await carl.context.close();
});

test('an operator is offered no accounts page and is refused it', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await expect(page.getByRole('link', { name: 'Accounts' })).toHaveCount(0);
  await expect(page.getByRole('link', { name: 'Audit' })).toHaveCount(0);
  for (const address of ['/accounts', '/admin/users', '/audit', '/admin/audit']) {
    await page.goto(address);
    await expect(page.getByText("Your account's role does not include this page.")).toBeVisible();
    await expect(page.getByRole('heading', { name: /^(Accounts|Audit)$/ })).toHaveCount(0);
  }
  await context.close();
});

test('a folder under the roots is added as a workspace by browsing, and nothing outside is offered', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  // The switcher's Add folder… opens the folder browser in a dialog over the Runs screen.
  await page.getByRole('button', { name: /^Workspace:/ }).click();
  await page.getByRole('button', { name: 'Add folder…' }).click();
  let dialog = page.getByRole('dialog', { name: 'Add folder' });
  await dialog.getByRole('button', { name: process.env.E2E_DATA, exact: true }).click();
  await dialog.getByRole('button', { name: 'reports', exact: true }).click();
  await dialog.getByRole('button', { name: 'Make this folder a workspace' }).click();
  // The column switches to it, as the window does with a folder just added.
  await expect(page.getByText('reports is a workspace of the service now.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Workspace: reports' })).toBeVisible();

  // Nothing outside the roots is offered: Up from a root is the list of roots again, never the folder above it.
  await page.getByRole('button', { name: /^Workspace:/ }).click();
  await page.getByRole('button', { name: 'Add folder…' }).click();
  dialog = page.getByRole('dialog', { name: 'Add folder' });
  await dialog.getByRole('button', { name: process.env.E2E_DATA, exact: true }).click();
  await dialog.getByRole('button', { name: 'Up' }).click();
  await expect(dialog.getByRole('button', { name: process.env.E2E_DATA, exact: true })).toBeVisible();
  await expect(dialog.getByRole('button', { name: path.dirname(process.env.E2E_DATA), exact: true })).toHaveCount(0);
  await expect(dialog.getByRole('button', { name: 'Make this folder a workspace' })).toHaveCount(0);
  await context.close();
});

test('an admin adds a provider with its key on the settings page, and the key never comes back to a page', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'ada');
  // The rail's Settings opens the window's sections; Providers is under AI.
  await page.getByRole('link', { name: 'Settings', exact: true }).click();
  await expect(page).toHaveURL(/\/settings$/);
  await page.getByRole('navigation', { name: 'Settings sections' }).getByRole('link', { name: 'Providers' }).click();
  await expect(page).toHaveURL(/\/settings\/ai-providers$/);

  // Added in the window's provider editor, over the settings; Apply puts it into the copy, Save writes it.
  await page.getByRole('button', { name: 'Add', exact: true }).click();
  let editor = page.getByRole('dialog', { name: 'Provider' });
  await editor.getByLabel('Id', { exact: true }).fill('box');
  await editor.getByLabel('Base URL').fill('http://127.0.0.1:8080/v1');
  await editor.getByLabel('API key').fill('sk-e2e-secret');
  await editor.getByLabel('Models', { exact: true }).fill('qwen');
  await editor.getByRole('button', { name: 'Apply' }).click();
  await expect(page.getByRole('list', { name: 'Providers' }).getByText('box', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText(/^Saved\./)).toBeVisible();

  // Opened again, the key is said to be kept - and is nowhere on the page, not even as the value of its box.
  await page.reload();
  await expect(page.getByRole('list', { name: 'Providers' }).getByText('box', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Edit box' }).click();
  editor = page.getByRole('dialog', { name: 'Provider' });
  await expect(editor.getByText('A key is stored. Leave the box empty to keep it.')).toBeVisible();
  await expect(editor.getByLabel('API key')).toHaveValue('');
  expect(await page.content()).not.toContain('sk-e2e-secret');
  await context.close();
});

test('an admin adds an MCP server with a token header, and the page names the header but never shows the token', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'ada');
  // The old address lands on the settings.
  await page.goto('/admin/settings');
  await expect(page).toHaveURL(/\/settings$/);
  await page.getByRole('navigation', { name: 'Settings sections' }).getByRole('link', { name: 'MCP' }).click();
  await expect(page.getByText('MCP servers', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Add', exact: true }).click();
  let editor = page.getByRole('dialog', { name: 'MCP server' });
  await editor.getByLabel('Server ID').fill('tracker');
  await editor.getByLabel('Transport').selectOption('Http');
  await editor.getByLabel('Endpoint').fill('https://mcp.example.com/');
  await editor.getByLabel(/New headers/).fill('Authorization: Bearer tk-e2e-secret');
  await editor.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByRole('list', { name: 'MCP servers' }).getByText('tracker', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText(/^Saved\./)).toBeVisible();

  await page.reload();
  await page.getByRole('button', { name: 'Edit tracker' }).click();
  editor = page.getByRole('dialog', { name: 'MCP server' });
  await expect(editor.getByText('Set: Authorization: …')).toBeVisible();
  expect(await page.content()).not.toContain('tk-e2e-secret');
  await context.close();
});

test('a change in one settings section is kept while another is open, and Cancel drops it', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'ada');
  await page.goto('/settings/web');
  const sections = page.getByRole('navigation', { name: 'Settings sections' });
  await page.getByLabel('Let tasks read the web').check();
  await page.getByLabel('Search server').fill('http://localhost:8888');

  // Another section is another address of the same page, over the same copy (the window's one working copy).
  await sections.getByRole('link', { name: 'SMTP' }).click();
  await expect(page).toHaveURL(/\/settings\/smtp$/);
  await sections.getByRole('link', { name: 'Web' }).click();
  await expect(page.getByLabel('Search server')).toHaveValue('http://localhost:8888');

  await page.getByRole('button', { name: 'Cancel', exact: true }).click();
  await expect(page.getByLabel('Search server')).toHaveValue('');
  await expect(page.getByLabel('Let tasks read the web')).not.toBeChecked();
  await context.close();
});

test('an operator writes a template and schedules it, and the schedule says when it runs next', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  // Templates are the rail's, on the workspace used last - "project" is picked in the screen's switcher when it is not.
  await page.getByRole('link', { name: 'Templates · a saved task', exact: true }).click();
  // On the Templates screen first: the Runs screen has a workspace card of its own, read before it was replaced.
  await expect(page.getByRole('link', { name: 'New template', exact: true })).toBeVisible();
  await onProject(page);

  // Written on the editor's form (the window's), its parameter added on the form too.
  await page.getByRole('link', { name: 'New template', exact: true }).click();
  // Exact: the parameter's own "id" is beside the template's "Id".
  await page.getByLabel('Id', { exact: true }).fill('digest');
  await page.getByLabel('Name', { exact: true }).fill('Digest');
  await page.getByLabel('Goal', { exact: true }).fill('Write the digest of {topic}.');
  await page.getByRole('button', { name: 'Add parameter' }).click();
  await page.getByLabel('id', { exact: true }).fill('topic');
  await page.getByLabel('Label shown to you').fill('Topic');
  // With a default: the schedule's form is the window's, which gives a template no answers of its own - at its hour
  // nobody is there to type one, so what the template asks must have one already.
  await page.getByLabel('Default value (blank = none)').fill('the inbox');
  // The JSON tab is the same template: what the form says, in the store's own words.
  await page.getByRole('tab', { name: 'JSON' }).click();
  await expect(page.getByLabel('The template, as JSON')).toHaveValue(/"Id": "topic"/);
  // Into the workspace's library, chosen: a new template is offered the global one first, as the window offers it, and
  // that is the service account's own, shared by every workspace.
  await page.getByLabel('Kept in').selectOption('Workspace');
  await page.getByRole('button', { name: 'Save', exact: true }).click();

  // Back in the library, on the template saved: what it asks for, and what it will be asked to do.
  await expect(page.getByText("Saved 'Digest' to the workspace's library.")).toBeVisible();
  const template = page.getByRole('region', { name: 'Template' });
  await expect(template.getByText('Write the digest of {topic}.')).toBeVisible();
  await expect(template.getByLabel('Topic')).toHaveValue('the inbox');
  await expect(template.getByRole('button', { name: 'Run', exact: true })).toBeEnabled();

  // Scheduled on the rail's Schedules, on the same workspace - the one used last.
  await page.getByRole('link', { name: 'Schedules · run it on a day and a time', exact: true }).click();
  // On the Schedules screen first: the Templates screen has a workspace card of its own, read before it was replaced.
  await expect(page.getByRole('region', { name: 'Schedules', exact: true })).toBeVisible();
  await onProject(page);
  await page.getByRole('button', { name: 'New', exact: true }).click();
  const form = page.getByRole('region', { name: 'Schedule form' });
  await form.getByLabel('Name', { exact: true }).fill('morning digest');
  await form.getByLabel('What it runs').selectOption({ label: 'Template · Digest' });
  await form.getByLabel('Hour').fill('7');
  await form.getByLabel('Minute').fill('30');
  await form.getByLabel('Minute').blur();
  // Before it is saved, the form says what it will agree to - when it runs next among it.
  await expect(form.getByText(/^Next run: /)).toBeVisible();
  await expect(form.getByText(/^Every day at 07:30/)).toBeVisible();
  await form.getByRole('button', { name: 'Save', exact: true }).click();

  const card = page.getByRole('region', { name: 'Schedules', exact: true }).getByRole('button', { name: /morning digest/ });
  await expect(card).toContainText(/Next run: /);
  const schedule = page.getByRole('region', { name: 'Schedule', exact: true });
  await expect(schedule.getByText('Nothing yet — this schedule has not reported back.')).toBeVisible();
  await expect(schedule.getByRole('button', { name: 'Disable', exact: true })).toBeVisible();
  await context.close();
});

test('a viewer reads the service log, narrowed, and is offered no model to ask', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'vera');
  await page.getByRole('link', { name: 'Global log', exact: true }).click();
  await expect(page).toHaveURL(/\/log$/);
  const lines = page.getByRole('list', { name: 'Lines' });
  const status = page.getByRole('status').filter({ hasText: / shown · / });
  // Narrowed first: the list draws only the lines in view, and it opens on the newest.
  const search = page.getByRole('searchbox', { name: 'Search' });
  await search.fill('Application started');
  await expect(lines).toContainText('Application started');
  // A line chosen shows its whole entry under the list, as the window's detail box.
  await lines.getByRole('button', { name: /Application started/ }).first().click();
  await expect(page.getByRole('textbox', { name: 'Payload' })).toHaveValue(/Application started/);
  await expect(page.getByRole('button', { name: 'AI Analyze' })).toHaveCount(0);
  await expect(page.getByText('Capture raw wire (Trace)')).toHaveCount(0);

  await search.fill('no line says this');
  await expect(status).toContainText(/^0 shown · /);

  // Clear is this page's: the view empties, and the service's log is there again for the next page that opens it.
  await search.fill('Application started');
  await expect(lines).toContainText('Application started');
  await page.getByRole('button', { name: 'Clear', exact: true }).click();
  await expect(lines.getByRole('button')).toHaveCount(0);
  await page.reload();
  await page.getByRole('searchbox', { name: 'Search' }).fill('Application started');
  await expect(page.getByRole('list', { name: 'Lines' })).toContainText('Application started');
  await context.close();
});

test('an admin captures the raw wire, and an operator is offered the analysis but not the capture', async ({ browser }) => {
  const operator = await signedIn(browser, 'alice');
  await operator.page.goto('/log');
  await expect(operator.page.getByRole('button', { name: 'AI Analyze' })).toBeVisible();
  await expect(operator.page.getByText('Capture raw wire (Trace)')).toHaveCount(0);
  await operator.context.close();

  const { context, page } = await signedIn(browser, 'ada');
  await page.goto('/log');
  const capture = page.getByRole('checkbox', { name: 'Capture raw wire (Trace)' });
  const status = page.getByRole('status').filter({ hasText: / shown · / });
  await capture.check();
  await expect(status).toContainText('hub level ≥ Trace');
  // Off again, back to the level the service runs at: the scenarios after this one read the log as it normally is.
  await capture.uncheck();
  await expect(status).toContainText('hub level ≥ Debug');
  await context.close();
});

test('an admin demoted while the log is open is refused the capture, and the box shows it off', async ({ browser }) => {
  setRole('bob', 'Admin');
  try {
    const { context, page } = await signedIn(browser, 'bob');
    await page.goto('/log');
    const capture = page.getByRole('checkbox', { name: 'Capture raw wire (Trace)' });
    await expect(capture).not.toBeChecked();
    setRole('bob', 'Operator');

    // A click, not check(): check() insists the box stays ticked, and the point is that it does not.
    await capture.click();
    await expect(capture).not.toBeChecked();
    await context.close();
  } finally {
    setRole('bob', 'Operator');
  }
});

test('the timeline and the environment show the workspace in the viewer', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'vera');
  await page.getByRole('link', { name: 'Timeline · project memory', exact: true }).click();
  await expect(page).toHaveURL(/\/timeline$/);
  const timeline = page.getByRole('region', { name: 'Project Memory' });
  await expect(timeline).toContainText('PROJECT MEMORY — ');
  await expect(timeline.getByRole('button', { name: 'Copy all' })).toBeVisible();

  await page.getByRole('link', { name: 'Environment', exact: true }).click();
  await expect(page).toHaveURL(/\/environment$/);
  await expect(page.getByRole('region', { name: 'Environment' })).toContainText('Host: ');
  await context.close();
});


test('a viewer reads a staged run\'s proposal, and an operator applies it to the file', async ({ browser }) => {
  const viewer = await signedIn(browser, 'vera');
  await viewer.page.getByRole('link', { name: 'Proposals', exact: true }).click();
  await viewer.page.getByRole('link', { name: /redraft the proposal/ }).click();
  await expect(viewer.page).toHaveURL(/\/proposals\/[0-9a-f-]{36}\/[0-9a-f]{32}$/);
  await expect(viewer.page.locator('.diff')).toContainText('+ second draft');
  await expect(viewer.page.getByRole('button', { name: 'Apply' })).toHaveCount(0);
  await viewer.context.close();

  const { context, page } = await signedIn(browser, 'alice');
  await page.goto('/proposals');
  await page.getByRole('link', { name: /redraft the proposal/ }).click();
  await page.getByRole('button', { name: 'Apply' }).click();
  await expect(page.getByText(/^Applied: proposal\.md\. Every proposal of this run is decided\./)).toBeVisible();
  // The rail's dot goes with the last waiting change, with no navigation: the page stays where it is.
  await expect(page.locator('[data-rail="/proposals"] .dot')).toHaveCount(0);

  await page.goto('/proposals');
  await expect(page.getByText('Nothing waits.')).toBeVisible();
  await context.close();
});

/** The rail's Inbox icon, named by its tooltip: "Inbox", or "Inbox — 2 unread". */
const inboxIcon = (page) => page.getByRole('navigation', { name: 'Sections' }).getByRole('link', { name: /^Inbox/ });

/** How many the rail's Inbox icon says are unread. */
async function unread(page) {
  const name = await inboxIcon(page).getAttribute('aria-label');
  return Number(/(\d+) unread/.exec(name)?.[1] ?? 0);
}

test('a question a background run stopped at is read in the inbox by a viewer and answered there by an operator', async ({ browser }) => {
  // The harness filed it: "mail the digest" stopped at "Approve send_email" with nobody there to answer.
  const vera = await signedIn(browser, 'vera');
  await inboxIcon(vera.page).click();
  await expect(vera.page).toHaveURL(/\/inbox$/);
  await vera.page.getByRole('list', { name: 'Inbox items' }).getByRole('button', { name: /mail the digest/ }).click();
  const veraSees = vera.page.getByRole('region', { name: 'Waiting for your decision' });
  await expect(veraSees).toContainText('Approve send_email');
  // Hers to read, not to answer - nor to mark read for everybody.
  await expect(veraSees.getByRole('button')).toHaveCount(0);
  await expect(vera.page.getByRole('button', { name: 'Mark all read' })).toHaveCount(0);
  await vera.context.close();

  const { context, page } = await signedIn(browser, 'alice');
  await inboxIcon(page).click();
  const before = await unread(page);
  expect(before).toBeGreaterThan(0);
  await page.getByRole('list', { name: 'Inbox items' }).getByRole('button', { name: /mail the digest/ }).click();
  // Opened, it is read - and the rail's count follows at once, with no page to leave.
  await expect.poll(() => unread(page)).toBe(before - 1);

  const waiting = page.getByRole('region', { name: 'Waiting for your decision' });
  await expect(waiting.getByRole('heading', { name: 'WAITING FOR YOUR DECISION' })).toBeVisible();
  await waiting.getByRole('button', { name: 'Allow (recommended)' }).click();

  // Carried on under the same task, opened where the Runs screen shows a run - which asks its own question, answered so
  // the folder is free for the next scenario.
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);
  await answerWhateverWaits(page);

  // And no longer waiting in the inbox.
  await inboxIcon(page).click();
  await page.getByRole('list', { name: 'Inbox items' }).getByRole('button', { name: /mail the digest/ }).click();
  await expect(page.getByRole('region', { name: 'Waiting for your decision' })).toHaveCount(0);
  await context.close();
});

test('a lost connection disables actions until it is back', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'alice');
  await startRun(page, 'connection lost');
  // The page's connection, through a route the test holds. Going offline alone does not cut it: Chromium's offline
  // emulation stops new requests, and the WebSocket already open went on carrying the circuit - the page never knew.
  // Offline still matters: it fails Blazor's attempts to reconnect (each starts with a request) until the test is back.
  const sockets = [];
  await context.routeWebSocket(/\/_blazor/, (socket) => {
    socket.connectToServer();
    sockets.push(socket);
  });
  await page.reload();
  await expect(question(page)).toContainText('Approve write_file');
  const bar = page.locator('header.top');
  await expect(bar.getByText('Connected', { exact: true })).toBeVisible();

  await context.setOffline(true);
  for (const socket of sockets) await socket.close({ code: 4000, reason: 'the network went away' });
  await expect(bar.getByText('Reconnecting…', { exact: true })).toBeVisible();
  // A click while the connection is down would be dropped without a word: the page takes none. A trial click only
  // checks that the button would receive it, and times out when something lies over it.
  const deny = question(page).getByRole('button', { name: 'Deny' });
  await expect(deny.click({ trial: true, timeout: 1_000 })).rejects.toThrow();

  await context.setOffline(false);
  await expect(bar.getByText('Connected', { exact: true })).toBeVisible();
  await deny.click();
  await expect(question(page)).toBeHidden();
  await context.close();
});

test('a connection given up on says so, and reload is the one thing that can be clicked', async ({ browser }) => {
  const { context, page } = await signedIn(browser, 'vera');
  const bar = page.locator('header.top');
  await expect(bar.getByText('Connected', { exact: true })).toBeVisible();

  // The mark Blazor's script puts on the element once it has stopped trying (its retries take minutes to run out):
  // the state is what the page is told, and the test tells it directly.
  await page.evaluate(() => document.getElementById('components-reconnect-modal').classList.add('components-reconnect-failed'));
  await expect(bar.getByText('Disconnected', { exact: true })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Runs' }).click({ trial: true, timeout: 1_000 })).rejects.toThrow();

  await bar.getByRole('button', { name: 'Reload' }).click();
  await expect(bar.getByText('Connected', { exact: true })).toBeVisible();
  await expect(bar.getByRole('button', { name: 'Reload' })).toBeHidden();
  await context.close();
});

// ── A phone ──────────────────────────────────────────────────────────────────────────────────────────────────────────

/** A phone's screen: 390 by 844 and touch, the page laid out at the device's width (App.razor's viewport meta). */
const phone = { viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true };

/** Signs in on a phone. The Runs column - its RUNS heading with it - is in the drawer there, so the bar says the screen is up. */
async function signedInOnPhone(browser, name) {
  const context = await browser.newContext({ baseURL: base(), ignoreHTTPSErrors: true, ...phone });
  const page = await context.newPage();
  await page.goto('/');
  await expect(page).toHaveURL(/\/signin/);
  await page.getByLabel('Name').fill(name);
  await page.getByLabel('Password').fill(password);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await expect(sections(page).getByRole('link', { name: 'Runs', exact: true })).toBeVisible();
  return { context, page };
}

/** The rail, which is a bar along the bottom on a phone. Exact: the settings' own list is "Settings sections". */
const sections = (page) => page.getByRole('navigation', { name: 'Sections', exact: true });

const drawerButton = (page) => page.getByRole('button', { name: 'Workspace and runs' });

const runsColumn = (page) => page.getByRole('complementary', { name: 'Runs column' });

/** Nothing on the page is wider than the screen: a page that scrolls sideways takes the bars with it. */
async function fitsAcross(page) {
  const { scroll, inner } = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, inner: window.innerWidth }));
  expect(scroll, `${page.url()} is ${scroll} px across on a ${inner} px screen`).toBeLessThanOrEqual(inner);
}

test('on a phone the runs screen stacks and a run can be started and answered', async ({ browser }) => {
  const { context, page } = await signedInOnPhone(browser, 'alice');
  // The bar at the foot of the screen, across it; the column out of the way, the command bar the screen.
  const bar = await sections(page).boundingBox();
  expect(bar.width).toBe(390);
  expect(Math.round(bar.y + bar.height)).toBe(844);
  await expect(runsColumn(page)).toBeHidden();
  await expect(page.getByLabel('Task', { exact: true })).toBeVisible();
  await fitsAcross(page);

  // The column is a drawer from the top bar: open, it has the focus; Escape closes it and gives the focus back.
  const button = drawerButton(page);
  await expect(button).toHaveAttribute('aria-controls', 'workspace-drawer');
  await expect(button).toHaveAttribute('aria-expanded', 'false');
  await button.click();
  await expect(button).toHaveAttribute('aria-expanded', 'true');
  await expect(runsColumn(page)).toBeVisible();
  await expect(page.locator('#workspace-drawer')).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(runsColumn(page)).toBeHidden();
  await expect(button).toHaveAttribute('aria-expanded', 'false');
  await expect(button).toBeFocused();

  // A dialog opened in the drawer takes its own Escape: the drawer stays, and its scrim closes it.
  await button.click();
  await runsColumn(page).getByRole('button', { name: /^Workspace:/ }).click();
  await page.getByRole('button', { name: 'Add folder…' }).click();
  const browse = page.getByRole('dialog', { name: 'Add folder' });
  await expect(browse).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(browse).toBeHidden();
  await expect(runsColumn(page)).toBeVisible();
  await page.locator('.drawer-scrim').click({ position: { x: 370, y: 400 } });
  await expect(runsColumn(page)).toBeHidden();

  // Choosing the workspace in the drawer closes it.
  await button.click();
  await runsColumn(page).getByRole('button', { name: /^Workspace:/ }).click();
  await page.locator('.switcher').getByRole('link', { name: /^project/ }).click();
  await expect(runsColumn(page)).toBeHidden();
  await expect(page).toHaveURL(/\?ws=/);

  await page.getByLabel('Task', { exact: true }).fill('asked on a phone');
  await page.getByRole('button', { name: 'Run', exact: true }).click();
  await expect(page).toHaveURL(/\/runs\/[0-9a-f-]{36}$/);
  const run = page.url();
  await expect(question(page)).toContainText('Approve write_file');
  // The status column, and the question in it, under the run's views rather than beside them.
  const views = await page.getByRole('region', { name: 'Current action' }).boundingBox();
  const status = await page.getByRole('complementary', { name: 'Status' }).boundingBox();
  expect(status.y).toBeGreaterThanOrEqual(views.y + views.height - 1);
  expect(Math.abs(status.x - views.x)).toBeLessThan(2);
  await fitsAcross(page);

  // Away by the bottom bar, and back to the run through the drawer's RUNNING list - which closes the drawer.
  await sections(page).getByRole('link', { name: /^Inbox/ }).click();
  await expect(page).toHaveURL(/\/inbox$/);
  await expect(drawerButton(page)).toHaveCount(0);
  await sections(page).getByRole('link', { name: 'Runs', exact: true }).click();
  await drawerButton(page).click();
  await openRunning(page, 'asked on a phone');
  await expect(page).toHaveURL(run);
  await expect(runsColumn(page)).toBeHidden();

  await question(page).getByRole('button', { name: 'Allow', exact: true }).click();
  await expect(question(page)).toBeHidden();
  await expect(stop(page)).toBeDisabled();
  await context.close();
});

test('on a phone every section fits the screen across, and the foot of the rail is behind More', async ({ browser }) => {
  const { context, page } = await signedInOnPhone(browser, 'ada');
  const bar = sections(page);
  // Each section, and what says it has drawn what it shows: its width is measured with its content in it.
  const shown = {
    'Runs': () => page.getByLabel('Task', { exact: true }),
    'Timeline · project memory': () => page.getByRole('region', { name: 'Project Memory' }).getByText(/PROJECT MEMORY — /),
    'Templates · a saved task': () => page.getByRole('region', { name: 'Templates', exact: true }),
    'Schedules · run it on a day and a time': () => page.getByRole('region', { name: 'Schedules', exact: true }),
    'Inbox': () => page.locator('.inbox-bar'),
    'Proposals': () => page.getByRole('heading', { name: 'Proposed changes' }),
    'Global log': () => page.getByRole('list', { name: 'Lines' }),
    'Environment': () => page.getByRole('region', { name: 'Environment' }).getByText(/Host: /),
    'Settings': () => page.getByRole('navigation', { name: 'Settings sections' }),
    'Accounts': () => page.getByRole('list', { name: 'Accounts' }),
    'Audit': () => page.getByRole('table', { name: 'Audit trail' })
  };
  const more = bar.getByRole('button', { name: 'More sections' });
  const behindMore = ['Environment', 'Settings', 'Accounts', 'Audit'];

  for (const [name, ready] of Object.entries(shown)) {
    // The Inbox is named with its count when something is unread.
    const item = bar.getByRole('link', { name: name === 'Inbox' ? /^Inbox/ : name, exact: name !== 'Inbox', includeHidden: true });
    if (behindMore.includes(name)) {
      await expect(item).toBeHidden();
      await more.click();
      await expect(more).toHaveAttribute('aria-expanded', 'true');
    }
    await item.click();
    await expect(item).toHaveAttribute('aria-current', 'page');
    await expect(ready()).toBeVisible();
    if (behindMore.includes(name)) {
      // Closed by the choice, and the "you are here" mark on More, which holds it.
      await expect(more).toHaveAttribute('aria-expanded', 'false');
      await expect(more).toHaveClass(/\bon\b/);
    }
    await fitsAcross(page);
  }
  await context.close();
});
