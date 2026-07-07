# =============================================================================
# CodingAgentWebUI Agent Dockerfile (opencode-java21)
# Runs the Agent Worker process with OpenCode as the agent backend.
# Includes .NET 10 SDK (for quality gates), JDK 21, Maven, OpenCode binary,
# tini, and git.
# OpenCode runs as a sidecar HTTP server (localhost:4096) within the container.
# Does NOT expose port 4096 externally — container-internal only.
# =============================================================================

# Stage 1: Build (.NET compilation)
# --platform=$BUILDPLATFORM: SDK runs natively on the build host (ARM64 in CI, x64 locally).
# Cross-compiles to the target platform via -a $TARGETARCH.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.301 AS build
ARG TARGETARCH
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/CodingAgentWebUI.Pipeline/CodingAgentWebUI.Pipeline.csproj src/CodingAgentWebUI.Pipeline/
COPY src/CodingAgentWebUI.Pipeline.CodeReview/CodingAgentWebUI.Pipeline.CodeReview.csproj src/CodingAgentWebUI.Pipeline.CodeReview/
COPY src/CodingAgentWebUI.Infrastructure/CodingAgentWebUI.Infrastructure.csproj src/CodingAgentWebUI.Infrastructure/
COPY src/CodingAgentWebUI.Orchestration/CodingAgentWebUI.Orchestration.csproj src/CodingAgentWebUI.Orchestration/
COPY src/CodingAgentWebUI/CodingAgentWebUI.csproj src/CodingAgentWebUI/
COPY src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj src/CodingAgentWebUI.Agent/
COPY src/CodingAgentWebUI.Agent.KiroCli/CodingAgentWebUI.Agent.KiroCli.csproj src/CodingAgentWebUI.Agent.KiroCli/
COPY src/CodingAgentWebUI.Agent.OpenCode/CodingAgentWebUI.Agent.OpenCode.csproj src/CodingAgentWebUI.Agent.OpenCode/
RUN dotnet restore src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj -a $TARGETARCH

# Copy everything else and publish the Agent project
COPY . .
RUN dotnet publish src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Runtime (full SDK — quality gates run dotnet build/test + mvn test)
FROM mcr.microsoft.com/dotnet/sdk:10.0.301 AS runtime
ARG TARGETARCH

# Pin OpenCode version via build ARG for reproducible builds
ARG OPENCODE_VERSION=1.17.15

# Install runtime dependencies: tini (PID 1), JDK 21, Maven, curl (health checks), git (workspace ops)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        tini \
        curl \
        ca-certificates \
        git \
        openjdk-21-jdk-headless \
        maven \
        nodejs \
        npm \
    && rm -rf /var/lib/apt/lists/*

# JAVA_HOME varies by architecture — set dynamically via symlink
# The JDK package installs to java-21-openjdk-amd64 or java-21-openjdk-arm64
RUN JAVA_ARCH=$(dpkg --print-architecture) && \
    ln -sf /usr/lib/jvm/java-21-openjdk-${JAVA_ARCH} /usr/lib/jvm/java-21-openjdk
ENV JAVA_HOME=/usr/lib/jvm/java-21-openjdk

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
# Required: Shared secret for authenticating with the orchestrator
ENV AGENT_API_KEY=""
# Predefined agent labels for this image type (overridable at runtime)
ENV AGENT_LABELS=opencode,java,java21

# LLM API keys — NOT embedded, must be provided at runtime
# ENV ANTHROPIC_API_KEY=
# ENV OPENAI_API_KEY=
# ENV OPENROUTER_API_KEY=

# Do NOT expose port 4096 — OpenCode server is container-internal only (localhost:4096)

# Use tini as PID 1 for signal forwarding and zombie reaping
ENTRYPOINT ["/usr/bin/tini", "--", "/app/entrypoint.sh"]
