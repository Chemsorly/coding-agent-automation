# =============================================================================
# CodingAgent.Web Agent Dockerfile — one image per agent tool
# Runs the Agent Worker process that executes the full pipeline end-to-end.
# Every image carries every supported tech stack, so one image serves .NET, Java and Python
# repositories, polyglot ones included:
#   .NET 10 SDK, JDK 21 + Maven, Python 3.12 (pip, venv, uv), Node.js + npm, git, user-apt.
# Pick the agent tool with --target:
#   claude   — Claude Code CLI
#   kiro     — Kiro CLI
#   opencode — OpenCode server (container-internal sidecar) under tini
# Does NOT include Blazor UI or presentation layer.
# =============================================================================

# Routing labels of the stacks the toolchain stage installs; each target prefixes its agent
# tool. Keep in sync with the toolchain packages and docs/label-routing.md.
ARG STACK_LABELS=dotnet,dotnet10,java,java21,python,python312

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
RUN dotnet publish src/CodingAgent.Agent/CodingAgent.Agent.csproj \
    -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Toolchain shared by every agent tool. Full SDK, not the ASP.NET runtime: the quality
# gates run dotnet build/test, mvn and pytest inside the agent pod.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS toolchain

# Stack toolchains (JDK 21 + Maven, Python 3.12), git, Node.js/npm for MCP servers, tini as
# PID 1 for OpenCode and unzip for the Kiro CLI installer
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        ca-certificates \
        git \
        unzip \
        tini \
        nodejs \
        npm \
        libasound2t64 \
        libvips42t64 \
        openjdk-21-jdk-headless \
        maven \
        python3 \
        python3-pip \
        python3-venv \
    && rm -rf /var/lib/apt/lists/*

# user-apt lets the agent, which runs as non-root with all capabilities dropped, install Ubuntu
# packages (libraries, command-line tools) at runtime. See dockerfiles/user-apt.sh.
COPY --chmod=755 dockerfiles/user-apt.sh /usr/local/bin/user-apt

# JAVA_HOME varies by architecture — set via a symlink. The JDK package installs to
# java-21-openjdk-amd64 or java-21-openjdk-arm64.
# Reuse the existing ubuntu user (UID 1000) from the base image. It owns its home and /app,
# which holds the published Agent app and the run workspaces.
RUN JAVA_ARCH=$(dpkg --print-architecture) && \
    ln -sf /usr/lib/jvm/java-21-openjdk-${JAVA_ARCH} /usr/lib/jvm/java-21-openjdk && \
    mkdir -p /home/ubuntu/.local/bin /app/workspaces && \
    chown -R ubuntu:ubuntu /home/ubuntu /app
ENV JAVA_HOME=/usr/lib/jvm/java-21-openjdk

USER ubuntu
ENV PATH="/home/ubuntu/.local/bin:${PATH}"

# uv for MCP servers (uvx) AND Python package management (pytest, etc.)
RUN curl -LsSf https://astral.sh/uv/install.sh | sh

WORKDIR /app

# --- Environment variables ---
# Required: URL of the orchestrator's SignalR hub
ENV ORCHESTRATOR_URL=""
# Optional: Agent identifier (defaults to container hostname if not set)
ENV AGENT_ID=""
# Required: Shared secret for authenticating with the orchestrator
ENV AGENT_API_KEY=""

# =============================================================================
# Target: claude — Claude Code CLI
# =============================================================================
FROM toolchain AS claude
ARG STACK_LABELS

# Install the Claude Code CLI (native installer, pinned version) as non-root user.
# The installer puts the launcher at ~/.local/bin/claude. The image is the update channel:
# DISABLE_UPDATES blocks the background updater and `claude update` alike. It is set after the
# install because it would also block the installer's own `claude install` step.
# ~/.claude/rules holds the pipeline steering the agent writes before each run.
ARG CLAUDE_CODE_VERSION=2.1.286
RUN mkdir -p /home/ubuntu/.claude/rules && \
    curl -fsSL --retry 3 --retry-delay 5 https://claude.ai/install.sh | bash -s "${CLAUDE_CODE_VERSION}" && \
    claude --version
ENV DISABLE_UPDATES=1
ENV DISABLE_AUTOUPDATER=1

# Predefined agent labels: the agent tool and every stack in the image (overridable at runtime;
# Kubernetes Jobs set them from their job template)
ENV AGENT_LABELS=claude,${STACK_LABELS}
# Credentials arrive per Job from the agent Secret (claude-api-key / claude-oauth-token) as
# AGENT_CLAUDE_API_KEY / AGENT_CLAUDE_OAUTH_TOKEN; outside Kubernetes ANTHROPIC_API_KEY or
# CLAUDE_CODE_OAUTH_TOKEN work too. No credential volume is needed.

# Copy published Agent app (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .

# Build args for version tracking — ENV (which persists to runtime) must be set in the runtime
# stage.
ARG BUILD_COMMIT_SHA=local
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

ENTRYPOINT ["dotnet", "CodingAgent.Agent.dll"]

# =============================================================================
# Target: kiro — Kiro CLI
# =============================================================================
FROM toolchain AS kiro
ARG TARGETARCH
ARG STACK_LABELS

# Install Kiro CLI as non-root user, with its auto-updates off (the image is the update channel)
ARG KIRO_CLI_VERSION=2.10.0
RUN mkdir -p /home/ubuntu/.kiro && \
    KIRO_ARCH=$([ "$TARGETARCH" = "arm64" ] && echo "aarch64" || echo "x86_64") && \
    curl --proto '=https' --tlsv1.2 -sSf \
        "https://desktop-release.q.us-east-1.amazonaws.com/${KIRO_CLI_VERSION}/kirocli-${KIRO_ARCH}-linux.zip" \
        -o /tmp/kirocli.zip && \
    unzip /tmp/kirocli.zip -d /tmp/kirocli && \
    /tmp/kirocli/kirocli/install.sh --no-confirm && \
    rm -rf /tmp/kirocli /tmp/kirocli.zip && \
    kiro-cli settings "app.disableAutoupdates" "true"

# Predefined agent labels: the agent tool and every stack in the image (overridable at runtime;
# Kubernetes Jobs set them from their job template)
ENV AGENT_LABELS=kiro,${STACK_LABELS}

# Copy published Agent app (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .

# Build args for version tracking — ENV (which persists to runtime) must be set in the runtime
# stage.
ARG BUILD_COMMIT_SHA=local
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

VOLUME ["/home/ubuntu/.local/share/kiro-cli", "/home/ubuntu/.aws"]

ENTRYPOINT ["dotnet", "CodingAgent.Agent.dll"]

# =============================================================================
# Target: opencode — OpenCode as a sidecar HTTP server (localhost:4096) within the container
# =============================================================================
FROM toolchain AS opencode
ARG TARGETARCH
ARG STACK_LABELS

# Download and install the OpenCode binary as root (pinned version, architecture-aware)
ARG OPENCODE_VERSION=1.18.21
USER root
RUN OC_ARCH=$([ "$TARGETARCH" = "arm64" ] && echo "arm64" || echo "x64") && \
    curl -fsSL --retry 3 --retry-delay 5 --retry-all-errors \
        "https://github.com/anomalyco/opencode/releases/download/v${OPENCODE_VERSION}/opencode-linux-${OC_ARCH}.tar.gz" \
        -o /tmp/opencode.tar.gz && \
    tar -xzf /tmp/opencode.tar.gz -C /usr/local/bin && \
    chmod +x /usr/local/bin/opencode && \
    rm -f /tmp/opencode.tar.gz && \
    opencode --version

# The entrypoint script stays owned by root: ubuntu (as "other") may read and execute it, not
# write it.
COPY --chmod=755 dockerfiles/opencode/entrypoint.sh /app/entrypoint.sh

# Switch back to the non-root user (UID 1000)
USER ubuntu
RUN mkdir -p /home/ubuntu/.config/opencode /home/ubuntu/.local/share/opencode

# Predefined agent labels: the agent tool and every stack in the image (overridable at runtime;
# Kubernetes Jobs set them from their job template)
ENV AGENT_LABELS=opencode,${STACK_LABELS}

# LLM API keys — NOT embedded, must be provided at runtime
# ENV ANTHROPIC_API_KEY=
# ENV OPENAI_API_KEY=
# ENV OPENROUTER_API_KEY=

# Copy published Agent app (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .

# Build args for version tracking — ENV (which persists to runtime) must be set in the runtime
# stage.
ARG BUILD_COMMIT_SHA=local
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

# Do NOT expose port 4096 — OpenCode server is container-internal only (localhost:4096)

# Use tini as PID 1 for signal forwarding and zombie reaping
ENTRYPOINT ["/usr/bin/tini", "--", "/app/entrypoint.sh"]
