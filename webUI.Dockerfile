# =============================================================================
# KiroWebUI Dockerfile
# Runs the Kiro Web UI (Blazor Server) inside a Linux container.
# Kiro CLI auth files must be mounted at runtime.
# =============================================================================

# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY KiroCliPoc.sln ./
COPY src/KiroCliLib/KiroCliLib.csproj src/KiroCliLib/
COPY src/KiroWebUI/KiroWebUI.csproj src/KiroWebUI/
COPY src/KiroCliPoc/KiroCliPoc.csproj src/KiroCliPoc/
COPY tests/KiroCliPoc.Tests/KiroCliPoc.Tests.csproj tests/KiroCliPoc.Tests/
RUN dotnet restore

# Copy everything else and publish
COPY . .
RUN dotnet publish src/KiroWebUI/KiroWebUI.csproj -c Release -o /app/publish

# Stage 2: Runtime (ASP.NET for Blazor Server)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS runtime

# Install dependencies for Kiro CLI (curl, unzip, ca-certificates)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        curl \
        unzip \
        ca-certificates \
        git \
    && rm -rf /var/lib/apt/lists/*

# Reuse existing ubuntu user (UID 1000) from the base image
RUN mkdir -p /home/ubuntu/.local/bin /home/ubuntu/.kiro && \
    chown -R ubuntu:ubuntu /home/ubuntu

# Install Kiro CLI as non-root user
USER ubuntu
ENV PATH="/home/ubuntu/.local/bin:${PATH}"
RUN curl --proto '=https' --tlsv1.2 -sSf \
        'https://desktop-release.q.us-east-1.amazonaws.com/latest/kirocli-x86_64-linux.zip' \
        -o /tmp/kirocli.zip && \
    unzip /tmp/kirocli.zip -d /tmp/kirocli && \
    /tmp/kirocli/kirocli/install.sh --no-confirm && \
    rm -rf /tmp/kirocli /tmp/kirocli.zip

WORKDIR /app

# Configure ASP.NET to listen on port 5000
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

# Copy published app and Docker-specific config (owned by ubuntu user)
COPY --from=build --chown=ubuntu:ubuntu /app/publish .
COPY --chown=ubuntu:ubuntu config/appsettings.docker.json config/appsettings.json

# Mount points:
#   /home/ubuntu/.kiro   - Kiro CLI auth/session data (REQUIRED)
#   /workspace           - Target workspace for the agent to operate on
VOLUME ["/home/ubuntu/.kiro", "/workspace"]

ENTRYPOINT ["dotnet", "KiroWebUI.dll"]
