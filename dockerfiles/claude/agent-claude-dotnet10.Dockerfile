# =============================================================================
# CodingAgent.Web Agent Dockerfile (claude-dotnet10)
# Runs the Agent Worker process that executes the full pipeline end-to-end.
# Includes .NET 10 SDK, Claude Code CLI, Node.js, npm, uv, and git.
# Does NOT include Blazor UI or presentation layer.
# =============================================================================

# Stage 1: Build
# --platform=$BUILDPLATFORM: SDK runs natively on the build host (ARM64 in CI, x64 locally).
# Cross-compiles to the target platform via -a $TARGETARCH.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ARG TARGETARCH
WORKDIR /src

# Copy solution and project files first for layer caching
# Copy only the project files needed for the Agent and its dependencies (not test projects)
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
COPY src/CodingAgent.Agent.ClaudeCode/CodingAgent.Agent.ClaudeCode.csproj src/CodingAgent.Agent.ClaudeCode/
RUN dotnet restore src/CodingAgent.Agent/CodingAgent.Agent.csproj -a $TARGETARCH

# Copy everything else and publish the Agent project
COPY . .
RUN dotnet publish src/CodingAgent.Agent/CodingAgent.Agent.csproj -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Runtime (full SDK — agent runs dotnet build/test for quality gates)
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS runtime
ARG TARGETARCH

# Install dependencies for the Claude Code CLI, MCP servers, and pipeline execution
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        ca-certificates \
        git \
        nodejs \
        npm \
        libasound2t64 \
        libvips42t64 \
    && rm -rf /var/lib/apt/lists/*

# user-apt lets the agent, which runs as non-root with all capabilities dropped, install Ubuntu
# packages (libraries, command-line tools) at runtime. See dockerfiles/user-apt.sh.
COPY --chmod=755 dockerfiles/user-apt.sh /usr/local/bin/user-apt

# Reuse existing ubuntu user (UID 1000) from the base image.
# ~/.claude/rules holds the pipeline steering the agent writes before each run.
RUN mkdir -p /home/ubuntu/.local/bin /home/ubuntu/.claude/rules && \
    chown -R ubuntu:ubuntu /home/ubuntu

# Install the Claude Code CLI (native installer, pinned version) as non-root user.
# The installer puts the launcher at ~/.local/bin/claude. The image is the update channel:
# DISABLE_UPDATES blocks the background updater and `claude update` alike. It is set after the
# install because it would also block the installer's own `claude install` step.
USER ubuntu
ENV PATH="/home/ubuntu/.local/bin:${PATH}"
ARG CLAUDE_CODE_VERSION=2.1.286
RUN curl -fsSL --retry 3 --retry-delay 5 https://claude.ai/install.sh | bash -s "${CLAUDE_CODE_VERSION}" && \
    claude --version
ENV DISABLE_UPDATES=1
ENV DISABLE_AUTOUPDATER=1

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
# Required: Shared secret for authenticating with the orchestrator
ENV AGENT_API_KEY=""
# Predefined agent labels for this image type (overridable at runtime)
ENV AGENT_LABELS=claude,dotnet,dotnet10
# Credentials arrive per Job from the agent Secret (claude-api-key / claude-oauth-token) as
# AGENT_CLAUDE_API_KEY / AGENT_CLAUDE_OAUTH_TOKEN; outside Kubernetes ANTHROPIC_API_KEY or
# CLAUDE_CODE_OAUTH_TOKEN work too. No credential volume is needed.

# Copy published Agent app (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .

# Build args for version tracking — ARG must appear before COPY --from=build in build stage,
# but ENV (which persists to runtime) must be set in the runtime stage, after USER switch.
ARG BUILD_COMMIT_SHA=local
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

ENTRYPOINT ["dotnet", "CodingAgent.Agent.dll"]
