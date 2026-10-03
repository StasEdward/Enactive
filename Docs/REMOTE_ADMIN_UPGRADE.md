# Schema 3 to administrator-access preview

This is the stage-2 preview: administrator login, MFA enforcement, sessions and CLI recovery.
User lists, web registration decisions and quota editing are not implemented yet.
It includes the deployed master `f81ccee6142d` and adds schema 10. Protocol stays at 2.
Run server commands on **remoteenactive**, not the Keycloak host.

## 1. Transfer and inspect

Transfer `enactive-remote-admin-linux-x64.tar.gz` and its `.sha256` file to the server's
`/home/stasev/` using your existing SSH/SFTP connection. The public Cloudflare HTTP hostname
is not necessarily an SSH endpoint. On the server:

```bash
cd /home/stasev
sha256sum -c enactive-remote-admin-linux-x64.tar.gz.sha256
sudo systemctl stop enactive-deploy.timer
sudo systemctl is-active enactive-deploy.service
```

If the deploy service is still active, let it finish before continuing. Keep the timer stopped
until the configured deployment branch includes this preview; otherwise it could select an older build.
Do not run two installers concurrently.

```bash
sudo bash <<'BASH'
set -euo pipefail
! systemctl is-active --quiet enactive-deploy.service
test "$(/usr/bin/dotnet /opt/enactive-remote/current/Enactive.Remote.Gateway.dll --schema-version)" = 3
test "$(/usr/bin/dotnet /opt/enactive-remote/current/Enactive.Remote.Gateway.dll --protocol-version)" = 2
release=/opt/enactive-remote/releases/admin-preview-$(date -u +%Y%m%dT%H%M%SZ)
mkdir "$release"
tar --no-same-owner -xzf /home/stasev/enactive-remote-admin-linux-x64.tar.gz -C "$release"
chmod -R a+rX "$release"
test "$(/usr/bin/dotnet "$release/Enactive.Remote.Gateway.dll" --schema-version)" = 10
test "$(/usr/bin/dotnet "$release/Enactive.Remote.Gateway.dll" --protocol-version)" = 2
readlink -f /opt/enactive-remote/current > /opt/enactive-remote/admin-preview.previous
printf '%s\n' "$release" > /opt/enactive-remote/admin-preview.pending
printf 'Staged: %s\n' "$release"
BASH
```

This only stages files. It does not start the new binary or change the database.

## 2. Back up and install

Confirm `/etc/enactive-remote/backup.cnf` and the backup scripts target the database used
by the running service. The standard deployment uses `enactive_remote_v2`; preserve any local
backup overrides if your installation differs. Do not paste credentials into chat.

Run during a maintenance window: stopping the Gateway disconnects active clients. Take the backup
after stopping it so rollback would not discard writes made between the backup and migration.

```bash
sudo systemctl stop enactive-remote
sudo -u enactive /opt/enactive-remote/deploy/backup.sh
sudo -u enactive /opt/enactive-remote/deploy/verify-restore.sh
```

Proceed only if both backup and restore verification succeed. Record the printed backup paths
(database and Data Protection keys). If either fails, the current binary and schema are still unchanged:
restart `enactive-remote`, fix the backup, and repeat this section later.

```bash
sudo bash <<'BASH'
set -euo pipefail
! systemctl is-active --quiet enactive-remote
! systemctl is-active --quiet enactive-deploy.service
release=$(cat /opt/enactive-remote/admin-preview.pending)
case "$release" in /opt/enactive-remote/releases/admin-preview-*) ;; *) exit 1 ;; esac
test -f "$release/Enactive.Remote.Gateway.dll"
# Preserve the old verifier before replacing it with one that covers administrator records.
cp -p /opt/enactive-remote/deploy/verify-restore.sh /opt/enactive-remote/admin-preview.verify-restore.previous
install -m 755 "$release/operations/verify-restore.sh" /opt/enactive-remote/deploy/verify-restore.sh
ln -s "$release" /opt/enactive-remote/current.admin-preview
mv -Tf /opt/enactive-remote/current.admin-preview /opt/enactive-remote/current
systemctl start enactive-remote
BASH
curl --max-time 10 --fail-with-body -sS http://127.0.0.1:5099/health
sudo journalctl -u enactive-remote -n 50 --no-pager
```

Startup applies migration 10. A brief connection failure immediately after `start` may mean the
service is still starting; inspect the journal and retry health. Expect protocol 2 and `status: ok`.
**After migration, swapping the symlink back is insufficient.** The old build rejects schema 10.
Prefer fixing the new build/configuration. If rollback is necessary, stop the service and follow
`REMOTE_OPERATIONS.md` section 4.2 to restore the matching database and keys before starting the
previous binary. Never delete a migration marker to make an old binary accept the database.

## 3. Configure Keycloak access

Keep the existing SSH session open. Edit the service environment on the Gateway server:

```bash
sudoedit /etc/enactive-remote/gateway.env
```

Add all five settings, preserving all existing settings. Replace the secret locally with the actual
secret of Keycloak's confidential `enactive-admin` client:

```ini
ENACTIVE_ADMIN_ORIGIN=https://admin.enactive.dev
ENACTIVE_ADMIN_AUTHORITY=https://auth.enactive.app/realms/enactive
ENACTIVE_ADMIN_CLIENT_ID=enactive-admin
ENACTIVE_ADMIN_CLIENT_SECRET=REPLACE_LOCALLY
ENACTIVE_ADMIN_MFA_ACR=2
```

Keycloak must enforce password plus OTP for level 2 and return signed `acr` and `auth_time` claims.
Use the exact callback `https://admin.enactive.dev/admin/auth/callback`. Confirm the client flow
override and minimum/default ACR mapping in Keycloak before testing. A user attribute named `acr`
is not MFA enforcement. See `REMOTE_ADMINISTRATION.md` for the full trust requirements.

```bash
sudo systemctl restart enactive-remote
curl --max-time 10 --fail-with-body -sS http://127.0.0.1:5099/health
```

Obtain the user's exact subject from the trusted Keycloak administration console in realm `enactive`.
For the normal Keycloak subject mapping this is the user's ID. If a subject mapper is configured,
verify its actual mapping instead. An email address or username does not substitute for this ID.
Replace `EXACT_KEYCLOAK_SUBJECT` below locally:

```bash
sudo systemd-run --quiet --wait --pipe --collect \
  --property=User=enactive --property=Group=enactive \
  --property=WorkingDirectory=/opt/enactive-remote/current \
  --property=EnvironmentFile=/etc/enactive-remote/gateway.env \
  /usr/bin/dotnet /opt/enactive-remote/current/Enactive.Remote.Gateway.dll \
  admin administrators grant https://auth.enactive.app/realms/enactive EXACT_KEYCLOAK_SUBJECT
```

This lets systemd read its own environment-file syntax and keeps the database credential out of
shell commands, process arguments and chat. Use the same command with `revoke` instead of `grant`
to disable this administrator and end their sessions.

## 4. Verify the deployed boundary

- Open `https://admin.enactive.dev/admin/` in a private browser window. Complete password and OTP.
  Confirm the page shows an authenticated administrator session. Do not share tokens or cookies.
- Confirm `https://remote.enactive.dev/admin/` returns 404 and ordinary user login still works.
- Verify password-only authentication cannot complete administrative login; do not weaken the live
  Keycloak policy to perform this check. Canceling OTP must leave the administrative session anonymous.
- Sign out and confirm the administrative session is no longer authenticated.
- Run the backup and updated restore verification again to cover the new administrator tables.
- Keep the deployment timer paused until its configured branch contains this version or a successor.

Local validation covered 644 Gateway tests (including schema-3 upgrade and signed OIDC negative cases),
277 JavaScript tests and seven backup-completeness checks. It does not establish that the real
Keycloak client, tunnel and server environment are correctly configured; the checks above do that.
