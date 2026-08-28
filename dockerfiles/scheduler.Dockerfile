# =============================================================================
# CodingAgentWebUI Scheduler Dockerfile
# Runs the Scheduler microservice on port 8080.
# Owns all scheduled/periodic background work (poll loop, maintenance sweeps,
# orphaned-label recovery, metrics polling, Redis cleanup).
# No EF Core, no Postgres — all persistence goes through the Pipeline API.
# =============================================================================

# Stage 1: Build
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
ARG TARGETARCH
WORKDIR /src

# Copy solution and project files first for layer caching
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/CodingAgentWebUI.Pipeline/CodingAgentWebUI.Pipeline.csproj src/CodingAgentWebUI.Pipeline/
COPY src/CodingAgentWebUI.Pipeline.CodeReview/CodingAgentWebUI.Pipeline.CodeReview.csproj src/CodingAgentWebUI.Pipeline.CodeReview/
COPY src/CodingAgentWebUI.Infrastructure.Providers/CodingAgentWebUI.Infrastructure.Providers.csproj src/CodingAgentWebUI.Infrastructure.Providers/
COPY src/CodingAgentWebUI.Orchestration/CodingAgentWebUI.Orchestration.csproj src/CodingAgentWebUI.Orchestration/
COPY src/CodingAgentWebUI.Kubernetes/CodingAgentWebUI.Kubernetes.csproj src/CodingAgentWebUI.Kubernetes/
COPY src/CodingAgentWebUI.Api.Client/CodingAgentWebUI.Api.Client.csproj src/CodingAgentWebUI.Api.Client/
COPY src/CodingAgentWebUI.Scheduler/CodingAgentWebUI.Scheduler.csproj src/CodingAgentWebUI.Scheduler/
RUN dotnet restore src/CodingAgentWebUI.Scheduler/CodingAgentWebUI.Scheduler.csproj -a $TARGETARCH

# Copy everything else and publish
COPY . .
RUN dotnet publish src/CodingAgentWebUI.Scheduler/CodingAgentWebUI.Scheduler.csproj \
    -c Release \
    -a $TARGETARCH \
    --self-contained false \
    -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/* && \
    mkdir -p /app && \
    chown -R ubuntu:ubuntu /app

USER ubuntu
WORKDIR /app

# Hygiene: assert no kubeconfig landed in the runtime image.
RUN test ! -e /home/ubuntu/.kube/config && \
    test ! -e /root/.kube/config && \
    echo "OK: no kubeconfig in runtime image"

# Configure ASP.NET to listen on port 8080
ENV ASPNETCORE_URLS=http://+:8080

EXPOSE 8080

COPY --from=build --chown=ubuntu:ubuntu /app/publish .

ARG BUILD_COMMIT_SHA=local
ARG BUILD_BRANCH=local
ARG BUILD_TIMESTAMP=unknown
ARG BUILD_RUN_ID=
ARG BUILD_RUN_NUMBER=
ARG BUILD_IMAGE_TAG=local
ARG BUILD_REPOSITORY_URL=
RUN echo "{\"commitSha\":\"${BUILD_COMMIT_SHA}\",\"branch\":\"${BUILD_BRANCH}\",\"buildTimestamp\":\"${BUILD_TIMESTAMP}\",\"runId\":\"${BUILD_RUN_ID}\",\"runNumber\":\"${BUILD_RUN_NUMBER}\",\"imageTag\":\"${BUILD_IMAGE_TAG}\",\"repositoryUrl\":\"${BUILD_REPOSITORY_URL}\"}" > build-info.json

HEALTHCHECK --interval=10s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "CodingAgentWebUI.Scheduler.dll"]
