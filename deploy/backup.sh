#!/usr/bin/env bash
# One backup of the gateway's schema.
#
# Credentials come from a MySQL option file, never from the command line: an argument is visible in
# `ps` to every account on the machine for as long as the dump runs.
#
# The dump is written to a temporary name and moved into place only after mysqldump has exited
# successfully. A backup directory must never contain a file that looks like last night's backup
# and is half a dump - that is worse than an empty directory, because an empty directory is
# obviously empty.
set -euo pipefail

DEFAULTS_FILE="${ENACTIVE_BACKUP_DEFAULTS:-/etc/enactive-remote/backup.cnf}"
DATABASE="${ENACTIVE_BACKUP_DATABASE:-enactive_remote}"
DESTINATION="${ENACTIVE_BACKUP_DIR:-/var/backups/enactive-remote}"
KEEP_DAYS="${ENACTIVE_BACKUP_KEEP_DAYS:-30}"

mkdir -p "$DESTINATION"

stamp=$(date --utc +%Y%m%dT%H%M%SZ)
final="$DESTINATION/$DATABASE-$stamp.sql.gz"
partial="$final.partial"

cleanup() { rm -f "$partial"; }
trap cleanup EXIT

# --single-transaction: a consistent snapshot without locking the gateway out of its own tables.
# --routines --events --triggers: everything that is schema and not rows. There are none today, and
#   a backup that silently stops covering them the day one is added is the kind of gap nobody finds
#   until a restore.
# --set-gtid-purged=OFF: this dump is for restoring a copy, not for seeding a replica; leaving GTID
#   state in it makes a restore into a scratch database fail for reasons that have nothing to do
#   with the data.
# --no-tablespaces: mysqldump 8.0 reads INFORMATION_SCHEMA.FILES unless told not to, and reading it
#   needs the PROCESS privilege - which is server-wide and would let this account watch every query
#   running on the machine. This schema has no tablespaces of its own, so the choice is between a
#   flag and a privilege that exists for something else entirely.
mysqldump \
  --defaults-file="$DEFAULTS_FILE" \
  --single-transaction \
  --no-tablespaces \
  --routines --events --triggers \
  --set-gtid-purged=OFF \
  "$DATABASE" \
  | gzip -9 > "$partial"

mv "$partial" "$final"
trap - EXIT

# Deleted only after a new one is safely in place, and only by age. A count-based rule ("keep the
# last 7") quietly keeps seven copies of a failure that has been repeating for a week.
find "$DESTINATION" -name "$DATABASE-*.sql.gz" -type f -mtime "+$KEEP_DAYS" -delete

echo "$final"
