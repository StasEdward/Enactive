#!/usr/bin/env bash
#
# What backup.sh keeps, without a database: mysqldump is faked on PATH, and the rest is real.
#
# The dump itself is proven where it matters, by verify-restore.sh restoring it. What is pinned here is
# the part a restore cannot see: that the Data Protection keys are kept beside it, readable by nobody
# else, and that losing track of them is a failure somebody hears about.
#
#   bash deploy/backup.test.sh
set -uo pipefail

HERE=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

passed=0
failed=0

check() {
    local what=$1 expected=$2 actual=$3
    if [ "$expected" = "$actual" ]; then
        passed=$((passed + 1))
        printf 'ok   %s\n' "$what"
    else
        failed=$((failed + 1))
        printf 'FAIL %s\n       expected: %s\n       actual:   %s\n' "$what" "$expected" "$actual"
    fi
}

ROOT=$(mktemp -d /tmp/enactive-backup-test-XXXXXX)
trap 'rm -rf -- "$ROOT"' EXIT

mkdir -p "$ROOT/bin" "$ROOT/data/keys" "$ROOT/backups"
printf '#!/usr/bin/env bash\necho "-- a dump"\n' > "$ROOT/bin/mysqldump"
chmod +x "$ROOT/bin/mysqldump"
printf '<key id="1"/>\n' > "$ROOT/data/keys/key-1.xml"

backup() {
    PATH="$ROOT/bin:$PATH" ENACTIVE_DATA="$ROOT/data" ENACTIVE_BACKUP_DIR="$ROOT/backups" \
        ENACTIVE_BACKUP_DATABASE=gateway ENACTIVE_BACKUP_DEFAULTS=/dev/null \
        bash "$HERE/backup.sh" >"$ROOT/out" 2>"$ROOT/err"
}

backup
check "a backup succeeds" "0" "$?"

keys=$(ls "$ROOT/backups"/gateway-*.keys.tar.gz 2>/dev/null)
check "the keys are kept beside the dump" \
    "keys/key-1.xml" "$(tar -tzf "$keys" 2>/dev/null | grep 'key-1.xml')"

# Whoever can read the keys can mint a session for anybody.
check "and only the backup's own account can read them" \
    "600" "$(stat -c %a "$keys" 2>/dev/null)"

rm -rf "$ROOT/backups"/* "$ROOT/data/keys"
backup
status=$?

# The gateway makes its keys when it starts, so none at all means ENACTIVE_DATA is wrong - and a
# backup that quietly stopped keeping them would be found out on the day of the restore.
check "no keys where the gateway keeps them fails the job" "1" "$status"
check "after the dump was taken, not instead of it" \
    "1" "$(ls "$ROOT/backups"/gateway-*.sql.gz 2>/dev/null | wc -l | tr -d ' ')"

printf '\n%s passed, %s failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
