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
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src

# Copy solution and project files for restore layer caching
COPY CodingAgentAutomation.sln ./
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
COPY tests/CodingAgentWebUI.E2ETests/CodingAgentWebUI.E2ETests.csproj tests/CodingAgentWebUI.E2ETests/
RUN dotnet restore tests/CodingAgentWebUI.E2ETests/CodingAgentWebUI.E2ETests.csproj

# Copy source and build
# NOTE: Do NOT use --no-restore here. The prior restore step doesn't fully resolve
# Blazor framework static web assets (blazor.web.js). Letting build do its own restore
# ensures the staticwebassets.runtime.json manifest includes the NuGet _framework/ path.
COPY . .
RUN dotnet build tests/CodingAgentWebUI.E2ETests/ -c Debug

# Ensure the test host runs in Development mode so static web assets
# (including _framework/blazor.web.js from NuGet packages) are resolved correctly.
ENV ASPNETCORE_ENVIRONMENT=Development

# Install PowerShell (needed for playwright.ps1 browser installer)
RUN apt-get update && apt-get install -y --no-install-recommends wget apt-transport-https \
    && wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb \
    && dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb \
    && apt-get update && apt-get install -y --no-install-recommends powershell \
    && rm -rf /var/lib/apt/lists/*

# Install Playwright Chromium + system dependencies using the bundled script
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN pwsh tests/CodingAgentWebUI.E2ETests/bin/Debug/net10.0/playwright.ps1 install --with-deps chromium

# Run E2E tests (use --ipc=host when running the container for Chromium stability)
ENTRYPOINT ["dotnet", "test", "tests/CodingAgentWebUI.E2ETests/", "-c", "Debug", "--no-build", "--filter", "Category=E2E", "--logger", "trx", "--results-directory", "/src/TestResults"]
