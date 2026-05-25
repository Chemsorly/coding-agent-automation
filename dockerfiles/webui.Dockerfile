# =============================================================================
# CodingAgentWebUI Orchestrator Dockerfile
# Runs the Blazor Server UI + SignalR hub for agent coordination.
# The orchestrator does NOT run Kiro CLI, dotnet build/test, or quality gates.
# Those responsibilities belong to agent containers (see agent-*.Dockerfile).
# =============================================================================

# Stage 1: Build
# --platform=$BUILDPLATFORM: SDK runs natively on the build host (ARM64 in CI, x64 locally).
# Cross-compiles to the target platform via -a $TARGETARCH.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
ARG TARGETARCH
WORKDIR /src

# Copy solution and project files first for layer caching
# Copy only the project files needed for the WebUI and its dependencies (not test projects)
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
RUN dotnet restore src/CodingAgentWebUI/CodingAgentWebUI.csproj -a $TARGETARCH

# Copy everything else and publish
COPY . .
RUN dotnet publish src/CodingAgentWebUI/CodingAgentWebUI.csproj -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Runtime (ASP.NET only — no SDK, no Kiro CLI, no Node.js)
# The orchestrator only serves Blazor UI and SignalR hub.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install curl for docker-compose/Kubernetes healthcheck
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/*

# Pre-create config and app directories with correct ownership (before USER switch)
RUN mkdir -p /app/config/pipeline/providers/issue \
             /app/config/pipeline/providers/repository \
             /app/config/pipeline/providers/agent \
             /app/config/pipeline/providers/pipeline \
             /app/config/pipeline/runs && \
    chown -R ubuntu:ubuntu /app

USER ubuntu
WORKDIR /app

# Configure ASP.NET to listen on port 8080
ENV ASPNETCORE_URLS=http://+:8080

# Agent API key for authenticating agent SignalR connections
ENV AGENT_API_KEY=""

EXPOSE 8080

# Copy published app and Docker-specific config (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .
COPY --chown=ubuntu:ubuntu config/appsettings.docker.json config/appsettings.json

# Generate build-info.json from build args (populated by CI, defaults to "local" for dev builds)
ARG BUILD_COMMIT_SHA=local
ARG BUILD_BRANCH=local
ARG BUILD_TIMESTAMP=unknown
ARG BUILD_RUN_ID=
ARG BUILD_RUN_NUMBER=
ARG BUILD_IMAGE_TAG=local
ARG BUILD_REPOSITORY_URL=
RUN echo "{\"commitSha\":\"${BUILD_COMMIT_SHA}\",\"branch\":\"${BUILD_BRANCH}\",\"buildTimestamp\":\"${BUILD_TIMESTAMP}\",\"runId\":\"${BUILD_RUN_ID}\",\"runNumber\":\"${BUILD_RUN_NUMBER}\",\"imageTag\":\"${BUILD_IMAGE_TAG}\",\"repositoryUrl\":\"${BUILD_REPOSITORY_URL}\"}" > build-info.json

# Mount points:
#   /app/config/pipeline - Pipeline provider & settings config (mount for persistence across restarts)
VOLUME ["/app/config/pipeline"]

HEALTHCHECK --interval=10s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "CodingAgentWebUI.dll"]
