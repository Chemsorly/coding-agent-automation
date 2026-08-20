# =============================================================================
# CodingAgentWebUI Job Controller Dockerfile
# Runs the Job Controller service on port 8091.
# No Kiro CLI, Node.js, uv, or SDK in the runtime layer.
# =============================================================================

# Stage 1: Build
# --platform=$BUILDPLATFORM: SDK runs natively on the build host (ARM64 in CI, x64 locally).
# Cross-compiles to the target platform via -a $TARGETARCH.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
ARG TARGETARCH
WORKDIR /src

# Copy solution and project files first for layer caching
# Copy only the project files needed for the Job Controller and its dependencies (not test projects)
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY src/CodingAgentWebUI.Pipeline/CodingAgentWebUI.Pipeline.csproj src/CodingAgentWebUI.Pipeline/
COPY src/CodingAgentWebUI.Pipeline.CodeReview/CodingAgentWebUI.Pipeline.CodeReview.csproj src/CodingAgentWebUI.Pipeline.CodeReview/
COPY src/CodingAgentWebUI.Api.Client/CodingAgentWebUI.Api.Client.csproj src/CodingAgentWebUI.Api.Client/
COPY src/CodingAgentWebUI.Kubernetes/CodingAgentWebUI.Kubernetes.csproj src/CodingAgentWebUI.Kubernetes/
COPY src/CodingAgentWebUI.JobController/CodingAgentWebUI.JobController.csproj src/CodingAgentWebUI.JobController/
RUN dotnet restore src/CodingAgentWebUI.JobController/CodingAgentWebUI.JobController.csproj -a $TARGETARCH

# Copy everything else and publish
COPY . .
RUN dotnet publish src/CodingAgentWebUI.JobController/CodingAgentWebUI.JobController.csproj \
    -c Release \
    -a $TARGETARCH \
    --self-contained false \
    -o /app/publish

# Stage 2: Runtime (ASP.NET only — no SDK, no Kiro CLI, no Node.js)
# The Job Controller only manages Kubernetes job lifecycle and work distribution.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install curl for the docker-compose/Kubernetes healthcheck, and pre-create the app directory
# with the right ownership before the USER switch. Kept as one layer — these are a single setup
# step, and two RUN instructions cost two layers for it.
RUN apt-get update && \
    apt-get install -y --no-install-recommends curl && \
    rm -rf /var/lib/apt/lists/* && \
    mkdir -p /app && \
    chown -R ubuntu:ubuntu /app

USER ubuntu
WORKDIR /app

# Hygiene: assert no kubeconfig landed in the runtime image.
# A stray ~/.kube/config would let BuildDefaultConfig() silently redirect agent-Job
# creation to whatever cluster the file names — bypassing the in-cluster path even
# inside a real cluster. Fail the build immediately if any such file is present.
RUN test ! -e /home/ubuntu/.kube/config && \
    test ! -e /root/.kube/config && \
    echo "OK: no kubeconfig in runtime image"

# Configure ASP.NET to listen on port 8091
ENV ASPNETCORE_URLS=http://+:8091
ENV OTEL_SERVICE_NAME=coding-agent-jobcontroller

EXPOSE 8091

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

HEALTHCHECK --interval=10s --timeout=5s --retries=3 \
    CMD curl -f http://localhost:8091/healthz || exit 1

ENTRYPOINT ["dotnet", "CodingAgentWebUI.JobController.dll"]
