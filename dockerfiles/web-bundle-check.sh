#!/bin/sh
# web-bundle-check: the checks web.Dockerfile runs on the published client bundle (spec 052 DD11, design 6.0).
#
#   1. The brotli files under wwwroot/_framework sum to at most 4 MB (Req 7.2).
#   2. Every wwwroot/_framework/*.wasm, its fingerprint stripped, is on the allow-list, so no server
#      assembly or unreviewed package reaches the browser (Req 14.6).
#   3. wwwroot holds no appsettings*.json: the browser can download every file there.
#
# Usage: web-bundle-check.sh <publish>/wwwroot dockerfiles/web-bundle-allowlist.txt
set -eu

wwwroot=$1
allowlist=$2
framework="$wwwroot/_framework"
budget=4000000
cr=$(printf '\r')

fail() {
    echo "FAIL: $*" >&2
    exit 1
}

# True when $1 matches a line of the allow-list: an exact name, or a prefix followed by '*'.
allowed() {
    while IFS= read -r pattern || [ -n "$pattern" ]; do
        pattern=${pattern%"$cr"}
        case "$pattern" in '' | '#'*) continue ;; esac
        # shellcheck disable=SC2254 # the line is a pattern on purpose
        case "$1" in $pattern) return 0 ;; esac
    done < "$allowlist"
    return 1
}

[ -d "$framework" ] || fail "$framework is missing"

size=$(find "$framework" -type f -name '*.br' -exec cat {} + | wc -c)
[ "$size" -le "$budget" ] || fail "the brotli files under _framework sum to $size bytes, above the $budget-byte budget"
echo "OK: _framework brotli total $size bytes (budget $budget)"

count=0
rejected=""
for file in "$framework"/*.wasm; do
    [ -e "$file" ] || fail "no .wasm file in $framework"
    # Name.<fingerprint>.wasm -> Name. The fingerprint is ten lowercase letters or digits.
    name=$(basename "$file" .wasm | sed 's/\.[a-z0-9]\{10\}$//')
    allowed "$name" || rejected="$rejected $name"
    count=$((count + 1))
done
[ -z "$rejected" ] || fail "not on $allowlist:$rejected"
echo "OK: $count assemblies, all on the allow-list"

settings=$(find "$wwwroot" -name 'appsettings*.json')
[ -z "$settings" ] || fail "configuration in wwwroot, which the browser can download: $settings"
echo "OK: no appsettings*.json in wwwroot"
