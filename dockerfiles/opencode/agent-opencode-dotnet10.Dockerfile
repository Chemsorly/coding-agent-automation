# =============================================================================
# CodingAgent.Web Agent Dockerfile (opencode)
# Runs the Agent Worker process with OpenCode as the agent backend.
# Includes .NET 10 runtime, OpenCode binary, tini, and git.
# OpenCode runs as a sidecar HTTP server (localhost:4096) within the container.
# Does NOT expose port 4096 externally — container-internal only.
# =============================================================================

# Stage 1: Build (.NET compilation)
# --platform=$BUILDPLATFORM: SDK runs natively on the build host (ARM64 in CI, x64 locally).
# Cross-compiles to the target platform via -a $TARGETARCH.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
ARG TARGETARCH
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/CodingAgent.Pipeline/CodingAgent.Pipeline.csproj src/CodingAgent.Pipeline/
COPY src/CodingAgent.Pipeline.CodeReview/CodingAgent.Pipeline.CodeReview.csproj src/CodingAgent.Pipeline.CodeReview/
COPY src/CodingAgent.Infrastructure.Providers/CodingAgent.Infrastructure.Providers.csproj src/CodingAgent.Infrastructure.Providers/
COPY src/CodingAgent.Orchestration/CodingAgent.Orchestration.csproj src/CodingAgent.Orchestration/
COPY src/CodingAgent.Web/CodingAgent.Web.csproj src/CodingAgent.Web/
COPY src/CodingAgent.Agent/CodingAgent.Agent.csproj src/CodingAgent.Agent/
COPY src/CodingAgent.Agent.KiroCli/CodingAgent.Agent.KiroCli.csproj src/CodingAgent.Agent.KiroCli/
COPY src/CodingAgent.Agent.OpenCode/CodingAgent.Agent.OpenCode.csproj src/CodingAgent.Agent.OpenCode/
RUN dotnet restore src/CodingAgent.Agent/CodingAgent.Agent.csproj -a $TARGETARCH

# Copy everything else and publish the Agent project
COPY . .
RUN dotnet publish src/CodingAgent.Agent/CodingAgent.Agent.csproj -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Runtime
# Uses ASP.NET runtime + .NET SDK for quality gate compilation/testing in workspaces
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS runtime
ARG TARGETARCH

# Pin OpenCode version via build ARG for reproducible builds
ARG OPENCODE_VERSION=1.18.21

# Install runtime dependencies: tini (PID 1), curl (health checks), git (workspace ops)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        tini \
        curl \
        ca-certificates \
        git \
        libvips42 \
    && rm -rf /var/lib/apt/lists/*

# Download and install OpenCode binary (pinned version, architecture-aware)
RUN OC_ARCH=$([ "$TARGETARCH" = "arm64" ] && echo "arm64" || echo "x64") && \
    curl -fsSL --retry 3 --retry-delay 5 --retry-all-errors \
        "https://github.com/anomalyco/opencode/releases/download/v${OPENCODE_VERSION}/opencode-linux-${OC_ARCH}.tar.gz" \
        -o /tmp/opencode.tar.gz && \
    tar -xzf /tmp/opencode.tar.gz -C /usr/local/bin && \
    chmod +x /usr/local/bin/opencode && \
    rm -f /tmp/opencode.tar.gz && \
    opencode --version

# Reuse existing ubuntu user (UID 1000) from the base image
RUN mkdir -p /home/ubuntu/.config/opencode /home/ubuntu/.local/share/opencode && \
    chown -R ubuntu:ubuntu /home/ubuntu

WORKDIR /app

# Create workspaces directory for pipeline execution
RUN mkdir -p /app/workspaces && chown -R ubuntu:ubuntu /app

# Copy published Agent app (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .

# Copy entrypoint script as root, set restrictive permissions, then switch user
# nosonar: docker:S6504 — entrypoint.sh is owned by root; chmod 755 grants ubuntu (as "other")
# read+execute only, not write.
COPY dockerfiles/opencode/entrypoint.sh /app/entrypoint.sh
RUN chmod 755 /app/entrypoint.sh

# Switch to non-root user (UID 1000)
USER ubuntu

# Build args for version tracking — must be declared in the runtime stage to produce ENV
ARG BUILD_COMMIT_SHA=local
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

# --- Environment variables ---
# Required: URL of the orchestrator's SignalR hub
ENV ORCHESTRATOR_URL=""
# Optional: Agent identifier (defaults to container hostname if not set)
ENV AGENT_ID=""
# Required: Shared secret for authenticating with the orchestrator
ENV AGENT_API_KEY=""
# Predefined agent labels for this image type (overridable at runtime)
ENV AGENT_LABELS=opencode,dotnet,dotnet10

# LLM API keys — NOT embedded, must be provided at runtime
# ENV ANTHROPIC_API_KEY=
# ENV OPENAI_API_KEY=
# ENV OPENROUTER_API_KEY=

# Do NOT expose port 4096 — OpenCode server is container-internal only (localhost:4096)

# Use tini as PID 1 for signal forwarding and zombie reaping
ENTRYPOINT ["/usr/bin/tini", "--", "/app/entrypoint.sh"]
