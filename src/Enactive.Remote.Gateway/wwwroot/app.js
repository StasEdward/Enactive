// =============================================================================
// Enactive Remote — the panel.
//
// It holds no truth of its own. Everything on the screen came from the last
// snapshot the gateway sent, and every action is a request that the gateway may
// refuse and the computer may still decline. So this file is three things: a
// poll, a merge, and a renderer - and it is written so that none of them can
// quietly invent a state the server did not report.
//
// NOTHING here builds markup out of data. Every value that came from the wire -
// a task title, a model's reasoning, the arguments of a command about to run -
// is put on the page with textContent. The Content-Security-Policy this gateway
// sends is a second line, not the first one.
// =============================================================================

const POLL_MS = 3000;

// How much of the two streams a long-lived tab keeps. The gateway stops the
// cost of a POLL growing with history; without this the cost of the PAGE would
// grow instead, on a phone left open for a day.
const KEEP_EVENTS = 500;
const KEEP_NOTICES = 200;

const state = {
  cursor: null,
  hosts: [],
  tasks: [],
  runs: [],
  approvals: [],
  notices: [],
  events: [],
  unread: 0,
  retention: null,
  live: false
};

// One command id per logical action, kept across retries.
//
// The gateway treats a repeated id as the SAME instruction and returns the
// command it already queued. That is what makes a tapped button on a flaky
// phone connection safe: a new id each time would queue a second run of the
// same task, and the owner would find out by reading the timeline.
const commandIds = new Map();

function commandId(key) {
  if (!commandIds.has(key)) {
    commandIds.set(key, crypto.randomUUID());
  }

  return commandIds.get(key);
}

const $ = (id) => document.getElementById(id);

// ── talking to the gateway ───────────────────────────────────────────────

let csrf = "";

/** A refusal the gateway coded, kept apart from a network failure. */
class Refused extends Error {
  constructor(code, message) {
    super(message);
    this.code = code;
  }
}

async function get(path) {
  const response = await fetch(path, { headers: { accept: "application/json" } });
  return await unwrap(response);
}

async function post(path, body) {
  const response = await fetch(path, {
    method: "POST",
    headers: { "content-type": "application/json", "X-CSRF-TOKEN": csrf },
    body: JSON.stringify(body ?? {})
  });

  return await unwrap(response);
}

async function unwrap(response) {
  if (response.status === 401) {
    showLogin();
    throw new Refused("unauthenticated", "Sign in again.");
  }

  const text = await response.text();
  const body = text ? JSON.parse(text) : null;

  if (!response.ok) {
    // The gateway's faults carry a code, and the page says what the code means
    // rather than repeating a sentence written for a log.
    throw new Refused(body?.code ?? "unknown", body?.error ?? "That did not work.");
  }

  return body;
}

/** Refreshes the CSRF token, which is also how the page learns whether it is signed in. */
async function session() {
  const view = await get("/api/session");
  csrf = view.csrfToken;
  return view.authenticated;
}

// ── the poll ─────────────────────────────────────────────────────────────

/**
 * Folds a snapshot into what is on screen.
 *
 * The gateway says which of the two kinds it sent. `delta` is not inferred from
 * having asked with a cursor, because a delta too large to carry comes back as
 * a full snapshot in answer to exactly that request - and appending one to what
 * is already here would show every event twice.
 *
 * The mutable sets always arrive whole, so they are replaced and never merged.
 * The two streams are appended, and the cursor is what guarantees nothing in
 * them has been seen before.
 */
function apply(snapshot) {
  state.hosts = snapshot.hosts;
  state.tasks = snapshot.tasks;
  state.runs = snapshot.runs;
  state.approvals = snapshot.approvals;
  state.unread = snapshot.unreadNotices;
  state.retention = snapshot.retention;

  if (snapshot.delta) {
    state.events = state.events.concat(snapshot.events).slice(-KEEP_EVENTS);
    state.notices = state.notices.concat(snapshot.notices).slice(-KEEP_NOTICES);
  } else {
    state.events = snapshot.events.slice(-KEEP_EVENTS);
    state.notices = snapshot.notices.slice(-KEEP_NOTICES);
  }

  state.cursor = snapshot.cursor;
}

async function poll() {
  try {
    apply(await get(state.cursor === null ? "/api/state" : `/api/state?since=${state.cursor}`));
    setLive(true);
  } catch (error) {
    if (error instanceof Refused && error.code === "unauthenticated") {
      return;
    }

    // The screen keeps the last state it was given and says that it is old. An
    // empty page would read as "nothing is running", which is a different and
    // much worse claim than "I cannot see".
    setLive(false);
  }

  render();
}

/** Polls now rather than waiting out the interval - used after every action. */
async function refresh() {
  await poll();
}

// ── building nodes ───────────────────────────────────────────────────────

function node(tag, className, text) {
  const element = document.createElement(tag);

  if (className) {
    element.className = className;
  }

  if (text !== undefined && text !== null) {
    element.textContent = String(text);
  }

  return element;
}

function fill(container, children, empty) {
  container.replaceChildren(...children);

  if (empty) {
    empty.hidden = children.length > 0;
  }
}

const RUNNING = ["Queued", "Running", "WaitingForUser", "CancelRequested"];

function statusClass(status) {
  if (status === "Completed") return "status is-done";
  if (status === "Failed") return "status is-failed";
  if (status === "WaitingForUser") return "status is-waiting";
  if (RUNNING.includes(status)) return "status is-running";
  return "status";
}

/**
 * What a status or an event kind means to a person, rather than what the enum is
 * called. Anything this table has never heard of falls through as its own name:
 * a member added to the contract shows up looking unfamiliar, which is what it
 * is, instead of being quietly rendered as something else.
 */
function statusText(name) {
  return {
    Queued: "Queued",
    Running: "Running",
    WaitingForUser: "Needs you",
    CancelRequested: "Stopping",
    Completed: "Done",
    Failed: "Failed",
    Cancelled: "Cancelled",
    Incomplete: "Incomplete",
    Interrupted: "Interrupted",
    Progress: "",
    ApprovalRequested: "Permission requested",
    ApprovalResolved: "Permission answered"
  }[name] ?? name;
}

function when(iso) {
  const at = new Date(iso);
  const time = node("time", null, at.toLocaleString());
  time.dateTime = iso;
  return time;
}

// ── rendering ────────────────────────────────────────────────────────────

function render() {
  renderRuns();
  renderApprovals();
  renderInbox();
  renderHosts();
  renderCounts();
}

function renderCounts() {
  const active = state.runs.filter((run) => RUNNING.includes(run.status)).length;
  count("count-runs", active);
  count("count-approvals", state.approvals.length);
  count("count-inbox", state.unread);
}

function count(id, value) {
  const badge = $(id);
  badge.textContent = value > 0 ? String(value) : "";
  badge.classList.toggle("is-shown", value > 0);
}

function renderRuns() {
  // The whole task, not just its title. The prompt is what was actually asked
  // for, and until this map carried it there was nowhere in the panel that
  // could show it back: you typed what needed doing, it ran, and from then on
  // the request existed only as a heading you had written for it.
  const tasks = new Map(state.tasks.map((task) => [task.id, task]));

  const cards = state.runs.map((run) => {
    const task = tasks.get(run.taskId);
    const card = node("div", "card");
    const head = node("div", "card-head");
    head.append(node("h3", null, task?.title ?? "Task"));
    head.append(node("span", statusClass(run.status), statusText(run.status)));
    card.append(head);

    card.append(node("p", "meta", run.endedAt ? `Ended ${new Date(run.endedAt).toLocaleString()}`
      : `Started ${new Date(run.createdAt).toLocaleString()}`));

    if (run.summary) {
      card.append(node("p", null, run.summary));
    }

    const actions = node("div", "actions");
    const open = node("button", "secondary", "Timeline");
    open.type = "button";
    open.addEventListener("click", () => showRun(run, task));
    actions.append(open);

    // Only a run that has not ended. A stop offered on a finished run is a
    // button that can only ever produce a refusal.
    if (RUNNING.includes(run.status) && run.status !== "CancelRequested") {
      const stop = node("button", "danger", "Stop");
      stop.type = "button";
      stop.addEventListener("click", () => act(stop, () =>
        post(`/api/runs/${run.id}/cancel`, { commandId: commandId(`cancel:${run.id}`) })));
      actions.append(stop);
    }

    card.append(actions);
    return card;
  });

  fill($("run-list"), cards, $("runs-empty"));
}

function renderApprovals() {
  const cards = state.approvals.map((approval) => {
    const card = node("div", "card");

    const head = node("div", "card-head");
    head.append(node("h3", null, "Permission requested"));
    head.append(node("span", "status is-waiting", approval.remoteDecidable ? "Waiting" : "At the computer"));
    card.append(head);

    if (approval.reason) {
      card.append(node("p", null, approval.reason));
    }

    card.append(node("p", "meta", `${approval.tool} · in ${approval.workingDirectory}`));

    // The whole action, never a summary. A person cannot approve what they were
    // not shown, and a truncated command is one nobody read.
    card.append(node("pre", "action", approval.arguments));

    if (approval.remoteDecidable) {
      const actions = node("div", "actions");
      actions.append(decide(approval, "Allow", "primary"));
      actions.append(decide(approval, "Deny", "secondary"));
      card.append(actions);
    } else {
      // Not a disabled button: the server refuses this whatever the page draws,
      // and the card says why rather than looking broken.
      //
      // It says REFUSED and not "answer it on the computer", which is what it said first. A task
      // started from here does not run shell commands at all - not even for somebody sitting at
      // the machine - so telling the owner to go and answer it there would have sent them to a
      // card that was never going to appear.
      card.append(node("p", "local-only",
        "A task started from here does not run shell commands, so this was refused. "
        + "Run it on the computer if it needs one."));
    }

    return card;
  });

  fill($("approval-list"), cards, $("approvals-empty"));
}

function decide(approval, decision, className) {
  const button = node("button", className, decision);
  button.type = "button";

  button.addEventListener("click", () => act(button, () =>
    post(`/api/approvals/${approval.id}/resolve`, {
      // Keyed by the decision as well as the request. Retrying Allow is the same
      // instruction and reuses its id; changing your mind to Deny is a different
      // one, and reusing the id for it would come back as a conflict about ids
      // instead of the true answer, which is that this was already decided.
      commandId: commandId(`decide:${approval.id}:${decision}`),
      decision,
      // Sent back exactly as it arrived and never recomputed here: the machine
      // that will carry the action out is the one that says what the action is.
      actionHash: approval.actionHash
    })));

  return button;
}

function renderInbox() {
  const cards = [...state.notices].reverse().map((notice) => {
    // No per-notice read/unread styling on purpose. A notice's `read` flag is
    // whatever it was when the row arrived, and marking them read updates rows
    // the delta will never send again - so the flag on screen would go stale and
    // stay that way. The unread COUNT comes back whole on every poll, and that
    // is the number this page is allowed to believe.
    const card = node("div", "card");
    const head = node("div", "card-head");
    head.append(node("h3", null, notice.title));
    head.append(when(notice.at));
    card.append(head);

    if (notice.detail) {
      card.append(node("p", "meta", notice.detail));
    }

    return card;
  });

  fill($("notice-list"), cards, $("inbox-empty"));
  renderRetention();
}

/**
 * What the gateway has thrown away, said out loud.
 *
 * A trimmed history and a quiet fortnight look identical on a screen, and only
 * one of them is true. `trimmedBefore` is null until something has actually been
 * deleted, so a new gateway says nothing rather than warning about data that
 * never existed.
 */
function renderRetention() {
  const line = $("retention");
  const retention = state.retention;

  if (!retention) {
    line.hidden = true;
    return;
  }

  line.hidden = false;
  line.textContent = retention.trimmedBefore
    ? `History is kept for ${retention.days} days. Everything before `
      + `${new Date(retention.trimmedBefore).toLocaleDateString()} has been deleted.`
    : `History is kept for ${retention.days} days. Nothing has been deleted yet.`;
}

function renderHosts() {
  const cards = state.hosts.map((host) => {
    const card = node("div", "card");

    const head = node("div", "card-head");
    head.append(node("h3", null, host.name));
    head.append(node("span", host.online ? "status is-done" : "status",
      host.revoked ? "Revoked" : host.online ? "Online" : "Offline"));
    card.append(head);

    card.append(node("p", "meta", host.lastSeenAt
      ? `Last seen ${new Date(host.lastSeenAt).toLocaleString()}`
      : "Has never connected"));

    // Names, never paths. The gateway is not told where a workspace lives, so
    // this page has no way to name a folder even if someone asked it to.
    if (host.workspaces.length > 0) {
      const chips = node("div", "workspaces");
      host.workspaces.forEach((workspace) => chips.append(node("span", "chip", workspace.name)));
      card.append(chips);
    }

    if (!host.revoked) {
      const actions = node("div", "actions");
      const revoke = node("button", "danger", "Revoke access");
      revoke.type = "button";

      revoke.addEventListener("click", () => {
        if (confirm(`Revoke ${host.name}? Its connection closes and undelivered commands are `
          + "withdrawn. Work already accepted may still be running on the machine.")) {
          act(revoke, () => post(`/api/hosts/${host.id}/revoke`, {}));
        }
      });

      actions.append(revoke);
      card.append(actions);
    }

    return card;
  });

  fill($("host-list"), cards, null);
}

/**
 * One run's timeline.
 *
 * Built from the events this page happens to be holding, which is the last few
 * hundred and not the whole history - so it says so when it is showing a tail
 * rather than letting a partial list read as a short run.
 */
function showRun(run, task) {
  $("run-title").textContent = task?.title ?? "Task";

  const detail = $("run-detail");
  const mine = state.events.filter((event) => event.runId === run.id);
  const timeline = node("div", "timeline");

  if (mine.length === 0) {
    timeline.append(node("p", "empty", "No steps have been reported for this task."));
  }

  mine.forEach((event) => {
    const entry = node("div", event.kind === "Failed" ? "entry is-failed" : "entry");
    entry.append(when(event.at));

    const body = node("div", "body");
    body.append(node("strong", null, statusText(event.kind) + " "));
    body.append(document.createTextNode(event.detail ?? ""));
    entry.append(body);

    timeline.append(entry);
  });

  const head = node("p", "meta",
    `${statusText(run.status)} · started ${new Date(run.createdAt).toLocaleString()}`);

  detail.replaceChildren(head);

  // What was actually asked for, verbatim and before the steps - the same rule
  // the approval card follows, for the same reason. The title is a heading the
  // owner wrote; the prompt is the instruction the computer was given, and
  // judging what a run did against a heading is judging it against the wrong
  // thing. It was stored and sent from the first day and shown nowhere.
  if (task?.prompt) {
    detail.append(node("p", "meta", "Asked for"));
    detail.append(node("pre", "action", task.prompt));
  }

  detail.append(timeline);

  if (mine.length >= KEEP_EVENTS) {
    detail.append(node("p", "note", "Only the most recent steps are shown."));
  }

  $("run-dialog").showModal();
}

// ── acting ───────────────────────────────────────────────────────────────

/**
 * Runs one action, and never leaves the button in a state the server did not
 * put it in. On a refusal the page says the coded reason and then polls, so
 * what is on screen afterwards is the gateway's account of things rather than
 * this page's guess about what its own request achieved.
 */
async function act(button, action) {
  button.disabled = true;

  try {
    await action();
  } catch (error) {
    toast(error.message, true);
  } finally {
    button.disabled = false;
    await refresh();
  }
}

let toastTimer = 0;

function toast(message, isError) {
  const box = $("toast");
  box.textContent = message;
  box.classList.toggle("is-error", Boolean(isError));
  box.hidden = false;

  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { box.hidden = true; }, 6000);
}

function setLive(live) {
  state.live = live;
  $("stale").hidden = live;
  $("link").className = live ? "link-state is-live" : "link-state is-stale";
  $("link-label").textContent = live ? "Live" : "Stale";
}

function showView(name) {
  ["runs", "approvals", "inbox", "hosts"].forEach((view) => {
    $(`view-${view}`).hidden = view !== name;
  });

  document.querySelectorAll(".tab").forEach((tab) => {
    tab.classList.toggle("is-current", tab.dataset.view === name);
  });
}

// ── new task ─────────────────────────────────────────────────────────────

/**
 * The workspace list is built from what the computers published on their last
 * Sync. There is no free-text field here and there is not going to be one: the
 * gateway stores workspace ids and names and never a path, so the only folders
 * this page can name are the ones a computer offered.
 */
function openTaskDialog() {
  const select = $("task-workspace");
  const options = [];

  state.hosts
    .filter((host) => !host.revoked)
    .forEach((host) => host.workspaces.forEach((workspace) => {
      const option = node("option", null, `${workspace.name} · ${host.name}`);
      option.value = `${host.id}|${workspace.id}`;
      options.push(option);
    }));

  select.replaceChildren(...options);
  $("task-error").textContent = "";

  if (options.length === 0) {
    toast("No computer is offering a workspace yet.", true);
    return;
  }

  $("task-dialog").showModal();
}

async function createTask() {
  const [hostId, workspaceId] = $("task-workspace").value.split("|");
  const button = $("task-submit");
  button.disabled = true;

  try {
    const task = await post("/api/tasks", {
      hostId,
      workspaceId,
      title: $("task-title").value,
      prompt: $("task-prompt").value
    });

    // Two calls, and the gap between them is real: a task that was created and
    // not started is a draft the owner can see, not a lost request.
    await post(`/api/tasks/${task.id}/start`, { commandId: commandId(`start:${task.id}`) });

    $("task-dialog").close();
    $("task-title").value = "";
    $("task-prompt").value = "";
    toast("Queued. Your computer picks it up on its next check-in.");
  } catch (error) {
    $("task-error").textContent = error.message;
  } finally {
    button.disabled = false;
    await refresh();
  }
}

// ── registering a computer ───────────────────────────────────────────────

async function registerHost() {
  const button = $("host-submit");
  button.disabled = true;
  $("host-error").textContent = "";

  try {
    const device = await post("/api/hosts", { name: $("host-name").value });

    // The one moment this value exists anywhere but the machine it is going to.
    // It is not stored by this page and cannot be asked for again.
    $("host-id").value = device.id;
    $("host-token").value = device.token;
    $("host-secret").hidden = false;
    button.hidden = true;
  } catch (error) {
    $("host-error").textContent = error.message;
  } finally {
    button.disabled = false;
    await refresh();
  }
}

// ── signing in and out ───────────────────────────────────────────────────

function showLogin() {
  stopPolling();
  $("panel").hidden = true;
  $("login").hidden = false;
}

async function showPanel() {
  $("login").hidden = true;
  $("panel").hidden = false;
  await poll();
  startPolling();
}

let timer = 0;

function startPolling() {
  stopPolling();
  timer = setInterval(poll, POLL_MS);
}

function stopPolling() {
  clearInterval(timer);
  timer = 0;
}

// ── wiring ───────────────────────────────────────────────────────────────

const THEME_KEY = "enactive.theme";

function applyTheme(theme) {
  if (theme) {
    document.documentElement.dataset.theme = theme;
  } else {
    delete document.documentElement.dataset.theme;
  }
}

document.querySelectorAll(".tab").forEach((tab) =>
  tab.addEventListener("click", () => showView(tab.dataset.view)));

document.querySelectorAll("[data-close]").forEach((button) =>
  button.addEventListener("click", () => button.closest("dialog").close()));

$("login-form").addEventListener("submit", async (submitted) => {
  submitted.preventDefault();
  $("login-error").textContent = "";

  try {
    await post("/api/login", { key: $("owner-key").value });
    $("owner-key").value = "";
    await session();
    await showPanel();
  } catch (error) {
    // A wrong key and a locked-out gateway are different facts, and the second
    // one is the difference between "try again" and "wait".
    $("login-error").textContent = error.code === "locked-out"
      ? error.message
      : "That key was not accepted.";
  }
});

$("logout").addEventListener("click", async () => {
  await post("/api/logout", {});
  location.reload();
});

$("theme").addEventListener("click", () => {
  const next = document.documentElement.dataset.theme === "light" ? "dark" : "light";
  applyTheme(next);
  localStorage.setItem(THEME_KEY, next);
});

$("new-task").addEventListener("click", openTaskDialog);
$("task-submit").addEventListener("click", createTask);
$("add-host").addEventListener("click", () => {
  $("host-secret").hidden = true;
  $("host-submit").hidden = false;
  $("host-error").textContent = "";
  $("host-name").value = "";
  $("host-dialog").showModal();
});
$("host-submit").addEventListener("click", registerHost);
$("mark-read").addEventListener("click", (clicked) =>
  act(clicked.currentTarget, () => post("/api/notices/read", {})));

// A phone puts the page to sleep rather than closing it. Coming back to a
// screen that is minutes stale, with no sign that it is, is the failure this
// avoids: the poll runs at once and the banner says so until it succeeds.
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible" && !$("panel").hidden) {
    poll();
  }
});

applyTheme(localStorage.getItem(THEME_KEY));

if (await session()) {
  await showPanel();
} else {
  showLogin();
}
