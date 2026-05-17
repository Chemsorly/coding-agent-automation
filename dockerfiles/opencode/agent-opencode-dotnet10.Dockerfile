# =============================================================================
# CodingAgentWebUI Agent Dockerfile (opencode)
# Runs the Agent Worker process with OpenCode as the agent backend.
# Includes .NET 10 runtime, OpenCode binary, tini, and git.
# OpenCode runs as a sidecar HTTP server (localhost:4096) within the container.
# Does NOT expose port 4096 externally — container-internal only.
# =============================================================================

# Stage 1: Build (.NET compilation)
# --platform=linux/arm64: Forces native ARM execution on ARM CI runners (avoids .NET QEMU crash)
# Cross-compiles to linux-x64 via RID so the output runs in the amd64 runtime stage.
FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/CodingAgentWebUI.Pipeline/CodingAgentWebUI.Pipeline.csproj src/CodingAgentWebUI.Pipeline/
COPY src/CodingAgentWebUI.Infrastructure/CodingAgentWebUI.Infrastructure.csproj src/CodingAgentWebUI.Infrastructure/
COPY src/CodingAgentWebUI.Orchestration/CodingAgentWebUI.Orchestration.csproj src/CodingAgentWebUI.Orchestration/
COPY src/CodingAgentWebUI/CodingAgentWebUI.csproj src/CodingAgentWebUI/
COPY src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj src/CodingAgentWebUI.Agent/
COPY src/CodingAgentWebUI.Agent.KiroCli/CodingAgentWebUI.Agent.KiroCli.csproj src/CodingAgentWebUI.Agent.KiroCli/
COPY src/CodingAgentWebUI.Agent.OpenCode/CodingAgentWebUI.Agent.OpenCode.csproj src/CodingAgentWebUI.Agent.OpenCode/
RUN dotnet restore src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj -r linux-x64

# Copy everything else and publish the Agent project
COPY . .
RUN dotnet publish src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj -c Release -r linux-x64 --self-contained false -o /app/publish

# Stage 2: Runtime
# Uses ASP.NET runtime + .NET SDK for quality gate compilation/testing in workspaces
FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS runtime

# Pin OpenCode version via build ARG for reproducible builds
ARG OPENCODE_VERSION=1.14.50

# Install runtime dependencies: tini (PID 1), curl (health checks), git (workspace ops)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        tini \
        curl \
        ca-certificates \
        git \
    && rm -rf /var/lib/apt/lists/*

# Download and install OpenCode binary (pinned version)
RUN curl -fsSL \
        "https://github.com/anomalyco/opencode/releases/download/v${OPENCODE_VERSION}/opencode-linux-x64.tar.gz" \
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

# Copy entrypoint script
COPY --chown=ubuntu:ubuntu dockerfiles/opencode/entrypoint.sh /app/entrypoint.sh
RUN chmod +x /app/entrypoint.sh

# Switch to non-root user (UID 1000)
USER ubuntu

# --- Environment variables ---
# Required: URL of the orchestrator's SignalR hub
ENV ORCHESTRATOR_URL=""
# Optional: Agent identifier (defaults to container hostname if not set)
ENV AGENT_ID=""
# Required: Agent type for routing
ENV AGENT_TYPE=opencode-dotnet10
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
