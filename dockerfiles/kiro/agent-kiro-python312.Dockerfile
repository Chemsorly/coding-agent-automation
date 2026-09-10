# =============================================================================
# CodingAgent.Web Agent Dockerfile (kiro-python312)
# Runs the Agent Worker process that executes the full pipeline end-to-end.
# Includes Python 3.12, Kiro CLI, uv (for MCP servers + Python tooling), and git.
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
RUN dotnet restore src/CodingAgent.Agent/CodingAgent.Agent.csproj -a $TARGETARCH

COPY . .
RUN dotnet publish src/CodingAgent.Agent/CodingAgent.Agent.csproj -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Runtime (Python 3.12 for Python quality gates)
FROM mcr.microsoft.com/dotnet/sdk:10.0.401 AS runtime
ARG TARGETARCH

# Only install what this agent type needs: Python 3.12, git, and curl/unzip for Kiro CLI
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        unzip \
        ca-certificates \
        git \
        python3 \
        python3-pip \
        python3-venv \
        nodejs \
        npm \
        libasound2t64 \
        libvips42 \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /home/ubuntu/.local/bin /home/ubuntu/.kiro && \
    chown -R ubuntu:ubuntu /home/ubuntu

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

# uv for MCP servers AND Python package management (pytest, etc.)
RUN curl -LsSf https://astral.sh/uv/install.sh | sh

WORKDIR /app
RUN mkdir -p /app/workspaces

ENV ORCHESTRATOR_URL=""
ENV AGENT_ID=""
ENV AGENT_API_KEY=""
ENV AGENT_LABELS=kiro,python,python312

COPY --from=build --chown=ubuntu:ubuntu /app/publish .

ARG BUILD_COMMIT_SHA=local
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

VOLUME ["/home/ubuntu/.local/share/kiro-cli", "/home/ubuntu/.aws", "/app/workspaces"]
ENTRYPOINT ["dotnet", "CodingAgent.Agent.dll"]
