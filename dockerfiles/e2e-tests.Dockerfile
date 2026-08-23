# =============================================================================
# E2E Test Runner Dockerfile
# Runs Playwright browser tests against the Blazor Server app (in-process).
# Based on .NET 10 SDK with Playwright Chromium + system dependencies.
#
# Usage:
#   docker build -f dockerfiles/e2e-tests.Dockerfile -t e2e-tests .
#   docker run --rm --ipc=host e2e-tests
# =============================================================================

# --platform=$BUILDPLATFORM: SDK runs natively on the build host (ARM64 in CI, x64 locally).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

# Restore into a world-readable location instead of the build user's ~/.nuget. The generated
# staticwebassets.runtime.json records absolute package paths, and the tests run as a non-root
# user further down — with the default cache the Blazor host dies on every test with
# DirectoryNotFoundException: /root/.nuget/packages/…/_framework/.
ENV NUGET_PACKAGES=/nuget

# Copy solution and project files for restore layer caching
COPY CodingAgentAutomation.sln ./
COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/CodingAgentWebUI.Pipeline/CodingAgentWebUI.Pipeline.csproj src/CodingAgentWebUI.Pipeline/
COPY src/CodingAgentWebUI.Pipeline.CodeReview/CodingAgentWebUI.Pipeline.CodeReview.csproj src/CodingAgentWebUI.Pipeline.CodeReview/
COPY src/CodingAgentWebUI.Infrastructure.Persistence/CodingAgentWebUI.Infrastructure.Persistence.csproj src/CodingAgentWebUI.Infrastructure.Persistence/
COPY src/CodingAgentWebUI.Infrastructure.Providers/CodingAgentWebUI.Infrastructure.Providers.csproj src/CodingAgentWebUI.Infrastructure.Providers/
COPY src/CodingAgentWebUI.Orchestration/CodingAgentWebUI.Orchestration.csproj src/CodingAgentWebUI.Orchestration/
# Added by Specs 042/043 — the harness runs the Pipeline API alongside the Blazor app, so the
# API, its shared hub library, its typed client and the K8s toolkit all take part in the restore.
COPY src/CodingAgentWebUI.Hub/CodingAgentWebUI.Hub.csproj src/CodingAgentWebUI.Hub/
COPY src/CodingAgentWebUI.Kubernetes/CodingAgentWebUI.Kubernetes.csproj src/CodingAgentWebUI.Kubernetes/
COPY src/CodingAgentWebUI.Api/CodingAgentWebUI.Api.csproj src/CodingAgentWebUI.Api/
COPY src/CodingAgentWebUI.Api.Client/CodingAgentWebUI.Api.Client.csproj src/CodingAgentWebUI.Api.Client/
COPY src/CodingAgentWebUI/CodingAgentWebUI.csproj src/CodingAgentWebUI/
COPY src/CodingAgentWebUI.Agent/CodingAgentWebUI.Agent.csproj src/CodingAgentWebUI.Agent/
COPY src/CodingAgentWebUI.Agent.KiroCli/CodingAgentWebUI.Agent.KiroCli.csproj src/CodingAgentWebUI.Agent.KiroCli/
COPY src/CodingAgentWebUI.Agent.OpenCode/CodingAgentWebUI.Agent.OpenCode.csproj src/CodingAgentWebUI.Agent.OpenCode/
COPY tests/CodingAgentWebUI.E2ETests/CodingAgentWebUI.E2ETests.csproj tests/CodingAgentWebUI.E2ETests/
COPY tests/CodingAgentWebUI.TestUtilities/CodingAgentWebUI.TestUtilities.csproj tests/CodingAgentWebUI.TestUtilities/
RUN dotnet restore tests/CodingAgentWebUI.E2ETests/CodingAgentWebUI.E2ETests.csproj

# Copy source and build
# NOTE: Do NOT use --no-restore here. The prior restore step doesn't fully resolve
# Blazor framework static web assets (blazor.web.js). Letting build do its own restore
# ensures the staticwebassets.runtime.json manifest includes the NuGet _framework/ path.
# The comment must sit on its own line. BuildKit does not strip a trailing comment from an
# instruction — `COPY . . # NOSONAR …` parses the `#` and every word after it as further source
# paths and fails with `lstat /#: no such file or directory`. That broke `docker build` for this
# file, which is the documented way to run the E2E suite, so the image stopped being rebuilt.
# NOSONAR - COPY . is required to build the full solution; .dockerignore explicitly excludes sensitive files (.env, .git, .kiro, config/, *credentials*)
COPY . .
# -p:IsTestProject=true so the test host and adapters land in the output; the csproj keeps it
# false to stay out of ci.yml's solution-wide `dotnet test`. See the csproj for why.
RUN dotnet build tests/CodingAgentWebUI.E2ETests/ -c Debug -p:IsTestProject=true

# Ensure the test host runs in Development mode so static web assets
# (including _framework/blazor.web.js from NuGet packages) are resolved correctly.
# ARG-gated so the ENV instruction does not hardcode a literal value (SonarQube docker:S4507).
# Default is Development — a functional requirement for Blazor static web asset resolution.
# Override via `--build-arg ASPNETCORE_ENVIRONMENT=Production` if used outside E2E test contexts.
ARG ASPNETCORE_ENVIRONMENT=Development
ENV ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT}

# Disable the Terminal Logger's animated progress/timer display (noisy in non-TTY output)
ENV MSBUILDTERMINALLOGGER=off

# Install libvips for NetVips image processing (used by pipeline under test)
# Install PowerShell (needed for playwright.ps1 browser installer)
RUN apt-get update && apt-get install -y --no-install-recommends libvips42 wget apt-transport-https \
    && wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb \
    && apt-get update && apt-get install -y --no-install-recommends powershell \
    && rm -rf /var/lib/apt/lists/*

# Install Playwright Chromium + system dependencies using the bundled script,
# then create a non-root user for running tests.
# TestResults directory is mounted as a volume; ensure the non-root user can write to it.
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
# groupadd/useradd rather than addgroup/adduser: the latter are Debian wrapper scripts from the
# `adduser` package, which the .NET 10 SDK image no longer ships. groupadd and useradd come from
# `passwd` and are always present.
#
# --create-home is not optional. `adduser --system` made a home directory implicitly; useradd does
# not, and the .NET CLI refuses to start without one ("The user's home directory could not be
# determined"). USER does not set $HOME either, so it is set explicitly below.
RUN pwsh tests/CodingAgentWebUI.E2ETests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium \
    && groupadd --system appgroup \
    && useradd --system --gid appgroup --create-home --home-dir /home/appuser appuser \
    && mkdir -p /src/TestResults && chown -R appuser:appgroup /src/TestResults \
    && chown -R appuser:appgroup /ms-playwright \
    && chmod -R a+rX /nuget
USER appuser
ENV HOME=/home/appuser
ENV DOTNET_CLI_HOME=/home/appuser

# Run E2E tests (use --ipc=host when running the container for Chromium stability).
# 'dotnet vstest' against the built DLL, so the run never re-enters MSBuild inside the container.
# The project now sets IsTestProject=true — with it false the VSTest target is a no-op and
# `dotnet test` silently discovers zero tests, which is how the CI job passed while running nothing.
ENTRYPOINT ["dotnet", "vstest", "tests/CodingAgentWebUI.E2ETests/bin/Debug/net10.0/CodingAgentWebUI.E2ETests.dll", "--TestCaseFilter:Category=E2E", "--logger:trx", "--ResultsDirectory:/src/TestResults"]
