#!/bin/sh
# Installs the Playwright Chromium headless shell and its system libraries into
# $PLAYWRIGHT_BROWSERS_PATH, so agents can run Playwright E2E tests inside their pod.
#
# The Playwright CLI comes from the Microsoft.Playwright NuGet package at the version pinned in
# Directory.Packages.props, so the browser revision matches what the tests' Microsoft.Playwright
# expects. The package bundles its own Node: Playwright needs Node >= 20 and Ubuntu 24.04 ships 18.
# Only the headless shell is installed because the E2E fixture launches Chromium with Headless = true.
#
# Usage (as root, needs curl and unzip): install-playwright-chromium.sh <Directory.Packages.props> <owner>
set -eu

PROPS="$1"
OWNER="$2"
: "${PLAYWRIGHT_BROWSERS_PATH:?PLAYWRIGHT_BROWSERS_PATH must be set}"

VERSION=$(sed -n 's/.*Include="Microsoft.Playwright" Version="\([^"]*\)".*/\1/p' "$PROPS")
if [ -z "$VERSION" ]; then
    echo "Microsoft.Playwright version not found in $PROPS" >&2
    exit 1
fi

case "$(uname -m)" in
    aarch64|arm64) NODE_DIR=linux-arm64 ;;
    *) NODE_DIR=linux-x64 ;;
esac

WORK=$(mktemp -d)
curl --proto '=https' --tlsv1.2 -fsSL --retry 3 --retry-delay 5 --retry-all-errors \
    "https://api.nuget.org/v3-flatcontainer/microsoft.playwright/${VERSION}/microsoft.playwright.${VERSION}.nupkg" \
    -o "$WORK/playwright.nupkg"
unzip -q "$WORK/playwright.nupkg" '.playwright/package/*' ".playwright/node/${NODE_DIR}/*" -d "$WORK"
chmod +x "$WORK/.playwright/node/${NODE_DIR}/node"

"$WORK/.playwright/node/${NODE_DIR}/node" "$WORK/.playwright/package/cli.js" install --with-deps --only-shell chromium

# Writable by the agent user, so a browser revision that a newer Microsoft.Playwright needs can
# still be installed at runtime without root (see tests/CodingAgent.Web.E2ETests/README.md).
chown -R "$OWNER" "$PLAYWRIGHT_BROWSERS_PATH"
rm -rf "$WORK" /var/lib/apt/lists/*
echo "Installed Playwright ${VERSION} Chromium headless shell into ${PLAYWRIGHT_BROWSERS_PATH}"
