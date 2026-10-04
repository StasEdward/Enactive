# Quota release validation

The operator confirmed the quota interface works. On 2026-10-04 the public health endpoint was
healthy, anonymous administrative API access was denied, public-origin administrative access was
hidden, and all 31 panel manifest entries matched the tested quota package.

## Server checks

Run this on `remoteenactive`. It prints versions, service/timer status, quota metadata and audit
records, then takes a backup and restores it into a temporary verification database. It does not
stop the gateway or change the active release. The restore verifier retains its temporary database
on failure for investigation. Credentials and key contents are not printed.

```bash
sudo bash <<'BASH'
set -euo pipefail
cd /
release=$(readlink -f /opt/enactive-remote/current)
printf 'Release: %s\n' "$release"
test "$(dotnet "$release/Enactive.Remote.Gateway.dll" --schema-version)" = 12
test "$(dotnet "$release/Enactive.Remote.Gateway.dll" --protocol-version)" = 2
curl --max-time 5 -fsS http://127.0.0.1:5099/health
printf '\n'
systemctl is-active enactive-remote
systemctl show enactive-deploy.timer -p ActiveState -p UnitFileState
systemctl list-timers --all enactive-deploy.timer
# Print only deployment identity fields: the same file also contains an API token.
if [ -f /etc/enactive-remote/deploy.env ]; then
  sed -n '/^ENACTIVE_DEPLOY_\(REPO\|BRANCH\|WORKFLOW\|DATABASE\)=/p' /etc/enactive-remote/deploy.env
fi
mysql --defaults-file=/etc/enactive-remote/backup.cnf --database=enactive_remote_v2 --table <<'SQL'
SELECT id, revision, settings FROM quota_defaults;
SELECT owner_id, revision, settings FROM user_quotas;
SELECT id, at, actor, action, target, detail
FROM administrator_audit WHERE action = 'quota.changed' ORDER BY id DESC LIMIT 10;
SQL
sudo -u enactive env ENACTIVE_BACKUP_DATABASE=enactive_remote_v2 /opt/enactive-remote/deploy/backup.sh
sudo -u enactive env ENACTIVE_BACKUP_DATABASE=enactive_remote_v2 /opt/enactive-remote/deploy/verify-restore.sh
BASH
```

Expected: schema/protocol assertions pass, service is active, shared defaults has exactly its `id=1`
row, saved quota changes have corresponding audit records, backup prints database/key archive paths,
and restore ends with `OK`. If the database has a custom name, use it consistently in the three
database arguments above. Keep the command output as release evidence.

## Enforcement on a test account

1. Use a separate test account with one registered computer. In its quota editor set Computers to
   `1`, supply a reason and confirm. Refresh and reopen the editor: effective limit must remain `1`.
2. In that account's ordinary panel try to register a second computer. The server must reject it
   because of the quota; the existing computer must remain available and usage must remain `1`.
3. Clear the Computers override, supply a reason and confirm. Refresh: the effective value must
   inherit the shared/startup setting. Registering a second computer should now be allowed if that
   inherited limit is greater than one. Remove the additional test computer afterwards.
4. Run the audit query above again. Both the override and reset must appear with the administrator,
   reason and before/after values. Existing accounts and their resources must stay accessible.

## Automatic deployment

Do not enable the timer based only on panel validation. Confirm the actual configured branch
contains the quota implementation and its latest successful workflow artifact supports schema 12
and protocol 2. The local `origin/master` checked on 2026-10-04 did not contain quota commit
`68399fc`; that local reference may be stale. Once the branch/artifact and server checks pass,
the operator can run `sudo systemctl enable --now enactive-deploy.timer` and inspect
`systemctl list-timers enactive-deploy.timer`.

Stage 6 stays open until production enforcement, audit, restore and deployment state are evidenced.
