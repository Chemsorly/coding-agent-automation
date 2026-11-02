#!/usr/bin/env bash
# agent-image-check: checks a built agent image the way a pod runs it. No check prints a secret: the
# key is a canary, and every check is a test -r, a test -w or a comparison.
#
# Usage: agent-image-check.sh <claude|kiro|opencode> <image>
# CONTAINER_ENGINE selects the engine: docker (default) or podman.
set -euo pipefail

TARGET="$1"
IMAGE="$2"
ENGINE="${CONTAINER_ENGINE:-docker}"
NAME="agent-image-check-$$"
FAILED=0

cleanup() { "${ENGINE}" rm -f "${NAME}" >/dev/null 2>&1 || true; }
trap cleanup EXIT

fail() { echo "::error::${TARGET}: $1"; FAILED=1; }
in_container() { "${ENGINE}" exec "${NAME}" sh -c "$1"; }

# Chat mode with an orchestrator that refuses connections keeps the worker alive without any stub: it
# retries its hub connection for about three minutes (10 attempts, AgentConnectionLifecycle) before it
# gives up. --network none keeps the container off every network.
"${ENGINE}" run -d --name "${NAME}" --network none \
  -e AGENT_API_KEY=image-check-canary \
  -e ORCHESTRATOR_URL=http://127.0.0.1:9 \
  -e AGENT_ID=image-check \
  "${IMAGE}" --mode=chat >/dev/null

# grep -c, not grep -q: with pipefail, grep -q exits at the match and the closed pipe fails the pipeline.
# The worker logs this line once its startup hardening has run. In the OpenCode image it follows the
# OpenCode health check, which waits up to 90 seconds.
started=false
for _ in $(seq 1 120); do
  if "${ENGINE}" logs "${NAME}" 2>&1 | grep -c 'Agent Worker starting' >/dev/null; then
    started=true
    break
  fi
  [ "$("${ENGINE}" inspect -f '{{.State.Running}}' "${NAME}")" = "true" ] || break
  sleep 1
done
if [ "${started}" != "true" ]; then
  "${ENGINE}" logs "${NAME}" 2>&1 | tail -20
  fail "the worker did not start"
  exit 1
fi

# Every image: the app is read-only for the agent user, the workspaces are not.
in_container 'test -w /app' && fail "/app is writable by the agent user"
in_container 'test -w /app/CodingAgent.Agent.dll' && fail "/app/CodingAgent.Agent.dll is writable by the agent user"
in_container 'test -w /app/CodingAgent.Agent.runtimeconfig.json' && fail "the runtime configuration is writable by the agent user"
in_container 'test -w /app/workspaces' || fail "/app/workspaces is not writable by the agent user"

case "${TARGET}" in
  opencode)
    # This image only has to keep working: OpenCode starts in /app, which it may no longer write.
    "${ENGINE}" logs "${NAME}" 2>&1 | grep -c 'OpenCode server is healthy' >/dev/null || fail "the OpenCode server did not report healthy"
    in_container 'printenv DOTNET_EnableDiagnostics >/dev/null' && fail "DOTNET_EnableDiagnostics must not be set in this image: OpenCode and its tools would inherit it"
    ;;
  claude|kiro)
    [ "$(in_container 'cat /proc/1/comm')" = "dotnet" ] || fail "PID 1 is not dotnet, so the checks below prove nothing"
    in_container 'test -r /proc/1/environ' && fail "/proc/1/environ is readable by the agent user"
    in_container 'test -r /proc/1/mem' && fail "/proc/1/mem is readable by the agent user"
    [ "$(in_container 'printenv DOTNET_EnableDiagnostics')" = "0" ] || fail "DOTNET_EnableDiagnostics=0 is not set in the image"
    [ "$(in_container 'ls /tmp/dotnet-diagnostic-1-* 2>/dev/null | wc -l')" = "0" ] || fail "the worker opened a diagnostics socket"
    # The CLI starts from /app, as the provider's own checks do (--version, whoami, doctor).
    if [ "${TARGET}" = "claude" ]; then cli=claude; else cli=kiro-cli; fi
    in_container "cd /app && ${cli} --version >/dev/null" || fail "${cli} --version failed with cwd /app"
    ;;
  *)
    fail "unknown target; expected claude, kiro or opencode"
    ;;
esac

if [ "${FAILED}" = "0" ]; then echo "${TARGET}: image check passed"; fi
exit "${FAILED}"
