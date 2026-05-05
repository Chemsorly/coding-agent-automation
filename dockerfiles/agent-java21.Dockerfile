# =============================================================================
# CodingAgentWebUI Agent Dockerfile (kiro-java21)
# Runs the Agent Worker process that executes the full pipeline end-to-end.
# Includes JDK 21, Maven, Kiro CLI, uv (for MCP servers), and git.
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

# Stage 2: Runtime (JDK 21 + Maven for Java quality gates)
FROM mcr.microsoft.com/dotnet/sdk:10.0.203 AS runtime

# Only install what this agent type needs: JDK 21, Maven, git, and curl/unzip for Kiro CLI
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        unzip \
        ca-certificates \
        git \
        openjdk-21-jdk-headless \
        maven \
        nodejs \
        npm \
    && rm -rf /var/lib/apt/lists/*

ENV JAVA_HOME=/usr/lib/jvm/java-21-openjdk-amd64

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

# uv needed for MCP servers (Python-based tools like context7)
RUN curl -LsSf https://astral.sh/uv/install.sh | sh

WORKDIR /app
RUN mkdir -p /app/workspaces

ENV ORCHESTRATOR_URL=""
ENV AGENT_ID=""
ENV AGENT_TYPE=kiro-java21
ENV AGENT_API_KEY=""
ENV AGENT_LABELS=kiro,java,java21

COPY --from=build --chown=ubuntu:ubuntu /app/publish .

VOLUME ["/home/ubuntu/.local/share/kiro-cli", "/home/ubuntu/.aws", "/app/workspaces"]
ENTRYPOINT ["dotnet", "CodingAgentWebUI.Agent.dll"]
