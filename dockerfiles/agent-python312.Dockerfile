# =============================================================================
# CodingAgentWebUI Agent Dockerfile (kiro-python312)
# Runs the Agent Worker process that executes the full pipeline end-to-end.
# Includes Python 3.12, Kiro CLI, uv (for MCP servers + Python tooling), and git.
# Does NOT include Blazor UI or presentation layer.
# =============================================================================

# Stage 1: Build (compiles the .NET Agent Worker)
# --platform=linux/arm64: Forces native ARM execution on ARM CI runners (avoids .NET QEMU crash)
# Cross-compiles to linux-x64 via RID so the output runs in the amd64 runtime stage.
FROM --platform=linux/arm64 mcr.microsoft.com/dotnet/sdk:10.0.203 AS build
WORKDIR /src

# Copy only the project files needed for the Agent and its dependencies (not test projects)
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/CodingAgentWebUI.Pipeline/CodingAgentWebUI.Pipeline.csproj src/CodingAgentWebUI.Pipeline/
COPY src/CodingAgentWebUI.Infrastructure/CodingAgentWebUI.Infrastructure.csproj src/CodingAgentWebUI.Infrastructure/
COPY src/CodingAgentWebUI.Orchestration/CodingAgentWebUI.Orchestration.csproj src/CodingAgentWebUI.Orchestration/
COPY src/CodingAgentWebUI/CodingAgentWebUI.csproj src/CodingAgentWebUI/
COPY src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj src/CodingAgentWebUI.Agent/
RUN dotnet restore src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj -r linux-x64

COPY . .
RUN dotnet publish src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj -c Release -r linux-x64 --self-contained false -o /app/publish

# Stage 2: Runtime (Python 3.12 for Python quality gates)
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS runtime

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
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /home/ubuntu/.local/bin /home/ubuntu/.kiro && \
    chown -R ubuntu:ubuntu /home/ubuntu

USER ubuntu
ENV PATH="/home/ubuntu/.local/bin:${PATH}"

ARG KIRO_CLI_VERSION=2.0.1
RUN curl --proto '=https' --tlsv1.2 -sSf \
        "https://desktop-release.q.us-east-1.amazonaws.com/${KIRO_CLI_VERSION}/kirocli-x86_64-linux.zip" \
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
ENV AGENT_TYPE=kiro-python312
ENV AGENT_API_KEY=""
ENV AGENT_LABELS=kiro,python,python312

COPY --from=build --chown=ubuntu:ubuntu /app/publish .

VOLUME ["/home/ubuntu/.local/share/kiro-cli", "/home/ubuntu/.aws", "/app/workspaces"]
ENTRYPOINT ["dotnet", "CodingAgentWebUI.Agent.dll"]
