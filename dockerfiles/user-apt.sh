#!/bin/sh
# user-apt: install Ubuntu packages without root.
#
# Agent pods run as a non-root user with all Linux capabilities dropped, so neither apt-get
# install nor sudo works there. user-apt resolves the packages and their missing dependencies
# with apt, downloads them, unpacks them under $USER_APT_ROOT (default ~/.user-apt) with
# dpkg -x, and writes an env file that puts their programs and libraries on PATH and
# LD_LIBRARY_PATH. Maintainer scripts are not run, so this suits libraries and plain
# command-line tools, not services or packages that must live under /usr or /etc.
#
# Usage: user-apt install <package>...
#        . ~/.user-apt/env   (in the shell that runs the command needing the packages)
set -eu

ROOT="${USER_APT_ROOT:-$HOME/.user-apt}"
TRIPLET="$(uname -m)-linux-gnu"

if [ "${1:-}" != "install" ] || [ $# -lt 2 ]; then
    echo "Usage: user-apt install <package>..." >&2
    echo "Then run '. $ROOT/env' before the command that needs the packages." >&2
    exit 2
fi
shift

# apt keeps its lists and cache under $ROOT. Debug::NoLocking skips the root-owned dpkg lock,
# and apt downloads as the current user because switching to _apt needs CAP_SETUID.
apt_user() {
    apt-get -o "Dir::State::Lists=$ROOT/lists" -o "Dir::Cache=$ROOT/cache" \
        -o Debug::NoLocking=1 -o "APT::Sandbox::User=$(id -un)" "$@"
}

mkdir -p "$ROOT/lists/partial" "$ROOT/cache/archives/partial" "$ROOT/root"
touch "$ROOT/installed"

if [ ! -f "$ROOT/lists/.updated" ]; then
    echo "user-apt: updating package lists"
    if ! apt_user update >"$ROOT/update.log" 2>&1; then
        cat "$ROOT/update.log" >&2
        exit 1
    fi
    touch "$ROOT/lists/.updated"
fi

# A simulated install lists every package that dpkg does not have yet. Packages that an
# earlier user-apt call already unpacked are skipped.
if ! plan=$(apt_user -s install --no-install-recommends "$@" 2>&1); then
    echo "$plan" >&2
    exit 1
fi
pkgs=$(echo "$plan" | awk '/^Inst /{print $2}' | grep -vxF -f "$ROOT/installed" || true)

if [ -z "$pkgs" ]; then
    echo "user-apt: nothing to install"
else
    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    echo "user-apt: downloading $(echo "$pkgs" | wc -l) package(s)"
    # $pkgs is unquoted on purpose: one argument per package name.
    # shellcheck disable=SC2086
    if ! (cd "$work" && apt_user download $pkgs) >"$ROOT/download.log" 2>&1; then
        cat "$ROOT/download.log" >&2
        exit 1
    fi
    for deb in "$work"/*.deb; do
        dpkg -x "$deb" "$ROOT/root"
    done
    echo "$pkgs" >>"$ROOT/installed"
    echo "user-apt: installed $(echo "$pkgs" | tr '\n' ' ')"
fi

cat >"$ROOT/env" <<EOF
# Written by user-apt: puts the packages unpacked under $ROOT/root on PATH and LD_LIBRARY_PATH.
export PATH="$ROOT/root/usr/bin:$ROOT/root/bin:\$PATH"
export LD_LIBRARY_PATH="$ROOT/root/usr/lib/$TRIPLET:$ROOT/root/lib/$TRIPLET:$ROOT/root/usr/lib\${LD_LIBRARY_PATH:+:\$LD_LIBRARY_PATH}"
EOF
echo "user-apt: run '. $ROOT/env' in the shell that runs the command needing these packages."
