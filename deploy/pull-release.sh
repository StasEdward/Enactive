#!/usr/bin/env bash
#
# Installs the newest GREEN build of the gateway, if there is one and if it is safe to do so
# unattended. Run by enactive-deploy.timer as the enactive account.
#
# The server is on a private address and nothing on the internet can reach it, so deployment is
# PULL: this asks GitHub what the newest passing build of the tracked branch is, rather than a
# pipeline pushing one in. That also means the credential lives here and is read-only.
#
# What it will not do on its own is apply a MIGRATION. MySQL cannot roll DDL back, so a schema
# change is the one step whose failure cannot be undone by putting the old files back - and the
# reason deployment used to be manual altogether was that somebody should be watching when one is
# applied. A release carrying a migration is downloaded, checked and PARKED, and this says so
# loudly; installing it is a person's decision, made with --allow-migration while looking at it.
#
# Nor will it install a release that speaks a different PROTOCOL from the running one. A protocol
# change means a new database and a new environment, and no flag here provides either; such a release
# is parked for the cutover in Docs/REMOTE_OPERATIONS.md.
#
# cd / first, always. Started from a directory that later disappears, every relative path in here
# resolves somewhere else or fails with an error about the working directory that reads like a
# permissions problem - which cost an afternoon during the first install.
set -euo pipefail
cd /

ROOT=${ENACTIVE_DEPLOY_ROOT:-/opt/enactive-remote}
CONFIG=${ENACTIVE_DEPLOY_CONFIG:-/etc/enactive-remote}
UNIT=${ENACTIVE_DEPLOY_UNIT:-enactive-remote}
HEALTH=${ENACTIVE_DEPLOY_HEALTH:-http://127.0.0.1:5099/health}
KEEP=${ENACTIVE_DEPLOY_KEEP:-5}

# The database the running gateway uses. Protocol 2 lives in a new one beside the protocol-1 database,
# which is kept untouched as the rollback - so asking the old name would compare a protocol-2 release
# with the schema of a database it never runs against. Until the cutover, deploy.env sets
# ENACTIVE_DEPLOY_DATABASE=enactive_remote: this script goes onto the protocol-1 server first, so
# that its guard is there before the first protocol-2 build can be (REMOTE_OPERATIONS §cutover).
# Read again in main(), after deploy.env.
DATABASE=${ENACTIVE_DEPLOY_DATABASE:-enactive_remote_v2}

# How long to give the service to come up before calling it failed. It restarts in about a second;
# this is generous because the cost of being wrong is a rollback nobody needed.
HEALTH_ATTEMPTS=${ENACTIVE_DEPLOY_HEALTH_ATTEMPTS:-15}
HEALTH_INTERVAL=${ENACTIVE_DEPLOY_HEALTH_INTERVAL:-2}

say() { printf '%s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }

# ── what is running now ─────────────────────────────────────────────────────

# The commit of the release the symlink points at, or empty when there is none. Read from a file
# written at install time rather than worked out from the directory name: the name is a timestamp
# somebody may have renamed, and this answers "which commit is on the server", which is the first
# question of every incident.
deployed_commit() {
    local marker="$ROOT/current/.commit"
    [ -r "$marker" ] && cat "$marker" || true
}

# The highest migration this database has actually had applied. Read with the BACKUP account, which
# already exists and is read-only on this schema - a deploy check has no business holding a
# credential that can write.
applied_schema_version() {
    mysql --defaults-file="$CONFIG/backup.cnf" -N -B \
          -e "SELECT COALESCE(MAX(version), 0) FROM schema_version" "$DATABASE"
}

# What a release WOULD apply. Asked of the build itself: migrations are embedded resources, so an
# unzipped release has no .sql files to count, and this needs no configuration and no database.
release_schema_version() {
    local dir=$1
    dotnet "$dir/Enactive.Remote.Gateway.dll" --schema-version
}

# The protocol a build speaks, asked of the build the same way. The schema version cannot answer this:
# protocol 2 started its schema again at version 1, BELOW the protocol-1 database's 2, so by schema
# alone a protocol-2 release reads as a rollback and would be installed onto a database it cannot read.
#
# Prints the number, or NOTHING when the build does not answer. The protocol-1 gateway has no such
# switch: it does not refuse an unknown argument but goes on to start as a web server, and dies for want
# of ENACTIVE_REMOTE_DB with nothing on stdout. A broken build - a bad download, a missing runtime - says
# nothing too, so what a silence means is left to the caller, who knows what is running. The variable is
# taken away so that dying is what the old gateway does, and the timeout bounds the case where it starts.
protocol_version() {
    local dir=$1 answer
    answer=$(env -u ENACTIVE_REMOTE_DB timeout 60 \
                 dotnet "$dir/Enactive.Remote.Gateway.dll" --protocol-version 2>/dev/null) || answer=
    # A build run on Windows ends the line with \r, which would read as "not a number" there.
    answer=${answer%$'\r'}
    case $answer in
        ''|*[!0-9]*) ;;
        *) echo "$answer" ;;
    esac
}

# The protocol of the running release, kept in a file beside it once known. Asked of the protocol-1
# binary on every run, the question crashed it - an unhandled exception in the journal, and perhaps a
# core dump - every ten minutes for as long as a protocol-2 release waited parked for the cutover. That
# binary cannot answer, so step 0 of the cutover writes its file by hand.
#
# Silence from the RUNNING release counts as the protocol-1 gateway for this run - but only a number is
# kept. A silence kept as 1 outlived its cause: one unanswered question on a protocol-2 server (a first
# start after an install by hand, a timeout, a runtime half upgraded) parked every later protocol-2
# release as a "protocol change" until somebody found the file and deleted it.
running_protocol() {
    local cache="$ROOT/current/.protocol-version" answer
    if [ -s "$cache" ]; then
        cat "$cache"
        return 0
    fi
    answer=$(protocol_version "$ROOT/current")
    if [ -z "$answer" ]; then
        echo 1
        return 0
    fi
    printf '%s\n' "$answer" > "$cache" 2>/dev/null || true
    printf '%s\n' "$answer"
}

# The directory this commit was already downloaded into, if a run before this one fetched it and
# parked it. Found by the commit, not by the name a run would give it: that name starts with the
# moment of the run, so it never matched, and a release parked for days was downloaded again every ten
# minutes until the disk filled. Only a directory holding .commit counts - it is written after the
# download succeeds, so a half-unzipped one is fetched again rather than trusted.
downloaded_release() {
    local sha=$1 dir
    for dir in "$ROOT/releases/"*-"${sha:0:12}"; do
        if [ -r "$dir/.commit" ] && [ "$(cat "$dir/.commit")" = "$sha" ]; then
            printf '%s\n' "$dir"
            return 0
        fi
    done
}

# ── deciding ────────────────────────────────────────────────────────────────

# Whether installing this release would change the schema. Kept as its own function because it is
# the single decision the whole unattended story rests on, and it is the one worth testing.
carries_migration() {
    local release_version=$1 applied_version=$2
    [ "$release_version" -gt "$applied_version" ]
}

# ── acting ──────────────────────────────────────────────────────────────────

health_ok() {
    local attempt=1
    while [ "$attempt" -le "$HEALTH_ATTEMPTS" ]; do
        if curl -fsS --max-time 5 "$HEALTH" >/dev/null 2>&1; then
            return 0
        fi
        sleep "$HEALTH_INTERVAL"
        attempt=$((attempt + 1))
    done
    return 1
}

# Points current at a release and restarts. Separate from the deciding so a rollback is the same
# code path as an install - a rollback that runs code nothing else runs is a rollback nobody has
# ever tested.
activate() {
    local dir=$1
    ln -sfn "$dir" "$ROOT/current.new"
    mv -Tf "$ROOT/current.new" "$ROOT/current"
    systemctl restart "$UNIT"
}

prune() {
    # Newest first, skip the ones being kept, remove the rest. The release currently pointed at is
    # never removed even if it has fallen past the cut: what is running stays on disk.
    local current
    current=$(readlink -f "$ROOT/current" 2>/dev/null || true)

    ls -1dt "$ROOT/releases"/*/ 2>/dev/null | tail -n +$((KEEP + 1)) | while read -r old; do
        [ "$(readlink -f "$old")" = "$current" ] && continue
        rm -rf -- "$old"
        say "Removed old release $(basename "$old")"
    done
}

# ── talking to GitHub ───────────────────────────────────────────────────────

api() {
    curl -fsS --max-time 60 \
        -H "Authorization: Bearer $ENACTIVE_DEPLOY_TOKEN" \
        -H "Accept: application/vnd.github+json" \
        -H "X-GitHub-Api-Version: 2022-11-28" \
        "$@"
}

# The newest run of the workflow that SUCCEEDED on the tracked branch. Success is asked of GitHub
# rather than inferred from an artifact existing: a run can upload one and then fail a later step,
# and "there is a build" is not the same claim as "the tests passed".
#
# event=push, and that is not a detail. The artifact is named for github.sha, which equals the run's
# head_sha only on a push. On a PULL_REQUEST run github.sha is the merge commit GitHub builds - a
# commit that exists nowhere in the branch - so its artifact is called gateway-<merge sha> while
# this script, reading head_sha, looks for gateway-<branch head> and finds nothing. Both runs happen
# for every push once a pull request is open, and the pull_request one is often the newer of the
# two, so without this the deploy stops working the day a PR is opened and starts again the day it
# is merged, for no reason anybody could see from here.
newest_green_run() {
    api "https://api.github.com/repos/$ENACTIVE_DEPLOY_REPO/actions/workflows/$ENACTIVE_DEPLOY_WORKFLOW/runs?branch=$ENACTIVE_DEPLOY_BRANCH&status=success&event=push&per_page=1" \
        | python3 -c 'import json,sys; r=json.load(sys.stdin)["workflow_runs"]; print(f"{r[0]['"'"'id'"'"']} {r[0]['"'"'head_sha'"'"']}" if r else "")'
}

# The download URL of this run's gateway artifact, by NAME. A run has several artifacts and taking
# the first one would silently ship whichever the API happened to list first.
#
# When nothing matches it names what the run DOES have, on stderr. The first version said only that
# the artifact was missing and guessed at expiry, which sent the reader to look at retention while
# the actual answer - a pull_request run whose artifact is named for a merge commit - was sitting
# right there in the listing, unprinted.
artifact_url() {
    local run=$1 sha=$2
    artifacts_json "$run" | python3 -c "
import json, sys

name = 'gateway-$sha'
artifacts = json.load(sys.stdin)['artifacts']

for a in artifacts:
    if a['name'] == name and not a['expired']:
        print(a['archive_download_url'])
        break
else:
    have = ', '.join(f\"{a['name']}{' (expired)' if a['expired'] else ''}\" for a in artifacts)
    print('This run has: ' + (have or 'no artifacts at all'), file=sys.stderr)
"
}

# Split out so a test can stand in for the call without stubbing curl itself.
artifacts_json() {
    api "https://api.github.com/repos/$ENACTIVE_DEPLOY_REPO/actions/runs/$1/artifacts"
}

fetch() {
    local url=$1 into=$2 zip
    zip=$(mktemp /tmp/enactive-release-XXXXXX.zip)
    # shellcheck disable=SC2064
    trap "rm -f '$zip'" RETURN

    api -L -o "$zip" "$url"
    mkdir -p "$into"
    unzip -q "$zip" -d "$into"

    # The published output is not executable; the unit runs it through dotnet, but the apphost is
    # there and somebody will try it.
    chmod -R u+rwX "$into"
}

# ── the run ─────────────────────────────────────────────────────────────────

main() {
    # Named up front, because every one of these fails LATER as something else. A missing python3
    # empties a pipeline and reads as "GitHub returned nothing"; a missing unzip reads as a corrupt
    # artifact; a missing mysql reads as a database that cannot be reached.
    local missing=()
    for tool in curl unzip python3 mysql dotnet systemctl; do
        command -v "$tool" >/dev/null 2>&1 || missing+=("$tool")
    done
    [ ${#missing[@]} -eq 0 ] || fail "Not installed: ${missing[*]}. Install them and run this again."

    local allow_migration=no
    for argument in "$@"; do
        case $argument in
            --allow-migration) allow_migration=yes ;;
            *) fail "Unknown argument '$argument'. The only one is --allow-migration." ;;
        esac
    done

    # shellcheck source=/dev/null
    [ -r "$CONFIG/deploy.env" ] && . "$CONFIG/deploy.env"

    # Again, now that deploy.env has been read. The timer's unit hands it over as the environment, but
    # a run by hand (--allow-migration) gets it only here - and would ask the wrong database.
    DATABASE=${ENACTIVE_DEPLOY_DATABASE:-$DATABASE}

    : "${ENACTIVE_DEPLOY_REPO:?Set ENACTIVE_DEPLOY_REPO in $CONFIG/deploy.env, e.g. owner/repo}"
    : "${ENACTIVE_DEPLOY_BRANCH:?Set ENACTIVE_DEPLOY_BRANCH in $CONFIG/deploy.env}"
    : "${ENACTIVE_DEPLOY_TOKEN:?Set ENACTIVE_DEPLOY_TOKEN in $CONFIG/deploy.env - a fine-grained token with Actions: read on that repository, and nothing else}"
    ENACTIVE_DEPLOY_WORKFLOW=${ENACTIVE_DEPLOY_WORKFLOW:-build.yml}

    local run sha
    read -r run sha < <(newest_green_run)
    [ -n "${sha:-}" ] || fail "No successful build of $ENACTIVE_DEPLOY_BRANCH to deploy."

    if [ "$sha" = "$(deployed_commit)" ]; then
        say "Already on ${sha:0:12}. Nothing to do."
        return 0
    fi

    local release
    release=$(downloaded_release "$sha")

    if [ -n "$release" ]; then
        # A parked release this timer already downloaded. Do not fetch it twice.
        say "Release ${sha:0:12} is already downloaded at $release."
    else
        release="$ROOT/releases/$(date --utc +%Y%m%dT%H%M%SZ)-${sha:0:12}"

        local url
        url=$(artifact_url "$run" "$sha")
        [ -n "$url" ] || fail "Run $run has no gateway-$sha artifact (see above). Artifacts expire after 30 days."

        say "Fetching ${sha:0:12} from run $run."
        fetch "$url" "$release"
        printf '%s\n' "$sha" > "$release/.commit"
    fi

    local speaks running
    running=$(running_protocol)
    speaks=$(protocol_version "$release")

    # A release that does not answer is a protocol-1 build only where protocol 1 is running - on the
    # protocol-1 server between step 0 of the cutover and the cutover itself, where every ordinary
    # release is one. Anywhere else it is a build that cannot run, and calling that a protocol change
    # sent the operator to the cutover for a fault that has nothing to do with it.
    if [ -z "$speaks" ]; then
        [ "$running" = 1 ] || fail "Release ${sha:0:12} at $release could not read the protocol - not installed.
It did not answer --protocol-version, and the running gateway speaks protocol $running. Check the download
and the runtime: sudo -u enactive dotnet $release/Enactive.Remote.Gateway.dll --protocol-version"
        speaks=1
    fi

    # Before the schema check, and whatever the arguments: --allow-migration answers a migration, and a
    # protocol change is not one. It needs a new database and a new environment, made by a person.
    if [ "$speaks" != "$running" ]; then
        printf '%s\n' "$release" > "$ROOT/PARKED"
        cat >&2 <<PARKED
Release ${sha:0:12} speaks protocol $speaks and the running gateway speaks protocol $running:
protocol change - install by hand (REMOTE_OPERATIONS §cutover).
It has been downloaded to $release and NOT installed.

A new protocol runs on a new database with a new environment, and nothing here creates either.
Installing it onto this one would leave every computer and browser unable to talk to it.

PARKED
        return 0
    fi

    local wants applied
    wants=$(release_schema_version "$release")
    applied=$(applied_schema_version)

    if carries_migration "$wants" "$applied" && [ "$allow_migration" = no ]; then
        printf '%s\n' "$release" > "$ROOT/PARKED"
        # To stderr, so journald records it as an error and `systemctl status` shows it. A parked
        # release that reported success would sit there for weeks looking deployed.
        cat >&2 <<PARKED
Release ${sha:0:12} carries a MIGRATION: it applies schema version $wants and this database is at $applied.
It has been downloaded to $release and NOT installed.

MySQL cannot roll a schema change back, so this is the one step whose failure putting the old files
back does not undo. Take a backup, then install it while watching:

  sudo -u enactive $ROOT/deploy/backup.sh
  sudo -u enactive $ROOT/deploy/verify-restore.sh
  sudo -u enactive $ROOT/deploy/pull-release.sh --allow-migration

PARKED
        return 0
    fi

    local previous
    previous=$(readlink -f "$ROOT/current" 2>/dev/null || true)

    say "Installing ${sha:0:12} (schema $wants, database at $applied)."
    # What it speaks is known now; written here, the next run need not start it to ask.
    printf '%s\n' "$speaks" > "$release/.protocol-version"
    activate "$release"

    if health_ok; then
        rm -f "$ROOT/PARKED"
        say "Deployed ${sha:0:12}."
        prune
        return 0
    fi

    # It did not come up. What may be done about that depends entirely on whether the schema moved.
    if carries_migration "$wants" "$applied"; then
        cat >&2 <<STUCK
$UNIT did not become healthy after installing ${sha:0:12}, and this release APPLIED A MIGRATION
(schema $wants; the database was at $applied). It has been left in place deliberately.

Putting the previous release back would run old code against the new schema, which is a second
fault on top of the first. Read the log and decide:

  journalctl -u $UNIT -n 100 --no-pager
STUCK
        return 1
    fi

    if [ -z "$previous" ]; then
        fail "$UNIT did not become healthy after installing ${sha:0:12}, and there is no previous release to go back to."
    fi

    printf '%s\n' "$UNIT did not become healthy after installing ${sha:0:12}. Rolling back to $(basename "$previous")." >&2
    activate "$previous"

    if health_ok; then
        printf '%s\n' "Rolled back to $(basename "$previous"), which is healthy. ${sha:0:12} was NOT deployed." >&2
        return 1
    fi

    fail "Rolled back to $(basename "$previous") and it is not healthy either. This is not the release's fault; read: journalctl -u $UNIT -n 100 --no-pager"
}

# Sourced by the tests to exercise the decisions without running any of this.
if [ "${BASH_SOURCE[0]}" = "${0}" ]; then
    main "$@"
fi
