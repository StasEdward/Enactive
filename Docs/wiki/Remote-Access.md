# Remote Access

[Wiki home](README.md)

## What this is

Remote access lets a task be started from a browser and run on a computer where Enactive is
installed. The browser does not execute anything: it queues a request, the desktop application
picks it up, runs it through the same engine as a task typed into the app, and reports back.

Three parts:

| Part | Where it runs | What it does |
| --- | --- | --- |
| Gateway | A server you deploy | Serves the panel, holds the queue and the history, authenticates the owner and the computers |
| Panel | A browser | Starts tasks, shows their timeline, answers permission requests |
| Host | Inside the desktop application | Connects outward to the gateway, runs the tasks, publishes the events |

The host **dials out**. Nothing listens on the computer running Enactive, and no inbound port is
opened on it. A computer that is switched off is simply not available; the task waits in the queue
or its start command expires.

This replaces an earlier prototype that lived in `server/`. That directory no longer exists; its
files are kept for reference under [Docs/RemoteServerWWW](../RemoteServerWWW/README.md).

## Trust boundary

The gateway can see task text, workspace names, tool arguments and results. It is a trusted
gateway, not an end-to-end encrypted channel. Deploy it somewhere you would be willing to keep the
same information.

The panel authenticates with a single **owner key**. There are no user accounts: whoever has the
key is the owner. Each computer additionally holds its own **device token**, issued once when the
computer is added and revocable from the panel.

## What a remote run may and may not do

A task started from the web is not the same as a task typed into the app, and the difference is
enforced rather than described.

| Rule | Where it is enforced |
| --- | --- |
| No shell. `run_command` and `run_powershell` are denied for the whole run, at every autonomy tier including Autonomous | `RemotePolicy.ForRemoteRun` |
| A shell permission cannot be answered from the panel; it is published so the request is visible, then refused | `RemoteDecisionHandler`, and again in the gateway |
| A workspace set to stage changes cannot be run from the web | `RemoteAccessService` |
| Permissions and autonomy come from the workspace's own saved settings, not from whatever is selected on screen | `MainWindow.SnapshotEnvironment` |

The shell rule is absolute and has no exception for somebody being at the keyboard. The reasoning
is in [SANDBOX_PLAN](../SANDBOX_PLAN.md): the sandbox threat model is justified by "the machine is
the developer's own, the projects are theirs", and a network origin is exactly what that argument
does not cover. It is enforced in two independent places because one alone would be a promise and
the other alone would be trusting the panel.

The staging rule exists because a staged run needs a store that keeps its proposals until somebody
applies them, and there is not one here. Running anyway would write to the files directly while the
history said the changes were staged, with nobody at the machine to notice.

## Permissions

A run that needs an approval publishes it. The request appears **on the task's own card** in the
panel, with the whole action shown, and in the Permissions tab, which is the queue across every
computer and run. It appears on the desktop at the same time.

Both ends are asked and the first answer wins. Sitting down at the computer always works, whatever
the phone is doing.

An answer given in the panel is not an outcome. It becomes a command that still has to reach the
computer, which may refuse it — the desktop may have answered first, or the run may have ended. The
card says `Sent` until the computer has acted on it.

An unanswered request expires after two hours and is treated as a refusal. A step that waits
forever for an answer nobody can give is a hang, not a permission model.

## Setting it up

### 1. Deploy the gateway

Server-side installation, the systemd units, the database accounts, TLS through Cloudflare Tunnel,
backups and the automatic pull-based deploy are all in
[REMOTE_OPERATIONS](../REMOTE_OPERATIONS.md). That document is the runbook; this page does not
duplicate it.

### 2. Sign in to the panel

Open the gateway's address and enter the owner key configured as `ENACTIVE_OWNER_KEY`.

### 3. Add the computer

In the panel, add a computer. The gateway issues a device token **once** and does not store it in a
form it can show again. Copy it at that moment.

### 4. Connect the desktop application

In Enactive, open Settings → Remote access, enter the gateway address and the device token, and
enable it. **Test connection** verifies the address and the token and publishes this computer's
workspaces, which is what makes them selectable in the panel.

The token is stored encrypted with the operating system's user-level protection, alongside the
provider keys. Settings changes take effect without restarting the application.

### 5. Start a task

Choose a computer and one of its published workspaces, write the task, and start it. The prompt is
shown back in the panel exactly as it was typed — judging what a run did against a title somebody
wrote is judging it against the wrong thing.

## What the panel shows

| View | Contents |
| --- | --- |
| Tasks | Every run, its status, its prompt, and any permission it is waiting on |
| Permissions | The queue of unanswered requests across all computers |
| Inbox | Notices the runs produced, with an unread count |
| Computers | Registered computers, last seen, and revocation |

The page polls every three seconds and asks for a delta rather than the whole state. History older
than the configured retention (30 days by default) is deleted hourly, and the panel says so rather
than showing a truncated timeline as though it were complete.

A run's timeline is a modal opened from its card, and it updates while it is open.

## Connection behaviour

| Interval | What |
| --- | --- |
| 2 seconds | The host flushes queued events to the gateway |
| 15 seconds | The host re-publishes its workspace list and its liveness |
| up to 2 minutes | Backoff between retries when the gateway is unreachable |

Events are queued in a local SQLite outbox and delivered in order, so a run whose connection drops
mid-way reports the rest of itself when the connection returns rather than losing it. A run
interrupted by the application closing is reported as interrupted the next time the host connects.

The panel shows **Live** or **Stale** depending on whether its last poll succeeded.

## Limitations

These are current, deliberate, and worth knowing before relying on the feature.

- **One owner.** There are no accounts, roles or sharing. The key is the product's whole
  authorization model.
- **A trusted gateway.** Task text and results are visible to it.
- **No shells from the web**, as above. A task that genuinely needs a command line has to be run at
  the computer.
- **No staging from the web.**
- **Data Protection keys are the session.** Whoever can read the gateway's key directory can mint a
  session cookie. This is protected by directory permissions and nothing else in this build.
- **Not designed for a shared or hostile network origin** beyond the rules listed above.

## Implementation references

- [Design and staged plan](../REMOTE_DESIGN.md)
- [Deployment and operations runbook](../REMOTE_OPERATIONS.md)
- [Protocol contracts](../src/Enactive.Remote.Contracts/Messages.cs)
- [Gateway](../src/Enactive.Remote.Gateway/Program.cs)
- [Host connection and run loop](../src/Enactive.App.Ui/RemoteAccessService.cs)
- [Shell rule for a remote run](../src/Enactive.Remote.Host/RemotePolicy.cs)
- [Permission handling across both ends](../src/Enactive.Remote.Host/RemoteDecisionHandler.cs)
- [Local outbox](../src/Enactive.Remote.Host/HostStore.cs)
