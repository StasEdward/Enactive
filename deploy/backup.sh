#!/usr/bin/env bash
# One backup of the gateway's schema, and of the Data Protection keys beside it.
#
# The keys are what every session cookie is signed with. A restore without them still has every
# account, computer and run, and signs everybody out - acceptable, and better than no restore; but the
# keys are small, and kept here a restore need not cost that.
#
# Credentials come from a MySQL option file, never from the command line: an argument is visible in
# `ps` to every account on the machine for as long as the dump runs.
#
# The dump is written to a temporary name and moved into place only after mysqldump has exited
# successfully. A backup directory must never contain a file that looks like last night's backup
# and is half a dump - that is worse than an empty directory, because an empty directory is
# obviously empty.
set -euo pipefail

# Every path below is absolute, so where this was started from is nobody's business - except that
# it IS, silently: this runs as the service account, and it is normally started from somewhere that
# account cannot read. GNU find returns to its starting directory when it finishes and fails if it
# cannot, so `find -delete` ended the whole job with "Failed to restore initial working directory:
# /home/someone: Permission denied" - a message about the caller's home directory, from a backup
# script, which is not a sentence that leads anywhere useful at three in the morning.
cd /

DEFAULTS_FILE="${ENACTIVE_BACKUP_DEFAULTS:-/etc/enactive-remote/backup.cnf}"
DATABASE="${ENACTIVE_BACKUP_DATABASE:-enactive_remote_v2}"
DESTINATION="${ENACTIVE_BACKUP_DIR:-/var/backups/enactive-remote}"
KEEP_DAYS="${ENACTIVE_BACKUP_KEEP_DAYS:-30}"
# Where the gateway keeps them: ENACTIVE_DATA/keys, and ENACTIVE_DATA is set in enactive-remote.service.
KEYS="${ENACTIVE_DATA:-/var/lib/enactive-remote}/keys"

mkdir -p "$DESTINATION"

stamp=$(date --utc +%Y%m%dT%H%M%SZ)
final="$DESTINATION/$DATABASE-$stamp.sql.gz"
partial="$final.partial"
keys_final="$DESTINATION/$DATABASE-$stamp.keys.tar.gz"
keys_partial="$keys_final.partial"

cleanup() { rm -f "$partial" "$keys_partial"; }
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

# After the dump is in place, so a fault here never costs the dump. Readable by this account alone:
# whoever can read the keys can mint a session for anybody.
#
# A missing directory fails the job rather than being skipped. The gateway makes its keys when it
# starts, so there is always one; not finding it means ENACTIVE_DATA points somewhere else - and a
# backup that quietly stopped covering the keys would be found out on the day of the restore.
if [ ! -d "$KEYS" ]; then
  echo "$final" >&2
  echo "No Data Protection keys at $KEYS: the dump above was taken, the keys were NOT. Set ENACTIVE_DATA to the gateway's." >&2
  exit 1
fi
(umask 077 && tar -czf "$keys_partial" -C "$(dirname "$KEYS")" "$(basename "$KEYS")")
mv "$keys_partial" "$keys_final"
trap - EXIT

# Deleted only after a new one is safely in place, and only by age. A count-based rule ("keep the
# last 7") quietly keeps seven copies of a failure that has been repeating for a week.
find "$DESTINATION" -name "$DATABASE-*.sql.gz" -type f -mtime "+$KEEP_DAYS" -delete
find "$DESTINATION" -name "$DATABASE-*.keys.tar.gz" -type f -mtime "+$KEEP_DAYS" -delete

echo "$final"
echo "$keys_final"
