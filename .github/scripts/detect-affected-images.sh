#!/usr/bin/env bash
# Spec 048 Phase 3 — CI Docker path filtering.
#
# Reads the changed-file list (newline-separated) from stdin and the image→project
# mapping from .github/docker-image-projects.json, then emits the set of Docker images
# whose source closure (or own Dockerfile / shared asset) changed.
#
# Outputs (to $GITHUB_OUTPUT when set, and always echoed):
#   images_json : JSON array of {dockerfile, tag} — the docker-build matrix `image` dimension
#   push_json   : JSON array of {tag}             — the docker-push matrix `include`
#   any         : "true"/"false"                  — whether any image is affected
#
# Env:
#   BUILD_ALL=true  → select every image (used on main branch and tag pushes)
#   MAP=<path>      → override the mapping file (default .github/docker-image-projects.json)
set -euo pipefail

MAP="${MAP:-.github/docker-image-projects.json}"
BUILD_ALL="${BUILD_ALL:-false}"

# Changed files (newline-separated on stdin) → JSON array of non-empty paths.
changed_json="$(jq -R -s 'split("\n") | map(select(length > 0))')"

result="$(jq -c \
  --argjson changed "$changed_json" \
  --argjson buildAll "$BUILD_ALL" \
  '
  . as $map
  # changed src project names: the <Name> in src/<Name>/...
  | ($changed | map(select(startswith("src/")) | ltrimstr("src/") | split("/")[0]) | unique) as $cp
  # a global trigger changed?
  | ($map.globalTriggers | any(. as $g | $changed | index($g))) as $global
  | (if ($buildAll or $global) then $map.images
     else [ $map.images[] | . as $i
            | select(
                ($changed | index($i.dockerfile))
                or (($i.extraPaths // []) | any(. as $e | $changed | index($e)))
                or ($i.projects | any(. as $p | $cp | index($p)))
              ) ]
     end) as $sel
  | { images_json: ($sel | map({dockerfile, tag})),
      push_json:   ($sel | map({tag})),
      any:         ($sel | length > 0) }
  ' "$MAP")"

images_json="$(jq -c '.images_json' <<<"$result")"
push_json="$(jq -c '.push_json' <<<"$result")"
any="$(jq -r '.any' <<<"$result")"

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  {
    echo "images_json=${images_json}"
    echo "push_json=${push_json}"
    echo "any=${any}"
  } >> "$GITHUB_OUTPUT"
fi

echo "any=${any}"
echo "images_json=${images_json}"
echo "push_json=${push_json}"
