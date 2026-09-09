# Enactive Remote — running it

Stage 7 of `REMOTE_DESIGN.md`. What that stage is allowed to claim is *"it can be run by someone
other than the person who wrote it"*, so this is written for that person: what to install, what
each setting means, what to do when it breaks, and what has deliberately been left manual.

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
it as the client address — for the login rate limiter above all, which would otherwise put every
visitor on earth in one bucket and let a stranger lock the owner out by guessing.

**That trust is only sound while cloudflared is the only thing that can connect.** So the gateway
**refuses to start** if `ENACTIVE_BEHIND_TUNNEL` is on and it is bound anywhere but loopback. This
is not advice in a runbook; it is a check in `Deployment.RequireLoopbackListeners`, and there are
tests for both halves. If you ever put a different proxy in front of this, that check is the thing
to read first.

---

## 2. Settings

All of them are environment variables, and the two secret ones live only in
`/etc/enactive-remote/gateway.env`, owned by `enactive`, mode `0600`. They are **not** in the
systemd unit: a unit file is world-readable and `systemctl cat` prints it to anybody who asks.

| Variable | Required | What it is |
|---|---|---|
| `ENACTIVE_REMOTE_DB` | yes | MySQL connection string. The gateway refuses to start without it rather than finding out on the first request, by which time it has already told somebody it was healthy. |
| `ENACTIVE_OWNER_KEY` | yes | The owner's key, at least 24 characters. Changing it signs every browser out, everywhere — the only such control this build has. |
| `ENACTIVE_BEHIND_TUNNEL` | yes here | `true`. See §1. |
| `ASPNETCORE_URLS` | yes here | `http://127.0.0.1:5099`. Anything not loopback and the process will not start. |
| `ENACTIVE_DATA` | yes | Where the Data Protection keys live. `/var/lib/enactive-remote`, `0700`. |
| `ENACTIVE_RETENTION_DAYS` | no | Default 30. Events and notices older than this are deleted hourly, and the panel says so. |

**The Data Protection keys are the session.** Lose them and every browser is signed out; copy them
and whoever has the copy can mint a session cookie. They are protected by the directory's mode and
by nothing else in this build — this is stated rather than fixed, and it is the first thing to
change if the threat model ever grows.

---

## 3. Installing it the first time

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
mysql -u root -p -e "CREATE DATABASE IF NOT EXISTS enactive_remote
                     CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"

# 3. The secrets. openssl, not a password you thought of.
sudo install -o enactive -g enactive -m 0600 /dev/null /etc/enactive-remote/gateway.env
printf 'ENACTIVE_OWNER_KEY=%s\n' "$(openssl rand -base64 36)" | sudo tee -a /etc/enactive-remote/gateway.env
# ENACTIVE_REMOTE_DB=Server=127.0.0.1;User ID=enactive_gateway;Password=...;Database=enactive_remote;

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

---

## 4. Deploying a new version

`enactive-deploy.timer` checks every ten minutes and installs the newest **green** build of the
tracked branch. It is a PULL: this server is on a private address and nothing on the internet can
reach it, so there is no pipeline that could push a release in, and the credential it holds is
read-only.

It does **not** install a release that carries a migration. That distinction is the whole design,
and §4.2 is why.

### 4.1 What a run does

1. Asks GitHub for the newest run of `build.yml` on the branch with `status=success`. Success is
   asked of GitHub rather than inferred from an artifact existing — a run can upload one and then
   fail a later step, and "there is a build" is not "the tests passed".
2. Stops if that commit is already the one in `current/.commit`.
3. Downloads `gateway-<commit>` into `/opt/enactive-remote/releases/<timestamp>-<commit>`.
4. Asks the **new build** what schema version it carries, by running it with `--schema-version`.
   The build answers for itself: migrations are embedded resources, so an unzipped release has no
   `.sql` files to count, and a number the pipeline wrote into a manifest is a claim about the
   assembly that nothing keeps true.
5. Compares that with `MAX(version)` in `schema_version`, read with the read-only backup account.
6. Same version → swaps the symlink, restarts, and polls `/health`. Higher → parks it (§4.2).
7. Keeps the last five releases, never removing the one that is running.

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

### 4.3 What happens when it does not come up

`/health` is polled for about thirty seconds after the restart. If it never answers:

- **No migration in the release** — the symlink goes back to the previous one and the service is
  restarted. That is a true rollback: the schema never moved, so the old code meets the database it
  was written for. The run then exits non-zero, so the failure is visible in `systemctl status`.
- **A migration was applied** — nothing is rolled back, deliberately, and the run says so. Putting
  the previous release back would run old code against the new schema, which is a second fault on
  top of the first. Read `journalctl -u enactive-remote -n 100 --no-pager` and decide.

Rolling back by hand at any time is a symlink and a restart, and it is only ever a rollback of the
**code**:

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

`backup.sh` dumps to a `.partial` name and moves it into place only after `mysqldump` exits
cleanly, so the directory never holds something that looks like last night's backup and is half a
dump. Old files are deleted by **age**, not by count — "keep the last seven" quietly keeps seven
copies of a failure that has been repeating for a week.

`verify-restore.sh` is the half nobody writes. A backup job that has never been restored is a cron
entry with a good reputation. It restores the newest dump into a scratch database, checks three
things that could only be true of a working copy — every table this build expects, a recorded
schema version, and a stream counter that is not behind the rows numbered from it — and drops it.
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
| `Set ENACTIVE_REMOTE_DB…` / `Set ENACTIVE_OWNER_KEY…` | The env file is missing, unreadable by `enactive`, or has a typo. `sudo -u enactive cat` it. |
| Start times out after 180s | A migration is running against a large table, or is stuck on `GET_LOCK`. Look for another gateway process before doing anything else. |
| Panel loads, sign-in returns 429 | The login limiter. If it is refusing *you*, someone else is guessing — check `CF-Connecting-IP` in the logs. |
| Panel shows a computer as offline that is running | The Host syncs every 15s and is called offline after 45. Look at the desktop first: this is reported honestly, not inferred. |
| Runs stuck in `Queued` | Nothing accepted the start command. It expires after 24h and the run is then reported `Incomplete` rather than left queued forever. |
| Everyone signed out after a deploy | The Data Protection keys moved. `ENACTIVE_DATA` must point at the same directory as before — check that the release did not take `/var/lib/enactive-remote` with it. |

Logs are `journalctl -u enactive-remote`. `/health` answers without authentication and reports the
protocol version; it is what the tunnel and any external check should watch.

---

## 7. What is deliberately not automated, and why

- **Deployment.** Migrations are DDL and cannot be rolled back. A person should be watching. When
  that stops being true — a staging database, a tested rollback path — this is the first thing to
  revisit.
- **Secret rotation.** Changing `ENACTIVE_OWNER_KEY` signs everybody out; there is no per-device
  session to revoke instead. `REMOTE_DESIGN.md` §4.4 has named sessions and TOTP as *should*, not
  *must*, and neither is built.
- **Two gateways.** The schema and its `FOR UPDATE` transactions do not prevent a second instance,
  but nothing has been tested that way and it should not be claimed. One at a time.
- **Off-machine backups.** `backup.sh` writes to local disk. A disk that dies takes the database
  and its backups together. Copying them elsewhere is a decision about where "elsewhere" is, and
  belongs to whoever owns that answer.
- **Closing the desktop ends remote runs.** Stated in `REMOTE_DESIGN.md` §9 and unchanged: the Host
  is a library inside the app, not a service. The panel reports `Interrupted` honestly.
