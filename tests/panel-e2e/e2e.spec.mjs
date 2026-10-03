// The panel in a real browser, against a real gateway on MySQL and a real computer (tests/RemoteHostHarness):
// the first place app.js's wiring runs end to end. Eight scenarios, in order and sharing state - the device
// admitted in the second is the one removed in the fourth, and the seventh reads every request the others made.
import { test, expect } from '@playwright/test';
import {
  startComputer, dumpDatabase, readConnectionCode, readInviteLink, marker
} from './fixtures.mjs';

test.describe.configure({ mode: 'serial' });

const base = () => process.env.E2E_BASE_URL;

/** What the panel says of a grant that does not verify (trust.js REJECTED). */
const REJECTED = 'This key was not sent by your computer or a device you trust; it was ignored.';

/** What a browser at the account's device limit is told over the Devices list (trust.js DEVICE_LIMIT). */
const DEVICE_LIMIT = 'This account has as many browsers as it may; remove one to use this browser.';

/** What a request whose arguments do not hash to its action hash shows instead of Allow (app.js approvalNodes). */
const MISMATCH = 'This request does not match what the computer asked; answer it on the computer.';

// Everything the person writes or the computer seals, and every pairing secret: none of it may reach the
// gateway in the clear (scenario 7), or be found in its database (scenario 1).
const alice = marker('alice');
const bob = marker('bob');
const carol = marker('carol');
const dave = marker('dave');
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
    // allHeaders, not headers: the latter leaves out the cookie and other headers the browser adds itself.
    context.on('request', (request) => requests.push({
      url: request.url(), headers: request.allHeaders().catch(() => request.headers()), body: request.postData() ?? ''
    }));
  }

  [pageA, pageB, pageC] = await Promise.all([contextA.newPage(), contextB.newPage(), contextC.newPage()]);

  for (const [name, page] of [['A', pageA], ['B', pageB], ['C', pageC]]) {
    // Remove, Forget and Revoke ask first; the person says yes.
    page.on('dialog', (dialog) => dialog.accept());
    page.on('pageerror', (error) => pageErrors.push(`page ${name}: ${error.stack ?? error.message}`));
  }
});

/** Where each computer's output stood when the test began, so a test answers only for what it caused. */
let faultMarks = new Map();

/**
 * FAULT lines a scenario expects (regexes), set by the scenario. None does today: every one of them is a computer
 * doing what it should, and a refused command or a grant the gateway refused for good is something gone wrong.
 */
let expectedFaults = [];

const computers = () => [['studio', studio], ['carol', carolsComputer]].filter(([, computer]) => computer);

test.beforeEach(() => {
  expectedFaults = [];
  faultMarks = new Map(computers().map(([name, computer]) => [name, computer.lines.length]));
});

test.afterEach(() => {
  expect(pageErrors, 'exceptions the panel did not catch').toEqual([]);

  const faults = computers().flatMap(([name, computer]) => computer.lines
    .slice(faultMarks.get(name) ?? 0)
    .filter((line) => line.startsWith('FAULT ') && !expectedFaults.some((expected) => expected.test(line)))
    .map((line) => `${name}: ${line}`));
  expect(faults, 'what the computers said went wrong').toEqual([]);
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
 * Everything the account's key store in this browser holds, as text: the device's public key and id, and for each
 * computer every epoch's key bytes and the signing key pinned for it.
 */
async function keyStoreContents(page, userId) {
  return page.evaluate(async (userId) => {
    const { openKeystore } = await import('/js/keystore.js');
    const hex = (bytes) => (bytes ? Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('') : null);
    const store = await openKeystore(userId);
    try {
      const device = await store.device();
      const hosts = {};
      for (const hostId of await store.hosts()) {
        hosts[hostId] = {
          keys: Array.from(await store.hostKeys(hostId), ([epoch, key]) => [epoch, hex(key)]),
          pin: hex(await store.hostSigningKey(hostId))
        };
      }
      return { device: device?.id ?? null, devicePublic: hex(device?.publicRaw), hosts };
    } finally {
      store.close();
    }
  }, userId);
}

/** The id of the run of the task titled `title`, found by opening the tasks with this browser's keys. */
async function runIdOf(page, userId, title) {
  return page.evaluate(async ([userId, title]) => {
    const { openKeystore } = await import('/js/keystore.js');
    const { createReader } = await import('/js/reader.js');
    const store = await openKeystore(userId);
    try {
      // As the panel asks: naming this browser's device, without which the gateway answers nothing private.
      const device = (await store.device())?.id;
      const state = await (await fetch('/api/state', { headers: { 'X-Enactive-Device': device } })).json();
      const reader = createReader(store);
      for (const task of state.tasks) {
        if ((await reader.openTask(task))?.json?.title === title) {
          return state.runs.find((run) => run.taskId === task.id)?.id ?? null;
        }
      }
      return null;
    } finally {
      store.close();
    }
  }, [userId, title]);
}

/** The account's state as the gateway answers this browser, naming its device as the panel does: `{status, body}`. */
async function stateAsAsked(page, userId) {
  return page.evaluate(async (userId) => {
    const { openKeystore } = await import('/js/keystore.js');
    const store = await openKeystore(userId);
    try {
      const device = (await store.device())?.id;
      const response = await fetch('/api/state', { headers: { 'X-Enactive-Device': device } });
      return { status: response.status, body: await response.json() };
    } finally {
      store.close();
    }
  }, userId);
}

/**
 * Every run of `state` (an account's state, as the gateway answered some browser), opened with the account's key
 * store in this browser and the panel's own reader: `{createdAt, summary}` with the summary's text, or why it does
 * not open.
 */
async function runsAsOpenedHere(page, userId, state) {
  return page.evaluate(async ([userId, state]) => {
    const { openKeystore } = await import('/js/keystore.js');
    const { createReader } = await import('/js/reader.js');
    const store = await openKeystore(userId);
    try {
      const reader = createReader(store);
      return Promise.all(state.runs.map(async (run) => {
        const opened = await reader.openSummary(run);
        return { createdAt: run.createdAt, status: run.status, summary: opened?.text ?? opened?.unreadable ?? null };
      }));
    } finally {
      store.close();
    }
  }, [userId, state]);
}

/** Every cell of the dump that contains `words`, as `table.column`. */
function foundIn(dump, words) {
  return Object.entries(dump).flatMap(([table, rows]) => rows.flatMap((row) => Object.entries(row)
    .filter(([, value]) => value?.includes(words))
    .map(([column]) => `${table}.${column}`)));
}

/**
 * Each content column holds envelopes and nothing else - checked on a table that has rows, and a column with at
 * least one value: a check over no rows passes whatever the gateway would write. So do the sealed members of every
 * command's payload, the gateway's JSON around the browser's envelopes.
 */
function expectSealed(dump, columns) {
  for (const [table, column] of columns) {
    const values = (dump[table] ?? []).map((row) => row[column]).filter((value) => value !== null);
    expect(values.length, `rows with ${table}.${column}`).toBeGreaterThan(0);
    for (const value of values) expect(value, `${table}.${column}`).toMatch(/^e1:/);
  }
  expect(dump.commands.length, 'commands').toBeGreaterThan(0);
  for (const row of dump.commands) {
    const sealed = Object.entries(JSON.parse(row.payload)).filter(([member]) => /sealed/i.test(member));
    expect(sealed.length, `sealed members of a ${row.kind} command`).toBeGreaterThan(0);
    for (const [member, value] of sealed) expect(value, `commands.payload.${member}`).toMatch(/^e1:/);
  }
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
  for (const words of [first.title, first.prompt, workspace, '[1/1] Working', 'All done']) {
    expect(foundIn(dump, words), `"${words}" in the database`).toEqual([]);
  }

  // No approval exists yet; the last scenario looks at the database again once one does.
  expectSealed(dump, [
    ['tasks', 'sealed'], ['runs', 'sealed_summary'], ['events', 'sealed_detail'], ['notices', 'sealed_detail'],
    ['host_workspaces', 'sealed_name']
  ]);
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
  expect(runId).toBe(await runIdOf(pageB, aliceId, asking.title));
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
  const deviceA = (await keysHeld(pageA, aliceId, studioId)).device;
  const deviceB = (await keysHeld(pageB, aliceId, studioId)).device;

  await view(pageA, 'devices');
  const cardB = pageA.locator('#device-list .card').filter({ hasNotText: 'This device' }).filter({ hasNotText: 'Removed' });
  await expect(cardB).toHaveCount(1);
  const removedAt = studio.lines.length;
  await cardB.getByRole('button', { name: 'Remove' }).click();
  await expect(pageA.locator('#device-list .card', { hasText: 'Removed' })).toHaveCount(1);

  // The computer distrusts it and moves to a new key, which it signs over to the device that stays - and to no
  // other. Checked where the grants are made: the gateway refuses a grant for a removed device and answers it 403,
  // so a computer that went on granting it would look, from any browser, exactly like one that did not.
  await studio.waitFor(/^EPOCH 2$/);
  await studio.waitFor(new RegExp(`^GRANTED 2 ${deviceA}$`));
  await expect.poll(async () => (await keysHeld(pageA, aliceId, studioId)).newest).toBe(2);

  const card = await startTask(pageA, afterRemoval);
  await expect(card.locator(':scope > .card-head > .status')).toHaveText('Done');
  await expect(card).toContainText('All done');

  // Its session ended with it, so the first time it asks it is signed out; signed in again, the gateway tells it
  // the device was removed the first time it asks as that device.
  await expect(pageB.locator('#login')).toBeVisible();
  await expect(pageB.locator('#login-outcome')).toHaveText('You were signed out. Sign in again to go on.');
  await pageB.fill('#dev-name', alice);
  await pageB.click('#dev-sign-in button[type=submit]');
  await expect(pageB.locator('#device-removed')).toBeVisible();

  // The gateway answers it nothing more: every call names the device, and a removed one is refused all of them.
  const refused = await stateAsAsked(pageB, aliceId);
  expect(refused).toMatchObject({ status: 403, body: { code: 'device-revoked' } });

  // And were it handed the state anyway - here the state the remaining device is given - with every key it was
  // ever given it opens what it had and not the run after the removal.
  const held = await keysHeld(pageB, aliceId, studioId);
  expect(held).toMatchObject({ device: deviceB, newest: 1 });
  const { body: state } = await stateAsAsked(pageA, aliceId);
  const opened = (await runsAsOpenedHere(pageB, aliceId, state)).sort((a, b) => a.createdAt.localeCompare(b.createdAt));
  expect(opened.map((run) => run.summary)).toEqual([
    'All done', 'All done', 'this device has not been given the key for this'
  ]);

  // Everything epoch 2 went to, and nothing refused or dropped since the removal (afterEach checks the whole test).
  // From the removal and not from EPOCH 2: the computer may say a grant before it says the epoch it moved to.
  const sinceRemoval = studio.lines.slice(removedAt);
  expect(sinceRemoval.filter((line) => line.startsWith('GRANTED 2 '))).toEqual([`GRANTED 2 ${deviceA}`]);
  expect(sinceRemoval.filter((line) => /FAULT|GrantDropped/.test(line))).toEqual([]);
});

// ── 5 ────────────────────────────────────────────────────────────────────

test('5. a second account in the same browser sees none of the first one\'s keys or content', async () => {
  const before = await keysHeld(pageA, aliceId, studioId);
  const contentsBefore = await keyStoreContents(pageA, aliceId);
  expect(Object.keys(contentsBefore.hosts)).toEqual([studioId]);
  await signOut(pageA);
  await signIn(pageA, bob);
  const bobId = (await sessionUser(pageA)).id;
  expect(bobId).not.toBe(aliceId);

  await expect(pageA.locator('#runs-empty')).toBeVisible();
  await view(pageA, 'hosts');
  await expect(pageA.locator('#host-list .card')).toHaveCount(0);
  // Retrying, so a late drawing of something of Alice's - a poll of hers still in flight - would be caught too.
  for (const words of [first.title, afterRemoval.title, workspace, 'Studio PC', 'All done']) {
    await expect(pageA.locator('body'), `"${words}" on Bob's screen`).not.toContainText(words);
  }

  // One database per account: Bob's holds his own device key and nothing else, Alice's is still there.
  const names = (await pageA.evaluate(() => indexedDB.databases())).map((one) => one.name);
  expect(names).toEqual(expect.arrayContaining([`enactive-keys-${aliceId}`, `enactive-keys-${bobId}`]));
  expect(await keysHeld(pageA, bobId, studioId)).toMatchObject({ hosts: [], newest: null });
  expect((await keysHeld(pageA, bobId, null)).device).not.toBe(before.device);
  expect(await keysHeld(pageA, aliceId, studioId)).toEqual(before);
  expect(await keyStoreContents(pageA, aliceId)).toEqual(contentsBefore);

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
  await expect(card.getByRole('button', { name: 'Allow' })).toHaveCount(0);
  await expect(card.getByRole('button', { name: 'Deny' })).toHaveCount(0);
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

test('7. nothing the person wrote and no secret reached the gateway in the clear', async () => {
  expect(secrets.length).toBe(3);
  expect(requests.length).toBeGreaterThan(50);

  // Every key the browser ever held for the computer - epoch 1, which the removed device had too, and epoch 2 -
  // in each spelling it could be sent in.
  const held = Object.values((await keyStoreContents(pageA, aliceId)).hosts).flatMap((host) => host.keys);
  expect(held.map(([epoch]) => epoch)).toEqual([1, 2]);
  const keys = held.flatMap(([, hex]) => {
    const bytes = Buffer.from(hex, 'hex');
    return [hex, bytes.toString('base64'), bytes.toString('base64url')];
  });

  const words = [
    ...tasks.flatMap(({ title, prompt }) => [title, prompt]), workspace, carolWorkspace, ...secrets, ...keys
  ];

  // The gateway's whole database once everything has been written: the permission, its answer, the removal, the
  // stopped run. Besides what the person wrote, what the computer sealed: the action, the steps, the endings.
  const dump = dumpDatabase();
  const sealedByTheComputer = [
    'Run echo on the computer?', 'echo hi', '/harness/workspace', 'Allowed: echo hi', '[1/1] Working', 'All done',
    "Stopped at the owner's request."
  ];
  for (const found of [...words, ...sealedByTheComputer]) {
    expect(foundIn(dump, found), `"${found}" in the database`).toEqual([]);
  }
  expectSealed(dump, [
    ['tasks', 'sealed'], ['runs', 'sealed_summary'], ['events', 'sealed_detail'], ['notices', 'sealed_detail'],
    ['host_workspaces', 'sealed_name'], ['approvals', 'sealed_action']
  ]);
  expect(dump.approvals.length).toBe(2);

  for (const request of requests) {
    const sent = [request.url, ...Object.values(await request.headers), request.body].join('\n');
    for (const word of words) {
      const found = sent.includes(word) || sent.includes(encodeURIComponent(word));
      expect(found, `"${word}" in a request to ${request.url}`).toBe(false);
    }
  }
});

// ── 8 ────────────────────────────────────────────────────────────────────

test('8. a browser new to an account at its device limit removes one and is then let in', async ({ browser }) => {
  const [contextOld, contextNew] = await Promise.all([browser.newContext(), browser.newContext()]);
  try {
    const [old, fresh] = await Promise.all([contextOld.newPage(), contextNew.newPage()]);
    for (const [name, page] of [['old', old], ['new', fresh]]) {
      page.on('dialog', (dialog) => dialog.accept());
      page.on('pageerror', (error) => pageErrors.push(`page ${name}: ${error.stack ?? error.message}`));
    }

    // One browser of the account, and then as many more devices as the gateway's limit (10) allows - browsers whose
    // site data was cleared, say, which nothing ever removes.
    await signIn(old, dave);
    await old.evaluate(async () => {
      const { csrfToken } = await (await fetch('/api/session')).json();
      for (let i = 1; i <= 9; i++) {
        const pair = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits']);
        const raw = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey));
        const publicKey = btoa(String.fromCharCode(...raw)).replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');
        const response = await fetch('/api/devices', {
          method: 'POST',
          headers: { 'content-type': 'application/json', 'X-CSRF-TOKEN': csrfToken },
          body: JSON.stringify({ publicKey, label: `spare-${i}` })
        });
        if (!response.ok) throw new Error(`spare-${i}: ${response.status} ${await response.text()}`);
      }
    });

    // The new browser cannot register: it is shown the list, what to do, and nothing else.
    await fresh.goto(`${base()}/`);
    await fresh.fill('#dev-name', dave);
    await fresh.click('#dev-sign-in button[type=submit]');
    await expect(fresh.locator('#device-limit')).toHaveText(DEVICE_LIMIT);
    await expect(fresh.locator('#device-list .card')).toHaveCount(10);
    await expect(fresh.locator('.tab[data-view="runs"]')).toBeHidden();

    // Removing one makes room, and the browser registers in it at once.
    await fresh.locator('#device-list .card', { hasText: 'spare-1' }).getByRole('button', { name: 'Remove' }).click();
    await expect(fresh.locator('#device-limit')).toBeHidden();
    await expect(fresh.locator('#device-list .card', { hasText: 'This device' })).toHaveCount(1);
    await expect(fresh.locator('.tab[data-view="runs"]')).toBeVisible();
    await expect(fresh.locator('#link-label')).toHaveText('Live');
  } finally {
    await Promise.all([contextOld.close(), contextNew.close()]);
  }
});
