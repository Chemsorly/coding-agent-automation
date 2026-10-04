# =============================================================================
# CodingAgent.Web Agent Dockerfile (claude-java21)
# Runs the Agent Worker process that executes the full pipeline end-to-end.
# Includes JDK 21, Maven, Claude Code CLI, uv (for MCP servers), and git.
# Does NOT include Blazor UI or presentation layer.
# =============================================================================

# Stage 1: Build (compiles the .NET Agent Worker)
# --platform=$BUILDPLATFORM: SDK runs natively on the build host (ARM64 in CI, x64 locally).
# Cross-compiles to the target platform via -a $TARGETARCH.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401 AS build
ARG TARGETARCH
WORKDIR /src

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

COPY . .
RUN dotnet publish src/CodingAgent.Agent/CodingAgent.Agent.csproj \
    -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Runtime (JDK 21 + Maven for Java quality gates)
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS runtime
ARG TARGETARCH

# Only install what this agent type needs: JDK 21, Maven, git, and curl for the Claude Code CLI
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        ca-certificates \
        git \
        openjdk-21-jdk-headless \
        maven \
        nodejs \
        npm \
        libasound2t64 \
        libvips42t64 \
    && rm -rf /var/lib/apt/lists/*

# user-apt lets the agent, which runs as non-root with all capabilities dropped, install Ubuntu
# packages (libraries, command-line tools) at runtime. See dockerfiles/user-apt.sh.
COPY --chmod=755 dockerfiles/user-apt.sh /usr/local/bin/user-apt

# JAVA_HOME varies by architecture — set dynamically via symlink
# The JDK package installs to java-21-openjdk-amd64 or java-21-openjdk-arm64
RUN JAVA_ARCH=$(dpkg --print-architecture) && \
    ln -sf /usr/lib/jvm/java-21-openjdk-${JAVA_ARCH} /usr/lib/jvm/java-21-openjdk
ENV JAVA_HOME=/usr/lib/jvm/java-21-openjdk

RUN mkdir -p /home/ubuntu/.local/bin /home/ubuntu/.claude/rules && \
    chown -R ubuntu:ubuntu /home/ubuntu

USER ubuntu
ENV PATH="/home/ubuntu/.local/bin:${PATH}"

# Claude Code CLI, pinned. Updates are disabled after the install (see agent-claude-dotnet10).
ARG CLAUDE_CODE_VERSION=2.1.286
RUN curl -fsSL --retry 3 --retry-delay 5 https://claude.ai/install.sh | bash -s "${CLAUDE_CODE_VERSION}" && \
    claude --version
ENV DISABLE_UPDATES=1
ENV DISABLE_AUTOUPDATER=1

# uv needed for MCP servers (Python-based tools like context7)
RUN curl -LsSf https://astral.sh/uv/install.sh | sh

WORKDIR /app
RUN mkdir -p /app/workspaces

ENV ORCHESTRATOR_URL=""
ENV AGENT_ID=""
ENV AGENT_API_KEY=""
ENV AGENT_LABELS=claude,java,java21

COPY --from=build --chown=ubuntu:ubuntu /app/publish .

ARG BUILD_COMMIT_SHA=local
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

VOLUME ["/app/workspaces"]
ENTRYPOINT ["dotnet", "CodingAgent.Agent.dll"]
