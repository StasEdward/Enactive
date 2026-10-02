#!/usr/bin/env bash
#
# What pull-release.sh DECIDES, without a server, a token or a database.
#
# The plumbing - calling GitHub, unzipping, restarting a unit - is not what goes wrong here and is
# not what these cover. What goes wrong is the decisions: installing something already installed,
# applying a migration while nobody is watching, and rolling back after one. Each is a branch that
# fires exactly when a person is not there to see it, which is the reason to pin them.
#
# The script is SOURCED, not run, and its collaborators are replaced. That is why activate() and
# health_ok() are separate functions in it: a test can stand in for the two things that touch the
# machine and leave every decision above them real.
#
#   bash deploy/pull-release.test.sh
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

# ── a sandbox that looks like the server ────────────────────────────────────

# Each test gets its own tree, so one leaving a symlink behind cannot pass the next one for it.
sandbox() {
    ROOT=$(mktemp -d /tmp/enactive-deploy-test-XXXXXX)
    export ENACTIVE_DEPLOY_ROOT=$ROOT

    mkdir -p "$ROOT/releases/old" "$ROOT/releases/new"
    printf 'aaaaaaaaaaaa\n' > "$ROOT/releases/old/.commit"
    printf 'bbbbbbbbbbbb\n' > "$ROOT/releases/new/.commit"
    ln -sfn "$ROOT/releases/old" "$ROOT/current"

    # Sourced fresh each time so the stubs below are the ones this test set.
    # shellcheck source=/dev/null
    . "$HERE/pull-release.sh"

    ROOT=$ROOT
    activated=()
}

# Stands in for the two functions that touch the machine.
activate() { activated+=("$(basename "$1")"); ln -sfn "$1" "$ROOT/current"; }

cleanup() { rm -rf -- "$ROOT"; }

# ── the decision the whole unattended story rests on ────────────────────────

sandbox
check "a release whose schema is ahead of the database carries a migration" \
    "yes" "$(carries_migration 3 2 && echo yes || echo no)"

check "a release at the same schema does not" \
    "no" "$(carries_migration 2 2 && echo yes || echo no)"

# A rollback installs an OLDER build, which knows fewer migrations than the database has had
# applied. That must not read as "carries a migration", or a rollback would refuse itself.
check "a release behind the database does not carry a migration" \
    "no" "$(carries_migration 1 2 && echo yes || echo no)"
cleanup

# ── picking the artifact ────────────────────────────────────────────────────

sandbox

# What the API would return for a run carrying the artifact under its own name, one belonging to a
# different commit, and an expired one. Defined AFTER sandbox, which sources the script and would
# otherwise put the real one back over it.
artifacts_json() {
    cat <<'JSON'
{"artifacts": [
  {"name": "gateway-cccccccccccc", "expired": false, "archive_download_url": "https://example.invalid/other"},
  {"name": "gateway-bbbbbbbbbbbb", "expired": false, "archive_download_url": "https://example.invalid/right"},
  {"name": "gateway-dddddddddddd", "expired": true,  "archive_download_url": "https://example.invalid/gone"}
]}
JSON
}

# By name, never by position. A run carries more than one, and taking the first would ship whichever
# the API happened to list first - which is not a mistake anything downstream could catch, because
# the wrong build installs and runs perfectly well.
check "the artifact is chosen by the commit's own name" \
    "https://example.invalid/right" "$(artifact_url 1 bbbbbbbbbbbb 2>/dev/null)"

# The case that actually happened: a pull_request run's artifact is named for the MERGE commit, so
# nothing matches the branch head. Nothing must be returned - installing a near-match would be
# installing a commit that exists nowhere in the branch.
check "a run with no artifact for this commit yields nothing" \
    "" "$(artifact_url 1 aaaaaaaaaaaa 2>/dev/null)"

# And it must say what the run DOES have. The first version reported only that the artifact was
# missing and guessed at expiry, which sent the reader to look at retention while the answer was
# sitting unprinted in the listing.
check "and says what the run does have" \
    "yes" "$(artifact_url 1 aaaaaaaaaaaa 2>&1 >/dev/null | grep -q 'gateway-cccccccccccc' && echo yes || echo no)"

check "an expired artifact is not offered even under the right name" \
    "" "$(artifact_url 1 dddddddddddd 2>/dev/null)"
cleanup

# ── what is already installed ───────────────────────────────────────────────

sandbox
check "the deployed commit is read from the release the symlink points at" \
    "aaaaaaaaaaaa" "$(deployed_commit)"

rm -f "$ROOT/current"
check "no symlink means nothing is deployed, rather than an error" \
    "" "$(deployed_commit)"
cleanup

# ── keeping the last few releases ───────────────────────────────────────────

sandbox
KEEP=2

# Only the releases this test is about. The sandbox's own two would be the NEWEST of the set and
# would be the ones kept - which is how this test first failed, asserting about a fixture instead
# of about pruning.
rm -rf "$ROOT/releases"
mkdir -p "$ROOT/releases"

for n in 1 2 3 4 5; do
    mkdir -p "$ROOT/releases/r$n"
    # Distinct mtimes, oldest first, so "newest" means something.
    touch -d "2026-01-0$n" "$ROOT/releases/r$n"
done
ln -sfn "$ROOT/releases/r1" "$ROOT/current"

prune >/dev/null

check "pruning keeps the newest few" \
    "r4 r5" "$(cd "$ROOT/releases" && echo r4 r5 | tr ' ' '\n' | while read -r d; do [ -d "$d" ] && printf '%s ' "$d"; done | sed 's/ $//')"

check "pruning removes the ones past the cut" \
    "gone" "$([ -d "$ROOT/releases/r2" ] || [ -d "$ROOT/releases/r3" ] && echo kept || echo gone)"

# r1 is older than the cut and is what is RUNNING. Deleting the files under a running service to
# tidy up is the kind of cleanup that takes a site down at 3am.
check "pruning never removes the release that is running" \
    "yes" "$([ -d "$ROOT/releases/r1" ] && echo yes || echo no)"
cleanup

# ── a protocol change ───────────────────────────────────────────────────────

# main() itself, end to end. The guard is not a comparison worth testing on its own: what matters is
# that nothing after it - the schema check, --allow-migration - installs a release it refused. So
# GitHub, the download and the database are stood in for, and the BUILDS are faked by a `dotnet` on
# PATH that answers for the release directory it is pointed at, the way the real one does.
SAVED_PATH=$PATH

fake_server() {
    mkdir -p "$ROOT/bin" "$ROOT/config"
    CONFIG="$ROOT/config"

    # main() names any of these that is missing before it does anything.
    for tool in curl unzip python3 mysql systemctl; do
        printf '#!/usr/bin/env bash\nexit 0\n' > "$ROOT/bin/$tool"
    done

    # A build answers --schema-version and --protocol-version from files beside it. A build with no
    # .protocol file is the protocol-1 gateway, which had no such switch: it went on to start as a web
    # server, and died for want of a database with nothing on stdout - which is what this does.
    cat > "$ROOT/bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
dir=$(dirname "$1")
case $2 in
    --schema-version) cat "$dir/.schema" ;;
    --protocol-version)
        [ -f "$dir/.protocol" ] && exec cat "$dir/.protocol"
        echo "Unhandled exception. System.InvalidOperationException: Set ENACTIVE_REMOTE_DB" >&2
        exit 134 ;;
esac
FAKE
    chmod +x "$ROOT/bin/"*
    PATH="$ROOT/bin:$SAVED_PATH"

    export ENACTIVE_DEPLOY_REPO=owner/repo ENACTIVE_DEPLOY_BRANCH=main ENACTIVE_DEPLOY_TOKEN=token

    # What is running: the protocol-1 gateway at schema 2, on a database at 2.
    printf '2\n' > "$ROOT/releases/old/.schema"
    APPLIED=2

    newest_green_run() { echo "1 bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"; }
    artifact_url() { echo "https://example.invalid/right"; }
    applied_schema_version() { echo "$APPLIED"; }
    health_ok() { return 0; }

    # Sourcing the script put the real one back; this one only moves the symlink.
    activate() { ln -sfn "$1" "$ROOT/current"; }

    # A new name for every run, as a real clock gives when the timer comes back ten minutes later. Two
    # runs inside one second would otherwise share a name and hide a second download.
    cat > "$ROOT/bin/date" <<'FAKE'
#!/usr/bin/env bash
count=$(( $(cat "$ENACTIVE_DEPLOY_ROOT/date-count" 2>/dev/null || echo 0) + 1 ))
printf '%s\n' "$count" > "$ENACTIVE_DEPLOY_ROOT/date-count"
printf '20260101T0000%02dZ\n' "$count"
FAKE
    chmod +x "$ROOT/bin/date"

    # The download: a build of the schema and protocol this test sets.
    fetch() {
        printf 'x' >> "$ROOT/fetches"
        mkdir -p "$2"
        printf '%s\n' "$NEW_SCHEMA" > "$2/.schema"
        [ -z "$NEW_PROTOCOL" ] || printf '%s\n' "$NEW_PROTOCOL" > "$2/.protocol"
    }
}

# Run in a subshell, because fail() exits; what it did is read back from the tree.
run_main() { ( main "$@" ) >"$ROOT/out" 2>"$ROOT/err"; }
running() { cat "$ROOT/current/.commit"; }

sandbox
fake_server

# The case the guard exists for. The protocol-2 schema starts again at version 1, BELOW the protocol-1
# database's 2, so the migration check reads it as a rollback and would install it - onto a database
# it cannot read, where it would create its own tables beside the old ones.
NEW_SCHEMA=1 NEW_PROTOCOL=2
run_main

check "a protocol change is parked even when the schema version is lower" \
    "aaaaaaaaaaaa parked" "$(running) $([ -s "$ROOT/PARKED" ] && echo parked || echo not-parked)"

check "and it says to install it by hand" \
    "yes" "$(grep -qF 'protocol change - install by hand (REMOTE_OPERATIONS §cutover)' "$ROOT/err" && echo yes || echo no)"

# --allow-migration is the answer to a migration. A protocol change needs a new database and a new
# environment first, which no flag on this script provides.
run_main --allow-migration
check "--allow-migration does not install a protocol change either" \
    "aaaaaaaaaaaa" "$(running)"

# A protocol-2 build can sit parked for days before somebody does the cutover, and the timer comes back
# every ten minutes. Fetched again each time, under a new name each time, it filled the disk with copies.
check "a parked release is downloaded once, not on every run" \
    "x" "$(cat "$ROOT/fetches")"
cleanup

sandbox
fake_server

# And the guard does not stand in the way of an ordinary release of the protocol that is running.
printf '2\n' > "$ROOT/releases/old/.protocol"
NEW_SCHEMA=2 NEW_PROTOCOL=2
run_main
check "a release of the running protocol at the same schema is installed" \
    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" "$(running)"
cleanup

PATH=$SAVED_PATH

printf '\n%s passed, %s failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
