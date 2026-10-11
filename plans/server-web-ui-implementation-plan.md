# Enactive.Server with a web UI - implementation plan

Date: 2026-10-11 (revised the same day after review)

Status: draft. Implements [ADR 0002](../Docs/adr/0002-server-with-web-ui.md) (proposed). Nothing is implemented
yet. The review's findings are folded in below; the ADR itself still needs the amendments listed in
"ADR amendments".

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

**0.6 History, settings, decisions**
- `RunHistoryService`: open/build past run, retry, run again, delete, trim, forget unfinished, artifact undo /
  delete. Confirmations stay in the window (`Func<..., Task<bool>>`).
- `SettingsService.Save(AppSettings)` wrapping `SettingsSave`, with a **version** on the settings file (A7/phase 3).
- `DecisionRouter : IDecisionHandler`: remembered approvals (by principal), parking, `DecisionCompletion`;
  asks an `IDecisionPresenter`. `ContinueParkedAsync`/`ObserveBackgroundAsync` move with it.
- `IRunControl` (A7) assembled from the above; the desktop's `MainWindow` becomes its first client.

**Done when:** the desktop behaves as before (Stas's own run); touched tests pass; `MainWindow.axaml.cs` holds
controls, binding and drawing only. Estimate: **2 weeks** (0.3-0.4 are new behaviour, not a move).

## Phase 1 - the vertical slice, then reading

Reordered per the review: prove the architecture on one end-to-end scenario before porting screens.

**1.1 Host skeleton** - `src/Enactive.Server` (Web SDK), `UseWindowsService`, Blazor Interactive Server;
Kestrel HTTPS only from `server.json` (certificate by file or thumbprint; no certificate → refuse to start);
`SettingsFolderLock`; Event Log + rolling file; `/health`.

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

**1.6 Reading pages** - shell with the brand (link the gateway's `wwwroot/brand/**`; palette → CSS variables),
workspaces, history, run page (timeline, cards, routing, performance, work split, artifacts), artifact/diff view
with the path guard (canonicalise, inside the workspace, no junction escape, size cap).

**1.7 Tests** - `tests/Enactive.Server.Tests`: `WebApplicationFactory` for sign-in/lockout/roles/subnets/no-HTTP/
lock; bUnit for components with logic; **browser tests** (Playwright, `tests/server-e2e`, run against a local
instance like `tests/panel-e2e`): disconnect/reconnect, two tabs, two operators answering the same decision,
role revoked mid-session; **process tests** (PowerShell, `deploy/*.test.ps1` style): service stop with a run in
flight drains and writes a checkpoint; second host on a leased workspace is refused.

Estimate: **2 weeks** (1.1-1.5 one, 1.6-1.7 one).

## Phase 2 - running, complete

- Inbox page: parked decisions (A3 timeout path), finished background runs, schedule results; answering resumes.
- Crash recovery UI (A4): unfinished runs with the repeat-effects warning; manual resume.
- Owner/Operator rules on cancel and answer, audited; Viewer read-only verified by tests.
- `Stage` refused for web runs with a message (persistent staging is phase 3).
- Render batching (~100 ms), `<Virtualize>` for long card lists.

**Done when:** everything the desktop can do around a run except staging works from another machine. ~1 week.

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

## Phase 4 - desktop as a client (optional)

`IRunControl` and `IDecisionPresenter` are the seams; a SignalR client implementation of `IRunControl` is the
work. Not planned further.

## ADR amendments to make before implementation starts

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
