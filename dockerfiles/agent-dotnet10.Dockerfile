# =============================================================================
# CodingAgentWebUI Agent Dockerfile (kiro-dotnet10)
# Runs the Agent Worker process that executes the full pipeline end-to-end.
# Includes .NET 10 SDK, Kiro CLI, Node.js, npm, uv, and git.
# Does NOT include Blazor UI or presentation layer.
# =============================================================================

# Stage 1: Build
# Pinned to 10.0.200 feature band to match global.json (rollForward: latestFeature)
# --platform=linux/arm64: Forces native ARM execution on ARM CI runners (avoids .NET QEMU crash)
# Cross-compiles to linux-x64 via RID so the output runs in the amd64 runtime stage.
FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
# Copy only the project files needed for the Agent and its dependencies (not test projects)
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/CodingAgentWebUI.Pipeline/CodingAgentWebUI.Pipeline.csproj src/CodingAgentWebUI.Pipeline/
COPY src/CodingAgentWebUI.Infrastructure/CodingAgentWebUI.Infrastructure.csproj src/CodingAgentWebUI.Infrastructure/
COPY src/CodingAgentWebUI.Orchestration/CodingAgentWebUI.Orchestration.csproj src/CodingAgentWebUI.Orchestration/
COPY src/CodingAgentWebUI/CodingAgentWebUI.csproj src/CodingAgentWebUI/
COPY src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj src/CodingAgentWebUI.Agent/
RUN dotnet restore src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj -r linux-x64

# Copy everything else and publish the Agent project
COPY . .
RUN dotnet publish src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj -c Release -r linux-x64 --self-contained false -o /app/publish

# Stage 2: Runtime (full SDK — agent runs dotnet build/test for quality gates)
# Pinned to 10.0.200 feature band to match global.json (rollForward: latestFeature)
FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS runtime

# Install dependencies for Kiro CLI and pipeline execution
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        unzip \
        ca-certificates \
        git \
        nodejs \
        npm \
    && rm -rf /var/lib/apt/lists/*

# Reuse existing ubuntu user (UID 1000) from the base image
RUN mkdir -p /home/ubuntu/.local/bin /home/ubuntu/.kiro && \
    chown -R ubuntu:ubuntu /home/ubuntu

# Install Kiro CLI as non-root user
USER ubuntu
ENV PATH="/home/ubuntu/.local/bin:${PATH}"
# Pinned — 2.1.x has a subagent deadlock in --no-interactive mode
# (parent agent hangs indefinitely after dispatching parallel subagents, 2/3 runs affected)
# Tested: 2.1.0 and 2.1.1 both reproduce. Falling back to 2.0.1.
ARG KIRO_CLI_VERSION=2.0.1
RUN curl --proto '=https' --tlsv1.2 -sSf \
        "https://desktop-release.q.us-east-1.amazonaws.com/${KIRO_CLI_VERSION}/kirocli-x86_64-linux.zip" \
        -o /tmp/kirocli.zip && \
    unzip /tmp/kirocli.zip -d /tmp/kirocli && \
    /tmp/kirocli/kirocli/install.sh --no-confirm && \
    rm -rf /tmp/kirocli /tmp/kirocli.zip && \
    kiro-cli settings "app.disableAutoupdates" "true"

# Install uv (Python package manager) for MCP server support
RUN curl -LsSf https://astral.sh/uv/install.sh | sh

WORKDIR /app

# Create workspaces directory for pipeline execution
RUN mkdir -p /app/workspaces

# --- Environment variables ---
# Required: URL of the orchestrator's SignalR hub
ENV ORCHESTRATOR_URL=""
# Optional: Agent identifier (defaults to container hostname if not set)
ENV AGENT_ID=""
# Required: Agent type for routing (e.g., kiro-dotnet10)
ENV AGENT_TYPE=kiro-dotnet10
# Required: Shared secret for authenticating with the orchestrator
ENV AGENT_API_KEY=""
# Predefined agent labels for this image type (overridable at runtime)
ENV AGENT_LABELS=kiro,dotnet,dotnet10

# Copy published Agent app (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .

# Volume mount points:
#   /home/ubuntu/.local/share/kiro-cli - Per-agent Kiro CLI auth (SQLite DB, must NOT be shared)
#   /home/ubuntu/.aws                  - AWS SSO cache (read-only, shared from host)
VOLUME ["/home/ubuntu/.local/share/kiro-cli", "/home/ubuntu/.aws"]

ENTRYPOINT ["dotnet", "CodingAgentWebUI.Agent.dll"]
