# Install persisted quotas (schema 11 to 12)

This package adds shared quota defaults, per-user overrides, effective usage and the quota editor.
The protocol remains 2. Keycloak settings and existing administrator grants stay unchanged.
Run commands on the Gateway host, remoteenactive, using the standard `enactive_remote_v2` database.
If your backup uses a different database or overrides, adapt and verify those before proceeding.

Transfer `enactive-remote-quotas-linux-x64.tar.gz` and its `.sha256` file to `/home/stasev/`.
Verify the checksum there before executing the block below. It pauses automatic deployment, stages
and validates the binary, stops the Gateway, takes and verifies a backup, then switches the release.
There is downtime during backup verification. Before migration, a failure restarts the previous binary.
Once the new binary starts, no automatic rollback is attempted: schema 12 is incompatible with the
old binary's migration guard. Keep the recorded database/key backup and previous release path.

```bash
sudo bash <<'BASH'
set -euo pipefail
cd /home/stasev
sha256sum -c enactive-remote-quotas-linux-x64.tar.gz.sha256
systemctl stop enactive-deploy.timer
if systemctl is-active --quiet enactive-deploy.service; then
  echo 'Deployment is still running; retry after it finishes.'
  exit 1
fi
previous=$(readlink -f /opt/enactive-remote/current)
test "$(/usr/bin/dotnet "$previous/Enactive.Remote.Gateway.dll" --schema-version)" = 11
release=/opt/enactive-remote/releases/quotas-$(date -u +%Y%m%dT%H%M%SZ)
mkdir "$release"
tar --no-same-owner -xzf enactive-remote-quotas-linux-x64.tar.gz -C "$release"
chmod -R a+rX "$release"
test "$(/usr/bin/dotnet "$release/Enactive.Remote.Gateway.dll" --schema-version)" = 12
test "$(/usr/bin/dotnet "$release/Enactive.Remote.Gateway.dll" --protocol-version)" = 2
printf '%s\n' "$previous" > /opt/enactive-remote/quotas.previous
migrating=0
recover() {
  trap - ERR
  if [ "$migrating" = 0 ]; then
    systemctl start enactive-remote
    echo 'Installation stopped before migration; previous service restarted.'
  else
    echo 'New schema may already be applied. Inspect the journal; do not revert binaries alone.'
  fi
  exit 1
}
trap recover ERR
systemctl stop enactive-remote
sudo -u enactive env ENACTIVE_BACKUP_DATABASE=enactive_remote_v2 /opt/enactive-remote/deploy/backup.sh
sudo -u enactive env ENACTIVE_BACKUP_DATABASE=enactive_remote_v2 /opt/enactive-remote/deploy/verify-restore.sh
cp -p /opt/enactive-remote/deploy/verify-restore.sh /opt/enactive-remote/quotas.verify-restore.previous
install -m 755 "$release/operations/verify-restore.sh" /opt/enactive-remote/deploy/verify-restore.sh
ln -s "$release" "$release/activate-link"
mv -Tf "$release/activate-link" /opt/enactive-remote/current
migrating=1
systemctl start enactive-remote
for attempt in $(seq 1 15); do
  if curl --max-time 3 -fsS http://127.0.0.1:5099/health; then
    printf '\nQuota build started.\n'
    trap - ERR
    exit 0
  fi
  sleep 2
done
recover
BASH
```

Refresh `https://admin.enactive.dev/admin/` and reauthenticate with MFA before trying an intended
change. Check Shared quota defaults and View → Edit quotas. On a test account, save an override
with a reason, refresh and reopen the editor, verify the effective value, then reset it to inheritance.
Verify the before/after audit and confirm existing tasks can finish when storage is reduced.
Keep existing access-management controls and ordinary user login working as well.
Take and verify a post-upgrade backup with the newly installed verifier as well.

Keep automatic deployment paused until its configured branch includes this build or a successor.
For a necessary rollback, stop the service and restore the matching pre-upgrade database and Data
Protection keys following `REMOTE_OPERATIONS.md` section 4.2 before selecting the previous release.
Restoring that backup discards changes made since it; never remove schema-version rows to bypass
an older binary's guard. Prefer a forward correction after the new version has accepted writes.
