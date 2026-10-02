# Remote Access

[Wiki home](README.md)

## What this is

Remote access lets a task be started from a browser and run on a computer where Enactive is
installed. The browser does not execute anything: it queues a request, the desktop application
picks it up, runs it through the same engine as a task typed into the app, and reports back.

Three parts:

| Part | Where it runs | What it does |
| --- | --- | --- |
| Gateway | `remote.enactive.dev`, or a server you deploy | Signs people in, serves the panel, stores and forwards encrypted tasks, commands and history |
| Panel | A browser: each one you add is a **device** | Starts tasks, shows their timeline, answers permission requests, holds keys |
| Host | Inside the desktop application | Connects outward to the gateway, runs the tasks, publishes the events |

The host **dials out**. Nothing listens on the computer running Enactive, and no inbound port is
opened on it. A computer that is switched off is simply not available; the task waits in the queue
or its start command expires.

What you write and what your computer reports back is encrypted on your computer and on your
devices before it reaches the gateway, which stores it and passes it on but cannot read it. Signing
in proves who the account is; reading and commanding a computer needs a key that only that computer
and the devices it trusts hold. What the gateway can and cannot see, and where that protection
stops, is in [Remote security](Remote-Security.md).

## Signing in

Open the gateway's address and choose **Continue with GitHub** or **Continue with Google**. The page
offers only the providers the gateway has configured. The gateway keeps the provider's id for your
account and its display name; it asks for no email address and keeps no provider token.

Each way of signing in is its own account. A GitHub sign-in and a Google sign-in are not joined, so
use the same one every time.

**The beta.** A new identity cannot create an account until the operator has approved it. Until
then the sign-in page says *"Your sign-in is recorded; access is opened by hand during the beta."*
The service has no email address to tell you with, so sign in again later. Other answers you may
see:

| The page says | What it means |
| --- | --- |
| This sign-in was not given access to this gateway. | The operator turned this identity away |
| This account has been disabled on this gateway. | The operator stopped the account; its sessions and queued commands are gone |
| The sign-in did not complete. Try again. | The provider round trip failed |

A sign-in lasts at most 8 hours. The account menu (your name, top right) has **Sign out** for this
browser and **Sign out everywhere** for every session of the account.

The first sign-in in a browser makes that browser's **device key**. Its private half is kept in the
browser and cannot be exported; the gateway receives only the public half. Until the device is
paired with a computer or added by another device, it sees the lists but marks every task, step and
workspace name with *"This device has not been given the key for this."*

## Pairing a computer

Pairing connects a computer to your account and makes the browser you pair it from its first
trusted device.

1. In the panel, open **Computers** → **Register**, type a name, and press **Create connection
   code**. The name is not encrypted: the service sees it, and the dialog says so.
2. The dialog shows a **connection code** beginning `enactive-connect:`. It carries the gateway's
   address, the computer's id and token, which browser to trust first, and a pairing secret that
   never reaches the gateway. Whoever holds it can connect a computer to this browser, so give it to
   nobody else. It is shown once and works for 24 hours. Keep the dialog open.
3. On the computer, open the desktop application: **Settings → Remote access**, paste the code into
   **Connection code**, and press **Connect**. The token is stored encrypted with your Windows
   account; the code itself is not kept.
4. The computer makes its first key and sends it to the browser, authenticated with the pairing
   secret. The browser checks it, closes the dialog and says *"Paired - this device can read and
   command &lt;name&gt;"*.

If the key that arrives does not verify, nothing is stored and the panel says *"This key was not
sent by your computer or a device you trust; it was ignored."* A code answered after its 24 hours,
or after the pairing finished, is reported as *"No code is waiting for this computer any more - make
a new one from Computers."*

**Test connection** in the same settings pane tries what is stored: it greets the gateway and
publishes this computer's workspaces, which is what makes them selectable in the panel and what
shows the computer as online.

The **Computers** list says, for each computer, whether this device can read it: nothing when it
holds the computer's current key, *"not paired with this device"*, or *"a newer key exists that
this device has not been given"*.

## Adding a phone or another browser

A device can read a computer only once something that already holds the computer's key gives it to
the device. There are two places to do that from.

### From a device you already use

1. In the panel on the trusted device, open **Devices** → **Add a device**. It shows an invitation
   link and the same link as a QR code. The link works once, for 10 minutes, and gives the device
   that opens it every computer's key this one holds — give it to nobody else.
2. Open the link on the new device (scan the QR code with its camera, or send yourself the link),
   **signed in to the same account**. If it is not signed in, the sign-in page says *"Sign in to the
   account you are adding this device to."*
3. The new device shows *"Waiting for your other device to share its keys…"*; the trusted one shows
   *"Sharing keys with &lt;label&gt;…"*, then *"Added &lt;label&gt;."* The new device ends with
   *"This device can now read &lt;computers&gt;."*

The secret in the link sits after the `#`, which a browser never sends to a server; the new device
removes it from the address bar once read. If anybody but your new device answers the invitation,
the trusted device says *"Someone other than your new device answered this invitation. Nothing was
shared."*

A trusted device that is behind on a computer's newest key says so before showing the link, and
does not share that computer's keys. Add the new device from that computer instead, or try again
once this device has caught up.

### From the computer

In the desktop application, **Settings → Remote access → Trusted devices → Add a device** opens a
window with the link and its QR code, for another browser signed in to the same account. It works
once, for ten minutes, and only while the computer is connected.

This way does not depend on any other browser's word, which is why it is the one to use when
something looks wrong, and the only one when no browser holds the computer's key any more.

## Removing a device

A removed device can still read what it has already opened. It reads nothing new once each
computer has moved to a new key.

**From the panel.** **Devices** → **Remove** on the device's card. The question is *"&lt;label&gt;
can still read what it has already opened. It will not read anything new. Continue?"* The gateway
refuses every call from that device at once - every call names the browser it comes from - and
every computer this browser holds the current key of is sent a sealed removal. The computer
distrusts the device, makes a new key and gives it to every device still trusted. The removed
browser's session is not ended: still signed in, it could register itself as a new device. If the
device is lost or stolen, also use **Sign out everywhere**.

The card then has one line per computer, saying only what the panel can see:

| Line | Meaning |
| --- | --- |
| told under key N | The removal was sealed under the key the computer uses now, N |
| key changed - told again under key N | The computer moved to a new key before acting, so the removal was sent again under it |
| key changed - waiting for key N to tell it again | The computer has a newer key this browser has not received yet |
| not confirmed - tell the computers again | The key kept changing; the removal was sent again three times and then stopped |
| not confirmed - this device never received the computer's new key; ... | No new key arrived within 10 minutes; do it from a device that holds the key, or from the computer |
| Cannot tell &lt;computer&gt; from this device - ... | This browser does not hold that computer's current key, so it cannot send it anything |

A computer acts only on what is sealed under its current key, so a removal sealed under an older key
is refused there; this is why the panel tells it again rather than assuming it worked. A removed
device keeps a **Tell the computers again** button, on every device and after a reload. Pressing it
sends the same removal again; a computer that already acted takes it as nothing new.

A removal waits up to 30 days for a computer that is off, and a full queue of other requests never
turns it away. If a computer has not collected it after 30 days, the inbox says *Removal not
delivered*: *"Your computer &lt;label&gt; never received the removal of a device - remove it again
when the computer is back."* Until then that computer still trusts the device.

**From the computer.** **Settings → Remote access → Trusted devices → Remove**. It asks *"&lt;label&gt;
will not read anything new. Every other device gets a new key. Continue?"* The gateway is told at
once, or when the computer next connects.

**Removing a computer.** **Computers** → **Revoke access**. Its connection closes and the commands
it had not collected are withdrawn; work it had already accepted may still be running on the
machine.

### Forgetting this device

On this browser's own card, **Forget this device** removes it from the account and from every
computer it can tell, deletes its keys from the browser, and signs it out. The sign-in page then says
*"This device was forgotten: its keys are deleted from this browser, and it will not read anything
new."* If the removal for a computer could not reach the gateway, the browser keeps its keys so the
removal can be sent again, and says that **Forget this device** can be pressed again. If the browser
would not let the keys be deleted (another tab of the site holding them open), it says so and asks
you to clear the site's data in the browser settings.

A browser that was removed from somewhere else shows *"This device was removed"* the next time it
calls the gateway, with **Delete this device's keys** and **Sign out**.

## When devices are lost

A lost browser is not removed by being lost. It still holds the computer's current key and stays on
the computer's trusted list, so until a removal reaches the computer it can read everything new and
send commands — start tasks, answer permissions — for as long as the gateway serves it. Replacing it
is not enough: remove it too.

| What is lost | What to do |
| --- | --- |
| One browser | From another browser, **Devices → Remove** on its card, then **Sign out everywhere**: the gateway refuses every call that names the removed browser, but its session stays signed in and could register itself as a new device until it is signed out. Sign in again afterwards. |
| Every browser | First, on the computer, **Add a device** (above): the computer still holds its keys and admits a new browser itself. Then remove each lost browser — on the computer under **Settings → Remote access → Trusted devices → Remove**, or under **Devices → Remove** on the new browser — and use **Sign out everywhere** to end their sessions. |
| The computer | Its history stays readable on the devices that hold its keys. Revoke it under **Computers**, then register the new computer afresh with a new connection code. |
| The computer and every browser | The history cannot be read by anyone, the service included. From a new browser, remove the lost browsers under **Devices → Remove**, revoke the old computer under **Computers → Revoke access**, and use **Sign out everywhere**: with no computer to tell, the gateway refusing them is what keeps them out. Then delete the account, or pair a new computer. |

## Deleting the account

Account menu → **Delete account…**. The question is *"This deletes your account and everything
stored for it on the service. Your computers stop. Continue?"*

Deleting needs a sign-in opened within the last **10 minutes**. An older session is signed out with
*"Sign in again, then delete the account from the menu."* — sign in, then delete.

Everything the gateway stores for the account goes in one step: computers, devices, tasks, history,
the security log, and the records of your first sign-in. The computers' connections are closed and
their registrations are gone with the account. This browser's keys are deleted afterwards. The
sign-in page then says *"Your account and everything stored for it on the service are gone. Backups
are kept for 30 days and then removed."*

## Taking your data out

Account menu → **Download my data** fetches everything the service stores for the account
(`GET /api/export`), at most once an hour. The browser opens every encrypted item it holds a key for —
tasks, run summaries, progress messages, notifications, permission requests and workspace names —
and names the reason for each one it cannot open. Command payloads and the keys sent to your browsers
stay sealed as stored: opened, a key would sit in clear in a file that gets copied around. The result
is offered as `enactive-export-<date>.json`.
The readable text is put together in the browser and is never sent anywhere.

## The security log

Account menu → **Security log**: sign-ins (which provider, not from where), devices added and
removed, invitations made, computers registered and removed, signing out everywhere, and the operator
disabling or enabling the account — the newest first. A row someone else caused says so: *"by the
operator"*, or *"by one of your computers"*. Entries are kept for 90 days.

## Limits

What one account may hold or start. These are the gateway's defaults; an operator can change them.

| Limit | Default |
| --- | --- |
| Computers | 5 |
| Devices | 10 |
| Runs in progress, across all computers | 3 |
| Requests waiting for one computer to collect them | 50 |
| Tasks made in the last 24 hours | 200 |
| Open invitations to add a device | 5 |
| Stored tasks and history (encrypted) | 200 MB |

A refusal names the limit and what frees it, in the shape *"This account already has 3 runs in
progress, the most it may; wait for one to end, or stop one, to start another."* Devices and
invitations have their own sentences of the same kind. Storage says *"This would take this account
past its 200 MB of stored tasks and history, the most it may; older runs are removed after 30
days."* What a computer reports about a run already started is always stored, even past the storage
limit, so that a run can always report its end; the next start is what is refused.

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
is that the sandbox threat model is justified by "the machine is
the developer's own, the projects are theirs", and a network origin is exactly what that argument
does not cover. It is enforced in two independent places because one alone would be a promise and
the other alone would be trusting the panel.

The staging rule exists because a staged run needs a store that keeps its proposals until somebody
applies them, and there is not one here. Running anyway would write to the files directly while the
history said the changes were staged, with nobody at the machine to notice.

## Permissions

A run that needs an approval publishes it, encrypted. The request appears **on the task's own
card** in the panel, with the whole action shown, and in the Permissions tab, which is the queue
across every computer and run. It appears on the desktop at the same time.

Both ends are asked and the first answer wins. Sitting down at the computer always works, whatever
the phone is doing.

The panel offers **Allow** only when the action it shows is the one the computer asked about: it
recomputes the action's fingerprint from what it opened and compares it with the one the computer
sent. Otherwise the card says *"This request does not match what the computer asked; answer it on
the computer."*, or *"This device cannot read this request; answer it on the computer."*

An answer given in the panel is not an outcome. It becomes a command that still has to reach the
computer, which may refuse it — the desktop may have answered first, or the run may have ended. The
card says *"Your answer is on its way to the computer."* until the computer has acted on it.

An unanswered request expires after two hours and is treated as a refusal. A step that waits
forever for an answer nobody can give is a hang, not a permission model.

## What the panel shows

| View | Contents |
| --- | --- |
| Tasks | Every run, its status, its prompt, and any permission it is waiting on |
| Permissions | The queue of unanswered requests across all computers |
| Inbox | Notices the runs produced, with an unread count |
| Computers | Registered computers, online or last seen, whether this device holds their key, and revocation |
| Devices | The browsers of the account, this one marked, with Add a device, Remove and Forget this device |

The page polls every three seconds and asks for a delta rather than the whole state. History older
than the configured retention (30 days by default) is deleted hourly, and the panel says so rather
than showing a truncated timeline as though it were complete.

A run's timeline is a modal opened from its card, and it updates while it is open.

## Connection behaviour

| Interval | What |
| --- | --- |
| 2 seconds | The host flushes queued events to the gateway |
| 15 seconds | The host re-publishes its workspace list and its liveness |
| 45 seconds | Without a sync for this long, the panel shows the computer as offline |
| up to 2 minutes | Backoff between retries when the gateway is unreachable |

Events are queued in a local SQLite outbox and delivered in order, so a run whose connection drops
mid-way reports the rest of itself when the connection returns rather than losing it. A run
interrupted by the application closing is reported as interrupted the next time the host connects.

The panel shows **Live** or **Stale** depending on whether its last poll succeeded.

## Limitations

These are current, deliberate, and worth knowing before relying on the feature.

- **The panel is code the gateway sends.** Altered code could take the keys from the browser. What
  the service does about it, and how to check, is in [Remote security](Remote-Security.md).
- **Metadata is visible to the gateway**: computer names, how many runs, when, their statuses and
  sizes. See [Remote security](Remote-Security.md).
- **No shells from the web**, as above. A task that genuinely needs a command line has to be run at
  the computer.
- **No staging from the web.**
- **One person per account.** There are no teams or sharing, and GitHub and Google sign-ins are not
  joined into one account.
- **Closing the desktop ends remote runs.** The host is part of the application, not a service.

## Setting up your own gateway

Server-side installation, the settings, the admission command line, backups and restore, the
automatic pull-based deploy and the cutover from protocol 1 are in
[REMOTE_OPERATIONS](../REMOTE_OPERATIONS.md); a short version is under
[Operations](Operations.md#running-the-remote-gateway). This page does not duplicate them.

## Implementation references

- [Deployment and operations runbook](../REMOTE_OPERATIONS.md)
- [Protocol contracts](../src/Enactive.Remote.Contracts/Messages.cs)
- [Gateway](../src/Enactive.Remote.Gateway/Program.cs)
- [Per-account limits](../src/Enactive.Remote.Gateway/Services/Limits.cs)
- [Account deletion](../src/Enactive.Remote.Gateway/Services/AccountDeletion.cs)
- [Panel: removing devices](../src/Enactive.Remote.Gateway/wwwroot/js/devices.js)
- [Panel: invitations](../src/Enactive.Remote.Gateway/wwwroot/js/invite.js)
- [Host connection and run loop](../src/Enactive.App.Ui/RemoteAccessService.cs)
- [Pairing on the computer](../src/Enactive.Remote.Host/Pairing.cs)
- [Shell rule for a remote run](../src/Enactive.Core/RemotePolicy.cs)
- [Permission handling across both ends](../src/Enactive.Remote.Host/RemoteDecisionHandler.cs)
- [Local outbox](../src/Enactive.Remote.Host/HostStore.cs)
