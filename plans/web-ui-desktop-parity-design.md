# The service's web UI, as the desktop window - design

Date: 2026-10-12. Status (2026-10-15): **implemented and accepted** on `feature/application-layer`, PR #57 to
`master`. All 22 implementation tasks and 36 Chromium browser scenarios are complete; the user confirmed the
installed service works. Remaining release work is clean-checkout/CI verification and merging the PR.

The implementation plan records the small departures from this initial design: dialog editors, no historical
MODEL PERFORMANCE without recorded timings, and Source only when there is rendered text to switch from.

Builds on [ADR 0002](../Docs/adr/0002-server-with-web-ui.md) and the
[server plan](server-web-ui-implementation-plan.md), whose phases 0-3 are done. The pages those phases built work
and are tested, but they look nothing like the desktop window: a top row of links over plain forms. This design
replaces them with a web UI that is the desktop window, served.

## What was decided, and by whom

Asked and answered in the session of 2026-10-12:

| Question | Answer |
| --- | --- |
| Which look | Mockup 1 of the canvas "Enactive Web UI — mockups": the desktop main window, served - icon rail, workspace and runs column, command bar, run feed, status column, desktop palette (`Brand.cs`). |
| UI language | English, as the desktop. |
| Scope of the first delivery | Everything at once: the shell, the Runs screen, and every other screen the desktop has a window for. |
| The desktop's run controls the service lacks | Made to work: autonomy slider, Stage changes, worker role, Run in background, the verb chips. |
| The desktop's separate windows | Pages in the same shell, one address each. |
| Technology | Blazor Server, interactive (approach A). Rejected: restyling the static form pages (B) - every action a reload, nothing behaves like the window; a separate SPA over a JSON/SignalR API (C) - a second code base and API surface, against ADR 0002's "nothing here may grow a second copy". |
| Component tests | bUnit is added to `tests/Enactive.Server.Tests`. |

## Goals and non-goals

**Goals.** Somebody who uses the desktop window finds the same screen in the browser: the same places, labels,
colours and actions, with what only a service needs (who is signed in, who else is watching, the connection) added
without moving anything. What the window shows is computed by the same code (`Enactive.Application` projections).
Every action is still authorized at the action, in the server's services, as today.

**Non-goals.** Pixel parity on a phone (narrow screens get a usable stacked layout, not the desktop at 390 px).
The desktop as a client of the service (plan phase 4). Features the engine does not have - pausing a run, choosing a
model per run, a separate live terminal stream: the reference mockup 3 drew them, the engine has none of them.

## 1. The shell, navigation and theme

**Shell** (every page but sign-in):

- **Top bar, 40 px**, in place of the window's title bar: the loop mark, "Enactive", the server's host name; on the
  right the connection (Connected / Reconnecting… / Disconnected, with a dot), the signed-in name and role, Sign out.
- **Icon rail, 52 px**, the desktop's icons in its order and with its tooltips: Runs, Timeline, Templates,
  Schedules, Inbox (unread count), Proposals (a dot while proposals wait), Log; at the bottom Environment, Settings;
  for an Admin also Accounts and Audit. A section the person's role cannot open is not shown at all. The section
  you are in is marked in Ember.

**Addresses**, one per desktop window:

| Address | Screen | Desktop counterpart |
| --- | --- | --- |
| `/`, `/runs/{id}`, `/history/{workspaceId}/{run}` | Runs | main window (live run, past run) |
| `/timeline` | project memory of the workspace | Timeline viewer |
| `/templates`, `/templates/{id}` | library, template editor | `TemplatesWindow`, `TemplateEditWindow` |
| `/schedules` | schedules | `SchedulesWindow` |
| `/inbox` | inbox | `InboxWindow` |
| `/proposals`, `/proposals/{workspaceId}/{set}` | proposed changes (web only) | the run's Artifacts tab |
| `/log` | log, analysis | `LogWindow`, `LogAnalysisWindow` |
| `/environment` | environment summary | Environment viewer |
| `/settings`, `/settings/{section}` | settings | `SettingsWindow` |
| `/accounts`, `/audit` | accounts, audit (web only, Admin) | - |
| `/signin` | sign in | - |

A workspace is named in an address by its id (`WorkspaceInfo.Id`, which the folder carries), never by its path: a
Windows path does not survive a URL segment. The old addresses (`/history?ws=…`, `/history/run?…`, `/library?…`, `/workspaces`, `/admin/…`, `/file?…`) redirect
to the new ones, so a link in an earlier mail or bookmark still lands.

**Rendering.** The whole app is interactive (`InteractiveServer`, `prerender: false`), set once on `Routes`: a page
appears with its connection, so nothing typed or clicked before the connection is lost (the reason the current
pages are static forms, found by `tests/server-e2e`). Sign-in stays a static form post: it sets the cookie, which a
live page cannot. While the connection is down the top bar says Reconnecting… and actions are disabled, so a click
is never dropped without a word.

**Theme.** One stylesheet whose custom properties are the tokens of `Brand.cs`, named as there (`--ink-950` …
`--ink-50`, `--ember-500`, `--amber-400`, `--success`, `--warning`, `--danger`, `--info`, `--card-fill`, …); the
font Segoe UI / system-ui, monospace Consolas. Classes mirror `Styles/Controls.axaml`: `panelHeader`, `hint`,
`label`, buttons `go`, `goSoft`, `destructive`, `primary`, tabs, cards, status pills, the step card's edge. Which
state gets which colour is one table in C# (`Looks.cs`, the web's `Palette.cs`), over every value of each enum and
throwing on one it does not know - a test asks every value, as `ViewModelsHoldNoLooksTests` does for the window.
The brand is linked from the gateway's `wwwroot/brand`, as now.

**Narrow screens.** The rail becomes a bottom bar, the workspace column a drawer, the status column moves below
the feed.

## 2. The Runs screen

Replaces Home, RunPage, HistoryPage and PastRunPage. Columns: rail | workspace and runs (268 px) | main |
status (300 px), as the main window.

**Workspace and runs column.**

- WORKSPACE: the current folder as a card; a click opens the switcher - every workspace the service knows, each row
  with its edge (current blue, missing red, others grey) - and Add folder…, which opens the folder browser under
  `WorkspaceRoots` in a dialog (Operator).
- RUNS with Refresh; RUNNING: runs going now in any workspace, each with Open.
- The filter, as the window's: All runs / Unfinished / Completed / Older than a week, and beside it "Delete these N"
  (Operator, destructive, the count on the button) that forgets exactly the rows shown.
- UNFINISHED: interrupted runs with carry on and forget icons (as crash recovery today).
- The history of the selected workspace: status edge, title, when, the "×N" attempts pill. Choosing a row opens it
  read-only in place of the live run; the live run keeps going.

**Command bar.**

- Verb chips Fix, Build, Investigate, Explain, Refactor: each puts its word at the front of the input, as the
  window's `PrefixInput`.
- The input, "What do you want to accomplish?"; Enter runs, Ctrl+Enter is a new line.
- Run (starts and opens the run), Stop (cancels the open run), Run in background (starts it and stays: see below).
- Worker role, Stage changes, and the autonomy slider with its coloured label - the workspace's settings, saved as
  the window saves them (`WorkspaceRegistry.SaveSettings`), which the service's engine already reads at every run.
  An Operator changes them, each change in the audit trail; a Viewer sees them read-only.

**The live run.**

- Header: title, the request, who started it and when, the phase pill; and "also watching: …" - the other people
  with this run open.
- Tabs Execution (the step cards with their status edge and the expandable action list - following stays in
  `RunFollower`), Artifacts (files written - Review; proposed changes - Diff, Apply, Reject once the run has ended,
  as `ServerStaging` rules), Log (this run's lines).
- Below the feed: Current action, and the repository bar (branch, pull request, checks) when the workspace is a git
  repository.
- Status column: AI STATUS tiles (Artifacts, Tool calls, Tokens, Elapsed), ROUTING, WHERE IT RAN, MODEL
  PERFORMANCE folded to one line; USER DECISION REQUIRED with the full action, answer buttons for an Operator, the
  remember-it options for the run's owner only (as `RunControl.Answer` rules), and "waits N min more, then goes to
  the Inbox".

**A past run.** Header with Retry and Run again and the attempt label; tabs Execution, Timeline (with DECISIONS),
Artifacts (Review, Delete); status column THIS RUN, ROUTING, WHERE IT RAN, RAN AS, MODEL PERFORMANCE.

**Where the figures come from.** A live run: the session's fold of its events (`RunSession`) and the
`Enactive.Application` projections the window uses. A past run: its record, as `ServerHistory.OpenAsync` does now.
A projection that today exists only inside the window (`Enactive.App.Ui`) is MOVED to `Enactive.Application`,
unchanged, with its tests - never copied - so the window and the browser compute a run the same way.

**New on the server**, each with a test that fails without it:

1. **Workspace settings from the browser**: autonomy, worker, Stage changes saved for a known workspace by an
   Operator, audited ("workspace settings changed"); refused for a Viewer or a folder the service does not know.
2. **Run in background**: started as the window's background run - questions are parked to the Inbox at once
   (`ParkingDecisionHandler`, as `BackgroundRun`), the run ends with its Inbox item whatever its ending - and listed
   in RUNNING while it goes. Answering its question in the Inbox carries it on, as today.
3. **Who is watching**: a subscription to a `RunSession` carries the account name; the session says who is watching
   now (each name once).
4. **Delete these N**: forgets the runs shown by a filter in one action (Operator), each audited as a delete is
   today.

## 3. The other screens

Each desktop window becomes a page in the shell with its layout, labels and buttons. Authorization stays in the
services; a page only hides what the role cannot do.

- **Inbox** (`InboxWindow`): list (360 px) with Unread only, Mark all read, Refresh; the chosen item on the right -
  WAITING FOR YOUR DECISION with its options, then RUN, ARTIFACTS, DECISIONS. An Operator answers; the run carries
  on, as today.
- **Templates** (`TemplatesWindow`, `TemplateEditWindow`): library (300 px); on the right "What it will be asked to
  do", its parameters, Run. The editor has the window's Form / JSON switch (the browser has only JSON today) - Name,
  Description, Category, Goal, parameters (Add parameter, Required, choices, default), checks (Add check), the
  highest autonomy, Ask first / Deny lists, "Review each step before it counts as done", the worker. Operator.
- **Schedules** (`SchedulesWindow`): list (320 px) with New, Edit, Delete; the form - Name, What it runs, How often,
  Time, Day, Date, Time zone, What it may do - with "What this will do" (the schedule resolved, as `ScheduleDrafts`)
  and "What came of it" (its Inbox outcomes). Operator.
- **Log** (`LogWindow`, `LogAnalysisWindow`): Level ≥, Sources, Pause, Auto-scroll, Clear (your view only - the
  service's log is everybody's), Export, AI Analyze (Operator), whose result opens on the page with Copy and Source.
  "Capture raw wire (Trace)" changes the service's log level for everybody, so it is an Admin's.
- **Settings** (`SettingsWindow`): section navigation on the left, the section on the right, Save and Cancel below.
  General, SMTP, Web, About; AI - General, Providers, Team, Phases, MCP, Templates. Not offered: Account and Remote
  access (the desktop's pairing with the remote gateway - nothing a service does). Writable folders shows the
  service's `WorkspaceRoots` read-only (they are set at install, in `server.json`). Admin only; the version check and
  "secrets never return to a page" stay as `ServerSettings` has them.
- **Timeline** and **Environment** (the window's viewer): the workspace's project memory and the service's
  environment summary, with Copy all and Source.
- **Proposals** (web only): every waiting set across workspaces, and a set's page with its diffs - in the window
  they live in a run's Artifacts tab, in the service they outlive the run (`ServerStaging`).
- **Accounts** and **Audit** (web only, Admin): as the settings lists.
- **Workspaces**: no page of its own any more - the switcher and Add folder… on the Runs screen are it.
- **Sign in**: the same form, in the theme.

## 4. Code, tests, order of work

**Code** under `src/Enactive.Server/Components/`:

- `Shell/` - `MainLayout`, `TopBar`, `Rail`, the connection state.
- `Ui/` - the shared pieces, each the counterpart of a desktop control: `PanelHeader`, `Pill`, `Tile`, `StepCard`,
  `Tabs`, `DecisionCard`, `DiffView`, `WorkspaceSwitcher`, `Dialog`.
- `Runs/` - the Runs screen in parts: workspace column, command bar, live run, past run, status column.
- `Pages/` - one per screen of section 3.
- `Looks.cs` - state to colour class (section 1).

Components stay thin: what a screen decides lives in plain C# classes beside it (as `RunFollower`), testable without
a browser. Actions call the server's services directly (`IRunControl`, `ServerHistory`, `ServerInbox`,
`ServerLibrary`, `ServerSettings`, `ServerStaging`, `AccountAdmin`) with the page's user; their checks are unchanged.
The form-post endpoints (`/inbox/answer`, `/runs/resume`, `/proposals/act`, `/history/act`, `/admin/users/act`,
`/workspaces/act`, `/library/…/act`) go with the pages that posted to them; `/signin`, `/signout` and `/health` stay.

**Tests.**

- Server additions (section 2): unit tests in `Enactive.Server.Tests`, each failing first.
- Components, with bUnit: a Viewer is offered no action (no Run, Stop, Apply, answer buttons, settings, Delete),
  an Operator is offered none of an Admin's, empty states say what they should, and every state draws in its colour
  (`Looks`).
- Browser, `tests/server-e2e` rewritten for the new markup, by accessible names and roles, not classes: the 15
  scenarios that pass today, plus the slider saved for the workspace, a background run reaching the Inbox and
  carried on, the workspace switcher with Add folder…, and a lost connection disabling actions.
- The desktop: a projection moved to `Enactive.Application` takes its tests with it; the window's suites pass.

**Order of work**, on `feature/application-layer`, one commit per step, the service usable after each:

1. Theme and shell: every current page inside the new shell and theme; the old addresses redirect.
2. The Runs screen: workspace column, command bar, live run, status column, and the server additions.
3. A past run on the Runs screen.
4. Inbox and Proposals.
5. Templates (with the Form / JSON editor) and Schedules.
6. Log with its analysis, Timeline, Environment.
7. Settings, Accounts, Audit, Sign in.
8. Narrow screens; the whole e2e set; the old pages and endpoints removed.

## Risks

| Risk | What is done about it |
| --- | --- |
| A live-run projection exists only in the window | Moved to `Enactive.Application` unchanged, with its tests; the window's suites run after each move |
| Every open page holds a circuit and its state on the server | Fine for the handful of people on a local network the service is for (ADR 0002); not a design for hundreds |
| The e2e set is rewritten | The 15 scenarios are kept as they are, only their selectors change; new ones are added, none dropped |
| Size | Larger than phase 3 of the server plan; by its pace, several working days of agent time |
