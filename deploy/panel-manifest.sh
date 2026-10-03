#!/usr/bin/env bash
#
# Prints the panel manifest of a web root, byte for byte the JSON a gateway serving those files publishes
# at /.well-known/enactive-panel.json (PanelAssets.Manifest):
#
#   bash deploy/panel-manifest.sh publish/wwwroot > panel-manifest.json
#
# The pipeline runs it on the files a build SHIPS, so that what the server sends can be compared with
# what the release says it sent. The gateway's own list proves nothing on its own: a server that sends
# altered code can publish an altered list of it. A list made where the build is made, and published
# there, is the one an altered server cannot reach.
#
# Byte for byte, because comparing is `cmp`, not reading. So this follows the gateway exactly: every
# .js, .mjs and .css under the root whatever the case of the extension, keyed by its served path, in
# ordinal order, two-space indentation, "\n" line ends and no newline after the closing brace.
set -euo pipefail

root=${1:?Pass the web root, e.g. publish/wwwroot.}
[ -d "$root" ] || { printf 'No directory at %s.\n' "$root" >&2; exit 1; }
cd -- "$root"

# Byte order, which for these names is the gateway's ordinal order. A locale's order sorts "/b-c.js"
# after "/b.js" (it ignores punctuation); the gateway does not, and the lists would differ for nothing.
mapfile -t files < <(find . -type f \( -iname '*.js' -o -iname '*.mjs' -o -iname '*.css' \) \
                         | sed 's|^\./|/|' | LC_ALL=C sort)

printf '{\n  "algorithm": "sha256",\n'

if [ ${#files[@]} -eq 0 ]; then
    printf '  "files": {}\n}'
    exit 0
fi

printf '  "files": {\n'
last=$(( ${#files[@]} - 1 ))

for i in "${!files[@]}"; do
    path=${files[$i]}

    # The gateway's JSON writer escapes anything outside a safe set (+, &, quotes, non-ASCII...), and
    # this does not reproduce those escapes. A name that would need one is refused rather than printed
    # differently: a list that differs from the served one by an escape reads as an altered file.
    case $path in
        *[!A-Za-z0-9._/-]*)
            printf 'Refusing %s: the gateway would write it with JSON escapes this does not reproduce.\n' \
                   "$path" >&2
            exit 1 ;;
    esac

    hash=$(sha256sum -- ".$path" | cut -d' ' -f1)
    separator=$([ "$i" -eq "$last" ] && echo '' || echo ',')
    printf '    "%s": "%s"%s\n' "$path" "$hash" "$separator"
done

printf '  }\n}'
