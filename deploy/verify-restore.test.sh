#!/usr/bin/env bash
# Exercise version-aware completeness without touching a server. MySQL answers are mocked;
# selection and decompression of the backup and the verifier's failure exit are real.
set -euo pipefail
HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
scratch=$(mktemp -d /tmp/enactive-restore-test-XXXXXX)
trap 'rm -rf -- "$scratch"' EXIT
mkdir -p "$scratch/bin" "$scratch/backups"
printf '%s\n' '-- fixture dump' | gzip > "$scratch/backups/gateway-fixture.sql.gz"
cat > "$scratch/bin/mysql" <<'MOCK'
#!/usr/bin/env bash
query=''
while [ "$#" -gt 0 ]; do
  if [ "$1" = '-e' ]; then query=$2; break; fi
  shift
done
case "$query" in
  *'MAX(version)'*) echo "$TEST_VERSION" ;;
  *"column_name = 'revision'"*) printf '%s\n' ${TEST_COLUMNS:-} ;;
  *'SELECT table_name FROM information_schema.tables'*) printf '%s\n' $TEST_TABLES ;;
  '') cat >/dev/null ;;
esac
exit 0
MOCK
chmod +x "$scratch/bin/mysql"
base='admissions approvals audit commands devices enrollments events external_identities grants host_workspaces hosts invites notices runs schema_version tasks user_retention user_sessions user_streams users'
check() {
  local version=$1 tables=$2 expected=$3 status=0
  PATH="$scratch/bin:$PATH" TEST_VERSION="$version" TEST_TABLES="$tables" \
    ENACTIVE_BACKUP_DATABASE=gateway ENACTIVE_BACKUP_DIR="$scratch/backups" \
    ENACTIVE_BACKUP_DEFAULTS=/dev/null bash "$HERE/verify-restore.sh" >"$scratch/out" 2>&1 || status=$?
  if [ "$status" != "$expected" ]; then cat "$scratch/out"; exit 1; fi
}
check 1 "$base" 0
check 3 "$base signin_redemptions" 0
check 3 "$base" 1
admins='administrators administrator_sessions administrator_audit'
check 10 "$base signin_redemptions $admins" 0
for missing in $admins; do
  remaining=''
  for table in $admins; do [ "$table" = "$missing" ] || remaining="$remaining $table"; done
  check 10 "$base signin_redemptions $remaining" 1
  grep -q "no '$missing' table" "$scratch/out"
done
export TEST_COLUMNS='admissions.revision administrator_audit.detail'
check 11 "$base signin_redemptions $admins" 0
for missing in admissions.revision administrator_audit.detail; do
  TEST_COLUMNS=${TEST_COLUMNS//$missing/}
  check 11 "$base signin_redemptions $admins" 1
  grep -q "no '$missing' column" "$scratch/out"
  TEST_COLUMNS='admissions.revision administrator_audit.detail'
done
echo '10 restore completeness checks passed'
