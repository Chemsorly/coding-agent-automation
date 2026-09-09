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

# Changed files arrive newline-separated on stdin. They are read straight from stdin
# (jq -R -s) rather than interpolated into an argument: a large changeset — e.g. a
# repo-wide rename — would overflow ARG_MAX if passed via `--argjson`, so jq would
# fail to exec ("Argument list too long", exit 126). The mapping is loaded from a file
# with --slurpfile for the same reason.
result="$(jq -c -R -s \
  --slurpfile mapfile "$MAP" \
  --argjson buildAll "$BUILD_ALL" \
  '
  (split("\n") | map(select(length > 0))) as $changed
  | ($mapfile[0]) as $map
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
  ')"

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
