# Enactive can run as a service on the local network, with a web UI

Status: **proposed**, 2026-10-11; amended the same day after review (trust model, ownership per workspace, staging,
several people at once, security at the action, plan). The implementation plan is
[plans/server-web-ui-implementation-plan.md](../../plans/server-web-ui-implementation-plan.md).

Enactive is used from one machine: the desktop window is the only host with the whole interface. Using it
from another machine on the same network - with no access from the internet - is the ask. Today that leaves
two partial answers, neither of which fits:

- **Remote access** (`Enactive.Remote.Gateway` + `Remote.Host`) goes through a gateway on the internet. It is
  end-to-end encrypted and deliberately narrow: no shell, no staging, a poll every three seconds, and the panel
  shows tasks, their timeline, permissions, the inbox and devices - not step cards, settings, templates or
  schedules. Its limits are its security model, not gaps to fill.
- **The console** (`Enactive.App.Console --template`, `--due`) runs without a window, but has no interface at all.

So: a new host, `Enactive.Server`, that runs as a Windows service, owns the engine on its machine, and serves
a web UI to the other machines of the network. The desktop app stays as it is; later it may become a client
of the service (phase 4).

The network it is for: **the client machines are in the Ceragon domain, the server is not** - it is in a
workgroup. That decides authentication (below).

## Decision

### 1. One more host, composing through `RunComposer`

`Enactive.Server` is an ASP.NET Core application hosted as a Windows service (`UseWindowsService`). It
composes every run through `RunComposer`, as the window, the console and remote access already do
([ADR 0001](0001-one-run-composer.md)), and keeps only what a host keeps: who answers a decision (a person in
a browser), where the events go (the browsers watching), and cancellation.

### 2. The application layer leaves the window

What the window does around a run is not the engine and not the interface: starting and cancelling runs, the
foreground run and the background ones, pumping events, decisions and the inbox, history, saving settings,
schedules. Part of it is already in classes of its own with no Avalonia in them (`ForegroundRunSlot`,
`BackgroundRunGroup`, `RunEventPump`, `InboxDecisions`, `SchedulePolicy`, `RemoteAccessService`); the rest is
in `MainWindow.axaml.cs` (2,700 lines). It moves into a new project, `Enactive.Application`, which the desktop
and the server both use. A rule added there then holds in both - the same argument as ADR 0001, one level up.

What is computed from a run to show it (`RunTimeline`, `RunRouting`, `ModelPerformance`, `ModelWorkSplit`,
`RunActivity`, `UnfinishedChanges`) moves with it, so both interfaces show the same figures from the same code.
What describes one person's view of a run does not: a step card folded open (`StepCardViewModel`) stays with the
host that draws it. The test is "does this describe the run, or one person's look at it?" - not "does it name
Avalonia". `ForegroundRunSlot` and `RunEventPump` stay desktop classes: the service owns its runs differently (§6).

### 3. The web UI is Blazor Server

The interface runs on the server and the browser receives only what changed, over SignalR.

- It is live without polling: a step card or the model figures change when the event arrives.
- It is the cheapest to build for this project: the interface is written in the C# the rest is in, and calls the
  application layer in process. An SPA would not have to copy the logic - it could call the same layer through an
  API - but it would need that API designed, versioned and secured for every screen, and a second language.
- The application layer is still reached through an explicit interface, `IRunControl` (start, cancel, answer,
  list, subscribe - each with the account that asks), not through the pages' own calls into its classes. The
  Blazor pages are one client of it, the tests another, a desktop connected to the service (phase 4) a third.
- The cost - a connection held per open page, the server doing the rendering - is nothing on a local network
  with a handful of users.

Rejected: **Avalonia in the browser (WASM)** - the engine cannot run in a browser, so it needs the API anyway,
and every window would have to become a view; **a React/TypeScript SPA** - an API for every screen and a second
language, for a handful of users on a LAN; **growing the remote panel** - its limits are deliberate (above).

The look is the app's: the brand palette and type (`Brand`, `wwwroot/brand/brand.css` of the gateway) become
CSS variables, light and dark.

### 4. Authentication: accounts of Enactive's own, because the server is outside the domain

A server outside the domain cannot check a Kerberos ticket, so Windows sign-in with the domain account (the
obvious answer on a domain network) is not available.

| Option | Why not, or why |
| --- | --- |
| Kerberos / Windows integrated sign-in | Needs the server in the domain |
| NTLM against the server's local Windows accounts | Works, but every user needs a Windows account on the server, the browser prompts with a dialog of its own, and NTLM is being retired |
| Microsoft Entra ID (OpenID Connect) | Single sign-on with the corporate account and groups; needs an app registration from IT and the server reaching the internet outbound. A later option - the gateway already carries the OIDC package |
| Client certificates | Strong, but issuing and renewing them per machine is a job of its own |
| **Accounts kept by Enactive** | Works with no domain, no IT request and no internet |

**Chosen: accounts kept by Enactive**, with Entra ID left as an option to add.

- Passwords hashed (ASP.NET Core `PasswordHasher`, PBKDF2), a cookie session, lockout after repeated failures.
- **The first administrator is created on the server itself** (`Enactive.Server --add-user`), never from the
  web: a fresh service listening on the network must not let the first visitor make themselves its owner.
- Roles: **Admin** (settings, providers, workers, MCP, users), **Operator** (runs tasks, answers permissions,
  edits templates and schedules), **Viewer** (reads runs and logs).
- Every start, cancel, permission answer and settings change is written to an audit log with the account.

**Trust model: the people signed in to one service trust each other.** An Operator's run executes the shell as
the service's account, and that account reads the service's secrets, `settings.json` and the user store. Roles
separate what each person may DO IN THE INTERFACE; they do not separate operators from each other or from the
Admin. The service is for a small team that trusts each other, and the Users page says so beside the role
picker. Keeping operators apart would mean running tools in a separate process under another account per person
- out of scope, and a later ADR if it is ever wanted.

### 5. Reachable on the network, not from it at large

- HTTPS only, with a certificate configured in the service's settings (the corporate CA's, or a self-signed
  one whose fingerprint the users check once). No plain HTTP listener.
- It listens on configured addresses and answers only the configured subnets; the installer adds a Windows
  Firewall rule for the domain/private profiles, not the public one.
- Cookies are `Secure`, `HttpOnly`, `SameSite=Strict`; the sign-in page is static server-rendered, with
  anti-forgery.
- **Authorization at the action, not only at the page.** Every `IRunControl` method takes the account and checks
  its role; a page that forgot its attribute still cannot start a run for a Viewer.
- **Revocation.** A disabled account or a lowered role ends its open circuits within one check interval (a
  revalidating authentication state, one minute), and its next action fails even before that.
- **The SignalR hub** accepts only the configured host names as origins, and the subnet filter runs before
  routing, so it applies to `/_blazor` as to every page.

### 6. What a run from the web may do

A person in the browser is a person at a screen: they see the run and answer its permissions, so a run started
from the web runs under the workspace's own policy, as one typed into the app does - **including the shell**.
That is the difference from remote access, and it is why the account, the role and the network limits above
are not optional: an Operator account on this service is a shell on the server, as the service's user.

**Staging is not offered to web runs at first.** The window's proposals live in memory (`StagingArtifactStore`):
on a service a closed tab leaves nobody to apply them, and a restart loses them while the history says the
changes were staged. `RunComposer` refuses `Stage` for a web run until **persistent staging** exists (phase 3):
proposals written under the workspace's `.enactive/` with their owner and age, applied or rejected by the owner
or an Operator, audited.

A run belongs to the service, not to a page: a `RunSession` drains the engine's events into a journal and the
browsers subscribe to it. A slow or closed page is dropped from the broadcast, never waited for; reopening the
page is a snapshot plus the events after it.

**Stopping the service** is one of two things. A **planned stop** (an upgrade) stops accepting, cancels runs at
their next step boundary, waits a bounded time and exits, with checkpoints written as today. **Crash recovery**
lists the unfinished runs in the inbox with a warning: the step that was running when it died **runs again from
its start**, and may repeat what it did outside the machine (a mail sent, a push). Resuming is a person's
click, never automatic.

### 7. The service runs as a user, not as LocalSystem

Secrets - API keys, the SMTP password, MCP tokens - are protected with DPAPI for the **current user**
(`Enactive.Secrets/Secret.cs`). A service running as LocalSystem cannot read the ones saved by a person, and
would not have their git credentials or rights on the workspaces either. The service runs under a named
account - a dedicated one, or the owner's - and its secrets are entered under that account (the settings
screens of the web UI do that).

### 8. One owner of the data

A lock on the data folder alone does not give one owner: `%APPDATA%\Enactive` is per account, so the service's
lock does not stop the desktop of another account - or a `--due` console - from opening the same workspace.
Ownership is therefore kept **per workspace**: every host that runs a task (window, console, service) holds a
**lease** on the workspace while a run is going there (`.enactive/lease`, taken by `RunComposer`, so no host can
forget it). A second process is refused and told which program, process and user holds it; a scheduled run that
finds its folder taken stays due and runs on the next tick.

The service additionally takes a lock on its own data folder, so two services cannot share one profile. The
desktop and the console do not take that lock: a window and a `--due` console of one account share their data
folder today, and an exclusive lock there would stop schedules while the window is open. Phase 4 lets the
desktop connect to the service instead.

### 9. Several people at once

- **One queue** on the service: at most `Server.MaxActiveRuns` runs at once (default 2), and one run per
  workspace. A run that cannot start yet waits and says why (another run in its folder, or every slot taken and
  how many are ahead); a run for a free folder is not held behind one waiting for a busy folder. There are no
  per-person foreground slots.
- `MaxParallelSteps` stays a property of each run; the service's own limit is the number of active runs. The
  settings advice says so, so nobody expects one to limit the other.
- **Remembered approvals are the person's.** An "Allow (workspace)" given in the browser binds that account in
  that workspace - not everyone signed in, and not the service account's own approvals from its desktop. The
  desktop and the console remember as the account they run under, as before.
- **Who answers a waiting question:** the run's owner or any Operator, first answer wins, and the others see who
  answered. If the person who started the run is gone (tab closed, network lost), the question waits a set time
  and is then parked in the inbox, as a background run's question is today, for any Operator.
- A run started from the browser is its own source (`IntentSource.Web`): attended as the command bar is, with the
  shell, and with the person who started it named on the run. A phone's run (`Remote`) keeps its own rules.

### 10. What the window did that a service does instead

- **Schedules** run in the service: it is up all the time, which the window and `--due` from Task Scheduler
  only approximated.
- **Tray notices** become the inbox in the web UI, and mail.
- **Paths are the server's**: a workspace is a folder on the server, chosen with a folder browser of the
  server's disks, limited to configured roots.

## Plan

Each phase ends with something usable and keeps the desktop working. The steps are in the implementation plan.

| Phase | Content | Done when | Estimate |
| --- | --- | --- | --- |
| **0. Application layer** | `Enactive.Application`: the run control, decisions, history and the shared projections out of `MainWindow.axaml.cs`; `IntentSource.Web` and approvals per person; the workspace lease | The desktop behaves as before; the touched tests pass | ~2 weeks (done in code 2026-10-11) |
| **1. The slice, then reading** | `Enactive.Server`: Windows service, HTTPS, subnets and hub origins, accounts and roles, audit; `RunSession` and its journal; one page that starts a run, shows it live, answers its permission, cancels, and survives a closed tab; then the reading pages | An Operator starts a task, closes the browser, comes back, answers the waiting permission; a second run in that folder queues and says why. Then a Viewer reads any run as the app shows it | ~2 weeks |
| **2. Running, complete** | The inbox, parked decisions, crash recovery with the repeat warning, owner/Operator rules on cancel and answer; `Stage` refused for web runs | Everything the desktop does around a run except staging works from another machine | ~1 week |
| **3. Configuration and persistent staging** | The scheduler inside the service; templates, schedules, settings (with a version: an older copy cannot overwrite a newer), users, audit, logs; persistent staging | The service is administered entirely from the browser | ~2-3 weeks |
| **4. Desktop as a client** *(optional)* | The desktop connects to a service through `IRunControl` instead of running its own engine | One engine per machine, two interfaces | later |

Total **7-9 weeks**, provisional until the slice (1.5) is done; phases 2-3 are estimated again then.

Tests: the application layer is tested as the engine is (`tests/Enactive.Engine.Tests`); the server gets a
test project with `WebApplicationFactory` for sign-in, roles, the audit log and the network limits, and bUnit
for the components that carry logic.

## Open questions

- **Viewing files.** The artifact viewer and the diff open files of the server; in the browser that is a view
  of the file, read through the service and limited to the workspace.
- **The log window and log analysis** come over as pages; the analysis calls a model, so it is an Operator's.
- **Upgrades** of the service while runs are going: stop accepting, let runs reach a checkpoint, restart -
  resume exists (checkpoints) and can be used here.
