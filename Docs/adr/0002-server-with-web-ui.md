# Enactive can run as a service on the local network, with a web UI

Status: **proposed**, 2026-10-11.

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

The view models that carry no Avalonia (`RunTimeline`, `RunRouting`, `ModelPerformance`, `ModelWorkSplit`,
`StepCardViewModel`...) move with it, so both interfaces show the same figures from the same code.

### 3. The web UI is Blazor Server

The interface runs on the server and the browser receives only what changed, over SignalR.

- It is live without polling: a step card or the model figures change when the event arrives.
- It reuses the C# above directly: no JSON API to design for the interface's own needs, no second copy of the
  folding and formatting logic in JavaScript - which is how the window and the panel would drift.
- The cost - a connection held per open page, the server doing the rendering - is nothing on a local network
  with a handful of users.

Rejected: **Avalonia in the browser (WASM)** - the engine cannot run in a browser, so it needs the API anyway,
and every window would have to become a view; **a React/TypeScript SPA** - an API for every screen and the
logic written twice; **growing the remote panel** - its limits are deliberate (above).

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

### 5. Reachable on the network, not from it at large

- HTTPS only, with a certificate configured in the service's settings (the corporate CA's, or a self-signed
  one whose fingerprint the users check once). No plain HTTP listener.
- It listens on configured addresses and answers only the configured subnets; the installer adds a Windows
  Firewall rule for the domain/private profiles, not the public one.
- Anti-forgery and same-origin checks are Blazor's own; cookies are `Secure`, `HttpOnly`, `SameSite=Strict`.

### 6. What a run from the web may do

A person in the browser is a person at a screen: they see the run and answer its permissions, so a run started
from the web runs under the workspace's own policy, as one typed into the app does - **including the shell**.
That is the difference from remote access, and it is why the account, the role and the network limits above
are not optional: an Operator account on this service is a shell on the server, as the service's user.

Staging works: the service keeps the proposals, and someone applies them from the browser.

### 7. The service runs as a user, not as LocalSystem

Secrets - API keys, the SMTP password, MCP tokens - are protected with DPAPI for the **current user**
(`Enactive.Secrets/Secret.cs`). A service running as LocalSystem cannot read the ones saved by a person, and
would not have their git credentials or rights on the workspaces either. The service runs under a named
account - a dedicated one, or the owner's - and its secrets are entered under that account (the settings
screens of the web UI do that).

### 8. One owner of the data

`settings.json`, the templates and every workspace's `.enactive` database belong to one process. The service
takes a lock on the data folder at start; the desktop app on the same machine, finding it taken, says so and
does not start its own engine. Phase 4 lets the desktop connect to the service instead.

### 9. What the window did that a service does instead

- **Schedules** run in the service: it is up all the time, which the window and `--due` from Task Scheduler
  only approximated.
- **Tray notices** become the inbox in the web UI, and mail.
- **Paths are the server's**: a workspace is a folder on the server, chosen with a folder browser of the
  server's disks, limited to configured roots.

## Plan

Each phase ends with something usable and keeps the desktop working.

| Phase | Content | Done when | Estimate |
| --- | --- | --- | --- |
| **0. Application layer** | `Enactive.Application`: the run control out of `MainWindow.axaml.cs`; the Avalonia-free helpers and view models moved in; the desktop switched to it | The desktop behaves as before; the engine tests pass; `MainWindow.axaml.cs` keeps only the window | ~1 week |
| **1. Service and reading** | `Enactive.Server`: Windows service, HTTPS, accounts and roles, `--add-user`, audit log, data-folder lock; Blazor shell with the brand; run history and a run's page (timeline, step cards, routing, model performance, artifacts) | A Viewer on another machine signs in and reads a run as the app shows it | ~1 week |
| **2. Running** | Start a task (free text or template), the live run, cancel, permissions answered in the browser, the inbox, staging applied from the browser | An Operator runs a task end to end from another machine, answering its permissions | ~1 week |
| **3. Configuration** | Templates and their editor, schedules (the scheduler inside the service), settings: providers, workers, phases, MCP, SMTP, web, writable folders; workspaces | The service is administered entirely from the browser | ~2 weeks |
| **4. Desktop as a client** *(optional)* | The desktop app connects to a service instead of running its own engine | One engine per machine, two interfaces | later |

Tests: the application layer is tested as the engine is (`tests/Enactive.Engine.Tests`); the server gets a
test project with `WebApplicationFactory` for sign-in, roles, the audit log and the network limits, and bUnit
for the components that carry logic.

## Open questions

- **Several people at once.** The window has one foreground run. On a server each signed-in person needs their
  own "current run"; the background runs and `MaxParallelSteps` are shared. Per-person slots, or one queue?
- **Viewing files.** The artifact viewer and the diff open files of the server; in the browser that is a view
  of the file, read through the service and limited to the workspace.
- **The log window and log analysis** come over as pages; the analysis calls a model, so it is an Operator's.
- **Upgrades** of the service while runs are going: stop accepting, let runs reach a checkpoint, restart -
  resume exists (checkpoints) and can be used here.
