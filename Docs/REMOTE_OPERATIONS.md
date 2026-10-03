# Enactive Remote — running it

The last stage of the remote-access design. What that stage is allowed to claim is *"it can be run by someone
other than the person who wrote it"*, so this is written for that person: what to install, what
each setting means, what to do when it breaks, and what has deliberately been left manual.
What the people using it do — signing in, pairing a computer, adding and removing devices — is in
[the remote access guide](wiki/Remote-Access.md), and what the service can and cannot see in
[remote security](wiki/Remote-Security.md).

This has been done, once, on the machine it describes. `remote.enactive.dev` serves this gateway;
the preview it replaced is disabled rather than deleted, because that is the rollback.

The domain is `.dev`. An earlier draft of this document said `.com` throughout, which is what the
project was described as using, and the tunnel routes only `.dev` — so every check against `.com`
came back empty from the catch-all `http_status:404` rule and looked like a gateway that was not
answering. If `.com` is ever meant to serve this too, it needs its own hostname in the tunnel.

Three things in these instructions were wrong and were found by somebody following them rather than
by any test: a `reload` that cloudflared's unit does not implement, scripts that cared which
directory they were started from, and "find the old service by name" when both services have
`enactive` in their names. They are fixed below, and each says why, because the next person will
meet the same machine.

---

## 1. The shape

```
browser ──HTTPS──> Cloudflare edge ──tunnel──> cloudflared ──HTTP──> gateway (127.0.0.1:5099)
                                                                        │
desktop Host ──HTTPS/WebSocket──> (same path) ──────────────────────────┘
                                                                        │
                                                                   MySQL 8.0
```

Three facts follow from the tunnel and they are the reason most of this document exists.

**Nothing on the server listens publicly.** `cloudflared` connects outward. There is no inbound
port to firewall, and no certificate on the box.

**Every request arrives from 127.0.0.1.** The real caller is in `CF-Connecting-IP`, which
Cloudflare writes itself and strips from whatever the client sent. The gateway reads it and treats
it as the client address — for the sign-in rate limiter above all, which would otherwise put every
visitor on earth in one bucket, so that one caller's sign-ins could turn everybody else away.

**That trust is only sound while cloudflared is the only thing that can connect.** So the gateway
**refuses to start** if `ENACTIVE_BEHIND_TUNNEL` is on and it is bound anywhere but loopback. This
is not advice in a runbook; it is a check in `Deployment.RequireLoopbackListeners`, and there are
tests for both halves. If you ever put a different proxy in front of this, that check is the thing
to read first.

---

## 2. Settings

All of them are environment variables. The ones that are secret or that differ per deployment live
only in `/etc/enactive-remote/gateway.env`, owned by `enactive`, mode `0600`; `deploy/gateway.env.example`
is that file with every setting named and explained, and `first-install.sh` starts from it. They are
**not** in the systemd unit: a unit file is world-readable and `systemctl cat` prints it to anybody
who asks. The file is read by systemd, not by a shell — one `NAME=value` per line, nothing quoted —
so it cannot be sourced with `.`.

| Variable | Required | What it is |
|---|---|---|
| `ENACTIVE_REMOTE_DB` | yes | MySQL connection string, `Database=enactive_remote_v2`. The gateway refuses to start without it rather than finding out on the first request, by which time it has already told somebody it was healthy. |
| `ENACTIVE_PUBLIC_ORIGIN` | with a provider | The address people use, e.g. `https://remote.enactive.dev`: scheme, host and nothing more. The providers send people back to it. |
| `ENACTIVE_GITHUB_CLIENT_ID`, `ENACTIVE_GITHUB_CLIENT_SECRET` | a pair | From a GitHub OAuth app whose callback URL is `<origin>/auth/github/callback`. Both or neither: half a pair stops the start. |
| `ENACTIVE_GOOGLE_CLIENT_ID`, `ENACTIVE_GOOGLE_CLIENT_SECRET` | a pair | From a Google OAuth client whose redirect URI is `<origin>/auth/google/callback`. Both or neither. |
| `ENACTIVE_ADMISSION` | no | `list` (the default): only identities the operator approved may create an account — §3.1. `open`: anyone who signs in. What stands between an open service and one stranger filling it is the `ENACTIVE_LIMIT_*` values below — the gateway refuses `open` only with no limits at all, which configuration cannot produce (an unset limit is its default). It stays off until the privacy and terms pages are approved. Anything else stops the start. |
| `ENACTIVE_LIMIT_*` | no | What one account may use; the table below. Each a whole number of at least 1, or the start stops naming it. |
| `ENACTIVE_DEV_SIGNIN` | **never** | A sign-in without a provider, for tests. The gateway refuses to start with it outside Development. |
| `ENACTIVE_GITHUB_BASE`, `ENACTIVE_GITHUB_API`, `ENACTIVE_GOOGLE_AUTHORITY` | **never** | Move the providers to another host, for tests against a fake. Honoured anywhere else, whoever ran that host could sign in as anybody, so outside Development they stop the start. |
| `ENACTIVE_BEHIND_TUNNEL` | yes here | `true`. See §1. Set in the unit. |
| `ASPNETCORE_URLS` | yes here | `http://127.0.0.1:5099`. Anything not loopback and the process will not start. Set in the unit. |
| `ENACTIVE_DATA` | yes | Where the Data Protection keys live: `/var/lib/enactive-remote`, `0700`. Set in the unit. |
| `ENACTIVE_RETENTION_DAYS` | no | Default 30. Events, notices and runs that ended longer ago are deleted hourly, with what they own, and the panel says so. |

The limits, with the gateway's defaults — guesses at what one person uses, to be revised from
measurement. A refusal names the limit to the person who met it.

| Variable | Default | What it limits |
|---|---|---|
| `ENACTIVE_LIMIT_HOSTS_PER_USER` | 5 | Computers connected to one account. |
| `ENACTIVE_LIMIT_DEVICES_PER_USER` | 10 | Browsers and phones holding the account's keys. |
| `ENACTIVE_LIMIT_ACTIVE_RUNS_PER_USER` | 3 | Runs started and not yet ended, across the account's computers. |
| `ENACTIVE_LIMIT_QUEUED_COMMANDS_PER_HOST` | 50 | Commands waiting for one computer to collect them. Removals and endorsements of devices are counted apart, up to twice the devices limit, so a full queue never refuses a removal. |
| `ENACTIVE_LIMIT_TASKS_PER_DAY` | 200 | Tasks one account makes in the last 24 hours. |
| `ENACTIVE_LIMIT_OPEN_INVITES_PER_USER` | 5 | Invitations to add a device that are not yet used or expired. |
| `ENACTIVE_LIMIT_SEALED_BYTES_PER_USER` | 209715200 | Bytes of sealed content one account keeps (200 MiB). Retention gives them back. |

**The Data Protection keys are the session.** Lose them and every browser is signed out; copy them
and whoever has the copy can mint a session cookie. They are protected by the directory's mode and
by nothing else in this build — this is stated rather than fixed, and it is the first thing to
change if the threat model ever grows. `backup.sh` keeps them beside each dump (§5) for the same
reason the mode matters: the archive is `0600` and the backup directory is the service account's.

The deploy timer reads its own file, `/etc/enactive-remote/deploy.env` (§4.4). Besides the repository,
branch and token, it takes `ENACTIVE_DEPLOY_DATABASE`: the database whose schema version a release is
compared with, `enactive_remote_v2` when unset. It is set to `enactive_remote` only on a protocol-1
server between step 0 of the cutover and step 3 (§8).

---

## 3. Installing it the first time

On a fresh machine, `deploy/first-install.sh` does all of this and stops where a person is needed:
the first run writes `gateway.env` from `gateway.env.example` and stops until a sign-in provider is
filled in. A machine already running protocol 1 is not a fresh machine — that is §8.

By hand, the same steps:

```bash
# 1. The account and its directories.
sudo useradd --system --home /opt/enactive-remote --shell /usr/sbin/nologin enactive
sudo install -d -o enactive -g enactive -m 0755 /opt/enactive-remote
sudo install -d -o root    -g enactive -m 0750 /etc/enactive-remote

# 2. The database accounts. Read deploy/grants.sql first — it explains why the gateway's account
#    may create tables but not databases — then set real passwords.
mysql -u root -p < deploy/grants.sql
mysql -u root -p -e "ALTER USER 'enactive_gateway'@'localhost' IDENTIFIED BY '...'"
mysql -u root -p -e "ALTER USER 'enactive_backup'@'localhost'  IDENTIFIED BY '...'"
mysql -u root -p -e "CREATE DATABASE IF NOT EXISTS enactive_remote_v2
                     CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"

# 3. The settings: the example, with the gateway's password in ENACTIVE_REMOTE_DB, the public
#    origin, and the client id and secret of at least one OAuth app (§2 says where each comes from).
sudo install -o enactive -g enactive -m 0600 deploy/gateway.env.example /etc/enactive-remote/gateway.env
sudoedit /etc/enactive-remote/gateway.env

# 4. The service.
sudo cp deploy/enactive-remote.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now enactive-remote
curl -fsS http://127.0.0.1:5099/health
```

Then point the Cloudflare tunnel's ingress at `http://127.0.0.1:5099` — either in cloudflared's
`config.yml`, or in Cloudflare Zero Trust under **Networks → Tunnels → Public hostnames** if the
tunnel is managed from there, in which case the file on the machine decides nothing and editing it
will look like a change that did not take.

```bash
sudo systemctl restart cloudflared      # restart, NOT reload: this unit has no reload
curl -s https://remote.enactive.dev/health
```

Check it from outside, not from the box. `curl` against `127.0.0.1:5099` proves the gateway is up
and says nothing about which service the tunnel is pointed at, which is the thing being changed.

Then stop whatever was serving the site before — and find it **by the port it listens on**, not by
searching unit names:

```bash
sudo ss -ltnp | grep -v ':5099 '        # the old service is on some other port
sudo systemctl status <the pid that printed>
sudo systemctl disable --now <the unit it names>
```

This gateway's unit is `enactive-remote`. Whatever it replaces is likely to be called something
close enough that `systemctl list-units | grep -i enactive` matches both, and the first person to
follow these instructions disabled this one. A port is unambiguous; a name is not.

### 3.1 Admitting people: the admission CLI

With `ENACTIVE_ADMISSION=list` an identity nobody has approved is told to wait when it signs in, and
is listed for the operator. Deciding is a command on the same binary, run on this machine against the
same database; every change is written to the audit trail as actor `operator`.

It needs the gateway's environment, and that file cannot be sourced (§2), so the command runs the way
the service does — systemd reads the file:

```bash
admin() {
  sudo systemd-run --quiet --pipe --wait --uid=enactive \
       --property=EnvironmentFile=/etc/enactive-remote/gateway.env \
       /usr/bin/dotnet /opt/enactive-remote/current/Enactive.Remote.Gateway.dll admin "$@"
}

admin admissions                    # the identities waiting: provider:subject, name, when they asked
admin approve github:12345          # may create an account at the next sign-in
admin refuse google:1098...         # turned away; does not stop an account that already exists
admin disable <userId>              # stops an account: sign-ins, sessions, undelivered commands
admin enable <userId>               # lets a disabled account sign in again
admin sessions revoke <userId>      # signs the account out everywhere
```

Exit status 0 is done, 1 is understood and impossible (no such account), 2 is not understood — which
prints the usage. An operator may approve an identity before it ever signs in. The first approval on a
new installation is the operator's own: sign in, then `admin admissions`, then `admin approve`.

---

## 4. Deploying a new version

`enactive-deploy.timer` checks every ten minutes and installs the newest **green** build of the
tracked branch. It is a PULL: this server is on a private address and nothing on the internet can
reach it, so there is no pipeline that could push a release in, and the credential it holds is
read-only.

It does **not** install a release that carries a migration. That distinction is the whole design,
and §4.2 is why.

It has been run on the real machine and installed a real release end to end: found the push run,
matched the artifact by name, asked the build for its schema version (2, against a database at 2),
swapped the symlink, restarted the gateway through the polkit rule, and passed the health check —

```
Fetching 124e455a4b00 from run 34346160780.
Installing 124e455a4b00 (schema 2, database at 2).
Deployed 124e455a4b00.
```

What that leaves **unproven** is every branch it did not take: the parked-migration path, the
rollback after a failed health check, and pruning on a real tree. Those are covered by
`deploy/pull-release.test.sh` and by nothing else yet.

### 4.1 What a run does

1. Asks GitHub for the newest run of `build.yml` on the branch with `status=success` **and
   `event=push`**. Success is asked of GitHub rather than inferred from an artifact existing — a run
   can upload one and then fail a later step, and "there is a build" is not "the tests passed".

   `event=push` is not a detail. The artifact is named for `github.sha`, which equals the run's
   `head_sha` only on a push; on a `pull_request` run `github.sha` is the merge commit GitHub
   builds, a commit that exists nowhere in the branch. Once a PR is open every push produces both
   runs and the `pull_request` one is often newer, so without this filter deployment stops the day
   a PR is opened and resumes the day it is merged, with nothing in the log to say why. It is the
   first thing this actually did.
2. Stops if that commit is already the one in `current/.commit`.
3. Downloads `gateway-<commit>` into `/opt/enactive-remote/releases/<timestamp>-<commit>`.
4. Asks the **new build** what schema version it carries, by running it with `--schema-version`.
   The build answers for itself: migrations are embedded resources, so an unzipped release has no
   `.sql` files to count, and a number the pipeline wrote into a manifest is a claim about the
   assembly that nothing keeps true.
5. Asks the new build **and the running one** what protocol they speak, with `--protocol-version`.
   Different → parks it (§4.2) before anything else is looked at. A build that does not answer is the
   protocol-1 gateway, which has no such switch, and counts as 1.
6. Compares the schema version with `MAX(version)` in `schema_version` of `enactive_remote_v2`, read
   with the read-only backup account.
7. Same version → swaps the symlink, restarts, and polls `/health`. Higher → parks it (§4.2).
8. Keeps the last five releases, never removing the one that is running.

A parked release is downloaded once: the next run finds it by its commit and does not fetch it again.

Watching it, or running one on demand:

```bash
systemctl list-timers enactive-deploy.timer
sudo systemctl start enactive-deploy          # check now rather than waiting
journalctl -u enactive-deploy -n 50 --no-pager
cat /opt/enactive-remote/current/.commit      # which build is on the server
```

### 4.2 Migrations are parked, not applied

A release whose schema version is ahead of the database is downloaded, checked, and left
**uninstalled**, with the reason on stderr — so it lands in the journal as an error and
`systemctl status enactive-deploy` shows it. `/opt/enactive-remote/PARKED` names the directory.

The reason is not caution for its own sake. MySQL cannot roll DDL back, so a schema change is the
one step whose failure putting the old files back does not undo, and the person who would have to
repair a half-applied schema should be looking at it when it is applied. That was the reason
deployment used to be manual altogether; it still holds for migrations and no longer holds for
anything else.

Install a parked release while watching:

```bash
sudo -u enactive /opt/enactive-remote/deploy/backup.sh
sudo -u enactive /opt/enactive-remote/deploy/verify-restore.sh     # the backup is only worth what a restore proves
sudo -u enactive /opt/enactive-remote/deploy/pull-release.sh --allow-migration
```

**Migration 3 (`003_signin_redemptions.sql`)** is the first since the cutover, and the timer parks the
release that carries it (*schema version 3 and this database is at 1*). It adds the table
`signin_redemptions` — the random id of each provider sign-in redeemed and when, so a kept copy of the
sign-in cannot be redeemed again; the hourly retention pass removes rows older than a day — and the
column `users.sessions_revoked_at`, set by **Sign out everywhere**, `admin sessions revoke` and `admin
disable`, so a sign-in begun before it opens no session after it. A sign-in in flight at the moment of
the deploy fails once (`/#failed`) and works on the next try: the cookie the older build wrote carries no
ticket, and the new one refuses an answer without one.

**Rolling the code back past migration 3 needs one statement first.** An older build refuses to start on
a database that records a version it does not ship, so the symlink and restart of §4.3 alone leave the
gateway down. The build before migration 3 says so in words meant for protocol 1's database — *This
database was written by another protocol of Enactive Remote (schema version 3). Install by hand: …
§cutover* — and it is **not** a protocol change: §8 is not the procedure. (Builds from migration 3 on say
*This database was migrated by a newer build of Enactive Remote (schema version N)* for this case.) The
table and the column are additions the older build never reads, so it is safe to forget the version and
then switch:

```bash
sudo mysql -e "DELETE FROM enactive_remote_v2.schema_version WHERE version = 3"
sudo -u enactive ln -sfn /opt/enactive-remote/releases/<older> /opt/enactive-remote/current
sudo systemctl restart enactive-remote
```

The rolled-back gateway gives up both protections until the newer release is back. Installing that release
again runs 003 again, which finds the table and the column there and only records the version. The timer
parks it as a migration again, since the database reads 1: install it with `--allow-migration` as above.

Copy the new `verify-restore.sh` first (§4.5): it asks for `signin_redemptions` only of a dump at version
3 or later, so it passes on the backup taken before the install and on every one after. There is no
version 2, on purpose: protocol 1's database records it, and a migration 2 of this protocol would make
that database read as current to a gateway pointed at it by mistake, which would start on it instead of
refusing (§8).

**A protocol change is parked too, and `--allow-migration` does not install it.** The journal says
*protocol change - install by hand (REMOTE_OPERATIONS §cutover)*: such a release needs a new database
and a new environment, which no flag provides, and installing it onto the running one would leave
every computer and browser unable to talk to it. The schema version cannot catch this on its own —
protocol 2 started its schema again at 1, below the protocol-1 database's 2, so by schema alone it
reads as a rollback. The procedure is §8.

### 4.3 What happens when it does not come up

`/health` is polled for about thirty seconds after the restart. If it never answers:

- **No migration in the release** — the symlink goes back to the previous one and the service is
  restarted. That is a true rollback: the schema never moved, so the old code meets the database it
  was written for. The run then exits non-zero, so the failure is visible in `systemctl status`.
- **A migration was applied** — nothing is rolled back, deliberately, and the run says so. Putting
  the previous release back would run old code against the new schema, which is a second fault on
  top of the first. Read `journalctl -u enactive-remote -n 100 --no-pager` and decide.

Rolling back by hand at any time is a symlink and a restart, and it is only ever a rollback of the
**code** — to a build older than the database's newest migration, only after the step §4.2 gives for that
migration, or the older build refuses to start:

```bash
sudo -u enactive ln -sfn /opt/enactive-remote/releases/<older> /opt/enactive-remote/current
sudo systemctl restart enactive-remote
```

### 4.4 Setting it up

**There is no checkout on the server** and there is not meant to be — it holds a release, not a
repository. The files come from a workstation that has one. From the checkout:

```bash
scp deploy/pull-release.sh deploy/enactive-deploy.service \
    deploy/enactive-deploy.timer deploy/49-enactive-deploy.rules \
    <you>@<server>:/tmp/
```

Then on the server:

```bash
sudo install -o enactive -g enactive -m 0755 /tmp/pull-release.sh /opt/enactive-remote/deploy/
sudo install -m 0644 /tmp/enactive-deploy.service /tmp/enactive-deploy.timer /etc/systemd/system/
sudo install -m 0644 /tmp/49-enactive-deploy.rules /etc/polkit-1/rules.d/

sudo install -o enactive -g enactive -m 0600 /dev/null /etc/enactive-remote/deploy.env
sudo -u enactive tee /etc/enactive-remote/deploy.env >/dev/null <<'ENV'
ENACTIVE_DEPLOY_REPO=StasEdward/Enactive
ENACTIVE_DEPLOY_BRANCH=feature/remote-access
ENACTIVE_DEPLOY_TOKEN=github_pat_...
ENV

sudo systemctl daemon-reload
sudo systemctl enable --now enactive-deploy.timer
```

Run the first one by hand and read the journal rather than waiting for the timer — the first run is
the one that finds a wrong repository name, a token without the right scope, or a missing tool:

```bash
sudo systemctl start enactive-deploy
journalctl -u enactive-deploy -n 50 --no-pager
```

**The token** is a fine-grained personal access token, scoped to this repository alone, with
**Actions: read** and nothing else. It can download build artifacts and cannot push, cannot read
secrets and cannot start a workflow. Downloading an artifact needs authentication even from a
public repository, so there is no token-free version of this.

**The polkit rule** lets `enactive` restart `enactive-remote.service` — one verb, one unit, one
account. Not a sudoers line: the deploy unit sets `NoNewPrivileges=true`, under which `sudo` cannot
work at all, so using sudo would mean weakening the unit to accommodate it.

Requires `curl`, `unzip`, `python3`, `mysql`, `dotnet` and `systemctl`. The script names any that
are missing and stops, because each of them otherwise fails later as something else — a missing
`python3` empties a pipeline and reads as "GitHub returned nothing".

Artifacts expire after 30 days. A server that has been off longer than that has no release to pull
and says so; build the branch again.

### 4.5 The edge caches the panel

The gateway now serves every panel file with `Cache-Control: no-cache`, so a browser revalidates and
gets a 304 when nothing changed. That is the half this repository controls.

The other half is **Cloudflare's Browser Cache TTL**. When the origin sent no header, Cloudflare
supplied four hours of its own — so a release reached the server in ten minutes and the owner went
on seeing the old page all afternoon, with no way to tell a stale page from a broken one. If a
change is deployed (`current/.commit` has moved) and a hard refresh still shows the old panel, that
setting is overriding the origin: set Browser Cache TTL to **Respect Existing Headers** for this
zone.

Checking what the server actually serves, which settles "stale or broken" in one command:

```bash
curl -sI https://remote.enactive.dev/app.js | grep -i 'cache-control\|last-modified'
curl -s  https://remote.enactive.dev/app.js | grep -c 'Asked for'   # or whatever the change added
```

**The deploy scripts do not update themselves.** `/opt/enactive-remote/deploy/` is copied there by
hand, and a change to `pull-release.sh` in the repository does nothing until it is copied again.
That is deliberate: a script that replaces itself and then runs the replacement has no way back if
the replacement is broken — the one component whose failure removes the means of fixing it. Copy it
the way it got there:

```bash
scp deploy/*.sh you@your-server:/tmp/
sudo install -o enactive -g enactive -m 0700 /tmp/pull-release.sh /opt/enactive-remote/deploy/
```

---

## 5. Backups, and the half that is usually missing

```
0 3 * * *  enactive  /opt/enactive-remote/deploy/backup.sh
30 3 * * * enactive  /opt/enactive-remote/deploy/verify-restore.sh
```

Neither script reads `gateway.env`. Their settings go on their cron lines, and the defaults are the
ones this server uses: `ENACTIVE_BACKUP_DATABASE` (`enactive_remote_v2`), `ENACTIVE_BACKUP_DIR`
(`/var/backups/enactive-remote`), `ENACTIVE_BACKUP_DEFAULTS` (the read-only account's option file,
`/etc/enactive-remote/backup.cnf`), `ENACTIVE_BACKUP_KEEP_DAYS` (30, `backup.sh` only) and
`ENACTIVE_DATA` (`/var/lib/enactive-remote`, where `backup.sh` finds the keys).

`backup.sh` dumps to a `.partial` name and moves it into place only after `mysqldump` exits
cleanly, so the directory never holds something that looks like last night's backup and is half a
dump. Old files are deleted by **age**, not by count — "keep the last seven" quietly keeps seven
copies of a failure that has been repeating for a week.

Beside each dump it keeps the Data Protection keys: `enactive_remote_v2-<stamp>.keys.tar.gz`, a tar
of `ENACTIVE_DATA/keys`, mode `0600` — whoever reads it can mint a session for anybody. If the keys
directory is missing the job fails after the dump is in place, because the gateway makes its keys
when it starts, and none at all means `ENACTIVE_DATA` points somewhere else. **A restore without the
keys signs everyone out**, and nothing else: every account, computer and run is in the dump. That is
acceptable, and it is why the keys are kept anyway.

Restoring for real, with the gateway stopped:

```bash
sudo systemctl stop enactive-remote
gunzip -c /var/backups/enactive-remote/enactive_remote_v2-<stamp>.sql.gz \
  | sudo mysql enactive_remote_v2                   # the dump drops and recreates each table
sudo -u enactive tar -xzf /var/backups/enactive-remote/enactive_remote_v2-<stamp>.keys.tar.gz \
  -C /var/lib/enactive-remote                        # puts back keys/
sudo systemctl start enactive-remote
```

`verify-restore.sh` is the half nobody writes. A backup job that has never been restored is a cron
entry with a good reputation. It restores the newest dump into a scratch database, checks what
could only be true of a working copy — every table this build expects, a recorded schema version,
for **each account** an event line (`user_streams`) that is not behind the events and notices
numbered from it, and **no row in any owned table** whose `owner_id` or `user_id` has no account —
and drops it. The last is what the foreign keys forbid, and a restore is when one would find out
otherwise: a dump loads with the checks turned off.
It refuses a backup older than two days, because a job that stopped a week ago and a job that works
look identical if you only ever restore the newest file. On failure it leaves the scratch database
behind to look at.

Both are wired to send their output somewhere a person reads. A cron job whose failure mail goes
nowhere has the same reputation problem.

---

## 6. When something is wrong

| What you see | What it usually is |
|---|---|
| A blank dark page after switching the tunnel, on the machine you always use — while a phone shows it fine | The browser's cache. The page it replaced used the same brand assets and the same dark background, so a stale copy looks like a broken new one rather than like an old page. `Ctrl+Shift+R`; it has happened on both switchovers so far. A second device is the fastest way to tell a caching problem from a server one. |
| A blank dark page everywhere | `app.js` did not run. Both the sign-in form and the panel start `hidden` and only the script reveals them, so anything that stops it leaves the background and nothing else. The Console names it in one line. |
| Empty body, no error, from any URL | A hostname the tunnel has no rule for, falling through its catch-all `http_status:404`. Check the hostname before the service. |
| Service will not start, log names `CF-Connecting-IP` and an address | `ASPNETCORE_URLS` is not loopback while `ENACTIVE_BEHIND_TUNNEL` is on. This is the check in §1 doing its job; fix the binding, do not turn the setting off. |
| `Set ENACTIVE_REMOTE_DB…`, or a sentence naming any other setting | The env file is missing, unreadable by `enactive`, or has a typo — every setting the gateway reads stops the start with its own name rather than being guessed at. `sudo -u enactive cat` it. |
| Start times out after 180s | A migration is running against a large table, or is stuck on `GET_LOCK`. Look for another gateway process before doing anything else. |
| Signing in returns 429 | The callback limiter: ten provider callbacks a minute per caller address. A person signing in makes one; if it is refusing *you*, something else is calling from that address — check `CF-Connecting-IP` in the logs. |
| Signing in says to wait | Admission is a list and this identity is not on it yet: §3.1. |
| A person is refused because of a limit | One of the per-account limits in §2. There are three sentence shapes (`GatewayFault.cs`). Computers, runs in progress, tasks a day and waiting commands: *"This account already has {N} {things}, the most it may; {remedy}."* Devices and invitations: *"This account already has {N} devices. Remove one that is no longer used to add another."* and *"This account already has {N} open invitations. Wait for one to be used or to expire - each lasts ten minutes - to make another."* Storage: *"This would take this account past its {N} MB of stored tasks and history, the most it may; older runs are removed after {days} days."* Raising a limit is a change to `gateway.env` and a restart. |
| The desktop says *"This computer and the service speak different versions - update Enactive."* | A desktop older than protocol 2, or a gateway older than the desktop. Nothing on the server fixes it but the right release. |
| Panel shows a computer as offline that is running | The Host syncs every 15s and is called offline after 45. Look at the desktop first: this is reported honestly, not inferred. |
| Runs stuck in `Queued` | Nothing accepted the start command. It expires after 24h and the run is then reported `Incomplete` rather than left queued forever. |
| Everyone signed out after a deploy | The Data Protection keys moved. `ENACTIVE_DATA` must point at the same directory as before — check that the release did not take `/var/lib/enactive-remote` with it. |

Logs are `journalctl -u enactive-remote`. `/health` answers without authentication and reports the
protocol version; it is what the tunnel and any external check should watch.

---

## 7. What is deliberately not automated, and why

- **Deployment.** Migrations are DDL and cannot be rolled back. A person should be watching. When
  that stops being true — a staging database, a tested rollback path — this is the first thing to
  revisit. A protocol change is never automated at all: it is §8.
- **Admission.** Approving a sign-up, refusing one, disabling and enabling an account are each an
  `admin` command a person runs (§3.1). `ENACTIVE_ADMISSION=open` would remove the first of them; it
  stays `list` until the privacy and terms pages are approved.
- **Comparing the panel with its build.** The pipeline publishes the fingerprints of every build and
  release on its own (§9); nothing compares them with what the server sends unless a person runs §9.
- **Signing people out.** One account: `admin sessions revoke <userId>` (§3.1). Everybody at once:
  replace the Data Protection keys, which no command does — it is a decision about every person
  using the service, made rarely and by hand.
- **Two gateways.** The schema and its `FOR UPDATE` transactions do not prevent a second instance,
  but nothing has been tested that way and it should not be claimed. One at a time.
- **Off-machine backups.** `backup.sh` writes to local disk. A disk that dies takes the database
  and its backups together. Copying them elsewhere is a decision about where "elsewhere" is, and
  belongs to whoever owns that answer.
- **Closing the desktop ends remote runs.** By design, and unchanged: the Host
  is a library inside the app, not a service. The panel reports `Interrupted` honestly.

---

## 8. The cutover from protocol 1 (§cutover)

This section starts before the message that points to it can exist. Step 0 puts on the server the
script that parks a protocol-2 release with *protocol change - install by hand (REMOTE_OPERATIONS
§cutover)*, and it must be done before protocol 2 is merged into the tracked branch; the message, when
it comes, means step 0 was done and the rest is due. Protocol 2 shares nothing with protocol 1 — not the schema, not the
owner key, not a device token — so nothing is migrated: the new gateway starts on a **new, empty
database**, `enactive_remote_v2`, and the old one is kept untouched beside it.

If step 0 was missed and a protocol-2 gateway is started on the protocol-1 database, it refuses to start, and
leaves the database as it was: *This database was written by another protocol of Enactive Remote (schema
version 2). Install by hand: Docs/REMOTE_OPERATIONS.md §cutover.* Put the old binary back (§4.3) and do this
section from step 0.

**There is no rollback to protocol 1 on the new database.** Rollback means the old binary on the old
database, which this procedure never writes to. Plan the window: the site is down from step 1 to step 4.

Everything below runs on the server. From a checkout of protocol 2, copy the new `deploy/` files
(`backup.sh`, `verify-restore.sh`, `pull-release.sh`, `grants.sql`, `gateway.env.example`) to `/tmp/`
there, as in §4.4.

**0. Put the guard on the server — before protocol 2 can reach the tracked branch.** The script on
the server is the protocol-1 one, and the deploy scripts do not update themselves (§4.5). It has no
protocol check: the first green protocol-2 build on `ENACTIVE_DEPLOY_BRANCH` reads to it as schema 1
against a database at 2 — no migration — and it installs it, unattended, onto the protocol-1 database.
`/health` answers, so it reports success; nobody can sign in and every computer is refused. So the new
`pull-release.sh` goes in first, told which database this server still runs on:

```bash
ops=/opt/enactive-remote
if [ -e $ops/deploy.protocol-1 ]; then
  echo "Step 0 is already done: $ops/deploy.protocol-1 holds the protocol-1 scripts. Go on with step 1."
else
  sudo cp -a $ops/deploy $ops/deploy.protocol-1                                  # the rollback's scripts
  sudo install -o enactive -g enactive -m 0755 /tmp/pull-release.sh $ops/deploy/
  # What the protocol-1 binary cannot say about itself; without it the timer asks it, and it crashes.
  echo 1 | sudo -u enactive tee "$(readlink -f $ops/current)/.protocol-version" >/dev/null
  # Replaced, not appended: one line, however often this has run.
  sudo sed -i '/^ENACTIVE_DEPLOY_DATABASE=/d' /etc/enactive-remote/deploy.env
  echo 'ENACTIVE_DEPLOY_DATABASE=enactive_remote' | sudo -u enactive tee -a /etc/enactive-remote/deploy.env >/dev/null
  sudo systemctl start enactive-deploy
  journalctl -u enactive-deploy -n 20 --no-pager         # "Already on ..." - or a protocol-1 release installed
fi
```

It refuses a second run because a second copy would put today's `deploy/` - by then the new scripts -
where the rollback expects the protocol-1 ones.

From now on it installs protocol-1 releases as before and parks the first protocol-2 one with
*protocol change - install by hand*. Only now may protocol 2 be merged into the tracked branch.

**1. Back up, and keep the old database for 30 days, read-only.**

```bash
sudo systemctl stop enactive-deploy.timer                  # nothing installs itself while this happens
sudo -u enactive /opt/enactive-remote/deploy/backup.sh     # the OLD script: enactive_remote-<stamp>.sql.gz
sudo -u enactive /opt/enactive-remote/deploy/verify-restore.sh
sudo systemctl stop enactive-remote
sudo mysql -e "ALTER DATABASE enactive_remote READ ONLY = 1"   # MySQL 8.0.22 or later
[ -e /opt/enactive-remote/protocol-1-release ] ||
  sudo -u enactive cp -a "$(readlink -f /opt/enactive-remote/current)" /opt/enactive-remote/protocol-1-release
```

The last line keeps the protocol-1 binary where nothing removes it. Where `current` points depends on
how it got there — `first-install.sh` unpacks into `/opt/enactive-remote/<stamp>`, the timer into
`releases/<stamp>-<commit>` — and the timer prunes `releases/` to the newest five once protocol-2
releases deploy, which would take the rollback's binary with it.

Read-only rather than renamed or dumped and dropped: it is the rollback, and a rollback should be the
old binary started against exactly what it left. The new `backup.sh` neither backs it up nor deletes
its dumps (its file names start `enactive_remote_v2-`), so the dump from this step stays until a person
removes it. After 30 days, if nothing called for the rollback:

```bash
sudo mysql -e "ALTER DATABASE enactive_remote READ ONLY = 0; DROP DATABASE enactive_remote"
sudo rm /var/backups/enactive-remote/enactive_remote-*.sql.gz
sudo rm -r /opt/enactive-remote/protocol-1-release /opt/enactive-remote/deploy.protocol-1 \
           /etc/enactive-remote/gateway.env.protocol-1
```

**2. Create `enactive_remote_v2` and grant it.**

```bash
sudo mysql -e "CREATE DATABASE enactive_remote_v2 CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"
sudo mysql < /tmp/grants.sql        # the gateway's and the backup's rights, on the new schema
```

The accounts already exist, so `CREATE USER IF NOT EXISTS` leaves their passwords as they are, and
`gateway.env` and `backup.cnf` keep working. Their rights on `enactive_remote` stay too — the rollback
needs them.

**3. Update the environment.**

```bash
sudo install -o enactive -g enactive -m 0755 /tmp/backup.sh /tmp/verify-restore.sh /opt/enactive-remote/deploy/
sudo sed -i '/^ENACTIVE_DEPLOY_DATABASE=/d' /etc/enactive-remote/deploy.env      # every such line
grep -c '^ENACTIVE_DEPLOY_DATABASE=' /etc/enactive-remote/deploy.env                 # 0
sudo cp -p /etc/enactive-remote/gateway.env /etc/enactive-remote/gateway.env.protocol-1   # the rollback's
sudoedit /etc/enactive-remote/gateway.env
```

Without the `ENACTIVE_DEPLOY_DATABASE` line the timer compares releases with `enactive_remote_v2`,
which is where protocol 2 runs from step 4 on.

In `gateway.env`, against `/tmp/gateway.env.example`:

- `ENACTIVE_REMOTE_DB`: `Database=enactive_remote_v2;`, the rest unchanged.
- Delete `ENACTIVE_OWNER_KEY`. Nothing reads it any more.
- Add `ENACTIVE_PUBLIC_ORIGIN`, the client id and secret of the GitHub and/or Google OAuth apps
  (registered with the callbacks in §2), `ENACTIVE_ADMISSION=list`, and the `ENACTIVE_LIMIT_*` lines.
- `ENACTIVE_DEV_SIGNIN` must not be there.

The Data Protection keys in `/var/lib/enactive-remote/keys` stay where they are.

**4. Install by hand.** The timer already downloaded the release and named it in `PARKED`; if it has
not seen it yet, `sudo systemctl start enactive-deploy` once and read the journal.

```bash
release=$(cat /opt/enactive-remote/PARKED)
sudo -u enactive dotnet "$release/Enactive.Remote.Gateway.dll" --protocol-version     # 2
sudo -u enactive ln -sfn "$release" /opt/enactive-remote/current
sudo systemctl start enactive-remote                       # the first start creates the schema
curl -fsS http://127.0.0.1:5099/health                     # "protocolVersion":2
curl -s https://remote.enactive.dev/health                 # and from outside
sudo -u enactive rm /opt/enactive-remote/PARKED
sudo -u enactive /opt/enactive-remote/deploy/backup.sh && sudo -u enactive /opt/enactive-remote/deploy/verify-restore.sh
sudo systemctl start enactive-deploy.timer
```

Then sign in and approve yourself (§3.1), and compare the panel with its build (§9).

If it does not come up and cannot be made to: the rollback, which touches nothing of the new database.

```bash
sudo systemctl stop enactive-deploy.timer enactive-remote
sudo cp -p /etc/enactive-remote/gateway.env.protocol-1 /etc/enactive-remote/gateway.env
sudo mysql -e "ALTER DATABASE enactive_remote READ ONLY = 0"
sudo -u enactive ln -sfn /opt/enactive-remote/protocol-1-release /opt/enactive-remote/current
sudo systemctl start enactive-remote
# The backups, back onto the database people use again:
sudo -u enactive install -m 0755 /opt/enactive-remote/deploy.protocol-1/backup.sh \
     /opt/enactive-remote/deploy.protocol-1/verify-restore.sh /opt/enactive-remote/deploy/
sudo sed -i '/^ENACTIVE_DEPLOY_DATABASE=/d' /etc/enactive-remote/deploy.env
echo 'ENACTIVE_DEPLOY_DATABASE=enactive_remote' | sudo -u enactive tee -a /etc/enactive-remote/deploy.env >/dev/null
sudo -u enactive /opt/enactive-remote/deploy/backup.sh && sudo -u enactive /opt/enactive-remote/deploy/verify-restore.sh
sudo systemctl start enactive-deploy.timer
```

The backup scripts go back with the binary. Left as they are, the protocol-2 ones would dump and verify
`enactive_remote_v2` every night - green, and of a database nobody uses - while the live one was never
backed up. Pointing them at `enactive_remote` instead is not enough: the protocol-2 `verify-restore.sh`
checks protocol 2's tables, and would fail every night on a good dump. The new `pull-release.sh` stays,
with the line from step 0 back in `deploy.env`: it installs protocol-1 releases and parks protocol 2
again, as it should.

**Retrying after a rollback.** The rollback leaves the server where step 0 left it — the new
`pull-release.sh`, one interim line in `deploy.env`, the protocol-1 scripts in `deploy.protocol-1` —
so step 0 is not repeated (run again, it says it is done). In order:

1. If the timer has installed a newer protocol-1 release since the rollback — `readlink -f
   /opt/enactive-remote/current` is no longer `protocol-1-release` — the kept copy is stale: remove it,
   `sudo rm -r /opt/enactive-remote/protocol-1-release`, and step 1 copies the running one. Never while
   `current` points at it.
2. `enactive_remote_v2` holds what the failed attempt made. Unless somebody used it, drop it, so the
   retry starts empty like the first attempt: `sudo mysql -e "DROP DATABASE enactive_remote_v2"`.
3. Steps 1 to 5 as written. Step 1 keeps an existing `protocol-1-release`, and step 3 removes every
   interim line — one left behind would have the timer compare protocol-2 releases with the protocol-1
   database, and install a migration unattended as "no migration".

**5. Tell the people who used it.** Every computer must be connected again with a new code (design
D2): the old owner key and every old device token mean nothing to protocol 2. Each person signs in
with GitHub or Google, waits to be approved, registers each computer under **Computers** and enters
the connection code it shows in the desktop app — which must itself be a protocol-2 build; an older
one is refused by version. Nothing they had in the old panel is carried over. The steps they follow
are in [the remote access guide](wiki/Remote-Access.md#pairing-a-computer).

---

## 9. Comparing the panel with its build

The panel holds the keys that open a person's content, and it is code this server sends. What the
server sends is listed at `/.well-known/enactive-panel.json`: the SHA-256 of every script and
stylesheet. The same list is made by the pipeline from the files the build ships
(`deploy/panel-manifest.sh` over `publish/wwwroot`), where the server cannot reach it, and published:

- in the summary of every `build` run (**Panel fingerprints**), readable by anyone;
- inside the build's artifact, so the server has its own release's list at
  `/opt/enactive-remote/current/panel-manifest.json`;
- in the notes of every tagged release, and attached to it as `panel-manifest.json`.

The two are the same bytes, so the comparison is `cmp`, not reading:

```bash
# On the server: what it sends against what its release says it sent.
curl -s https://remote.enactive.dev/.well-known/enactive-panel.json \
  | cmp - /opt/enactive-remote/current/panel-manifest.json && echo "the panel is the build"

# Anywhere: against the list of the build the operator says is running, saved from that run's summary
# or that release as panel-manifest.json.
curl -s https://remote.enactive.dev/.well-known/enactive-panel.json | cmp - panel-manifest.json
```

A difference names a file that is not what the build shipped. Check the Cloudflare cache first (§4.5)
— a stale file is a difference too — and then treat it as what it may be: an altered panel.
