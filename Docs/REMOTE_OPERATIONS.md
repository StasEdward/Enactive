# Enactive Remote — running it

Stage 7 of `REMOTE_DESIGN.md`. What that stage is allowed to claim is *"it can be run by someone
other than the person who wrote it"*, so this is written for that person: what to install, what
each setting means, what to do when it breaks, and what has deliberately been left manual.

Everything here has been read against a running gateway. What has **not** been done is a real
deployment onto `remote.enactive.com` — that server currently runs the preview, and replacing it is
a decision with a date on it, not a step in a document.

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
curl -s https://remote.enactive.com/health
```

Check it from outside, not from the box. `curl` against `127.0.0.1:5099` proves the gateway is up
and says nothing about which service the tunnel is pointed at, which is the thing being changed.

---

## 4. Deploying a new version

CI publishes `gateway-<commit>` on every green build of the branch. Download it, put it beside the
current one, and move a symlink:

```bash
release=/opt/enactive-remote/$(date --utc +%Y%m%dT%H%M%SZ)
sudo -u enactive mkdir -p "$release"
sudo -u enactive unzip -q gateway-<commit>.zip -d "$release"

sudo systemctl stop enactive-remote
sudo -u enactive ln -sfn "$release" /opt/enactive-remote/current
sudo systemctl start enactive-remote
sudo journalctl -u enactive-remote -n 50 --no-pager
curl -fsS http://127.0.0.1:5099/health
```

**Take a backup before a release that carries a migration**, and know which one it is: the gateway
applies migrations at startup, MySQL cannot roll DDL back, and a rollback to the previous release
does **not** undo a schema change. That is the whole reason deployment is manual — a person who
would have to fix a half-applied schema should be watching when it is applied. `journalctl` will
name the migration if one runs.

Rolling back is the same three lines with the previous directory, and it is only a rollback of the
code.

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
