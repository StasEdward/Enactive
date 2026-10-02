#!/usr/bin/env bash
#
# panel-manifest.sh prints what the gateway publishes, byte for byte.
#
# The expected lists below are what PanelAssets.Manifest produced for these same fixtures; if the gateway's
# list changes shape, regenerate them from it rather than from this script. A difference of one byte - a
# newline at the end, a locale's sort order - makes every comparison of a release with its server fail,
# and a list that never matches is a list nobody checks.
#
#   bash deploy/panel-manifest.test.sh
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

ROOT=$(mktemp -d /tmp/enactive-manifest-test-XXXXXX)
trap 'rm -rf -- "$ROOT"' EXIT

# A web root with the cases that differ: an extension in capitals (listed, as the gateway compares without
# case), names whose ordinal and locale orders disagree (/Z.js before /a..., /b-c.js before /b.js before
# /b/c.js), an empty file, and files that are not code (left out).
www="$ROOT/www"
mkdir -p "$www/b" "$www/js" "$www/css"
printf '<html></html>\n'        > "$www/index.html"
printf "console.log('app');\n"  > "$www/app.js"
printf 'upper\n'                > "$www/Z.js"
printf 'b\n'                    > "$www/b.js"
printf 'b-c\n'                  > "$www/b-c.js"
printf 'c\n'                    > "$www/b/c.js"
printf 'export const k = 1;\n'  > "$www/js/keys.mjs"
printf 'body{}\n'               > "$www/css/Panel.CSS"
printf ''                       > "$www/empty.js"
printf 'png'                    > "$www/logo.png"
printf '{}'                     > "$www/data.json"

expected=$(cat <<'JSON'
{
  "algorithm": "sha256",
  "files": {
    "/Z.js": "e83189db38554920ea572093f9ad32facf682f28ccecdac085c1511735a2b492",
    "/app.js": "23f4382db03786db366309b3205ecac1abc7c3f675ac4eaf3ddc81278080703a",
    "/b-c.js": "4002c12d8b897cf88d24a71fe0988a1567e1e33c0996bcfe1a11ee5c25c7cf68",
    "/b.js": "0263829989b6fd954f72baaf2fc64bc2e2f01d692d4de72986ea808f6e99813f",
    "/b/c.js": "a3a5e715f0cc574a73c3f9bebb6bc24f32ffd5b67b387244c2c909da779a1478",
    "/css/Panel.CSS": "2708d73bf31c36cdfa1aa466551ed101017280fa546caba4473cfef6e92a93b5",
    "/empty.js": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
    "/js/keys.mjs": "f46931c0ea9483d74dc23c07364a26991b038f87c1e1a1b001d834543eeb9364"
  }
}
JSON
)

check "the list of a web root is the gateway's, byte for byte" \
    "$expected" "$(bash "$HERE/panel-manifest.sh" "$www")"

# Compared as bytes too: $(...) drops trailing newlines, and the gateway's list has none after the brace.
check "and it ends at the closing brace, as the gateway's does" \
    "7d" "$(bash "$HERE/panel-manifest.sh" "$www" | tail -c 1 | od -An -tx1 | tr -d ' \n')"

# A web root with no code in it: the gateway writes an empty object, not a missing key.
empty="$ROOT/empty"
mkdir -p "$empty"
printf '<html></html>\n' > "$empty/index.html"

check "a web root with no scripts or stylesheets lists none" \
    "$(printf '{\n  "algorithm": "sha256",\n  "files": {}\n}')" "$(bash "$HERE/panel-manifest.sh" "$empty")"

# A name the gateway would write with a JSON escape is refused, not printed some other way.
printf 'x\n' > "$www/a+b.js"
check "a name the gateway would escape is refused" \
    "refused" "$(bash "$HERE/panel-manifest.sh" "$www" >/dev/null 2>&1 && echo printed || echo refused)"

printf '\n%s passed, %s failed\n' "$passed" "$failed"
[ "$failed" -eq 0 ]
