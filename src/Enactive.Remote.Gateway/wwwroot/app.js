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

import {
  get, post, session, Refused, Stale, endSession, abandonRequests, generation, isCurrent,
  onUnauthenticated, onDeviceRevoked
} from "./js/api.js";
import { emptyState, resetState, forgetScreen, pollOnce } from "./js/session-guard.js";
import { providerLinks, outcomeOf, createDevelopmentProbe } from "./js/signin.js";
import { singleFlight } from "./js/single-flight.js";
import { openKeystore } from "./js/keystore.js";
import {
  ensureDevice, collectGrants, troubleFor, worthSaying, connectPendingId, keyStanding, createGrantSchedule, epochsChanged,
  DEVICE_HEADER
} from "./js/trust.js";
import { createReader, answerable, NOT_GIVEN } from "./js/reader.js";
import {
  createWriter, createSendCache, sendRefusal, draftKey, draftAlreadyRan, NOT_YET_GIVEN
} from "./js/writer.js";
import { formatConnectionCode, formatInviteLink, newPairingSecret, derivePairKey } from "./js/pairing.js";
import {
  openInviteLink, keepInvite, takeKeptInvite, newInviteId, enrollThisDevice, inviteAnswered, answerEnrollment,
  inviteQrSvg, INVITE_LIFETIME_MS, SWAPPED_KEY
} from "./js/invite.js";

const POLL_MS = 3000;

// How much of the two streams a long-lived tab keeps. The gateway stops the
// cost of a POLL growing with history; without this the cost of the PAGE would
// grow instead, on a phone left open for a day.
const KEEP_EVENTS = 500;
const KEEP_NOTICES = 200;

// `openRun` is the run whose timeline is open, so the poll can redraw it; null when the dialog is closed.
const state = emptyState();

// One id and one sealed envelope per logical action, kept across retries
// (writer.js createSendCache).
//
// The gateway treats a repeated id with the same envelope as the SAME
// instruction and returns the command it already queued. That is what makes a
// tapped button on a flaky phone connection safe: a new id each time would
// queue a second run of the same task, and the owner would find out by reading
// the timeline.
//
// Kept across a sign-out and every other reset, on purpose. Each key names the
// resource it acts on - a task, a run, an approval - which another account
// cannot act on, so nothing of one account's is reused for another's. Clearing
// it would give the same person's retry after signing in again a new id, and a
// command the gateway had already queued before the reset would be queued twice.
// Drafts of tasks not yet started are the exception (resetSession).
const sends = createSendCache();

const $ = (id) => document.getElementById(id);

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

// One poll at a time (see single-flight.js): two at once carry the same cursor and get the same delta,
// and apply() appends it twice. Keyed by the session generation, so the first poll of a new session
// never waits on one of the session before it.
const poll = singleFlight(pollNow, generation);

async function pollNow() {
  // Nobody signed in: there is nothing of anybody's to ask for, and the answer would be a 401.
  if (!account) {
    return;
  }

  const started = generation();
  let drawn = false;

  try {
    drawn = await pollOnce({
      read: () => get(state.cursor === null ? "/api/state" : `/api/state?since=${state.cursor}`),
      accountId: account.id,
      apply,
      // Another account's state: none of it is drawn. The page forgets this account and asks the
      // gateway afresh who is signed in.
      otherAccount: () => {
        resetSession();
        bootAfterMismatch();
      }
    });

    if (!drawn) {
      return;
    }

    setLive(true);
  } catch (error) {
    // The session ended under it - signed out, a 401 or this browser removed - and the screen that
    // put up is the right one. Drawing the old state now would draw it over that.
    if (!isCurrent(started)) {
      return;
    }

    // The screen keeps the last state it was given and says that it is old. An
    // empty page would read as "nothing is running", which is a different and
    // much worse claim than "I cannot see".
    setLive(false);
  }

  await render();

  if (drawn) {
    // Not awaited: a rotation grant is a round trip of its own, and the next poll does not wait on it.
    catchUpKeys();
  }
}

/**
 * Polls now rather than waiting out the interval - used after every action. A poll already running
 * started before the action and may not show it, so this waits for it to finish and then asks again.
 */
async function refresh() {
  await poll.pending;
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

// ── opening ──────────────────────────────────────────────────────────────

/** Opens what the computers sealed with this device's keys (reader.js); a reader over no keys until a store opens. */
let reader = createReader(null);

/** Seals what the person sends with the same keys (writer.js); over no keys, it seals nothing. */
let writer = createWriter(null);

/**
 * What this device opened of the snapshot on screen, looked up by `"<kind>:<id>"`: a task, a run's summary,
 * an event, a notice, an approval's action, a workspace name, or a computer's key standing. Kept for the
 * drawing that follows and for a timeline opened by a click between two polls.
 */
let content = () => undefined;

/**
 * Opens everything on screen, all at once. The reader keeps what it opened, so a poll that brings nothing
 * new costs a lookup per record and not a decryption.
 */
async function openContent() {
  const read = reader;
  const store = keystore;
  const opened = new Map();

  const put = (name, promise) => Promise.resolve(promise).then((value) => { opened.set(name, value); }, () => {
    // The key store could not be read - closed by a sign-out in the meantime, or the browser's storage
    // failing. Either way this device cannot reach a key for it, and the record says so rather than
    // taking the whole screen down with it.
    opened.set(name, { unreadable: NOT_GIVEN });
  });

  await Promise.all([
    ...state.tasks.map((task) => put(`task:${task.id}`, read.openTask(task))),
    ...state.runs.map((run) => put(`summary:${run.id}`, read.openSummary(run))),
    ...state.events.map((event) => put(`event:${event.id}`, read.openEvent(event))),
    ...state.notices.map((notice) => put(`notice:${notice.id}`, read.openNotice(notice))),
    ...state.approvals.map((approval) => put(`approval:${approval.id}`, read.openAction(approval))),
    ...state.hosts.flatMap((host) => host.workspaces.map((workspace) =>
      put(`workspace:${host.id}:${workspace.id}`, read.openWorkspaceName(host.id, workspace)))),
    ...state.hosts.map((host) => put(`host:${host.id}`, host.revoked ? null : keyStanding(store, host)))
  ]);

  return (name) => opened.get(name);
}

/** The muted line drawn where content would be, saying why it is not: never an empty space. */
function unreadable(result, tag = "p") {
  const reason = result?.unreadable ?? NOT_GIVEN;
  return node(tag, "muted", reason[0].toUpperCase() + reason.slice(1) + ".");
}

/** A task's title as this device opened it, or a stand-in for one it cannot read. */
function taskTitle(task) {
  const title = task ? content(`task:${task.id}`)?.json?.title : null;
  return typeof title === "string" && title.length > 0 ? title : "Task";
}

/**
 * A workspace's name as its computer sealed it, or its id and why the name cannot be read. Not "unnamed":
 * the computer did name it, and this device not being able to read the name is a different thing to say.
 */
function workspaceName(hostId, workspace) {
  const opened = content(`workspace:${hostId}:${workspace.id}`);

  if (typeof opened?.text === "string" && opened.text.length > 0) {
    return opened.text;
  }

  return opened?.unreadable
    ? `workspace ${workspace.id} (cannot read its name: ${opened.unreadable})`
    : `workspace ${workspace.id}`;
}

// ── rendering ────────────────────────────────────────────────────────────

// Which render is the newest. Opening is asynchronous, so two renders can overlap, and the older one
// finishing last would draw the snapshot before the one on screen over it.
let drawing = 0;

/**
 * The cursor of the snapshot the inbox was last drawn from; null before the first. A poll moves
 * `state.cursor` on before the drawing that follows it has opened the new notices, and a click in between
 * marked read notices that were not yet on screen.
 */
let drawnCursor = null;

async function render() {
  const turn = ++drawing;
  const started = generation();
  const opened = await openContent();

  // A newer render is under way, or the session this one opened for has ended: drawing would put back
  // what the newer one, or the reset, has replaced.
  if (turn !== drawing || !isCurrent(started)) {
    return;
  }

  content = opened;
  renderRuns();
  renderApprovals();
  renderInbox();
  renderHosts();
  renderCounts();
  refreshOpenRun();
  // Taken as the lists are drawn, from the state they were drawn from (see mark-read).
  drawnCursor = state.cursor;
  $("mark-read").disabled = drawnCursor === null;
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
    head.append(node("h3", null, taskTitle(task)));
    head.append(node("span", statusClass(run.status), statusText(run.status)));
    card.append(head);

    const opened = task ? content(`task:${task.id}`) : null;

    if (opened?.unreadable) {
      card.append(unreadable(opened));
    }

    card.append(node("p", "meta", run.endedAt ? `Ended ${new Date(run.endedAt).toLocaleString()}`
      : `Started ${new Date(run.createdAt).toLocaleString()}`));

    // Null while the run is going, and for a run the computer never saw: there is no summary to show.
    const summary = content(`summary:${run.id}`);

    if (summary?.unreadable) {
      card.append(unreadable(summary));
    } else if (summary?.text) {
      card.append(node("p", null, summary.text));
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
      stop.addEventListener("click", () => act(stop, async () => {
        const cancel = await sealFor(`cancel:${run.id}`, run.hostId,
          (write, commandId) => write.sealCancel(run.hostId, commandId, run.id));
        await post(`/api/runs/${run.id}/cancel`, { commandId: cancel.id, sealed: cancel.sealed });
      }));
      actions.append(stop);
    }

    card.append(actions);

    // The question, where the answer is. "Needs you" on this card and the question one tab away
    // meant you knew you were wanted and had to go looking for what for - and what arrived over
    // there was the action without the request that produced it. The tab is still the queue; this
    // is where it is read in context.
    asksOf(run.id).forEach((approval) => {
      const ask = node("div", "ask");
      ask.append(...approvalNodes(approval));
      card.append(ask);
    });

    return card;
  });

  fill($("run-list"), cards, $("runs-empty"));
}

/**
 * The question itself, as a list of nodes for a caller to wrap.
 *
 * Nodes rather than a finished card because this is drawn in three places now: the Permissions
 * tab, the task's own card, and its timeline. Written three times it would be three renderings of
 * one thing, and two of them would fall behind the day the shape changed.
 *
 * The tab is a QUEUE - what is waiting for you, across every computer and every run, with a count
 * in the header. The task card is where the question is actually answered, because that is where
 * its context is: what you asked for, what has happened so far, and what is being asked now, on
 * one screen. A card in a separate list is the same question with its reason stripped off - the
 * unabridged-action rule kept to the letter and broken in spirit.
 */
function approvalNodes(approval) {
  const parts = [];

  // An answer is not an outcome. The gateway marks the approval DecisionQueued the moment it
  // takes the answer, because the command still has to reach the computer and the computer may
  // refuse it - the desktop may have answered first, or the run may be over. The panel was told
  // all of that on every poll and drew "Waiting" regardless.
  const queued = approval.status === "DecisionQueued";
  const opened = content(`approval:${approval.id}`);
  const action = opened?.json;
  const canAnswer = answerable(approval, opened);

  const head = node("div", "card-head");
  head.append(node("h3", null, "Permission requested"));
  head.append(queued
    ? node("span", "status is-queued", "Sent")
    : node("span", "status is-waiting", canAnswer ? "Waiting" : "At the computer"));
  parts.push(head);

  if (!opened || opened.unreadable) {
    parts.push(unreadable(opened));
  } else {
    if (action?.topic) {
      parts.push(node("p", null, action.topic));
    }

    parts.push(node("p", "meta", `${action?.tool ?? "A tool"} · in ${action?.workingDirectory || "no folder"}`));

    // The whole action, never a summary. A person cannot approve what they were
    // not shown, and a truncated command is one nobody read.
    parts.push(node("pre", "action", action?.fullText ?? ""));
  }

  if (queued) {
    // No buttons at all rather than disabled ones. The answer has been given; what is left is
    // the computer picking it up, and that is not something a second click makes happen sooner.
    parts.push(node("p", "local-only",
      "Your answer is on its way to the computer. If it was already answered there, that answer "
      + "stands - sitting at the machine always wins."));
  } else if (canAnswer) {
    const actions = node("div", "actions");
    actions.append(decide(approval, "Allow", "primary"));
    actions.append(decide(approval, "Deny", "secondary"));
    parts.push(actions);
  } else if (approval.remoteDecidable) {
    // Not answerable from here although the computer would take an answer: what this device would show
    // is not what the computer checks an answer against (the action hash), or it cannot be read at all.
    // An Allow given here would be for a command the person was not shown.
    parts.push(node("p", "local-only", opened?.unreadable
      ? "This device cannot read this request; answer it on the computer."
      : "This request does not match what the computer asked; answer it on the computer."));
  } else {
    // Not a disabled button: the server refuses this whatever the page draws,
    // and the card says why rather than looking broken.
    //
    // It says REFUSED and not "answer it on the computer", which is what it said first. A task
    // started from here does not run shell commands at all - not even for somebody sitting at
    // the machine - so telling the owner to go and answer it there would have sent them to a
    // card that was never going to appear.
    parts.push(node("p", "local-only",
      "A task started from here does not run shell commands, so this was refused. "
      + "Run it on the computer if it needs one."));
  }

  return parts;
}

/**
 * Everything this run is still asking. A LIST, not one: parallel steps can leave two questions
 * open at once - the desktop shows them one at a time behind its own gate, but the gateway holds
 * both, and a card that drew only the first would hide a question that was blocking the run.
 */
function asksOf(runId) {
  return state.approvals.filter((approval) => approval.runId === runId);
}

function renderApprovals() {
  const cards = state.approvals.map((approval) => {
    const card = node("div", "card");
    card.append(...approvalNodes(approval));
    return card;
  });

  fill($("approval-list"), cards, $("approvals-empty"));
}

function decide(approval, decision, className) {
  const button = node("button", className, decision);
  button.type = "button";

  button.addEventListener("click", () => act(button, async () => {
    // Keyed by the decision as well as the request. Retrying Allow is the same
    // instruction and reuses its id; changing your mind to Deny is a different
    // one, and reusing the id for it would come back as a conflict about ids
    // instead of the true answer, which is that this was already decided.
    //
    // The action hash is sealed and sent back exactly as it arrived and never
    // recomputed here: the machine that will carry the action out is the one
    // that says what the action is.
    const answer = await sealFor(`decide:${approval.id}:${decision}`, approval.hostId,
      (write, commandId) => write.sealDecision(approval.hostId, commandId, approval.id, approval.actionHash, decision));

    await post(`/api/approvals/${approval.id}/resolve`, {
      commandId: answer.id,
      hostId: approval.hostId,
      decision,
      actionHash: approval.actionHash,
      sealed: answer.sealed
    });
  }));

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
    head.append(node("h3", null, noticeTitle(notice.kind)));
    head.append(when(notice.at));
    card.append(head);

    // Which task it is about: the gateway knows only the run, and the title is sealed.
    const run = state.runs.find((one) => one.id === notice.runId);

    if (run) {
      const task = state.tasks.find((one) => one.id === run.taskId);
      const opened = task ? content(`task:${task.id}`) : null;
      card.append(node("p", "meta", opened?.unreadable
        ? `task cannot be read: ${opened.unreadable}`
        : taskTitle(task)));
    }

    const detail = content(`notice:${notice.id}`);

    if (detail?.unreadable) {
      card.append(unreadable(detail));
    } else if (detail?.text) {
      card.append(node("p", null, detail.text));
    } else if (notice.kind === "NotStarted") {
      // The gateway's own notice: the computer never saw the run, so there is no sealed sentence to show.
      card.append(node("p", null, "Your computer did not pick this task up in time, so it never started."));
    }

    return card;
  });

  fill($("notice-list"), cards, $("inbox-empty"));
  renderRetention();
}

/**
 * A notice's heading, made from its kind - the one part of a notice the gateway writes, since it can seal
 * nothing. The terminal kinds are the run statuses' own names; anything else falls through as its name.
 */
function noticeTitle(kind) {
  return {
    PermissionRequested: "Permission requested",
    PermissionAtComputer: "Permission asked at the computer",
    NotStarted: "Not started"
  }[kind] ?? statusText(kind);
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
    head.append(node("h3", null, host.label));
    head.append(node("span", host.online ? "status is-done" : "status",
      host.revoked ? "Revoked" : host.online ? "Online" : "Offline"));
    card.append(head);

    card.append(node("p", "meta", host.lastSeenAt
      ? `Last seen ${new Date(host.lastSeenAt).toLocaleString()}`
      : "Has never connected"));

    // Whether this device holds the computer's current key. Without it everything the computer sends is
    // unreadable here, and this is where the person learns why.
    const standing = content(`host:${host.id}`);

    if (standing) {
      card.append(node("p", "muted", standing[0].toUpperCase() + standing.slice(1) + "."));
    }

    // Names, never paths. The gateway is not told where a workspace lives, so
    // this page has no way to name a folder even if someone asked it to.
    if (host.workspaces.length > 0) {
      const chips = node("div", "workspaces");
      host.workspaces.forEach((workspace) => chips.append(node("span", "chip", workspaceName(host.id, workspace))));
      card.append(chips);
    }

    if (!host.revoked) {
      const actions = node("div", "actions");
      const revoke = node("button", "danger", "Revoke access");
      revoke.type = "button";

      revoke.addEventListener("click", () => {
        if (confirm(`Revoke ${host.label}? Its connection closes and undelivered commands are `
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
  // Remembered so the poll can redraw it. This dialog was built once on click and never touched
  // again, so a timeline opened on a running task froze at the moment it opened: no new steps, and
  // - once the question moved in here - buttons answering something that might already be settled.
  state.openRun = run.id;

  $("run-title").textContent = taskTitle(task);

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
    const detail = content(`event:${event.id}`);

    if (detail?.unreadable) {
      body.append(unreadable(detail, "span"));
    } else if (detail?.text) {
      body.append(document.createTextNode(detail.text));
    }

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
  const asked = task ? content(`task:${task.id}`) : null;

  if (asked?.unreadable) {
    detail.append(node("p", "meta", "Asked for"));
    detail.append(unreadable(asked));
  } else if (typeof asked?.json?.prompt === "string" && asked.json.prompt.length > 0) {
    detail.append(node("p", "meta", "Asked for"));
    detail.append(node("pre", "action", asked.json.prompt));
  }

  // Before the steps, not after them. The timeline showed "Permission requested" as history and
  // offered no way to answer it, which is the same split as the separate tab - only inside one
  // screen, where it is harder to excuse.
  asksOf(run.id).forEach((approval) => {
    const ask = node("div", "ask");
    ask.append(...approvalNodes(approval));
    detail.append(ask);
  });

  detail.append(timeline);

  if (mine.length >= KEEP_EVENTS) {
    detail.append(node("p", "note", "Only the most recent steps are shown."));
  }

  const dialog = $("run-dialog");

  // Already open means this is a redraw from the poll, and showModal() on an open dialog throws.
  if (!dialog.open) {
    dialog.addEventListener("close", () => { state.openRun = null; }, { once: true });
    dialog.showModal();
  }
}

/** Redraws the open timeline, if one is open and its run is still in the snapshot. */
function refreshOpenRun() {
  if (!state.openRun) {
    return;
  }

  const run = state.runs.find((one) => one.id === state.openRun);

  if (!run) {
    return;
  }

  showRun(run, state.tasks.find((task) => task.id === run.taskId));
}

// ── acting ───────────────────────────────────────────────────────────────

/**
 * Runs one action, and never leaves the button in a state the server did not
 * put it in. On a refusal the page says the coded reason and then polls, so
 * what is on screen afterwards is the gateway's account of things rather than
 * this page's guess about what its own request achieved.
 */
async function act(button, action) {
  // The whole group, not just the button that was clicked. Allow and Deny are one question, and
  // leaving Deny live while Allow is in flight offers an answer that is already being given.
  const group = Array.from(button.parentElement?.querySelectorAll("button") ?? [button]);
  group.forEach((one) => { one.disabled = true; });
  const started = generation();

  try {
    await action();
  } catch (error) {
    // A refusal from a session that has ended is not news to whoever is looking now.
    if (isCurrent(started)) {
      toast(error.message, true);
    }
  } finally {
    // Refreshed BEFORE the buttons come back. They used to be re-enabled first, so a card whose
    // answer the gateway had already accepted spent a whole round trip looking exactly as it did
    // before the click - same "Waiting", same two live buttons. That is what makes a person press
    // it again, and pressing Deny after Allow is a different command, not a retry.
    await refresh();
    group.forEach((one) => { one.disabled = false; });
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
      const option = node("option", null, `${workspaceName(host.id, workspace)} · ${host.label}`);
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
  const started = generation();

  try {
    const title = $("task-title").value;
    const prompt = $("task-prompt").value;

    // The task's id and envelope are kept per draft - this computer, workspace, title and prompt - until it
    // is started, so pressing Create again after a failure sends the same task: the gateway takes the same
    // id with another envelope for a different task, and a new id for a second one.
    const draft = await draftKey(hostId, workspaceId, title, prompt);

    // A start whose answer was lost may have gone through: then the run is on the screen, and these words
    // are not sent again (draftAlreadyRan). After a key change they would be sealed under a new task id,
    // which the gateway takes for another task and runs a second time.
    if (draftAlreadyRan(state.runs, (await sends.kept(draft))?.id)) {
      sends.forget(draft);
      finishTaskDialog("This task was already started.");
      return;
    }

    const task = await sealFor(draft, hostId,
      (write, taskId) => write.sealTask(hostId, taskId, workspaceId, { title, prompt }));
    await post("/api/tasks", { taskId: task.id, hostId, workspaceId, sealedTask: task.sealed });

    // Two calls, and the gap between them is real: a task that was created and
    // not started is kept by the gateway but not shown - the panel draws runs -
    // and pressing Create again starts the same task rather than a second one.
    const start = await sealFor(`start:${task.id}`, hostId,
      (write, commandId) => write.sealStart(hostId, commandId, task.id, workspaceId));
    await post(`/api/tasks/${task.id}/start`, { commandId: start.id, sealed: start.sealed });

    // Started: the same words written again are a new task, not this one retried.
    sends.forget(draft);
    finishTaskDialog("Queued. Your computer picks it up on its next check-in.");
  } catch (error) {
    if (isCurrent(started)) {
      $("task-error").textContent = error.message;
    }
  } finally {
    button.disabled = false;
    await refresh();
  }
}

/**
 * The `{id, sealed}` to send for one action to computer `hostId`: kept from an earlier attempt of the same
 * action under the same key, or sealed now by `seal(writer, id)` under the newest key this device holds.
 *
 * Refused, with the sentence to show, when this device holds no key for the computer or the snapshot says it
 * moved to a key whose grant has not reached this device (writer.js sendRefusal): sealed under the key before,
 * a cancel or an answer would be refused by the computer and the panel would never hear of it. The grants are
 * asked for at once, so the person's next click goes through.
 */
async function sealFor(key, hostId, seal) {
  const store = keystore;
  const write = writer;
  const newest = store ? await store.newestEpoch(hostId) : null;
  const refusal = sendRefusal(state.hosts.find((host) => host.id === hostId), newest);

  if (refusal === NOT_YET_GIVEN) {
    catchUpKeys().catch(() => {});
  }

  if (refusal) {
    throw new Error(refusal);
  }

  return sends.once(key, newest, (id) => seal(write, id));
}

/** Closes the new-task dialog on a task that is running, empties it and says so. */
function finishTaskDialog(message) {
  $("task-dialog").close();
  $("task-title").value = "";
  $("task-prompt").value = "";
  toast(message);
}

// ── this device's keys ───────────────────────────────────────────────────

// The calls trust.js makes, through the same guard as every other request of the page.
const api = { get, post };

/**
 * The signed-in account's key store in this browser, and this browser's device id with the gateway. Null
 * while nobody is signed in, and closed by resetSession: the next account in this tab opens its own store,
 * and nothing the page does after a reset can still reach the last one's keys through a store left open.
 */
let keystore = null;
let deviceId = null;

/**
 * Opens the account's key store and registers this browser's key with the gateway, before the first poll.
 * Neither is needed to see what the gateway itself knows, so a browser that cannot keep keys - or an
 * account with no room for another device - still gets the panel, and is told why it cannot be paired.
 */
async function openTrust(user) {
  const started = generation();
  let store;

  try {
    store = await openKeystore(user.id);
  } catch (error) {
    if (isCurrent(started)) {
      toast(`This browser cannot keep keys for this account: ${error.message}`, true);
    }

    return;
  }

  // Signed out, or in as somebody else, while it opened: the store is not this session's.
  if (!isCurrent(started)) {
    store.close();
    return;
  }

  keystore = store;
  reader = createReader(store);
  writer = createWriter(store);

  try {
    const id = await ensureDevice(store, api);

    if (isCurrent(started)) {
      deviceId = id;
    }
  } catch (error) {
    if (isCurrent(started) && !(error instanceof Stale)) {
      toast(error.message, true);
    }
  }
}

/** Takes the grants waiting for this device. Null when there is no device to take them for, or the session ended. */
async function takeGrants(started) {
  const store = keystore;

  if (!store || !deviceId) {
    return null;
  }

  const result = await collectGrants(store, api, deviceId);

  if (!isCurrent(started)) {
    return null;
  }

  // The reader keeps what it could not open until it is told keys arrived (reader.js).
  if (result.added.length > 0) {
    reader.keysChanged();
  }

  return result;
}

/** When to ask for grants while a computer is not current on this device (trust.js); one per session. */
let grantSchedule = createGrantSchedule();

/**
 * Takes the grants waiting for this device when a snapshot shows a computer whose current key it does not
 * hold. That is how a rotation reaches an open tab: the computer moves to a new key when a device is
 * removed, and grants were otherwise taken only at a page load and in the register dialog, so a tab left
 * open went on showing everything the computer sent after the rotation as unreadable until it was
 * reloaded. Asked at most once per poll, and only while some computer is not current - on every poll for one
 * that rotated, ever less often for one never paired here (createGrantSchedule); one at a time, so a slow
 * answer is shared by the poll after it rather than asked again.
 */
const catchUpKeys = singleFlight(catchUpKeysNow, generation);

async function catchUpKeysNow() {
  const started = generation();
  const store = keystore;

  if (!store || !deviceId) {
    return;
  }

  try {
    // Keys another tab of this browser stored since the last poll (epochsChanged).
    let changed = await noticeKeys(store, started);

    const hosts = state.hosts.filter((host) => !host.revoked);
    const standings = await Promise.all(hosts.map(async (host) => [host.id, await keyStanding(store, host)]));

    if (grantSchedule.due(standings)) {
      const result = await takeGrants(started);

      if (!result) {
        return;
      }

      sayTrouble(result);
      // Read again after the delivery rather than trusting `added`: a grant another tab stored first is
      // passed over here as skipped, and the key store is what says this tab now holds a newer epoch.
      changed = (await noticeKeys(store, started)) || changed;
    }

    if (changed) {
      await render();
    }
  } catch {
    // Not reaching the gateway is what the poll already shows, and the next poll asks again.
  }
}

/** Each listed computer's newest epoch in the key store, as this tab last read it. */
let seenEpochs = new Map();

/**
 * Reads the newest epoch of every listed computer and, if any differs from the last read, tells the reader
 * (keysChanged), so what it remembered as unreadable or not held is tried again. Returns whether it did.
 */
async function noticeKeys(store, started) {
  const current = new Map(await Promise.all(state.hosts.map(async (host) =>
    [host.id, await store.newestEpoch(host.id)])));

  // The session ended while the store was read: what was read is not the next session's.
  if (!isCurrent(started)) {
    return false;
  }

  const changed = epochsChanged(seenEpochs, current);
  seenEpochs = current;

  if (changed) {
    reader.keysChanged();
  }

  return changed;
}

/** The last trouble with grants this page told the person of. */
let lastTrouble = "";

/**
 * Tells the person of trouble with a delivery (worthSaying), once. Grants are now taken on every poll while
 * a computer is behind, and the gateway lists a grant that does not verify on every call: said each time,
 * the same warning would have come back every three seconds for good.
 */
function sayTrouble(result) {
  const trouble = worthSaying(result, hostLabel);

  if (trouble && trouble !== lastTrouble) {
    toast(trouble, true);
  }

  lastTrouble = trouble;
}

function hostLabel(hostId) {
  return state.hosts.find((host) => host.id === hostId)?.label ?? "A computer";
}

// ── registering a computer ───────────────────────────────────────────────

// How long a connection code can be answered. The computer may be in another room or another building, so
// minutes are too few; a secret that waited for ever would stay on this device's disk for ever.
const PAIRING_LIFETIME_MS = 24 * 60 * 60 * 1000;
const PAIRING_POLL_MS = 3000;

/** The computer the open dialog is waiting for, and the timer asking for its grant; null when none. */
let pairing = null;

async function registerHost() {
  const button = $("host-submit");
  button.disabled = true;
  $("host-error").textContent = "";
  const started = generation();

  try {
    const store = keystore;

    if (!store) {
      throw new Error("This browser cannot keep keys, so it cannot be paired with a computer.");
    }

    // Asked again here, not only at sign-in: a registration refused then (a full account) may pass now.
    const id = deviceId ?? await ensureDevice(store, api);
    const host = await post("/api/hosts", { name: $("host-name").value });

    // The pairing secret stays on this device; the gateway never sees it. The code carries it to the
    // computer by hand, and the computer's first grant is checked with it.
    const secret = newPairingSecret();
    await store.setPending(connectPendingId(host.id), secret, Date.now() + PAIRING_LIFETIME_MS);
    const device = await store.device();

    if (!isCurrent(started)) {
      return;
    }

    // Closed while the computer was being registered: nobody is looking, so no code is shown and nobody is
    // waited for. Started anyway, the poll asked for grants every 3 s behind a closed dialog until the next
    // "Register". The computer stays registered, with no code that could ever reach it; the person sees it in
    // the list and can revoke it.
    if (!$("host-dialog").open) {
      await store.dropPending(connectPendingId(host.id));
      return;
    }

    deviceId = id;

    // The token travels inside the code: the one moment it exists anywhere but the machine it is going
    // to. It is not stored by this page and cannot be asked for again.
    $("host-code").value = formatConnectionCode({
      gateway: location.origin, hostId: host.id, token: host.token, deviceId: id,
      devicePublicRaw: device.publicRaw, secret
    });
    $("host-secret").hidden = false;
    $("host-status").textContent = "Waiting for the computer…";
    button.hidden = true;
    // `name` is what the registration answers with: the label the person typed, which snapshots call `label`.
    startPairing(host.id, host.name);
  } catch (error) {
    if (isCurrent(started) && !(error instanceof Stale)) {
      $("host-error").textContent = error.message;
    }
  } finally {
    button.disabled = false;
    await refresh();
  }
}

/** Asks for the computer's grant every few seconds while the dialog is open. */
function startPairing(hostId, label) {
  stopPairing();
  // Waited for by the dialog now; once it closes, the polls ask for this computer's grant again from the start.
  grantSchedule.reset(hostId);
  const started = generation();
  // One check at a time: a slow answer overlapping the next tick would take the same grants twice.
  const check = singleFlight(() => checkPairing(hostId, label, started));
  pairing = { hostId, timer: setInterval(check, PAIRING_POLL_MS) };
}

function stopPairing() {
  if (pairing) {
    clearInterval(pairing.timer);
  }

  pairing = null;
}

async function checkPairing(hostId, label, started) {
  const waiting = () => isCurrent(started) && pairing?.hostId === hostId;

  if (!waiting()) {
    return;
  }

  try {
    const result = await takeGrants(started);

    if (!result || !waiting()) {
      return;
    }

    $("host-error").textContent = troubleFor(result, hostId, label);

    // Held rather than "added now": another tab of this browser may have taken the grant first.
    if (await keystore.newestEpoch(hostId) !== null && waiting()) {
      stopPairing();
      $("host-dialog").close();
      toast(`Paired - this device can read and command ${label}`);
    }
  } catch (error) {
    // The gateway out of reach is said, and the next tick asks again. An ended session says nothing.
    if (waiting() && !(error instanceof Stale)) {
      $("host-status").textContent = `Waiting for the computer… (${error.message})`;
    }
  }
}

$("host-copy").addEventListener("click", async () => {
  const code = $("host-code");

  try {
    await navigator.clipboard.writeText(code.value);
    $("host-status").textContent = "Copied. Waiting for the computer…";
  } catch {
    // No clipboard here (a page not served over https, or permission refused): the code is selected,
    // for the person's own copy.
    code.focus();
    code.select();
  }
});

// ── adding a device ──────────────────────────────────────────────────────

const INVITE_POLL_MS = 3000;

/** The invitation the open "Add a device" dialog is waiting on, and its two timers; null when none. */
let inviting = null;

/**
 * "Add a device": makes an invitation and shows its link, as text and as a QR code, for its ten minutes, then
 * answers the device that enrolls (invite.js answerEnrollment). The pairing secret is made here and goes
 * nowhere but the link; the gateway is told the invitation's id only.
 */
async function openInviteDialog() {
  stopInviting();
  forgetInviteLink();
  $("invite-status").textContent = "";
  $("invite-error").textContent = "";
  $("account").open = false;
  $("invite-dialog").showModal();
  const started = generation();

  try {
    const store = keystore;

    if (!store) {
      throw new Error("This browser cannot keep keys, so it has none to share.");
    }

    // A device added from here would be let in and could read nothing.
    if ((await store.hosts()).length === 0) {
      throw new Error("This device holds no computer's key yet, so it has nothing to share. Pair it with a computer first.");
    }

    const id = deviceId ?? await ensureDevice(store, api);
    const inviteId = newInviteId();
    const secret = newPairingSecret();
    const pairKey = await derivePairKey(secret);
    await post("/api/invites", { id: inviteId }, { headers: { [DEVICE_HEADER]: id } });

    // Closed while the invitation was made, or signed out: nobody is looking and nobody is waited for. The
    // invitation lapses on its own, and its secret was never shown to anyone.
    if (!isCurrent(started) || !$("invite-dialog").open) {
      return;
    }

    deviceId = id;
    const link = formatInviteLink(location.origin, inviteId, secret);
    $("invite-link").value = link;
    drawQr(link);
    $("invite-secret").hidden = false;
    startInviting({ inviteId, deviceId: id, pairKey, deadline: Date.now() + INVITE_LIFETIME_MS, started });
  } catch (error) {
    if (isCurrent(started) && !(error instanceof Stale)) {
      $("invite-error").textContent = error.message;
    }
  }
}

/**
 * Draws the link as a QR code. The generator writes SVG text, which is parsed as an SVG document and imported,
 * never put into the page as markup; and what it encodes is only the link this page made from its own origin,
 * a random id and a random secret.
 */
function drawQr(link) {
  const svg = new DOMParser().parseFromString(inviteQrSvg(link), "image/svg+xml").documentElement;

  // A parse that failed gives a document describing the error, which is not drawn: the link is there as text.
  $("invite-qr").replaceChildren(...(svg.localName === "svg" ? [document.importNode(svg, true)] : []));
}

/** Takes the link and its QR code off the screen: they carry the secret, which nothing needs once it is used. */
function forgetInviteLink() {
  $("invite-secret").hidden = true;
  $("invite-link").value = "";
  $("invite-qr").replaceChildren();
}

function startInviting(invite) {
  stopInviting();
  // One check at a time: a slow answer overlapping the next tick would answer the same enrollment twice.
  const check = singleFlight(() => checkInvite(invite));
  inviting = {
    inviteId: invite.inviteId,
    answered: false,
    countdown: setInterval(() => countDown(invite), 1000),
    poll: setInterval(check, INVITE_POLL_MS)
  };
  countDown(invite);
}

function stopInviting() {
  if (inviting) {
    clearInterval(inviting.countdown);
    clearInterval(inviting.poll);
  }

  inviting = null;
}

/** The time the link has left, said every second. At its end the link comes down: nothing can answer it now. */
function countDown(invite) {
  if (inviting?.inviteId !== invite.inviteId || inviting.answered) {
    return;
  }

  const left = Math.ceil((invite.deadline - Date.now()) / 1000);

  if (left <= 0) {
    stopInviting();
    forgetInviteLink();
    $("invite-status").textContent = "";
    $("invite-error").textContent = "This invitation has expired. Choose Add a device again for a new link.";
    return;
  }

  $("invite-status").textContent =
    `Waiting for the new device… ${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")} left.`;
}

async function checkInvite(invite) {
  const waiting = () => isCurrent(invite.started) && inviting?.inviteId === invite.inviteId;
  const store = keystore;
  const write = writer;

  if (!waiting() || !store) {
    return;
  }

  try {
    const enrollment = await get(`/api/invites/${invite.inviteId}/enrollment`,
      { headers: { [DEVICE_HEADER]: invite.deviceId } });

    if (!enrollment || !waiting()) {
      return;
    }

    // Answered: an invitation with an answer is used, so its clock no longer matters.
    inviting.answered = true;
    $("invite-status").textContent = `Sharing keys with ${enrollment.label}…`;

    // This device's keys brought up to date first: a computer refuses an endorsement sealed under a key it has
    // moved on from, and the new device would be given only the keys this one had.
    await catchUpKeys();

    const result = await answerEnrollment({
      keystore: store, api, writer: write, deviceId: invite.deviceId, hosts: state.hosts,
      pairKey: invite.pairKey, inviteId: invite.inviteId, enrollment
    });

    if (!waiting()) {
      return;
    }

    stopInviting();
    forgetInviteLink();

    if (result.refused) {
      $("invite-status").textContent = "";
      $("invite-error").textContent = SWAPPED_KEY;
      return;
    }

    if (result.granted === 0) {
      $("invite-status").textContent = "";
      $("invite-error").textContent =
        `${enrollment.label} was added, but this device holds the key of no computer still on this account to share.`;
      return;
    }

    $("invite-status").textContent = `Added ${enrollment.label}.`;
    $("invite-error").textContent = result.notEndorsed
      .map(({ hostId, reason }) => `${hostLabel(hostId)} was not asked to trust it: ${reason}`)
      .join(" ");
    toast(`Added ${enrollment.label}`);
  } catch (error) {
    // Said, and the next tick asks again. An answer cut off half way is finished then: the enrollment is still
    // there to read, and a grant the new device holds already is passed over (answerEnrollment).
    if (waiting() && !(error instanceof Stale)) {
      $("invite-error").textContent = error.message;
    }
  }
}

$("add-device").addEventListener("click", openInviteDialog);

$("invite-copy").addEventListener("click", async () => {
  const link = $("invite-link");

  try {
    await navigator.clipboard.writeText(link.value);
    toast("Copied. Open it on the new device.");
  } catch {
    // No clipboard here: the link is selected, for the person's own copy.
    link.focus();
    link.select();
  }
});

// Closed - by its button, Escape, a reset or the end of the answer - nobody is waiting any more, and the link
// goes with it: it carries the secret, which would otherwise stay in the page until the next invitation.
$("invite-dialog").addEventListener("close", () => {
  stopInviting();
  forgetInviteLink();
});

// ── joining by an invitation ─────────────────────────────────────────────

/**
 * The invitation this tab was opened with and has not answered yet, `{inviteId, secret}`: read from the link
 * at load, or from session storage after a sign-in that left the page, and answered once the panel is
 * entered. Null when there is none.
 */
let invitation = null;

/** The new device's wait for its grants; null when it is not waiting. */
let joining = null;

/**
 * Where an invitation is kept across a sign-in (invite.js keepInvite): session storage, or null where the
 * browser refuses it - reading the property itself throws then.
 */
const tabStorage = (() => {
  try {
    return sessionStorage;
  } catch {
    return null;
  }
})();

/**
 * Answers the invitation this tab was opened with: this device's key registered, the enrollment posted, and
 * then a wait for the grants the inviting device makes for it.
 */
async function joinByInvitation(started) {
  const { inviteId, secret } = invitation;
  invitation = null;
  // Used now: a copy kept for the sign-in would otherwise be answered again on the next sign-in in this tab.
  takeKeptInvite(tabStorage);
  stopJoining();
  $("join-error").textContent = "";
  $("join-status").textContent = "Adding this device to your account…";
  $("join-dialog").showModal();

  try {
    const store = keystore;

    if (!store) {
      throw new Error("This browser cannot keep keys, so it cannot be added.");
    }

    const id = deviceId ?? await ensureDevice(store, api);

    if (isCurrent(started)) {
      deviceId = id;
    }

    const deadline = await enrollThisDevice({ keystore: store, api, deviceId: id, inviteId, secret, now: Date.now() });

    // Closed meanwhile: the page's own poll takes the grants when they come, while the secret waits for them.
    if (!isCurrent(started) || !$("join-dialog").open) {
      return;
    }

    $("join-status").textContent = "Waiting for your other device to share its keys…";
    startJoining({ inviteId, deadline, started });
  } catch (error) {
    if (isCurrent(started) && !(error instanceof Stale)) {
      $("join-status").textContent = "";
      $("join-error").textContent = error.message;
    }
  }
}

function startJoining(join) {
  stopJoining();
  // One check at a time, as for a computer's grant: two overlapping would take the same grants twice.
  const check = singleFlight(() => checkJoining(join));
  joining = { inviteId: join.inviteId, timer: setInterval(check, INVITE_POLL_MS) };
}

function stopJoining() {
  if (joining) {
    clearInterval(joining.timer);
  }

  joining = null;
}

async function checkJoining(join) {
  const waiting = () => isCurrent(join.started) && joining?.inviteId === join.inviteId;
  const store = keystore;

  if (!waiting() || !store) {
    return;
  }

  try {
    const outcome = await inviteAnswered({
      keystore: store, inviteId: join.inviteId, deadline: join.deadline, now: Date.now(),
      collect: async () => {
        const result = await takeGrants(join.started);

        if (result) {
          sayTrouble(result);
        }
      }
    });

    if (outcome === "waiting" || !waiting()) {
      return;
    }

    stopJoining();

    if (outcome === "expired") {
      $("join-status").textContent = "";
      $("join-error").textContent =
        "The invitation expired before your other device answered it. Ask it for a new link.";
      return;
    }

    // Taken here or by another tab: either way what was drawn as unreadable opens now.
    await noticeKeys(store, join.started);
    await render();

    const readable = [];

    for (const host of state.hosts) {
      if (await store.newestEpoch(host.id) !== null) {
        readable.push(host.label);
      }
    }

    if (isCurrent(join.started)) {
      $("join-status").textContent = readable.length > 0
        ? `This device can now read ${readable.join(", ")}.`
        : "This device was added.";
    }
  } catch (error) {
    // The gateway out of reach is said, and the next tick asks again. An ended session says nothing.
    if (waiting() && !(error instanceof Stale)) {
      $("join-status").textContent = `Waiting for your other device to share its keys… (${error.message})`;
    }
  }
}

$("join-dialog").addEventListener("close", stopJoining);

// ── signing in and out ───────────────────────────────────────────────────

/** Who the panel is showing, as the last /api/session said; null while nobody is signed in. */
let account = null;

const SIGNED_OUT = "You were signed out. Sign in again to go on.";

/**
 * Forgets everything the page holds for whoever was signed in.
 *
 * The one mechanism behind sign-out, an expired session, another account and a page restored from the
 * back-forward cache. Before it, only a sign-out reloaded the page: a 401 put the sign-in form over a
 * panel still holding the last person's runs and cursor, and the next sign-in in that tab showed those
 * runs until its first poll replaced them. So: polling stops, requests in flight are aborted and whatever
 * they bring back is dropped, the arrays, cursor, unread count and open run are emptied, every dialog
 * closes and every field and line drawn for the person is emptied (FORGOTTEN, in session-guard.js), and
 * nothing private is drawn again until a fresh /api/session says whose it is.
 */
function resetSession() {
  stopPolling();
  stopPairing();
  stopInviting();
  stopJoining();
  endSession();
  account = null;
  // Closed, not only dropped: one database per account, and this one's keys are not the next person's.
  keystore?.close();
  keystore = null;
  deviceId = null;
  // Its opened records too: they are the last account's content, in clear.
  reader = createReader(null);
  writer = createWriter(null);
  // Drafts too: a draft is the last person's task, and nothing of it is the next one's to resend. Commands
  // stay (see `sends`): each names a task, run or approval only its own account can act on.
  sends.forgetAll("draft:");
  content = () => undefined;
  drawnCursor = null;
  grantSchedule = createGrantSchedule();
  seenEpochs = new Map();
  lastTrouble = "";
  resetState(state);
  clearTimeout(toastTimer);
  forgetScreen($, document.querySelectorAll("dialog[open]"));
  $("account").open = false;
  $("account-name").textContent = "";
  $("panel").hidden = true;
  render();
}

/**
 * Asks the gateway who is signed in and shows that. The only way onto the panel, so what it draws is
 * always the account a fresh answer named.
 */
async function boot(outcome) {
  let view;

  try {
    view = await session();
  } catch (error) {
    if (!(error instanceof Stale)) {
      showSignedOut("The gateway could not be reached. Reload to try again.");
    }

    return;
  }

  if (view.authenticated) {
    await enterPanel(view.user);
  } else {
    showSignedOut(outcome);
  }
}

async function enterPanel(user) {
  account = user;
  $("account-name").textContent = user.displayName;
  $("login").hidden = true;
  $("device-removed").hidden = true;
  $("panel").hidden = false;

  const started = generation();
  await openTrust(user);

  if (!isCurrent(started)) {
    return;
  }

  await poll();

  // Grants that arrived while no page was open - a connection code pasted after its dialog was closed.
  // After the poll, so a computer in doubt is named rather than called "A computer".
  try {
    const result = await takeGrants(started);

    if (result) {
      sayTrouble(result);
    }

    // Keys that arrived while no page was open: what the first poll drew as unreadable opens now.
    if (result?.added.length > 0) {
      await render();
    }

    // A code answered after it expired is listed by the gateway on every call. Said in a toast it was said on
    // every load, for good; the console keeps it for whoever is looking into why a computer never paired.
    result?.rejected.filter((one) => one.code === "no-secret").forEach((one) =>
      console.info(`${hostLabel(one.hostId)}: ${one.reason}`));
  } catch {
    // Not reaching the gateway is what the poll already shows.
  }

  // Not when the first poll ended the session: a timer started now would poll for nobody. Nor while
  // hidden: a hidden tab does not poll (see visibilitychange), and coming back starts it.
  if (isCurrent(started) && document.visibilityState === "visible") {
    startPolling();
  }

  // Opened by an invitation link: answered now that someone is signed in, after the first poll so the
  // computers this device comes to read are named.
  if (invitation && isCurrent(started)) {
    joinByInvitation(started);
  }
}

function showSignedOut(outcome) {
  $("panel").hidden = true;
  $("device-removed").hidden = true;
  $("login").hidden = false;

  // A provider's sign-in leaves this page and comes back to another address, without the link's fragment.
  if (invitation) {
    keepInvite(tabStorage, invitation);
  }

  const said = outcome ?? (invitation ? "Sign in to the account you are adding this device to." : null);
  const line = $("login-outcome");
  line.textContent = said ?? "";
  line.hidden = !said;

  offerProviders();
  offerDevelopmentSignIn();
}

function showDeviceRemoved() {
  $("panel").hidden = true;
  $("login").hidden = true;
  $("device-removed").hidden = false;
}

/** One plain link per provider the gateway lists. A link, not a script: signing in is a navigation. */
async function offerProviders() {
  const note = $("providers-note");

  try {
    const links = providerLinks(await get("/api/providers")).map(({ href, label }) => {
      const link = node("a", "provider", label);
      link.href = href;
      return link;
    });

    $("providers").replaceChildren(...links);
    note.textContent = "No way of signing in is configured on this gateway.";
    note.hidden = links.length > 0;
  } catch (error) {
    if (error instanceof Stale) {
      return;
    }

    $("providers").replaceChildren();
    note.textContent = "The gateway could not be reached. Reload to try again.";
    note.hidden = false;
  }
}

const developmentSignIn = createDevelopmentProbe(get);

/**
 * The development sign-in, offered only on this machine and only by a gateway that has it.
 *
 * Asked of the gateway rather than assumed from the address: localhost is also where an operator sees a
 * production gateway through an SSH tunnel, and a form there would sign nobody in while looking like a
 * way in. How it is asked is createDevelopmentProbe's: a GET, which no limit counts.
 */
async function offerDevelopmentSignIn() {
  const form = $("dev-sign-in");

  if (!["localhost", "127.0.0.1", "[::1]"].includes(location.hostname)) {
    form.hidden = true;
    return;
  }

  form.hidden = !(await developmentSignIn());
}

/**
 * Signs out here, or everywhere, and only then says so. A sign-in page shown while the gateway still
 * holds the session would claim something untrue: the next reload would open the panel again.
 */
async function signOut(path) {
  // Before the request, not after it. A poll in flight that is answered 401 once the gateway has ended
  // the session would run the 401's reset, which aborts this very request - and the sign-out would end
  // in "Not signed out" over the sign-in page. Abandoned now, its answer is dropped unread.
  stopPolling();
  abandonRequests();

  try {
    await post(path, {});
  } catch (error) {
    // Stale: another sign-out got there first. Unauthenticated: the session was already over, and the
    // 401 has put the sign-in page up.
    if (error instanceof Stale || error.code === "unauthenticated") {
      return;
    }

    toast(`Not signed out. ${error instanceof Refused ? error.message : "The gateway could not be reached."}`, true);

    // Still signed in, so the panel goes on as it was.
    if (account && document.visibilityState === "visible") {
      startPolling();
    }

    return;
  }

  resetSession();
  showSignedOut();
}

/**
 * Checks what is on screen against the gateway's own answer.
 *
 * The cookie belongs to the browser, not the tab: in another tab the person may have signed out, or in
 * as somebody else. Every snapshot names its account (pollOnce), but a session that has ended gives no
 * snapshot to compare, so the session is asked first, before the poll restarts.
 *
 * One at a time, like the poll: coming back to a tab fires visibilitychange and focus together, and two
 * checks would each start a poll.
 */
const revalidate = singleFlight(revalidateNow, generation);

async function revalidateNow() {
  if (!account) {
    return;
  }

  const expected = account.id;
  let view;

  try {
    view = await session();
  } catch (error) {
    // Unreachable: the poll says so on screen, and keeps trying until the gateway answers - each
    // snapshot is still checked against the account (pollOnce), so nothing unconfirmed is drawn.
    if (!(error instanceof Stale) && account?.id === expected) {
      await poll();

      if (account?.id === expected && document.visibilityState === "visible") {
        startPolling();
      }
    }

    return;
  }

  if (!view.authenticated) {
    resetSession();
    showSignedOut(SIGNED_OUT);
  } else if (view.user.id !== expected) {
    resetSession();
    await boot();
  } else if (account?.id === expected) {
    await poll();

    if (account?.id === expected && document.visibilityState === "visible") {
      startPolling();
    }
  }
}

// Both ids come from the same place on the gateway today. A gateway that contradicted itself - a snapshot
// that never matches its own session - would otherwise reset and boot in a loop: the page flickering, and
// asking for the session and the state as fast as they are answered. At most one such boot per pause.
const MISMATCH_PAUSE_MS = 5000;
let lastMismatchBoot = -Infinity;

/** Boots after a snapshot named another account: at once the first time, then at most once per pause. */
function bootAfterMismatch() {
  const started = generation();
  const wait = Math.max(0, lastMismatchBoot + MISMATCH_PAUSE_MS - Date.now());

  setTimeout(() => {
    // A sign-in or sign-out in the meantime has booted the page already.
    if (isCurrent(started)) {
      lastMismatchBoot = Date.now();
      boot();
    }
  }, wait);
}

onUnauthenticated(() => {
  const wasSignedIn = account !== null;
  resetSession();
  showSignedOut(wasSignedIn ? SIGNED_OUT : null);
});

onDeviceRevoked(() => {
  resetSession();
  showDeviceRemoved();
});

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

$("sign-out").addEventListener("click", () => signOut("/api/logout"));
$("sign-out-all").addEventListener("click", () => signOut("/api/logout-all"));

// The session is still open on the gateway - only this browser was removed - so signing out needs a
// token, and the reset that put this view up threw the last one away.
$("removed-sign-out").addEventListener("click", async () => {
  try {
    await session();
  } catch (error) {
    if (!(error instanceof Stale)) {
      toast("The gateway could not be reached.", true);
    }

    return;
  }

  await signOut("/api/logout");
});

$("dev-sign-in").addEventListener("submit", async (submitted) => {
  submitted.preventDefault();
  $("dev-error").textContent = "";

  try {
    // A fresh token: the one held may be from before a sign-out, bound to whoever that was.
    await session();
    await post("/api/dev/sign-in", { name: $("dev-name").value });
    $("dev-name").value = "";
    await boot();
  } catch (error) {
    if (!(error instanceof Stale)) {
      $("dev-error").textContent = error.message;
    }
  }
});

$("theme").addEventListener("click", () => {
  const next = document.documentElement.dataset.theme === "light" ? "dark" : "light";
  applyTheme(next);
  localStorage.setItem(THEME_KEY, next);
});

$("new-task").addEventListener("click", openTaskDialog);
$("task-submit").addEventListener("click", createTask);
$("add-host").addEventListener("click", () => {
  stopPairing();
  $("host-secret").hidden = true;
  $("host-submit").hidden = false;
  $("host-error").textContent = "";
  $("host-status").textContent = "";
  $("host-name").value = "";
  $("host-code").value = "";
  $("host-dialog").showModal();
});
// Closed - by its button, Escape, a reset or the computer's answer - nobody is waiting for the answer any more,
// and the code goes with it: it carries the computer's token, which would otherwise stay in the page until the
// next "Register".
$("host-dialog").addEventListener("close", () => {
  stopPairing();
  $("host-code").value = "";
});
$("host-submit").addEventListener("click", registerHost);
// Through the cursor of the snapshot on screen, and no further: a notice that arrived after it has not been
// seen, and marking "everything" read would have marked it too. With nothing drawn yet there is no cursor,
// the gateway refuses the call without one, and the button is disabled (render) rather than sending null.
$("mark-read").addEventListener("click", (clicked) => {
  const through = drawnCursor;

  if (through !== null) {
    act(clicked.currentTarget, () => post("/api/notices/read", { through }));
  }
});

// A phone puts the page to sleep rather than closing it. Coming back to a
// screen that is minutes stale, with no sign that it is, is the failure this
// avoids: the session is checked and the poll runs at once, and the banner says
// so until it succeeds.
//
// Hidden, the poll stops. A hidden tab went on polling under whatever cookie the
// browser held by then, so when Bob signed in, in another tab, Alice's hidden tab
// drew Bob's events onto her list, and they were on screen before anything could
// check. Back, the session is checked first and the poll restarts only when it
// is still the same account (revalidate).
document.addEventListener("visibilitychange", () => {
  if (document.visibilityState === "visible") {
    revalidate();
  } else {
    stopPolling();
  }
});

// Focus as well: a window that was never hidden - beside another one, where Bob
// just signed in - fires no visibilitychange when it is clicked back into.
window.addEventListener("focus", () => revalidate());

// A page restored from the back-forward cache is the page as it was left: whoever was signed in then,
// their runs on screen, and a poll timer that may belong to a session ended since. None of it is
// trusted - it is all forgotten, and the session asked again.
window.addEventListener("pageshow", (shown) => {
  if (shown.persisted) {
    resetSession();
    boot();
  }
});

applyTheme(localStorage.getItem(THEME_KEY));

// An invitation link, /pair#v=2&i=…&p=…: taken out of the address at once (openInviteLink) and held until
// someone is signed in. Without one, an invitation kept across a sign-in that left the page; taken out of
// storage either way, so a link opened since replaces it rather than leaving it there to be answered later.
invitation = takeKeptInvite(tabStorage);

if (location.pathname === "/pair" && location.hash) {
  try {
    invitation = openInviteLink(location.hash, history);
  } catch (error) {
    toast(error.message, true);
  }
}

// A link pasted into a tab already at /pair changes only the fragment: the page is not loaded again, so the
// lines above never ran, and the secret stayed in the address with nothing done about it.
window.addEventListener("hashchange", () => {
  if (location.pathname !== "/pair" || !location.hash) {
    return;
  }

  try {
    invitation = openInviteLink(location.hash, history);
  } catch (error) {
    toast(error.message, true);
    return;
  }

  if (account) {
    joinByInvitation(generation());
  } else if (!$("login").hidden) {
    // Said on the sign-in page, and kept for a sign-in that leaves it.
    showSignedOut();
  }
});

// Where a sign-in that opened no session ended. Read once and taken out of the address, so a reload
// does not say it again; any other fragment is left alone.
const outcome = outcomeOf(location.hash);

if (outcome) {
  history.replaceState(null, "", location.pathname + location.search);
}

await boot(outcome);
