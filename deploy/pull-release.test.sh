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

printf '\n%s passed, %s failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
