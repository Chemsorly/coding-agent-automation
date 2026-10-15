# =============================================================================
# CodingAgent.Web Dockerfile
# Runs the Blazor Server web UI. It calls the Pipeline API as the signed-in user (/api/ui, /hubs/ui)
# and references no server project (spec 051), so it holds no agent API key and no provider client.
# The web service does NOT run Kiro CLI, dotnet build/test, or quality gates.
# Those responsibilities belong to agent containers (see agent-*.Dockerfile).
# =============================================================================

# Stage 1: Build
# --platform=$BUILDPLATFORM: SDK runs natively on the build host (ARM64 in CI, x64 locally).
# Cross-compiles to the target platform via -a $TARGETARCH.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
ARG TARGETARCH
WORKDIR /src

# Copy solution and project files first for layer caching
# Copy only the project files needed for the web service and its dependencies (not test projects)
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/CodingAgent.Pipeline.CodeReview/CodingAgent.Pipeline.CodeReview.csproj src/CodingAgent.Pipeline.CodeReview/
COPY src/CodingAgent.Contracts/CodingAgent.Contracts.csproj src/CodingAgent.Contracts/
COPY src/CodingAgent.Infrastructure.Common/CodingAgent.Infrastructure.Common.csproj src/CodingAgent.Infrastructure.Common/
COPY src/CodingAgent.Web.Client/CodingAgent.Web.Client.csproj src/CodingAgent.Web.Client/
COPY src/CodingAgent.Web/CodingAgent.Web.csproj src/CodingAgent.Web/
RUN dotnet restore src/CodingAgent.Web/CodingAgent.Web.csproj -a $TARGETARCH

# Copy everything else and publish
COPY . .
RUN dotnet publish src/CodingAgent.Web/CodingAgent.Web.csproj \
    -c Release -a $TARGETARCH --self-contained false -o /app/publish

# Stage 2: Runtime (ASP.NET only — no SDK, no Kiro CLI, no Node.js)
# The web service only serves the Blazor UI.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install curl for docker-compose/Kubernetes healthcheck, and pre-create the app
# directory with correct ownership (before USER switch)
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/* && \
    mkdir -p /app && chown ubuntu:ubuntu /app

USER ubuntu
WORKDIR /app

# Hygiene: assert no kubeconfig landed in the runtime image.
# A stray ~/.kube/config would let BuildDefaultConfig() silently redirect agent-Job
# creation to whatever cluster the file names — bypassing the in-cluster path even
# inside a real cluster. Fail the build immediately if any such file is present.
RUN test ! -e /home/ubuntu/.kube/config && \
    test ! -e /root/.kube/config && \
    echo "OK: no kubeconfig in runtime image"

# Configure ASP.NET to listen on port 8080
ENV ASPNETCORE_URLS=http://+:8080

# Agent API key for authenticating agent SignalR connections
ENV AGENT_API_KEY=""

EXPOSE 8080

# Copy published app (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .

# Generate build-info.json from build args (populated by CI, defaults to "local" for dev builds)
ARG BUILD_COMMIT_SHA=local
ARG BUILD_BRANCH=local
ARG BUILD_TIMESTAMP=unknown
ARG BUILD_RUN_ID=
ARG BUILD_RUN_NUMBER=
ARG BUILD_IMAGE_TAG=local
ARG BUILD_REPOSITORY_URL=
RUN echo "{\"commitSha\":\"${BUILD_COMMIT_SHA}\",\"branch\":\"${BUILD_BRANCH}\",\"buildTimestamp\":\"${BUILD_TIMESTAMP}\",\"runId\":\"${BUILD_RUN_ID}\",\"runNumber\":\"${BUILD_RUN_NUMBER}\",\"imageTag\":\"${BUILD_IMAGE_TAG}\",\"repositoryUrl\":\"${BUILD_REPOSITORY_URL}\"}" > build-info.json

# Expose git SHA as SERVICE_VERSION for OTEL service.version resource attribute
ENV SERVICE_VERSION=${BUILD_COMMIT_SHA}

HEALTHCHECK --interval=10s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "CodingAgent.Web.dll"]
