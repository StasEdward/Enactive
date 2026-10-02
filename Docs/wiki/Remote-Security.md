# Remote Security

[Wiki home](README.md)

## In short

Remote access is end-to-end encrypted between the desktop application and the browsers you add as
devices. The gateway stores and forwards what they send without being able to read it, and it
cannot command a computer. It does see who uses it, when, and how much — the metadata it needs to
deliver anything at all.

Three things are outside that protection, and they are stated here rather than discovered later:
the panel is code the gateway sends ([the JavaScript limit](#the-javascript-limit)), the metadata is
visible, and a device that was compromised when it invited another can mislead that one
([the invitation limit](#the-invitation-limit)).

How to pair, add and remove devices is in [Remote access](Remote-Access.md). The service's privacy
page, `/privacy.html` on the gateway, covers the same ground; it is marked as a draft until the
operator approves it.

## What the service cannot read

- The titles and the text of your tasks.
- What a run reports while it works, and its summary when it ends.
- The permission requests your computer sends you: which tool, with what arguments, in which
  folder, and the full text of the question.
- The details of your notifications.
- The names of your workspaces.

All of this is stored only as encrypted data (AES-256-GCM). The keys that open it are made on your
computer and on the devices you have added, and they never reach the service. A leak of the
database, of a backup or of the server's disk shows the encrypted data and nothing more; the same
holds for anyone who runs the service.

Each encrypted value is bound to the record around it — the computer, the run, the step, the kind of
record — so moving it to another record makes it fail to open rather than show up somewhere else.
The panel then says *"this does not open - it may have been altered"*.

**One exception**, which makes answering a permission request safe: next to each request the service
keeps a SHA-256 fingerprint of the exact action (the run, the tool call, the tool, the folder and the
arguments), so that your browser can check that the action it shows you is the one your computer
asked about. The fingerprint cannot be turned back into the action, but someone who guesses the
action exactly could confirm the guess.

## What the service can see

To deliver anything, the service has to know who you are, which devices are yours, and what goes
where. It keeps:

| Record | What is in it |
| --- | --- |
| Account and sign-in | The name shown for the account; for each way you sign in, the provider, the provider's id for you, and the display name the provider gives it |
| Sessions | When each began, when it ends, whether it was ended early |
| Devices | Each one's public key; a label made from the browser's and operating system's names (such as "Firefox on Windows"); when it was added, last seen and removed |
| Computers | The name you give each one (not encrypted, and the panel says so where you type it); a fingerprint of its connection token, not the token; when it was last seen; which generation of its keys it is on |
| Workspaces | Their ids, not their names |
| Tasks, runs, commands, permission requests, progress messages, notifications | Ids, kinds, statuses and times, and the size of their encrypted contents |
| Invitations | When each was made, when it expires, whether it was used |
| Keys sent to devices | Encrypted, so that only the device they are for can open them |
| Security log | Sign-ins, devices and computers added and removed, operator actions; kept 90 days |

Put plainly: the service knows that you use it, how many computers and devices you have and which
browsers and operating systems they are, when you run tasks and how many, how they ended, how many
permission requests they raised, how much encrypted data they produced, and the names you give your
computers.

It does not store an IP address with the account or in the security log (rate limits hold an address
in memory for a few minutes), asks the providers for no email address, has no password of its own, and
keeps no GitHub or Google token. The service is reached through Cloudflare, which ends the HTTPS
connection and so sees what the service sees on the way through — but not what is encrypted end to
end.

## Commands the service cannot forge

Starting a task, cancelling a run, answering a permission, and removing or endorsing a device are
**commands** to a computer. Each is sealed by the device that sends it with the computer's current
key, which the gateway does not have, and bound to the computer, the command's id and its kind. The
computer acts only on a command that opens under its current key, and refuses one sealed more than 24
hours (plus 10 minutes of clock difference) ago, or 30 days for removing or endorsing a device, which
must still reach a computer that was off for a weekend — so the gateway can neither make a command up nor
replay an old one.

The keys themselves reach a device as **grants**, each wrapped for one device's public key and
authenticated in one of two ways: with the pairing secret, which travels only inside the connection
code or after the `#` of an invitation link and is never sent to the gateway; or with the computer's
own **signing key**, whose private half never leaves the computer. A device remembers a computer's
signing key at the first grant it accepts from it, and refuses any later grant that names another. The
gateway can wrap a key of its own for any device; it cannot make one that verifies.

**If someone takes over your GitHub or Google account**, they can sign in and see what the service
sees. They cannot read your tasks or send anything to your computer: that needs a device key. They
can remove your computers and devices, sign out every session, and delete the account. Secure the
provider account first, then sign in and use **Sign out everywhere**, and add back what was removed.

## Key rotation and what a removed device keeps

Each computer has a key per **epoch**, numbered from 1. Removing a device moves every computer to the
next epoch:

1. The gateway marks the device removed: it refuses every call that names the device and stops
   serving it keys. Every call of the panel names the browser it comes from, and one that names none
   is refused, so leaving the name off is no way round it. Every session the device was used through
   ends with it, so it cannot register itself as a new device either. Whoever can still sign in to
   the account at the provider can sign in again, from any browser - secure the provider account
   first.
2. Each computer receives the sealed removal, distrusts the device, makes a new key, and sends it to
   every device still trusted, signed with its signing key.
3. From then on the computer seals with the new key.

A removed device **keeps** what it already opened and the keys of the epochs before its removal. It
cannot read anything sealed under a later key, and it cannot make a grant that other devices accept:
no epoch key signs a grant. Between the removal and the computer acting on it — while the computer is
switched off, say — what keeps the device out is only the gateway refusing it. That is why the panel
reports, for each computer, whether it was told under its current key, and why **Tell the computers
again** exists ([Removing a device](Remote-Access.md#removing-a-device)).

The signing key is made once per computer and never rotated: a key derived from an epoch key could
not do its job, because the device being removed holds that epoch key.

## The invitation limit

A device that admits another by invitation also tells it which signing key the computer has. So a
device that was already compromised when it invited can make the device it admitted trust a key it
controls, and go on reading what that device sends even after it is itself removed.

Two things limit this. A device admitted that way sees the computer's real key change arrive under
another signing key and says so: *"&lt;computer&gt; is presenting a different identity than the one
this device trusts. Remove this device and add it again from the computer (desktop: Add a
device)."* And adding a device **from the computer** never depends on another browser's word. If you
have reason to doubt one of your devices, add new ones from the computer.

The invitation itself is protected against the gateway: the new device proves it holds the
invitation's secret when it answers, so the gateway cannot swap in a public key of its own. If the
answer does not verify, the inviting device shares nothing and says *"Someone other than your new
device answered this invitation. Nothing was shared."*

## The JavaScript limit

The panel holds the keys in the browser, and the panel is code the gateway sends. An operator who
sent altered code — or someone who had taken over the server, or Cloudflare — could take the keys
from the browser. No web page can prevent this; it is the limit of any end-to-end encryption
delivered over the web. A compromised computer or browser is outside the protection for the same
reason: whatever can read your screen or the browser's storage can read what you read there.

What the service does is make an alteration visible.

- It publishes the SHA-256 of every script and stylesheet it serves at
  `/.well-known/enactive-panel.json`.
- The build pipeline makes the same list from the files a build ships, where the server cannot
  reach it, and publishes it in the summary of every build run (**Panel fingerprints**), in the notes
  of every tagged release, and as `panel-manifest.json` attached to the release.
- The two lists are byte for byte the same format, so comparing them is a file comparison.

### Comparing the panel with a release

Compare against the release the operator says is running, and use the `panel-manifest.json`
attached to that release rather than a copy pasted from a page: the list has no newline after its
closing `}`, and a pasted copy usually gains one, which makes `cmp` report a difference that is not
there.

```bash
# What the server says it sends, against the panel-manifest.json attached to the release it runs.
curl -s https://remote.enactive.dev/.well-known/enactive-panel.json > served.json
cmp served.json panel-manifest.json && echo "the panel is the build"

# What a file actually is, against the server's own list: hash what you were sent.
curl -s https://remote.enactive.dev/js/keystore.js | sha256sum
grep '"/js/keystore.js"' served.json
```

`deploy/panel-manifest.sh <wwwroot>` prints the list for a directory of panel files, so anyone with a
published build can reproduce it themselves. A difference names a file that is not what the build
shipped. A cached file is a difference too, so reload and check again before treating it as an
alteration.

This is a check, not a guarantee. It shows what the server sends to whoever asks, and a server that
sent altered code only to some people would pass a check made from somewhere else. The desktop
application does not yet check the panel for you.

## Implementation references

- [Panel fingerprints](../src/Enactive.Remote.Gateway/PanelAssets.cs)
- [The manifest script](../../deploy/panel-manifest.sh)
- [Grants and their verification](../src/Enactive.Remote.Gateway/wwwroot/js/grants.js)
- [The panel's key store](../src/Enactive.Remote.Gateway/wwwroot/js/keystore.js)
- [Sealing and command freshness on the computer](../src/Enactive.Remote.Host/Sealer.cs)
- [The service's privacy page](../src/Enactive.Remote.Gateway/wwwroot/privacy.html)
- [Operating the panel comparison](../REMOTE_OPERATIONS.md#9-comparing-the-panel-with-its-build)
