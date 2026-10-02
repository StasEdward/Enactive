// The panel in a real browser, against a real gateway on MySQL and a real computer (tests/RemoteHostHarness):
// the first place app.js's wiring runs end to end. Seven scenarios, in order and sharing state - the device
// admitted in the second is the one removed in the fourth, and the seventh reads every request the others made.
import { test, expect } from '@playwright/test';
import {
  startComputer, dumpDatabase, readConnectionCode, readInviteLink, marker
} from './fixtures.mjs';

test.describe.configure({ mode: 'serial' });

const base = () => process.env.E2E_BASE_URL;

/** What the panel says of a grant that does not verify (trust.js REJECTED). */
const REJECTED = 'This key was not sent by your computer or a device you trust; it was ignored.';

/** What a request whose arguments do not hash to its action hash shows instead of Allow (app.js approvalNodes). */
const MISMATCH = 'This request does not match what the computer asked; answer it on the computer.';

// Everything the person writes or the computer seals, and every pairing secret: none of it may reach the
// gateway in the clear (scenario 7), or be found in its database (scenario 1).
const alice = marker('alice');
const bob = marker('bob');
const carol = marker('carol');
const workspace = marker('Workspace');
const carolWorkspace = marker('CarolWorkspace');
const first = { title: marker('Title-first'), prompt: marker('Please tidy the first folder') };
const asking = { title: marker('Title-asking'), prompt: `${marker('Please write the file')} ASK` };
const afterRemoval = { title: marker('Title-after'), prompt: marker('Please run after the removal') };
const mismatched = { title: marker('Title-mismatch'), prompt: `${marker('Please answer this')} BADHASH` };
const tasks = [first, asking, afterRemoval, mismatched];
const secrets = [];

/** Every request every context made: the URL, the headers and the body. */
const requests = [];

/** Exceptions the panel threw and nothing caught: none is expected, whatever a scenario is about. */
const pageErrors = [];

let contextA;
let contextB;
let contextC;
let pageA;
let pageB;
let pageC;
let studio;
let carolsComputer;
let studioId;
let aliceId;

test.beforeAll(async ({ browser }) => {
  [contextA, contextB, contextC] = await Promise.all([browser.newContext(), browser.newContext(), browser.newContext()]);

  for (const context of [contextA, contextB, contextC]) {
    context.on('request', (request) => requests.push({
      url: request.url(), headers: request.headers(), body: request.postData() ?? ''
    }));
  }

  [pageA, pageB, pageC] = await Promise.all([contextA.newPage(), contextB.newPage(), contextC.newPage()]);

  for (const [name, page] of [['A', pageA], ['B', pageB], ['C', pageC]]) {
    // Remove, Forget and Revoke ask first; the person says yes.
    page.on('dialog', (dialog) => dialog.accept());
    page.on('pageerror', (error) => pageErrors.push(`page ${name}: ${error.stack ?? error.message}`));
  }
});

test.afterEach(() => {
  expect(pageErrors, 'exceptions the panel did not catch').toEqual([]);
});

test.afterAll(async () => {
  await Promise.all([studio?.stop(), carolsComputer?.stop()]);
  await Promise.all([contextA?.close(), contextB?.close(), contextC?.close()]);
});

// ── the steps a person takes ─────────────────────────────────────────────

/**
 * Signs in with the development sign-in. On a page already at the sign-in view - after a sign-out - in that same
 * page, as a person would: the page then goes from one account to the next without a reload, which is where what
 * the last account left in it would show.
 */
async function signIn(page, name) {
  if (!(await page.locator('#login').isVisible())) {
    await page.goto(`${base()}/`);
  }
  await expect(page.locator('#dev-sign-in')).toBeVisible();
  await page.fill('#dev-name', name);
  await page.click('#dev-sign-in button[type=submit]');
  await expect(page.locator('#panel')).toBeVisible();
  await expect(page.locator('#account-name')).toHaveText(name);
  // The first snapshot is in: what is drawn now is what the gateway sent for this account.
  await expect(page.locator('#link-label')).toHaveText('Live');
}

async function signOut(page) {
  await page.click('#account-name');
  await page.click('#sign-out');
  await expect(page.locator('#login')).toBeVisible();
}

async function view(page, name) {
  await page.click(`nav.tabs button[data-view=${name}]`);
  await expect(page.locator(`#view-${name}`)).toBeVisible();
}

/** Registers a computer and returns its connection code, from the dialog that is left open waiting for it. */
async function register(page, label) {
  await view(page, 'hosts');
  await page.click('#add-host');
  await page.fill('#host-name', label);
  await page.click('#host-submit');
  await expect(page.locator('#host-code')).not.toHaveValue('');
  const code = await page.inputValue('#host-code');
  secrets.push(readConnectionCode(code).p);
  return code;
}

/** Writes a task on the computer's one workspace and starts it; returns the run's card. */
async function startTask(page, { title, prompt }) {
  // The workspace is offered once the computer has published it and this device can read its name.
  await view(page, 'hosts');
  await expect(page.locator('#host-list .chip', { hasText: workspace })).toBeVisible();
  await view(page, 'runs');
  await page.click('#new-task');
  await expect(page.locator('#task-dialog')).toBeVisible();
  await page.selectOption('#task-workspace', { label: `${workspace} · Studio PC` });
  await page.fill('#task-title', title);
  await page.fill('#task-prompt', prompt);
  await page.click('#task-submit');
  await expect(page.locator('#task-dialog')).toBeHidden();
  return runCard(page, title);
}

function runCard(page, title) {
  return page.locator('#run-list .card', { has: page.locator('h3', { hasText: title }) });
}

/** The run's timeline, opened and read, then closed again. */
async function timeline(page, title) {
  await runCard(page, title).getByRole('button', { name: 'Timeline' }).click();
  const detail = page.locator('#run-detail');
  await expect(detail).toBeVisible();
  const text = await detail.innerText();
  await page.click('#run-dialog [data-close]');
  return text;
}

// ── looking inside the browser ───────────────────────────────────────────

async function sessionUser(page) {
  return page.evaluate(async () => (await (await fetch('/api/session')).json()).user);
}

/** What the account's key store in this browser holds, read with the panel's own module. */
async function keysHeld(page, userId, hostId) {
  return page.evaluate(async ([userId, hostId]) => {
    const { openKeystore } = await import('/js/keystore.js');
    const store = await openKeystore(userId);
    try {
      return {
        device: (await store.device())?.id ?? null,
        hosts: await store.hosts(),
        newest: hostId ? await store.newestEpoch(hostId) : null
      };
    } finally {
      store.close();
    }
  }, [userId, hostId]);
}

/**
 * Every run in the account's state, opened with the account's key store in this browser and the panel's own
 * reader: `{createdAt, summary}` with the summary's text, or why it does not open.
 */
async function runsAsOpenedHere(page, userId) {
  return page.evaluate(async (userId) => {
    const { openKeystore } = await import('/js/keystore.js');
    const { createReader } = await import('/js/reader.js');
    const store = await openKeystore(userId);
    try {
      const state = await (await fetch('/api/state')).json();
      const reader = createReader(store);
      return Promise.all(state.runs.map(async (run) => {
        const opened = await reader.openSummary(run);
        return { createdAt: run.createdAt, status: run.status, summary: opened?.text ?? opened?.unreadable ?? null };
      }));
    } finally {
      store.close();
    }
  }, userId);
}

// ── 1 ────────────────────────────────────────────────────────────────────

test('1. pairing: a computer registered here runs a task, and the database holds only sealed content', async () => {
  await signIn(pageA, alice);
  aliceId = (await sessionUser(pageA)).id;

  const code = await register(pageA, 'Studio PC');
  studioId = readConnectionCode(code).h;
  studio = startComputer('studio', { workspace });
  studio.pair(code);
  await studio.waitFor(/^PAIRED /);

  // The computer's first grant verified with the code's secret and taken: the dialog closes on its own.
  await expect(pageA.locator('#host-dialog')).toBeHidden();
  expect((await keysHeld(pageA, aliceId, studioId)).newest).toBe(1);

  const card = await startTask(pageA, first);
  await expect(card.locator(':scope > .card-head > .status')).toHaveText('Done');
  await expect(card).toContainText('All done');

  const steps = await timeline(pageA, first.title);
  expect(steps).toContain(first.prompt);
  expect(steps).toContain('[1/1] Working');
  expect(steps).toContain('All done');

  // The gateway's own record of all that: content only as envelopes, and none of the words anywhere.
  const dump = dumpDatabase();
  const cells = Object.entries(dump).flatMap(([table, rows]) =>
    rows.flatMap((row) => Object.entries(row).map(([column, value]) => ({ table, column, value }))));

  for (const words of [first.title, first.prompt, workspace, '[1/1] Working', 'All done']) {
    expect(cells.filter(({ value }) => value?.includes(words)), `"${words}" in the database`).toEqual([]);
  }

  const sealedColumns = [
    ['tasks', 'sealed'], ['runs', 'sealed_summary'], ['events', 'sealed_detail'], ['notices', 'sealed_detail'],
    ['host_workspaces', 'sealed_name'], ['approvals', 'sealed_action']
  ];
  for (const [table, column] of sealedColumns) {
    for (const row of dump[table] ?? []) {
      if (row[column] !== null) expect(row[column], `${table}.${column}`).toMatch(/^e1:/);
    }
  }
  // A command's payload is the gateway's JSON around the browser's envelopes: every sealed member is one.
  for (const row of dump.commands) {
    for (const [member, value] of Object.entries(JSON.parse(row.payload))) {
      if (/sealed/i.test(member)) expect(value, `commands.payload.${member}`).toMatch(/^e1:/);
    }
  }
  expect(dump.commands.length).toBe(1);
  expect(dump.tasks.length).toBe(1);
  expect(dump.runs.filter((run) => run.sealed_summary !== null).length).toBe(1);
  expect(dump.host_workspaces.length).toBe(1);
});

// ── 2 ────────────────────────────────────────────────────────────────────

test('2. a second device by invitation reads the same runs and answers a permission', async () => {
  await signIn(pageB, alice);

  // Signed in, but holding no key: it sees that the run exists and cannot read it.
  await expect(runCard(pageB, 'Task')).toHaveCount(1);
  await expect(pageB.locator('#run-list')).not.toContainText(first.title);

  await view(pageA, 'devices');
  await pageA.click('#add-device');
  await expect(pageA.locator('#invite-link')).not.toHaveValue('');
  const link = await pageA.inputValue('#invite-link');
  secrets.push(readInviteLink(link).p);

  // Opened where nobody is signed in, as on a phone that scanned the code: the invitation waits for the sign-in.
  await signOut(pageB);
  await pageB.goto(link);
  await expect(pageB.locator('#login-outcome')).toHaveText('Sign in to the account you are adding this device to.');
  // The secret is taken out of the address at once.
  expect(new URL(pageB.url()).hash).toBe('');
  await signIn(pageB, alice);
  await expect(pageB.locator('#join-status')).toHaveText('This device can now read Studio PC.');
  await expect(pageA.locator('#invite-status')).toContainText('Added');
  await pageA.click('#invite-dialog [data-close]');
  await pageB.click('#join-dialog [data-close]');

  await view(pageB, 'runs');
  await expect(runCard(pageB, first.title)).toContainText('All done');

  // A permission, started on the first device and answered on the second.
  await startTask(pageA, asking);
  const ask = runCard(pageB, asking.title);
  await expect(ask).toContainText('Run echo on the computer?');
  await expect(ask.locator('pre.action')).toHaveText('echo hi');
  await ask.getByRole('button', { name: 'Allow' }).click();

  const [, runId] = await studio.waitFor(/^DECIDED (\S+) allow$/);
  expect(runId).toBeTruthy();
  await expect(runCard(pageB, asking.title).locator(':scope > .card-head > .status')).toHaveText('Done');
  expect(await timeline(pageB, asking.title)).toContain('Allowed: echo hi');
});

// ── 3 ────────────────────────────────────────────────────────────────────

test('3. a grant whose MAC was changed on the way is rejected, and nothing is stored', async () => {
  await signIn(pageC, carol);
  const carolId = (await sessionUser(pageC)).id;

  // The gateway - or anyone between it and the browser - changes one character of every grant's MAC.
  let tampered = 0;
  await pageC.route('**/api/grants', async (route) => {
    const response = await route.fetch();
    const groups = await response.json();
    for (const group of groups) {
      for (const grant of group.grants) {
        const at = Math.floor(grant.mac.length / 2);
        grant.mac = grant.mac.slice(0, at) + (grant.mac[at] === 'A' ? 'B' : 'A') + grant.mac.slice(at + 1);
        tampered++;
      }
    }
    await route.fulfill({ response, json: groups });
  });

  const code = await register(pageC, 'Carol PC');
  const carolHostId = readConnectionCode(code).h;
  carolsComputer = startComputer('carol', { workspace: carolWorkspace });
  carolsComputer.pair(code);
  await carolsComputer.waitFor(/^PAIRED /);

  await expect(pageC.locator('#host-error')).toHaveText(REJECTED);
  expect(tampered).toBeGreaterThan(0);
  await pageC.click('#host-dialog [data-close]');

  // Nothing stored, so nothing the computer sealed opens here.
  expect(await keysHeld(pageC, carolId, carolHostId)).toMatchObject({ hosts: [], newest: null });
  const card = pageC.locator('#host-list .card', { hasText: 'Carol PC' });
  await expect(card).toContainText('Not paired with this device.');
  await expect(card.locator('.chip')).toContainText('cannot read its name');
  await expect(pageC.locator('body')).not.toContainText(carolWorkspace);

  await pageC.unroute('**/api/grants');
});

// ── 4 ────────────────────────────────────────────────────────────────────

test('4. a removed device cannot read what the computer sends after it rotates', async () => {
  const deviceB = (await keysHeld(pageB, aliceId, studioId)).device;

  await view(pageA, 'devices');
  const cardB = pageA.locator('#device-list .card').filter({ hasNotText: 'This device' }).filter({ hasNotText: 'Removed' });
  await expect(cardB).toHaveCount(1);
  await cardB.getByRole('button', { name: 'Remove' }).click();
  await expect(pageA.locator('#device-list .card', { hasText: 'Removed' })).toHaveCount(1);

  // The computer distrusts it and moves to a new key, which it signs over to the device that stays.
  await studio.waitFor(/^EPOCH 2$/);
  await expect.poll(async () => (await keysHeld(pageA, aliceId, studioId)).newest).toBe(2);

  const card = await startTask(pageA, afterRemoval);
  await expect(card.locator(':scope > .card-head > .status')).toHaveText('Done');
  await expect(card).toContainText('All done');

  // The removed device is told so by the gateway the first time it asks as itself.
  await expect(pageB.locator('#device-removed')).toBeVisible();

  // And with every key it was ever given, it opens what it had and not the run after the removal.
  const held = await keysHeld(pageB, aliceId, studioId);
  expect(held).toMatchObject({ device: deviceB, newest: 1 });
  const opened = (await runsAsOpenedHere(pageB, aliceId)).sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  expect(opened.map((run) => run.summary)).toEqual([
    'All done', 'All done', 'this device has not been given the key for this'
  ]);
});

// ── 5 ────────────────────────────────────────────────────────────────────

test('5. a second account in the same browser sees none of the first one\'s keys or content', async () => {
  const before = await keysHeld(pageA, aliceId, studioId);
  await signOut(pageA);
  await signIn(pageA, bob);
  const bobId = (await sessionUser(pageA)).id;
  expect(bobId).not.toBe(aliceId);

  await expect(pageA.locator('#runs-empty')).toBeVisible();
  await view(pageA, 'hosts');
  await expect(pageA.locator('#host-list .card')).toHaveCount(0);
  const page = await pageA.locator('body').innerText();
  for (const words of [first.title, afterRemoval.title, workspace, 'Studio PC', 'All done']) {
    expect(page, `"${words}" on Bob's screen`).not.toContain(words);
  }

  // One database per account: Bob's holds his own device key and nothing else, Alice's is still there.
  const names = (await pageA.evaluate(() => indexedDB.databases())).map((one) => one.name);
  expect(names).toEqual(expect.arrayContaining([`enactive-keys-${aliceId}`, `enactive-keys-${bobId}`]));
  expect(await keysHeld(pageA, bobId, studioId)).toMatchObject({ hosts: [], newest: null });
  expect((await keysHeld(pageA, bobId, null)).device).not.toBe(before.device);
  expect(await keysHeld(pageA, aliceId, studioId)).toEqual(before);

  // Alice back in this browser reads everything as before.
  await signOut(pageA);
  await signIn(pageA, alice);
  await expect(runCard(pageA, first.title)).toContainText('All done');
  await expect(runCard(pageA, afterRemoval.title)).toContainText('All done');
});

// ── 6 ────────────────────────────────────────────────────────────────────

test('6. a permission whose arguments do not hash to its action hash offers no Allow', async () => {
  const card = await startTask(pageA, mismatched);
  await studio.waitFor(/^BADHASH /);

  await expect(card).toContainText(MISMATCH);
  await view(pageA, 'approvals');
  await expect(pageA.locator('#approval-list .card')).toContainText(MISMATCH);
  await expect(pageA.getByRole('button', { name: 'Allow' })).toHaveCount(0);
  await expect(pageA.getByRole('button', { name: 'Deny' })).toHaveCount(0);

  // Stopped from here, which is the only way this run ends.
  await view(pageA, 'runs');
  await card.getByRole('button', { name: 'Stop' }).click();
  await expect(card.locator(':scope > .card-head > .status')).toHaveText('Cancelled');
});

// ── 7 ────────────────────────────────────────────────────────────────────

test('7. nothing the person wrote and no pairing secret reached the gateway in the clear', async () => {
  expect(secrets.length).toBe(3);
  expect(requests.length).toBeGreaterThan(50);

  const words = [...tasks.flatMap(({ title, prompt }) => [title, prompt]), workspace, carolWorkspace, ...secrets];

  for (const request of requests) {
    const sent = [request.url, ...Object.values(request.headers), request.body].join('\n');
    for (const word of words) {
      const found = sent.includes(word) || sent.includes(encodeURIComponent(word));
      expect(found, `"${word}" in a request to ${request.url}`).toBe(false);
    }
  }
});
