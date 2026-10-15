# Enactive.Server with a web UI - implementation plan

Date: 2026-10-11 (revised the same day after review)

Status (2026-10-15): phases 0-3 implemented, with the desktop-parity follow-up complete on
`feature/application-layer` (PR #57 to `master`). The user confirmed that the installed service works;
manual service acceptance is complete. Phase 4 is optional and not planned. No mandatory WEB UI feature remains.
The ADR amendments below are made. Implements [ADR 0002](../Docs/adr/0002-server-with-web-ui.md).
Historical status entries below describe what was known on their stated dates.

Latest local validation: Server 505/505, Engine 4566/4566, Desktop UI 22/22, Playwright Chromium 36/36.
The browser suite uses the real Kestrel pipeline with a scripted engine; installed-service acceptance is the
user's manual confirmation, not an automated live-model/process test. Those process tests have not been added.
Clean-checkout build and PR CI are release integration checks, separate from feature acceptance.

## Objective

Run Enactive as a Windows service on a workgroup machine and give the domain machines of the same network a
browser interface to it: read runs, run tasks and answer their permissions, configure the service. No access
from the internet. The desktop app keeps working at every step.

## What the code looks like today (master `c3fd4d8`)

The ADR's premises checked against the source:

| ADR says | In the code | Consequence |
| --- | --- | --- |
| Runs compose through `RunComposer` | `Settings/RunComposer.cs`; window, console and `Remote.Host/RemoteRunner` call it | The server is a fourth caller |
| Part of the run control is already Avalonia-free | `ForegroundRunSlot`, `BackgroundRunGroup`, `RunEventPump`, `InboxDecisions`, `SchedulePolicy`, `DecisionCompletion`, `SettingsSave`, `LogFilter` - no Avalonia | Movable, but **`RunEventPump` is not server-grade** (see A2) |
| View models without Avalonia | `RunTimeline`, `RunRouting`, `ModelPerformance`, `ModelWorkSplit`, `StepCardViewModel`, `UnfinishedChanges`; depend only on hand-written `Mvvm/ObservableObject` | Only the *projections* are shared; per-viewer state is not (A6) |
| The rest is in `MainWindow.axaml.cs` | 2,743 lines; `RunAsync` is lines 827-1909; decisions at 2474-2620 | The bulk of phase 0 |
| Event folding is shared | `RunFeed`, `RunColumn`/`LiveRun` in `Enactive.Core` | A browser rebuilds a run by folding its events; no UI journal on the server |
| Schedules | Fired by the console (`--due`, `--due-all`) via `Core/ScheduleTick`; the window only edits | Server scheduler = `BackgroundService` over `ScheduleTick` |
| Accounts in the gateway | MySQL, built around external identity (OIDC) | Not reusable as-is; small local store on the server |
| Secrets per user | `Secrets/Secret.cs` (DPAPI CurrentUser); `settings.json`, `permissions.json` under `%APPDATA%\Enactive` | Confirms ADR §7 - **and breaks ADR §8** (A5) |
| "Staging works" | `StagingArtifactStore` is in-memory; `RunCheckpoint.Staged` runs are never resumable (`Checkpoints.cs:145`) | Staging on a service is a feature to build, not a screen (A4) |
| Resume | `RunCheckpoint.Remaining` counts the step that was running when the process died; it runs again from the start | Re-executed side effects (A4) |
| `MaxParallelSteps` | Read per `Orchestrator` instance (`Orchestrator.cs:254`) | Not a server-wide limit (A3) |
| Attended vs remote | `RunComposer.Attended()` = `Source == CommandBar`; `Source == Remote` → `RemotePolicy.ForRemoteRun` (no shell) | `IntentSource` has no value for the web host (A6) |
| Remembered approvals | `ApprovalStore` keyed by workspace path + tool, no user | A web operator's "Allow (workspace)" would bind everyone (A3) |
| Cross-process locks | `Core/FileLock` (lock file, `FileShare.None`, one machine) already exists for `ScheduleStore`/`JsonFileStore` | Reuse for the workspace lease (A5) |

## Review findings and what the plan does about them

**A1. Trust model: operators are mutually trusting.** An Operator's run executes the shell as the service account;
that account reads the service's secrets, `settings.json` and the user store. Roles separate *actions in the
interface*, not operators from each other or from the Admin. Decision for the first release: **the server is for
a small team that trusts each other**; the ADR states this and the Users page says it under the role picker.
Isolating operators would mean executing tools in a separate process under another account - out of scope,
noted as a possible later ADR.

**A2. Run lifecycle independent of the page.** `RunEventPump` couples reading events to rendering: a bounded
channel the engine *waits* on, and a render failure cancels the run. On the server the run is owned by a
`RunSession` in the host, not by a circuit: it drains the engine's events into a per-run **event journal**
(the recorded events already exist for history; the live tail is kept in memory, bounded, with the fold
`RunFeed` produces as the snapshot), and browsers subscribe to the journal. A slow or gone subscriber is
dropped from the broadcast, never blocks the engine. Reconnect = snapshot + events after the last seen
sequence number. `RunEventPump` stays a desktop class.

**A3. Several people at once - decided now, because phase 0's shape depends on it.**
- One **queue** on the server; **N active runs** (`Server.MaxActiveRuns`, default 2) and **one active run per
  workspace** (a `WorkspaceLease`, see A5). A second task for a busy workspace waits in the queue and says so.
  Per-person foreground slots are not introduced; `ForegroundRunSlot` stays a desktop concept.
- `MaxParallelSteps` stays per run; the server's own cap is the active-run count. Documented in the settings
  advice so nobody expects one to limit the other.
- Remembered approvals: `ApprovalStore` gains a **principal** dimension (desktop = the Windows user, web = the
  account). An approval given in the browser binds that account in that workspace, not the workspace at large.
  Migration: existing rows are the desktop user's.
- Who may answer a pending decision: the run's owner and any Operator; **first answer wins** through
  `DecisionCompletion`, others see who answered. Initiator gone (tab closed, network lost): the decision waits
  for `Server.DecisionTimeout`, then is **parked into the inbox** as a background run's is today, visible to
  every Operator.

**A4. Staging and restart, honestly.**
- Staging in the first release of phase 2 is **not offered** (`Stage` refused for web runs with a clear
  message). Phase 3 adds **persistent staging**: proposals written to `<workspace>/.enactive/staging/<run>`
  with owner, created time and the ordering `StagingArtifactStore` already carries; retention configurable;
  Apply/Reject by the owner or an Operator, audited. Only then does the browser get the screen.
- Two distinct stop modes: **planned stop** (service stop / upgrade): stop accepting, cancel at the next step
  boundary, bounded drain, exit; checkpoints written as today. **Crash recovery**: unfinished runs are listed
  on the Inbox with the warning that the interrupted step **will run again from its start** and may repeat
  external effects (mail, pushes); resume is a human's click, never automatic. The warning text comes from
  `UnfinishedChanges`, extended with "external effects of step N may be repeated".

**A5. One owner of the data, done per workspace.** `%APPDATA%` is per account, so a lock on *the service's* data
folder does not stop the desktop (another account) or a `--due` console from opening the same workspace. Two
leases, both in `Enactive.Application` and taken by **every executing host** (window, console, server):
- **Settings-folder lock**: one process per `%APPDATA%\Enactive` (what the ADR described).
- **Workspace lease**: `<workspace>/.enactive/lease` via `FileLock` while a run is active there; holder name,
  host kind and pid recorded. The second host refuses to start a run in that workspace and names the holder.
  The scheduler respects it (a due schedule for a leased workspace is deferred, not skipped).

**A6. What goes into `Enactive.Application`, and the intent classification.**
- Application = run control, decisions, scheduling, data access, leases. Shared *projections* (`RunFeed` is
  already one; `RunTimeline`, `RunRouting`, `ModelPerformance`, `ModelWorkSplit` compute figures from
  events) move too. **Per-viewer presentation state does not**: `StepCardViewModel.IsExpanded`/`ToggleCommand`
  stays in the desktop; the browser has its own component with its own expanded flag over the same `RunFeed`
  card. Criterion: "does this describe the run, or one person's view of it?" - not "does it reference Avalonia".
- `IntentSource` gets a **`Web`** value and `RunComposer` decides its policy centrally: `Attended` =
  `CommandBar || Web` (a person is at a screen), shell allowed, approvals remembered against the principal.
  `Remote` keeps its no-shell policy. No server-side special case - ADR 0001's rule holds.

**A7. Blazor, argued correctly; security made concrete.**
- The ADR's reason to pick Blazor Server is **lower development cost for this project** (C# reuse, few users on
  a LAN) - not that an SPA would force a second copy of the logic. Rephrased in the ADR.
- A small explicit **run-control interface** in Application anyway: `IRunControl` (start, cancel, answer, list,
  subscribe) - the Blazor pages are one client of it, the tests another, a phase-4 desktop client a third.
- Security beyond "Blazor handles it": authorization is checked **at the action** (`IRunControl` methods carry
  the principal and check the role), not only at the page; **revocation**: a disabled account or a lowered role
  ends its circuits within one check interval (`RevalidatingServerAuthenticationStateProvider`, 1 min) and the
  next action fails even before that; WebSocket origin is restricted (`AllowedOrigins` on the Blazor hub) and the
  subnet filter runs before the hub.

## Phase 0 - application layer (`Enactive.Application`)

Branch `feature/application-layer` from master. One PR per step; after each one the desktop behaves as before.
Only the tests the step touches are run.

**0.1 Project and the pure classes**
- `src/Enactive.Application` (`net10.0`, no Avalonia). References Core, Agents, Settings, Workspace, Secrets,
  Providers, Tools, Remote.Host. `InternalsVisibleTo` for App.Ui, App.Ui.Tests, Server, Server.Tests.
- Move: `BackgroundRunGroup`, `InboxDecisions`, `SchedulePolicy`, `DecisionCompletion`, `SettingsSave`,
  `LogFilter`. `ForegroundRunSlot` and `RunEventPump` **stay in the desktop** (A2, A3).
- Guard test: `Enactive.Application` references no `Avalonia*` assembly.

**0.2 Shared projections**
- Move `Mvvm/ObservableObject`, `Mvvm/RelayCommand`, `RunTimeline`, `RunRouting`, `ModelPerformance`,
  `ModelWorkSplit`, `UnfinishedChanges` to `Enactive.Application.Projections`. `StepCardViewModel` stays (A6).
- Rule in the project README: these raise change notifications on the thread that changed them; marshalling is
  the host's.

**0.3 `IntentSource.Web` and principals** (A6, A3)
- Add `Web` to `IntentSource`; `RunComposer.Attended` and the policy branch updated; tests in
  `Enactive.Engine.Tests` for the composer: Web attended, shell allowed; Remote unchanged.
- `ApprovalStore` gets `principal`; `RunRequest` gets `Principal` (desktop fills the Windows user name).
  Migration test: a pre-existing row is read as the desktop user's.
  *Done differently:* the desktop and the console pass **no** principal - the file is in one Windows
  account's profile, so its unnamed rows already are that account's and need no migration, and an older
  build keeps reading them. A named principal gets its own escaped key; an empty one is refused, never
  taken for the account. `SessionApprovals` got the same dimension (one engine serves every person of
  the service). A `Web` run must name its principal, and `Stage` is refused for it until phase 3.

**0.4 Leases** (A5)
- `SettingsFolderLock` and `WorkspaceLease` over `Core/FileLock`. Taken by the window (settings lock at start,
  lease per run), the console (both, per invocation) and later the server. Refusal message names the holder.
- Tests: two leases on one workspace → second refused with the holder; lease released on run end and on crash
  (`DeleteOnClose`); console `--due` on a leased workspace defers.
- *Done differently (decided 2026-10-11):* the settings-folder lock is taken **by the service only** - a
  window and a `--due` console of one account share `%APPDATA%\Enactive` today, and an exclusive lock there
  would stop schedules while the window is open. It is written with the service (1.1). The workspace lease
  is `Core/ProcessLease` (not `FileLock`: held for a run, refused rather than waited for, the holder readable
  by others), counted within a process so the window's foreground and background runs still share a folder,
  and taken in `RunComposer.ComposeAsync` - every host, with nothing to forget. `--due` takes the schedule
  and the lease together before marking the occurrence fired (`ScheduledStart`), so a busy folder defers.

**0.5 Run control out of `RunAsync`** - the largest step; two PRs if needed.
- `RunLauncher`: the checks (folder exists, model configured), building `RunRequest`, `RunComposer.ComposeAsync`,
  completion into history, inbox. Returns `RunHandle` (run id, `Events`, `Cancel`, `Completion`).
- `RunQueue`: FIFO with `MaxActiveRuns` and the workspace lease (A3). Desktop uses it with `MaxActiveRuns` =
  "as today" (foreground + background group); the queue is what the server shares.
- `LiveRuns`: the `_live` registry (`BeginLive/UpdateLive/EndLive`) over `RunColumn.Live`.
- `RunView`: the per-run fold the window keeps in fields (`RunFeed`, phase, progress, title, live artifacts,
  agent). The window keeps `RenderEvent`'s drawing, `RunEventPump` and the `Live()` journal.
- `WorkInFlight`: `HasWorkInFlight` / `DescribeWorkInFlight`.
- Tests: launcher refusals; queue ordering and per-workspace exclusivity; `LiveRuns` filtering.
- *Done differently:* `RunLauncher` is the checks and the provisional title only; `RunHandle` is left to
  `RunSession` (1.4), the one consumer that needs that shape - building it now would be guessing at it.
  `RunView` became `Projections/RunActivity` (agent, current action, tool calls, why it ended); phase,
  progress and title already come from `RunFeed`, and live artifacts stay with the window that draws
  them. `RunQueue` is **not** used by the desktop: its foreground run and background group are one
  person's, and a pass-through queue there would only be code to read. It starts the earliest waiter
  that can start, so a run for a free folder is not held behind one waiting for a busy folder.

**0.6 History, settings, decisions**
- `RunHistoryService`: open/build past run, retry, run again, delete, trim, forget unfinished, artifact undo /
  delete. Confirmations stay in the window (`Func<..., Task<bool>>`).
- `SettingsService.Save(AppSettings)` wrapping `SettingsSave`, with a **version** on the settings file (A7/phase 3).
- `DecisionRouter : IDecisionHandler`: remembered approvals (by principal), parking, `DecisionCompletion`;
  asks an `IDecisionPresenter`. `ContinueParkedAsync`/`ObserveBackgroundAsync` move with it.
- `IRunControl` (A7) assembled from the above; the desktop's `MainWindow` becomes its first client.
- *Done differently:*
  - `DecisionRouter` owns the queue, the choices offered (session / workspace / unwatched, none for a
    shell to keep), what an answer remembers and as whom, and the withdrawn question; the window is an
    `IDecisionPresenter`. Parking stays `ParkingDecisionHandler`'s (Agents) - the router is the
    attended side.
  - `RunHistoryService` returns its **questions** (`Question`) instead of taking confirm callbacks: a
    browser asks across a round trip, and the words are then the same in both hosts. It also refuses a
    record's path that climbs out of the workspace (`FileOf`) - the 1.6 path guard, started here. Undo
    of the live run's files stays with the window, which holds the run's disk store.
  - `BackgroundRun` (compose, run to the end, the Inbox item however it ends) and `ContinueFromAsync`
    carry `StartBackground`'s and `ContinueParkedAsync`'s host-free part; the background group and
    its rows stay with each host.
  - **Deferred:** the settings **version** goes with the browser settings editor (phase 3), the one
    writer that needs it, as it changes the settings file's format. `IRunControl` goes with
    `RunSession` (1.4/1.5): its shape - start, cancel, answer, list, subscribe with the principal -
    should be set by the first host that calls it over a wire, not guessed now.
  - `MainWindow.axaml.cs` still holds the foreground run's orchestration and workspace switching
    around the drawing; what it decides is now asked of the application layer.

**Done when:** the desktop behaves as before (Stas's own run); touched tests pass; `MainWindow.axaml.cs` holds
controls, binding and drawing only. Estimate: **2 weeks** (0.3-0.4 are new behaviour, not a move).

## Phase 1 - the vertical slice, then reading

Reordered per the review: prove the architecture on one end-to-end scenario before porting screens.

**1.1 Host skeleton** - `src/Enactive.Server` (Web SDK), `UseWindowsService`, Blazor Interactive Server;
Kestrel HTTPS only from `server.json` (certificate by file or thumbprint; no certificate → refuse to start);
`SettingsFolderLock` (the service only, over `ProcessLease`; see 0.4); Event Log + rolling file; `/health`.

**1.2 Network and origin** - subnet middleware before routing (also `/_blazor`); hub `AllowedOrigins` = the
configured host names; `deploy/install-server.ps1` (service under a named account, firewall rule for
Domain/Private with `-RemoteAddress`), `uninstall-server.ps1`.

**1.3 Accounts, roles, audit** - `server.db` (SQLite): users (PBKDF2 via `PasswordHasher`, role, disabled,
lockout), audit. CLI verbs `--add-user`, `--reset-password`, `--disable-user`, `--list-users` (password
prompted, never an argument). Cookie auth (`Secure`, `HttpOnly`, `SameSite=Strict`), static-SSR sign-in page,
`RevalidatingServerAuthenticationStateProvider` for revocation. Policies Viewer ⊂ Operator ⊂ Admin, enforced in
`IRunControl` as well as on pages.

**1.4 `RunSession` and the event journal** (A2) - the server-side owner of a run: drains `RunHandle.Events`,
keeps the bounded tail + `RunFeed` snapshot, sequence numbers, fan-out to subscribers with per-subscriber
bounded buffers (drop and mark "behind, reload" rather than block). Unit-tested with a fake event source,
including a stuck subscriber.

**1.5 The slice** - one page: pick a workspace, type a task, run; the live run (cards from the fold); a pending
permission answered in the browser (`WebDecisionPresenter`); cancel. The run page survives tab close / reopen
/ second tab (snapshot + catch-up). A second task in the same workspace queues and says why.
**Done when** the review's scenario passes: an Operator starts a task, closes the browser, comes back, sees
the state, answers the waiting permission; a second run in that workspace queued correctly.

*Status 2026-10-11:* 1.1-1.5 done in code on `feature/application-layer`. `IRunControl` (deferred from 0.6)
is here: start, list, find, cancel, answer, each checking the account at the action. Only the run's owner may
answer with an approval to remember. The orphaned-question timeout (park into the inbox) is phase 2's, with the
inbox page. The slice was checked against Kestrel by script (sign-in, the hub's origin and sign-in checks); a
browser run of the pages and a run on a real model are the user's to do.

**1.6 Reading pages** - shell with the brand (link the gateway's `wwwroot/brand/**`; palette → CSS variables),
workspaces, history, run page (timeline, cards, routing, performance, work split, artifacts), artifact/diff view
with the path guard (canonicalise, inside the workspace, no junction escape, size cap).

**1.7 Tests** - `tests/Enactive.Server.Tests`: `WebApplicationFactory` for sign-in/lockout/roles/subnets/no-HTTP/
lock; bUnit for components with logic; **browser tests** (Playwright, `tests/server-e2e`, run against a local
instance like `tests/panel-e2e`): disconnect/reconnect, two tabs, two operators answering the same decision,
role revoked mid-session; **process tests** (PowerShell, `deploy/*.test.ps1` style): service stop with a run in
flight drains and writes a checkpoint; second host on a leased workspace is refused.

Estimate: **2 weeks** (1.1-1.5 one, 1.6-1.7 one).

*Status 2026-10-12:* 1.6 and 1.7 done in code. Browser tests are `tests/server-e2e` over `tests/ServerHarness` (the
real pipeline on Kestrel with a scripted engine, as RemoteHostHarness is for the panel) and run in CI on Windows:
a run outliving its page, two tabs on one question, another Operator answering (but not remembering in the owner's
name), a run waiting for its folder, a Viewer offered nothing, a role lowered with its page open. They found two
defects the test server could not: a build served `blazor.web.js` empty, and text typed or a click made before a
live page connected was lost (the start form is now a static post; the run page is not prerendered). bUnit was
not needed: the component logic sits in `RunControl`/`RunSession`, tested directly. The planned stop is
`RunControl.StopAsync` on the host's stop (unit-tested); the PROCESS tests - a real service stopped with a run on a
real model, a second host refused its workspace - need an installed service and a model, and are not written.


## Re-estimate after the slice (2026-10-12)

Phases 0 and 1 took two working days of agent time, not four weeks - the estimate was for a person. What the
slice showed about the rest:
- **Phase 2 stays ~1 week (person).** The parts exist: `DecisionLedger` parks a question and gives the answer when
  the step is asked again, `InboxDecisions` answers from the inbox, `BackgroundRun` carries a run on. New: the
  orphaned-question timeout for web runs (park instead of wait), the inbox page, the crash-recovery list with the
  repeat warning and a resume button.
- **Phase 3 stays ~2-3 weeks.** The scheduler is `ScheduleTick` plus `ScheduledStart` (0.4) in a hosted service;
  the editors and settings are the bulk; persistent staging is the one new mechanism.
- Risks seen: Blazor pages must not rely on interactivity before the connection is up (found in 1.7); every
  security check had to be proved against Kestrel, not only the test server (two holes found that way).

## Phase 2 - running, complete

- Inbox page: parked decisions (A3 timeout path), finished background runs, schedule results; answering resumes.
- Crash recovery UI (A4): unfinished runs with the repeat-effects warning; manual resume.
- Owner/Operator rules on cancel and answer, audited; Viewer read-only verified by tests.
- `Stage` refused for web runs with a message (persistent staging is phase 3).
- Render batching (~100 ms), `<Virtualize>` for long card lists.

**Done when:** everything the desktop can do around a run except staging works from another machine. ~1 week.

*Status 2026-10-12:* done in code. A question waits `QuestionWaitMinutes` (30) and then parks to the inbox; the
inbox page answers it and carries the run on as the answerer's run under the same task; unfinished runs offer
Resume (owner or any Operator) after `RunRecovery`'s warning, and Forget (with or without putting files back);
a past run's page offers Try again, Run the template again, Delete, and deleting a file it wrote - each after the
window's own question on a confirmation page, each audited. `<Virtualize>` was not needed yet: the run page redraws
at most ten times a second from the session's fold, and the lists are short; it is for when they are not.

## Phase 3 - configuration and persistent staging

- Scheduler `BackgroundService` over `ScheduleTick` with the workspace lease; docs note to remove Task Scheduler
  `--due` entries on that machine.
- Templates and schedules editors. Settings (Admin) with the version check: a save against an older version is
  refused with who/when, never silently overwrites. Secrets entered in the browser are DPAPI-protected under
  the service account and never echoed (set/not set + replace).
- Users and audit pages (with the A1 trust note), log page (Viewer), log analysis (Operator), workspaces (folder
  browser under configured roots), tray notices → inbox + mail.
- **Persistent staging** (A4): on-disk proposals with owner and retention, Apply/Reject audited, resume of a
  staged run still refused (as `Checkpoints.cs` says) until proposals are durable *and* the checkpoint records
  them - the latter is its own small ADR if wanted.

**Done when:** the service is administered entirely from the browser. ~2-3 weeks.

*Status 2026-10-12:* 3.1 the scheduler (`ServerScheduler`, `DueSchedules` shared with the console's `--due`);
3.2 accounts and audit pages; 3.3 `WorkspaceRoots` and the folder browser (runs from the browser only under the
roots); 3.4 the settings page (providers with their keys, model bindings, workers, the shell rule, mail, web
search). *Done differently:* the settings "version" is the file's content hash compared at save, not a field in the
file - nothing for the desktop or older builds to read differently, and a change made at the desktop is seen too.
3.5 templates and schedules (`ServerLibrary`): schedules by a form checked by `ScheduleDrafts` - the scheduler's own
resolution - with the settings' shell rule on top (`SchedulePolicy`); *done differently:* a template is edited as
its file's JSON (TemplateStore's own format, `TemplateValidator` before writing) rather than a form of fields - every
field a template has is reachable at once; a field-by-field editor can come later.
3.6 the log page: the service's own lines and the engine's in one `LogHub`, read by anybody signed in with the
window's filter, analysed by an Operator with the review model; the line format and the export are `LogText`,
shared with the window's log.
3.7 notices by mail (`MailNotices`): what arrived in the inbox since the last mail, in one message a minute at
most, to `NoticeRecipients` in server.json (none: none) through the settings' mail server; the SMTP send is
`MailSending`, shared with the send_email tool.
3.8 MCP servers and the engine's switches on the settings page: an MCP server's environment and headers are named,
never shown, kept when nothing new is typed (`McpServerForm`); a connection is tried by an Admin only. The text of
the switches' boxes becomes the engine section by `EngineSwitches`, shared with the window's settings.
3.9 persistent staging: `StagingArtifactStore` takes a folder to keep its proposals in (bytes plus a manifest
rewritten after every change) and `Reopen`s it later with the same apply rules; `RunRequest.KeepProposalsIn`, and a
web run stages only with one. `ServerStaging` keeps a browser run's under `.enactive/staging/<run>` with its owner,
files an inbox item when proposals wait, lets anybody signed in read them (the Proposals page, with diffs) and an
Operator apply or reject each once the run has ended, audited; the folder goes when every proposal is decided.
*Not done:* a scheduled run still cannot stage (nobody watches it - RunComposer's rule stands).

*Status 2026-10-12, later:* proposals nobody decides are dropped after server.json's `ProposalsKeptDays` (30 by
default, 0 keeps them; `StagingRetention`), said in the inbox and the audit trail. A review of the web UI found ten
defects, all fixed with a test each - among them a drive named as a WorkspaceRoot refusing every folder, deleting a
history file through a junction, schedules not held to the roots, an inbox answer spent on a run that was then
refused, and the framework's host filter answering ahead of the subnet check (`NetworkGate` checks the Host now).
master's "the app runs its own schedules" is merged in through the one `DueSchedules` (Enactive.Application): the
window, `--due`, `--due-all` and the service take a due schedule by the same steps, the workspace lease included.
The engine's own words write numbers invariantly (`Invariant`), the same on every machine. Browser tests
(`tests/server-e2e`, 15 scenarios) pass.

## Phase 4 - desktop as a client (optional)

`IRunControl` and `IDecisionPresenter` are the seams; a SignalR client implementation of `IRunControl` is the
work. Not planned further.

## ADR amendments (made 2026-10-11)

1. Trust model paragraph (A1): mutually trusting operators; isolation is out of scope and why.
2. §6 / §8: "one owner" is per workspace via a lease taken by every host; the settings-folder lock alone is
   not enough because `%APPDATA%` is per account (A5).
3. "Staging works" → "staging is added in phase 3 as persistent staging; the in-memory store does not survive
   the service" (A4); add the planned-stop vs crash-recovery distinction and the repeated-effects caveat.
4. Resolve the "several people" open question with A3's rules; note `MaxParallelSteps` is per run.
5. §3: replace the "logic written twice" argument with development cost; name the explicit `IRunControl`.
6. §5: concrete items - authorization at the action, revocation via revalidation, hub origins, subnet filter
   before the hub (A7).
7. Plan table: phases and estimates from this document; total **7-9 weeks**, provisional until phase 1.5 is done.

## Risks

| Risk | Mitigation |
| --- | --- |
| Phase 0 changes behaviour while moving code | Small PRs, move before refactor, Stas's desktop run after each; 0.3-0.4 flagged as behaviour changes with their own tests |
| Operator account = shell as the service account | Stated trust model (A1), roles, lockout, subnet filter, HTTPS only, audit |
| Engine blocked by a slow browser | `RunSession` owns the run; subscribers have bounded buffers and are dropped, never awaited (A2) |
| Two hosts in one workspace | Workspace lease in every host (A5) |
| Resume repeats external effects | Manual resume with the explicit warning (A4) |
| Estimates | The slice (1.5) is the checkpoint: re-estimate phases 2-3 after it |

## Order of work and branches

1. `feature/application-layer`: 0.1 → 0.6, merged to master.
2. ADR amendments 1-7 (one PR on `Docs/adr/0002-server-with-web-ui.md`).
3. `feature/server`: 1.1 → 1.5 (the slice), re-estimate, then 1.6 → phase 3. One PR per numbered step.

*As it went:* phases 0-3 were done on one branch, `feature/application-layer`, with master merged into it as it
moved; it goes to master in one piece when the product is ready (2026-10-12), not step by step.
