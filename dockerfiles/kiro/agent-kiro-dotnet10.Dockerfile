# =============================================================================
# CodingAgent.Web Agent Dockerfile (kiro-dotnet10)
# Runs the Agent Worker process that executes the full pipeline end-to-end.
# Includes .NET 10 SDK, Kiro CLI, Node.js, npm, uv, and git.
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
RUN dotnet restore src/CodingAgent.Agent/CodingAgent.Agent.csproj -a $TARGETARCH

# Copy everything else and publish the Agent project
COPY . .
RUN dotnet publish src/CodingAgent.Agent/CodingAgent.Agent.csproj -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Runtime (full SDK — agent runs dotnet build/test for quality gates)
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS runtime
ARG TARGETARCH

# Install dependencies for Kiro CLI and pipeline execution
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        unzip \
        ca-certificates \
        git \
        nodejs \
        npm \
        libasound2t64 \
        libvips42 \
    && rm -rf /var/lib/apt/lists/*

# Reuse existing ubuntu user (UID 1000) from the base image
RUN mkdir -p /home/ubuntu/.local/bin /home/ubuntu/.kiro && \
    chown -R ubuntu:ubuntu /home/ubuntu

# Install Kiro CLI as non-root user
USER ubuntu
ENV PATH="/home/ubuntu/.local/bin:${PATH}"
ARG KIRO_CLI_VERSION=2.10.0
RUN KIRO_ARCH=$([ "$TARGETARCH" = "arm64" ] && echo "aarch64" || echo "x86_64") && \
    curl --proto '=https' --tlsv1.2 -sSf \
        "https://desktop-release.q.us-east-1.amazonaws.com/${KIRO_CLI_VERSION}/kirocli-${KIRO_ARCH}-linux.zip" \
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
# Required: Shared secret for authenticating with the orchestrator
ENV AGENT_API_KEY=""
# Predefined agent labels for this image type (overridable at runtime)
ENV AGENT_LABELS=kiro,dotnet,dotnet10

# Copy published Agent app (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .

# Build args for version tracking — ARG must appear before COPY --from=build in build stage,
# but ENV (which persists to runtime) must be set in the runtime stage, after USER switch.
ARG BUILD_COMMIT_SHA=local
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

VOLUME ["/home/ubuntu/.local/share/kiro-cli", "/home/ubuntu/.aws"]

ENTRYPOINT ["dotnet", "CodingAgent.Agent.dll"]
