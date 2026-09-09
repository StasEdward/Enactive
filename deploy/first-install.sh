#!/usr/bin/env bash
# First install of the gateway on a fresh Ubuntu box. Run as root:
#
#   sudo ./first-install.sh /path/to/gateway.tar.gz
#
# It is idempotent: it can be re-run after a failure without undoing what already worked, which
# matters because the alternative to re-running is unpicking a half-install by hand.
#
# WHAT IT DELIBERATELY DOES NOT DO: touch cloudflared, and stop whatever is serving the site today.
# Those are the two steps that change what the outside world sees, and they are printed at the end
# for a person to do with their eyes open. Everything before them is reversible by stopping a
# service nobody is pointed at yet.
#
# The database passwords are generated HERE and never printed. They go straight into
# /etc/enactive-remote/gateway.env, which is 0600 and owned by the service account. They are passed
# to mysql on stdin rather than as arguments, because an argument is visible in `ps` to every
# account on the machine for as long as the command runs.
set -euo pipefail

PACKAGE="${1:-}"
SERVICE=enactive-remote
ACCOUNT=enactive
ROOT=/opt/enactive-remote
CONFIG=/etc/enactive-remote
DATABASE=enactive_remote
PORT=5099

say() { printf '\n== %s\n' "$1"; }
die() { printf '\nSTOPPED: %s\n' "$1" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run this with sudo."
[ -n "$PACKAGE" ] && [ -f "$PACKAGE" ] || die "pass the path to gateway.tar.gz as the first argument."

say "Checking what is already here"

# The runtime. Checked rather than installed: which feed a machine should take .NET from is a
# decision about that machine, not one this script gets to make silently.
if ! command -v dotnet >/dev/null || ! dotnet --list-runtimes | grep -q "Microsoft.AspNetCore.App 10\."; then
  die "the ASP.NET Core 10 runtime is not installed. Install it, then run this again:
  sudo add-apt-repository ppa:dotnet/backports && sudo apt update && sudo apt install -y aspnetcore-runtime-10.0"
fi

command -v mysql >/dev/null || die "no mysql client on this machine."
command -v mysqldump >/dev/null || die "no mysqldump on this machine; the backup job needs it."
command -v curl >/dev/null || die "no curl on this machine; this script uses it to check /health."
command -v openssl >/dev/null || die "no openssl on this machine; it generates the passwords."
mysql --protocol=socket -e "SELECT 1" >/dev/null 2>&1 || die "root cannot reach MySQL over the local socket."

# A port already in use is almost always the previous version of this service, and starting a
# second one would have two gateways on one database - which the design says is untested.
if ss -ltn "sport = :$PORT" | grep -q ":$PORT"; then
  systemctl is-active --quiet "$SERVICE" \
    || die "something is already listening on $PORT and it is not $SERVICE."
  say "Stopping the running $SERVICE first"
  systemctl stop "$SERVICE"
fi

say "Account and directories"

id "$ACCOUNT" >/dev/null 2>&1 || useradd --system --home "$ROOT" --shell /usr/sbin/nologin "$ACCOUNT"
install -d -o "$ACCOUNT" -g "$ACCOUNT" -m 0755 "$ROOT"
install -d -o root      -g "$ACCOUNT" -m 0750 "$CONFIG"

say "Database and accounts"

# The schema. utf8mb4_0900_ai_ci is what every table in the migrations declares; a database created
# with a different default would still work and would be a surprise waiting for the first table
# somebody adds without saying.
mysql --protocol=socket <<SQL
CREATE DATABASE IF NOT EXISTS \`$DATABASE\`
  CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
SQL

mysql --protocol=socket < "$(dirname "$0")/grants.sql"

# Rotated on every run, and that is deliberate rather than lazy: the alternative is a script that
# either reuses a password it cannot read or asks a human to invent one. The service is restarted
# below with the new value, and nothing else uses these accounts.
gateway_password=$(openssl rand -base64 30 | tr -d '\n')
backup_password=$(openssl rand -base64 30 | tr -d '\n')

mysql --protocol=socket <<SQL
ALTER USER 'enactive_gateway'@'localhost' IDENTIFIED BY '$gateway_password';
ALTER USER 'enactive_backup'@'localhost'  IDENTIFIED BY '$backup_password';
FLUSH PRIVILEGES;
SQL

say "Secrets"

# The owner key is only generated if there is not one already: rotating it signs every browser out,
# which is not something a re-run of an install script should do to somebody.
if [ -f "$CONFIG/gateway.env" ] && grep -q '^ENACTIVE_OWNER_KEY=' "$CONFIG/gateway.env"; then
  owner_key=$(grep '^ENACTIVE_OWNER_KEY=' "$CONFIG/gateway.env" | cut -d= -f2-)
  say "Keeping the existing owner key"
else
  owner_key=$(openssl rand -base64 36 | tr -d '\n')
  new_owner_key=yes
fi

install -o "$ACCOUNT" -g "$ACCOUNT" -m 0600 /dev/null "$CONFIG/gateway.env"
cat > "$CONFIG/gateway.env" <<ENV
ENACTIVE_REMOTE_DB=Server=127.0.0.1;User ID=enactive_gateway;Password=$gateway_password;Database=$DATABASE;
ENACTIVE_OWNER_KEY=$owner_key
ENV
chown "$ACCOUNT:$ACCOUNT" "$CONFIG/gateway.env"
chmod 0600 "$CONFIG/gateway.env"

# Read by backup.sh and verify-restore.sh, which run as $ACCOUNT and never see the password on a
# command line either.
install -o "$ACCOUNT" -g "$ACCOUNT" -m 0600 /dev/null "$CONFIG/backup.cnf"
cat > "$CONFIG/backup.cnf" <<CNF
[client]
user=enactive_backup
password=$backup_password
host=127.0.0.1
CNF
chown "$ACCOUNT:$ACCOUNT" "$CONFIG/backup.cnf"
chmod 0600 "$CONFIG/backup.cnf"

say "Unpacking the build"

# Each release in its own directory, named for the moment it arrived, with `current` a symlink.
# Rolling back is then moving one symlink - and the previous release is still on disk to move back
# to, which is the whole point of not unpacking over the top.
release="$ROOT/$(date --utc +%Y%m%dT%H%M%SZ)"
install -d -o "$ACCOUNT" -g "$ACCOUNT" -m 0755 "$release"
tar -xzf "$PACKAGE" -C "$release"
chown -R "$ACCOUNT:$ACCOUNT" "$release"

[ -f "$release/Enactive.Remote.Gateway.dll" ] || die "that package has no Enactive.Remote.Gateway.dll in it."

ln -sfn "$release" "$ROOT/current"
chown -h "$ACCOUNT:$ACCOUNT" "$ROOT/current"

# The operations scripts live beside the releases and outside them, because they are not part of a
# release and a rollback must not take the backup job with it.
install -d -o "$ACCOUNT" -g "$ACCOUNT" -m 0755 "$ROOT/deploy"
install -o "$ACCOUNT" -g "$ACCOUNT" -m 0755 "$(dirname "$0")/backup.sh" "$ROOT/deploy/backup.sh"
install -o "$ACCOUNT" -g "$ACCOUNT" -m 0755 "$(dirname "$0")/verify-restore.sh" "$ROOT/deploy/verify-restore.sh"
install -d -o "$ACCOUNT" -g "$ACCOUNT" -m 0750 /var/backups/enactive-remote

say "Service"

install -m 0644 "$(dirname "$0")/$SERVICE.service" "/etc/systemd/system/$SERVICE.service"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null
systemctl restart "$SERVICE"

say "Waiting for it to answer"

# The first start applies the migrations, so this can legitimately take a while. It is also the
# step most likely to fail, which is why the log is printed rather than a one-line "failed".
for attempt in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1; then
    printf 'healthy after %ss: %s\n' "$attempt" "$(curl -fsS http://127.0.0.1:$PORT/health)"
    break
  fi
  if [ "$attempt" -eq 60 ]; then
    journalctl -u "$SERVICE" -n 60 --no-pager >&2
    die "it did not become healthy in 60s. The log is above."
  fi
  sleep 1
done

say "Backups"

# Twice a day would be a guess; nightly is the same guess with fewer files. What is not a guess is
# that the verification runs half an hour after the dump, so a broken backup is discovered the same
# night rather than on the morning it is needed.
cat > /etc/cron.d/enactive-remote <<CRON
SHELL=/bin/bash
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin
MAILTO=root
0  3 * * * $ACCOUNT $ROOT/deploy/backup.sh
30 3 * * * $ACCOUNT $ROOT/deploy/verify-restore.sh
CRON
chmod 0644 /etc/cron.d/enactive-remote

# Once, now, rather than trusting tomorrow morning. A backup regime whose first proof of life is
# the night you need it is not a backup regime.
#
# Started from / rather than from wherever the caller was standing. These run as the service
# account, which usually cannot read an administrator's home directory, and `find` refuses to
# finish if it cannot return to the directory it started in. The scripts cd there themselves too;
# this is the same fix from the other side, because the first version of this failed here and took
# the rest of the install's output with it.
say "Taking one backup and restoring it, to prove both work"
backups_proven=yes
(cd / && sudo -u "$ACCOUNT" "$ROOT/deploy/backup.sh") || backups_proven=no
[ "$backups_proven" = yes ] && { (cd / && sudo -u "$ACCOUNT" "$ROOT/deploy/verify-restore.sh") || backups_proven=no; }

# Not fatal, and deliberately so. The gateway is installed and answering by this point, and losing
# the instructions below to a failed backup would trade the useful part of this run for the part
# that can be re-run on its own in one command.
if [ "$backups_proven" != yes ]; then
  printf '\n!! The backup or its verification FAILED. The gateway is installed and running.\n'
  printf '!! Fix that above, then:  sudo -u %s %s/deploy/backup.sh && sudo -u %s %s/deploy/verify-restore.sh\n' \
    "$ACCOUNT" "$ROOT" "$ACCOUNT" "$ROOT"
fi

say "Done. What is left is the part that changes what the world sees."

cat <<NEXT

The gateway is running on http://127.0.0.1:$PORT and nothing outside this machine can reach it yet.
Two steps remain, and they are yours because they take the site down and put it back up:

  1. Point the tunnel at it. The ingress for remote.enactive.com becomes http://127.0.0.1:$PORT -
     in cloudflared's config.yml, or in Cloudflare Zero Trust under Networks > Tunnels > Public
     hostnames if the tunnel is managed from there, in which case the file on this machine decides
     nothing. Then:

       sudo systemctl restart cloudflared

     RESTART, not reload: cloudflared's unit does not implement reload, and systemd answers
     "Job type reload is not applicable for unit cloudflared.service" rather than doing nothing
     quietly. The tunnel drops for a second or two.

     Then check it from outside, which is the only place the answer counts:

       curl -s https://remote.enactive.com/health

  2. Stop whatever is serving the site now:  sudo systemctl disable --now <the preview's unit>
     Disable it, do not delete it. It is the rollback.

Then open the site, sign in, and register a computer under Computers. The token it shows you is
shown ONCE.

A thing worth knowing before you look: until the desktop app is wired to connect, the panel will
show no computers at all. That is expected and not a fault of this install.
NEXT

if [ "${new_owner_key:-}" = yes ]; then
  cat <<KEY

The owner key is new. It is in $CONFIG/gateway.env, readable only by root and $ACCOUNT:

  sudo grep ENACTIVE_OWNER_KEY $CONFIG/gateway.env

It is not printed here on purpose - this output is going to a terminal, a scrollback buffer, and
possibly a chat window.
KEY
fi
