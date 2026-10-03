#!/usr/bin/env bash
# Restores the newest backup into a scratch database and checks that what came back is a database.
#
# THIS IS THE HALF THAT IS USUALLY MISSING. A backup job that runs every night and has never been
# restored is not a backup; it is a cron entry with a good reputation. The failures it hides are
# ordinary ones - a dump truncated by a full disk, a schema the running build no longer matches, a
# grant that lets the account read rows but not routines - and every one of them is discovered on
# the day the data is already gone.
#
# So this restores for real, into a database named for the moment it ran, asserts a few things that
# could only be true of a working copy, and drops it. It leaves the scratch database behind ONLY
# when it failed, so there is something to look at.
set -euo pipefail

# See backup.sh: this runs as the service account, from wherever the caller happened to be, and
# `find` refuses to finish if it cannot get back there. Every path here is absolute.
cd /

DEFAULTS_FILE="${ENACTIVE_BACKUP_DEFAULTS:-/etc/enactive-remote/backup.cnf}"
DATABASE="${ENACTIVE_BACKUP_DATABASE:-enactive_remote_v2}"
DESTINATION="${ENACTIVE_BACKUP_DIR:-/var/backups/enactive-remote}"

newest=$(find "$DESTINATION" -name "$DATABASE-*.sql.gz" -type f -printf '%T@ %p\n' \
         | sort -rn | head -1 | cut -d' ' -f2-)

if [ -z "$newest" ]; then
  echo "No backup found in $DESTINATION. Nothing was verified." >&2
  exit 1
fi

# Refuse anything older than two days rather than cheerfully verifying a stale file: a backup job
# that stopped a week ago and a backup job that works look identical to a restore of the newest
# file, and only one of them is fine.
age_days=$(( ( $(date +%s) - $(stat -c %Y "$newest") ) / 86400 ))
if [ "$age_days" -gt 2 ]; then
  echo "The newest backup, $newest, is $age_days days old. The backup job is not running." >&2
  exit 1
fi

scratch="${DATABASE}_verify_$(date --utc +%Y%m%d%H%M%S)"
echo "Restoring $newest into $scratch"

mysql --defaults-file="$DEFAULTS_FILE" \
  -e "CREATE DATABASE \`$scratch\` CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci"

drop() { mysql --defaults-file="$DEFAULTS_FILE" -e "DROP DATABASE IF EXISTS \`$scratch\`"; }

# A failure anywhere from here on leaves the scratch database behind on purpose - there is
# something to look at, and a verification that tidies away its own evidence is not much of a
# verification. What it must not do is leave it behind SILENTLY: the restore failing on a
# permission was the first thing this job ever did, and without this the only trace was a database
# nobody was told about, once per nightly run, for as long as it kept failing.
stranded() {
  printf '\nLeft %s in place to look at. Drop it when you are done:\n' "$scratch" >&2
  printf '  mysql --defaults-file=%s -e "DROP DATABASE \\`%s\\`"\n' "$DEFAULTS_FILE" "$scratch" >&2
}
trap stranded ERR

gunzip -c "$newest" | mysql --defaults-file="$DEFAULTS_FILE" "$scratch"

ask() { mysql --defaults-file="$DEFAULTS_FILE" -N -B -D "$scratch" -e "$1"; }

failed=0
complain() { echo "FAILED: $1" >&2; failed=1; }

# The schema version the dump records, or 0 when it has no schema_version to ask - which check 1 then
# names as a missing table, rather than this stopping the run on the error.
version=$(ask "SELECT COALESCE(MAX(version), 0) FROM schema_version" 2>/dev/null) || version=0

# 1. The tables this build expects: protocol 2's (001_initial.sql), the Migrator's schema_version, and from
#    schema version 3 the sign-in redemptions (003_signin_redemptions.sql). A dump that ended early
#    restores without error and simply has fewer tables in it, which is the exact shape of a truncated
#    backup. By version, because this is run on the backup taken just BEFORE a parked migration is
#    installed (REMOTE_OPERATIONS §4.2): asking that dump for the migration's table would fail the check
#    that clears the way for the migration.
expected="admissions approvals audit commands devices enrollments events external_identities grants
          host_workspaces hosts invites notices runs schema_version tasks user_retention user_sessions
          user_streams users"
if [ "$version" -ge 3 ]; then
  expected="$expected signin_redemptions"
fi
# Administrative credentials and their revocations must be backed up too; otherwise a
# truncated schema-10 dump could pass while silently losing the separate security domain.
if [ "$version" -ge 10 ]; then
  expected="$expected administrators administrator_sessions administrator_audit"
fi
actual=$(ask "SELECT table_name FROM information_schema.tables
              WHERE table_schema = '$scratch' ORDER BY table_name" | tr '\n' ' ')

for table in $expected; do
  case " $actual " in
    *" $table "*) ;;
    *) complain "the restored database has no '$table' table" ;;
  esac
done

# 2. The schema version. A restore of a database that a newer build has since migrated is not a
#    restore this build can run against, and it is better to learn that here. Read above, before check 1.
if [ "$version" -lt 1 ]; then
  complain "the restored database records no applied migration"
fi
# Access management depends on revisions and independent audit detail. A dump with the
# version row but missing these columns must not be accepted as a usable schema-11 backup.
if [ "$version" -ge 11 ]; then
  columns=$(ask "SELECT CONCAT(table_name, '.', column_name) FROM information_schema.columns
                  WHERE table_schema = '$scratch' AND
                    ((table_name = 'admissions' AND column_name = 'revision') OR
                     (table_name = 'administrator_audit' AND column_name = 'detail'))" | tr '\n' ' ')
  for column in admissions.revision administrator_audit.detail; do
    case " $columns " in
      *" $column "*) ;;
      *) complain "the restored database has no '$column' column" ;;
    esac
  done
fi

echo "Schema version $version, $(echo "$actual" | wc -w) tables."

# 3. Each account's line is ahead of the rows numbered from it. Every account has its own counter in
#    user_streams, and its events and notices take their ordinals from it; one behind would hand out
#    an ordinal that already exists, the insert would fail on the account's unique ordinal, and that
#    person's panel would stop seeing anything new - while everybody else's went on working, which is
#    why one global check could not see it.
behind=$(ask "SELECT s.owner_id FROM user_streams s
              WHERE s.value < GREATEST(
                  COALESCE((SELECT MAX(e.ordinal) FROM events e WHERE e.owner_id = s.owner_id), 0),
                  COALESCE((SELECT MAX(n.ordinal) FROM notices n WHERE n.owner_id = s.owner_id), 0))
              ORDER BY s.owner_id")
for owner in $behind; do
  complain "the event line of account $owner is behind the rows numbered from it"
done

#    And every account HAS a line. The gateway refuses to number anything for an account without one,
#    so such an account could start nothing - and the check above, which reads the lines, cannot see it.
lineless=$(ask "SELECT u.id FROM users u
                WHERE NOT EXISTS (SELECT 1 FROM user_streams s WHERE s.owner_id = u.id)
                ORDER BY u.id")
for owner in $lineless; do
  complain "account $owner has no event line (no user_streams row)"
done

# 4. Nothing belongs to an account that does not exist. The foreign keys should make that impossible -
#    and a restore is exactly when one would find out otherwise: a dump turns the checks off while it
#    loads, so rows it carries are never checked against their owners. Every column that names an
#    owner is found by its name rather than listed here, so a table added later is covered without
#    anybody remembering this file.
owned=$(ask "SELECT CONCAT(table_name, '.', column_name) FROM information_schema.columns
             WHERE table_schema = '$scratch' AND column_name IN ('owner_id', 'user_id')
               AND table_name <> 'users'
             ORDER BY table_name, column_name")
for column in $owned; do
  table=${column%%.*}
  name=${column#*.}
  orphans=$(ask "SELECT COUNT(*) FROM \`$table\` t
                 WHERE t.\`$name\` IS NOT NULL
                   AND NOT EXISTS (SELECT 1 FROM users u WHERE u.id = t.\`$name\`)")
  if [ "$orphans" != "0" ]; then
    complain "$orphans row(s) in $table name an owner ($name) that has no account"
  fi
done
echo "Ownership checked in $(echo "$owned" | wc -w) columns."

if [ "$failed" -ne 0 ]; then
  echo "Left $scratch in place to look at." >&2
  exit 1
fi

drop
echo "OK: $newest restores into a working database."
