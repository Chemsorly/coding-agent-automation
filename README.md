# Coding Agent Automation

An automated development pipeline that uses AI coding agents (Kiro CLI) to implement GitHub issues end-to-end: analyze the issue, generate code, run quality gates, and create a pull request — all orchestrated through a Blazor Server web UI running in Docker.

## How It Works

1. **Pick an issue** — Select a GitHub issue from the web UI (or let the closed-loop mode pick the next `agent:next` issue automatically)
2. **Analysis** — The agent reads the issue, explores the codebase, and writes an analysis with a planned approach
3. **Implementation** — The agent implements the changes, guided by the analysis
4. **Quality gates** — Automated checks run: build, tests, code review (multi-agent), external CI
5. **Retry loop** — If quality gates fail, the agent gets feedback and retries (configurable max retries)
6. **Pull request** — On success, a PR is created with the changes, linked to the original issue

The pipeline runs inside a Docker container with Kiro CLI installed. The web UI provides real-time visibility into each step.

## Prerequisites

- **Docker** — For building and running the application
- **.NET 10 SDK** — For local development (optional if only running via Docker)
- **GitHub App** — For issue/repository access and PR creation (configured in Settings)
- **Kiro CLI authentication** — The container needs Kiro CLI auth tokens (see First-Time Setup)

## Quick Start

### Build the Docker image

```powershell
docker build -f webUI.Dockerfile -t kiro-webui:latest .
```

### Run the container

```powershell
docker run -it --rm -p 5000:5000 -v ${PWD}/config/kiro-cli-data:/home/ubuntu/.local/share/kiro-cli -v "$env:USERPROFILE\.aws:/home/ubuntu/.aws" -v ${PWD}/config/kiro-settings:/home/ubuntu/.kiro/settings -v ${PWD}/config/pipeline:/app/config/pipeline kiro-webui:latest
```

Open `http://localhost:5000` in your browser.

### First-time setup

1. **Authenticate Kiro CLI** — On first run, exec into the container and run `kiro-cli login`. The auth tokens are persisted via the `kiro-cli-data` volume mount, so you only need to do this once.
2. **Configure providers** — Go to **Settings** in the web UI and set up your Issue Provider (GitHub App), Repository Provider, Agent Provider, and Pipeline Provider.
3. **Start a run** — Go to **Agent Coding**, select an issue, and click Start.

## Volume Mounts

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Kiro CLI auth | `/home/ubuntu/.local/share/kiro-cli` | Kiro CLI login tokens (persists across container restarts) |
| AWS SSO | `/home/ubuntu/.aws` | AWS SSO cache and config for Kiro CLI auth |
| Kiro settings | `/home/ubuntu/.kiro/settings` | MCP server config and CLI settings |
| Pipeline config | `/app/config/pipeline` | Provider configs, pipeline settings, run history (persists across restarts) |

Without the pipeline config mount, any providers configured in Settings will be lost when the container restarts.

Workspaces are created inside the container at `/app/workspaces/` (configurable via `workspaceBaseDirectory` in pipeline config). The pipeline clones a fresh copy of the repository for each run, so no workspace volume mount is needed. Successful workspaces are cleaned up automatically; failed ones are retained based on the `failedWorkspaceRetentionDays` setting.

## Project Structure

<!-- TODO: Update after ARC-11 (#146) and ARC-12 (#147) refactoring -->

```
src/
  KiroCliLib/          — Shared library: process management, output parsing, configuration
  KiroWebUI/           — Blazor Server app: UI, pipeline engine, providers, persistence
tests/
  KiroWebUI.Tests/     — Unit, property, integration, and smoke tests
config/
  pipeline/            — Provider configs and pipeline run history
  appsettings.json     — Application configuration
```

## Architecture

<!-- TODO: Add architecture diagram after ARC-12 (#147) refactoring completes.
     Target structure:
       KiroWebUI (Presentation) → KiroWebUI.Pipeline (Core) + KiroWebUI.Infrastructure
       See #147 for the full dependency graph and migration plan. -->

The application follows Clean Architecture principles:

- **Pipeline (Core)** — Interfaces, models, and orchestration services. Defines the pipeline steps, provider contracts, and data models. Zero infrastructure dependencies.
- **Infrastructure** — Provider implementations (GitHub API via Octokit, Kiro CLI agent, JSON config store, Git operations via LibGit2Sharp). Implements the interfaces defined in Pipeline.
- **WebUI (Presentation)** — Blazor Server components, DI wiring, and the application entry point.
- **KiroCliLib** — Shared library for Kiro CLI process management, output parsing, and configuration. Used by the agent provider to invoke Kiro CLI.

## Pipeline Configuration

Pipeline behavior is configured in `config/pipeline/pipeline-config.json`:

| Setting | Default | Description |
|---------|---------|-------------|
| `maxRetries` | 3 | Max retry attempts when quality gates fail |
| `agentTimeout` | 01:00:00 | Maximum time for a single agent invocation |
| `minCoverageThreshold` | 40 | Minimum test coverage percentage |
| `codeReview.enabled` | true | Enable multi-agent code review |
| `codeReview.maxIterations` | 2 | Max review → fix cycles |
| `externalCiEnabled` | true | Wait for GitHub Actions CI to pass |
| `externalCiTimeout` | 00:15:00 | Max wait time for external CI |
| `blacklistedPaths` | .kiro, .github, .brain | Paths excluded from agent commits |
| `cleanupSuccessfulWorkspaces` | true | Auto-delete workspaces after successful runs |
| `failedWorkspaceRetentionDays` | 7 | Days to keep failed workspaces |

### Closed-loop mode

The pipeline can run autonomously, polling for `agent:next` labeled issues and processing them sequentially:

| Setting | Default | Description |
|---------|---------|-------------|
| `closedLoopPollInterval` | 00:01:00 | How often to check for new issues |
| `closedLoopMaxRunsPerCycle` | 0 | Max issues per cycle (0 = unlimited) |
| `closedLoopMaxConsecutivePollFailures` | 5 | Failures before backing off |
| `closedLoopMaxBackoffInterval` | 00:15:00 | Max backoff between poll attempts |

## MCP Server Support

Kiro CLI supports [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) servers for extending agent capabilities. The Docker image includes `uv`/`uvx` (Python) and `npm`/`npx` (Node.js) for running MCP servers.

Configure MCP servers in your Kiro settings directory (mounted at `/home/ubuntu/.kiro/settings/mcp.json`):

```json
{
  "mcpServers": {
    "context7": {
      "command": "uvx",
      "args": ["context7-mcp@latest"],
      "env": {},
      "disabled": false,
      "autoApprove": []
    }
  }
}
```

Kiro CLI automatically discovers and starts configured MCP servers during pipeline runs. The `.kiro/` directory is in the pipeline's blacklisted paths, so MCP config and any credentials it contains are never committed.

## Testing

### Run all tests

```bash
dotnet test
```

### Run tests in Docker (Linux)

```bash
docker run --rm -v "${PWD}:/app" -w /app mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

## Development

### Local development

```bash
dotnet build
dotnet run --project src/KiroWebUI
```

### Code conventions

- Microsoft C# coding conventions
- SOLID principles
- Immutability patterns (`init`-only properties, `IReadOnlyList<T>`)
- Input validation with `ArgumentNullException.ThrowIfNull`
- Async I/O with `CancellationToken` propagation

## Roadmap

See [open issues](https://github.com/Chemsorly/coding-agent-automation/issues) for planned features. Key upcoming work:

- [ARC-12](https://github.com/Chemsorly/coding-agent-automation/issues/147) — Split KiroWebUI into Pipeline, Infrastructure, and WebUI projects
- [ARC-08a](https://github.com/Chemsorly/coding-agent-automation/issues/142) — Confidence gate for issue quality assessment
- [AGT-01](https://github.com/Chemsorly/coding-agent-automation/issues/10) — Crush as alternative agent provider

## License

This project is for internal use.
