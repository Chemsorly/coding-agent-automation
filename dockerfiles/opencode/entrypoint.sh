#!/bin/sh
# =============================================================================
# OpenCode Agent Container Entrypoint Script
# =============================================================================
# This script manages the lifecycle of both the OpenCode server (sidecar) and
# the .NET Agent Worker process within the same container.
#
# Lifecycle:
#   1. Generate OPENCODE_SERVER_PASSWORD from /dev/urandom
#   2. Start OpenCode server in background (localhost:4096)
#   3. Poll /global/health every 1s for up to 30s
#   4. Start Agent Worker in foreground
#   5. On exit or SIGTERM: graceful shutdown (worker first, then server)
#
# Requirements: 8.3, 8.4, 8.9, 8.10, 2.1, 2.2, 2.3, 2.4, 2.5, 2.7
# =============================================================================

set -e

# -----------------------------------------------------------------------------
# 1. Generate OPENCODE_SERVER_PASSWORD
# -----------------------------------------------------------------------------
# Generate a 48-character alphanumeric password from /dev/urandom.
# This is used for HTTP Basic Auth between the Agent Worker and OpenCode server.
export OPENCODE_SERVER_PASSWORD
OPENCODE_SERVER_PASSWORD=$(tr -dc 'A-Za-z0-9' < /dev/urandom | head -c 48)

if [ -z "$OPENCODE_SERVER_PASSWORD" ]; then
    echo "ERROR: Failed to generate OPENCODE_SERVER_PASSWORD" >&2
    exit 1
fi

echo "Generated OPENCODE_SERVER_PASSWORD (${#OPENCODE_SERVER_PASSWORD} chars)"

# -----------------------------------------------------------------------------
# 1b. Write OPENCODE_CONFIG_CONTENT to config file (if provided)
# -----------------------------------------------------------------------------
if [ -n "$OPENCODE_CONFIG_CONTENT" ]; then
    mkdir -p /home/ubuntu/.config/opencode
    echo "$OPENCODE_CONFIG_CONTENT" > /home/ubuntu/.config/opencode/opencode.json
    echo "Wrote OPENCODE_CONFIG_CONTENT to /home/ubuntu/.config/opencode/opencode.json"
fi

# -----------------------------------------------------------------------------
# 2. Start OpenCode server in background
# -----------------------------------------------------------------------------
# Bind exclusively to 127.0.0.1 so the API is not accessible from outside the
# container (Requirement 2.4). Port 4096 is container-internal only.
echo "Starting OpenCode server on 127.0.0.1:4096..."
opencode serve --port 4096 --hostname 127.0.0.1 &
OPENCODE_PID=$!

echo "OpenCode server started (PID: $OPENCODE_PID)"

# Tail OpenCode logs to stdout (debug visibility via docker compose logs)
# Wait briefly for the log file to be created, then tail in background
(sleep 2 && tail -F /home/ubuntu/.local/share/opencode/log/*.log 2>/dev/null | sed 's/^/[opencode] /' &) &

# -----------------------------------------------------------------------------
# Signal handling — graceful shutdown (set up early, before health check loop)
# -----------------------------------------------------------------------------
# Trap SIGTERM/SIGINT to gracefully shut down both processes.
# Order: Agent Worker first (with 10s grace period), then OpenCode server.
# WORKER_PID may be empty at this point; cleanup() guards with [ -n "$WORKER_PID" ].
WORKER_PID=""
GRACE_PERIOD=10

cleanup() {
    echo "Received shutdown signal, initiating graceful shutdown..."

    # Stop Agent Worker first (with grace period)
    if [ -n "$WORKER_PID" ] && kill -0 "$WORKER_PID" 2>/dev/null; then
        echo "Sending SIGTERM to Agent Worker (PID: $WORKER_PID)..."
        kill -TERM "$WORKER_PID" 2>/dev/null || true

        # Wait up to GRACE_PERIOD seconds for worker to exit
        GRACE_WAITED=0
        while [ "$GRACE_WAITED" -lt "$GRACE_PERIOD" ]; do
            if ! kill -0 "$WORKER_PID" 2>/dev/null; then
                break
            fi
            GRACE_WAITED=$((GRACE_WAITED + 1))
            sleep 1
        done

        # Force kill if still running after grace period
        if kill -0 "$WORKER_PID" 2>/dev/null; then
            echo "Agent Worker did not exit within ${GRACE_PERIOD}s, sending SIGKILL..."
            kill -KILL "$WORKER_PID" 2>/dev/null || true
        fi
    fi

    # Stop OpenCode server
    if kill -0 "$OPENCODE_PID" 2>/dev/null; then
        echo "Sending SIGTERM to OpenCode server (PID: $OPENCODE_PID)..."
        kill -TERM "$OPENCODE_PID" 2>/dev/null || true
        wait "$OPENCODE_PID" 2>/dev/null || true
    fi

    echo "Graceful shutdown complete."
    exit 0
}

trap cleanup TERM INT

# -----------------------------------------------------------------------------
# 3. Poll health endpoint until ready (max 30 seconds)
# -----------------------------------------------------------------------------
# The health endpoint is GET /global/health with HTTP Basic Auth.
# Username: opencode, Password: OPENCODE_SERVER_PASSWORD
# Expected response: HTTP 200 with body containing "healthy": true
HEALTH_URL="http://127.0.0.1:4096/global/health"
AUTH_HEADER=$(printf 'opencode:%s' "$OPENCODE_SERVER_PASSWORD" | base64)
MAX_WAIT=30
WAITED=0

echo "Waiting for OpenCode server to become healthy (max ${MAX_WAIT}s)..."

while [ "$WAITED" -lt "$MAX_WAIT" ]; do
    # Check if the OpenCode process is still running
    if ! kill -0 "$OPENCODE_PID" 2>/dev/null; then
        echo "ERROR: OpenCode server process exited unexpectedly" >&2
        exit 1
    fi

    # Attempt health check with Basic auth
    RESPONSE=$(curl -s -o /dev/null -w "%{http_code}" \
        -H "Authorization: Basic $AUTH_HEADER" \
        "$HEALTH_URL" 2>/dev/null) || true

    if [ "$RESPONSE" = "200" ]; then
        echo "OpenCode server is healthy (took ${WAITED}s)"
        break
    fi

    WAITED=$((WAITED + 1))
    sleep 1
done

if [ "$WAITED" -ge "$MAX_WAIT" ]; then
    echo "ERROR: OpenCode server did not become healthy within ${MAX_WAIT} seconds" >&2
    # Clean up the server process before exiting
    kill "$OPENCODE_PID" 2>/dev/null || true
    wait "$OPENCODE_PID" 2>/dev/null || true
    exit 1
fi

# -----------------------------------------------------------------------------
# 4. Start Agent Worker in foreground
# -----------------------------------------------------------------------------
# The Agent Worker is the .NET process that orchestrates pipeline steps and
# communicates with the orchestrator via SignalR.
echo "Starting Agent Worker..."
dotnet /app/CodingAgentWebUI.Agent.dll &
WORKER_PID=$!

echo "Agent Worker started (PID: $WORKER_PID)"

# -----------------------------------------------------------------------------
# 5. Wait for Agent Worker to exit and propagate its exit code
# -----------------------------------------------------------------------------
# When the Agent Worker exits (for any reason), terminate the OpenCode server
# and exit with the worker's exit code (Requirement 2.7, 8.10).
wait "$WORKER_PID"
WORKER_EXIT_CODE=$?

echo "Agent Worker exited with code: $WORKER_EXIT_CODE"

# Terminate OpenCode server after worker exits
if kill -0 "$OPENCODE_PID" 2>/dev/null; then
    echo "Stopping OpenCode server..."
    kill -TERM "$OPENCODE_PID" 2>/dev/null || true
    wait "$OPENCODE_PID" 2>/dev/null || true
fi

exit "$WORKER_EXIT_CODE"
