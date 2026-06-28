# Pipeline Configuration

Pipeline behavior is configured in `config/pipeline/pipeline-config.json`.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for how these settings affect the state machine, [Label Routing](label-routing.md) for per-stack quality gate and reviewer configuration, and [Projects](projects.md) for per-project settings inheritance.

## Project-Level Settings

Projects can override most general settings on a per-project basis using a nullable override pattern. When a project setting is non-null, it replaces the corresponding global value for all templates in that project. See [Projects](projects.md) for full details on the inheritance model and configuration examples.

## General Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `maxRetries` | 3 | Max retry attempts when quality gates fail |
| `maxAnalysisRetries` | 2 | Max retry attempts for the analysis phase (assessment file missing, malformed JSON, or analysis too short) |
| `issuePageSize` | 25 | Number of issues fetched per page when polling the issue provider |
| `agentTimeout` | 00:30:00 | Maximum time for a single agent invocation |
| `codeReview.enabled` | true | Enable multi-agent code review |
| `codeReview.maxIterations` | 2 | Max review → fix cycles |
| `externalCiTimeout` | 00:15:00 | Max wait time for external CI completion (CI runs automatically when a Pipeline Provider is configured on the job template) |
| `externalCiPollInterval` | 00:00:30 | How often to poll external CI for status updates |
| `ciNotStartedTimeout` | 00:05:00 | How long to wait for CI runs to appear before concluding CI never started. Triggers re-push instead of burning the full `externalCiTimeout` |
| `ciNotStartedMaxRetries` | 5 | Max re-push retries when CI never starts (range: 0–20). Each retry creates an empty commit and force-pushes to re-trigger CI |
| `acceptanceCriteriaEnabled` | true | Enable acceptance criteria compliance check (runs in parallel with code reviewers, produces structured JSON report) |
| `blacklistedPaths` | .agent, .brain | Paths excluded from agent commits |

| `failedWorkspaceRetentionDays` | 7 | Days to keep failed workspaces before cleanup |
| `stallWarningInterval` | 00:02:00 | Time without agent output before a stall warning is logged |
| `stallPollInterval` | 00:00:30 | How often to check for agent silence |
| `brainReadOnly` | false | If true, brain repo is synced pre-run but not written to post-run |
| `brainPushMaxRetries` | 3 | Max retries for pushing brain repo changes (handles concurrent push conflicts) |
| `outputBufferCapacity` | 10000 | Max lines of agent output kept in memory for the UI |
| `agentDisconnectGracePeriod` | 00:05:00 | How long to wait for a disconnected agent to reconnect before failing the run |
| `agentBusyProgressTimeout` | 01:00:00 | How long a busy agent can go without reporting progress before being marked stuck |
| `maxInfrastructureRetries` | 5 | Max retries for transient infrastructure failures (range: 0–10). These retries don't consume the agent's quality gate retry budget. |

### Decomposition

| Setting | Default | Description |
|---------|---------|-------------|
| `maxDecompositionSubIssues` | 10 | Maximum sub-issues the decomposition agent may propose per epic (range: 1–20) |
| `maxConcurrentDecompositions` | 2 | Maximum decomposition runs (across both phases) executing simultaneously |
| `decompositionTimeout` | 00:15:00 | Timeout for decomposition phases (separate from `agentTimeout`) |
| `maxOpenIssuesForContext` | 50 | Maximum open issues downloaded for deduplication context |

## Quality Gate Settings

Quality gates are configured per-stack via Quality Gate Configurations (see [Label Routing](label-routing.md#quality-gate-configurations)). Each QGC has these fields:

| Field | Description |
|-------|-------------|
| `compilationCommand` / `compilationArguments` | Build command that must exit 0 |
| `testCommand` / `testArguments` | Test command that must have 0 failures |
| `coverageThreshold` | Minimum code coverage percentage (0-100). Set to `null` or `0` to disable coverage checks. |
| `coverageReportFormat` | `cobertura` or `jacoco` — determines how coverage reports are parsed |
| `coverageReportPaths` | Explicit file globs for coverage reports. When not specified, convention-based discovery is used. |

## Closed-Loop Mode

The pipeline can run autonomously, polling for `agent:next` labeled issues and processing them sequentially. Enable it from the web UI's pipeline loop controls.

See also: [Issue Workflows — Closed-Loop Mode](github-issue-workflows.md#closed-loop-mode) for behavioral details.

| Setting | Default | Description |
|---------|---------|-------------|
| `closedLoopPollInterval` | 00:01:00 | How often to check for new issues |
| `closedLoopMaxRunsPerCycle` | 0 | Max issues per cycle (0 = unlimited) |
| `closedLoopMaxConsecutivePollFailures` | 5 | Failures before backing off |
| `closedLoopMaxBackoffInterval` | 00:15:00 | Max backoff between poll attempts |
| `closedLoopCircuitBreakerCooldown` | 00:05:00 | Cooldown before circuit breaker auto-resumes polling after all templates fail |
| `closedLoopMaxPagesToFetch` | 10 | Max pages of issues to fetch when polling |

## Pipeline Job Templates

Pipeline Job Templates define which provider combination to use when polling for issues. Each template links an issue provider, repository provider, and optional brain/CI providers. Multiple templates enable round-robin polling across repositories.

Templates are managed in the **Agent Coding** page. When creating or viewing a template, the UI shows a preview of which label-mapped resources (quality gates, reviewers, agent profiles) will be assigned based on the repository's labels.

| Field | Required | Description |
|-------|----------|-------------|
| Name | Yes | Display name for the template |
| Issue Provider | Yes | Which repository to poll for `agent:next` issues |
| Repository Provider | Yes | Which repository to clone and push changes to |
| Brain Provider | No | Brain repository for knowledge persistence |
| Pipeline/CI Provider | No | External CI provider for pipeline status checks |
| ImplementationEnabled | No | Whether this template processes issues for implementation (default: true) |
| ReviewEnabled | No | Whether this template processes PRs for code review (default: true) |
| DecompositionEnabled | No | Whether this template processes epics for decomposition (default: false) |

## Environment Variables

These environment variables are used by the Docker containers:

### Database (DB+SignalR Mode)

| Variable | Description |
|----------|-------------|
| `Database__ConnectionString` | PostgreSQL connection string. When set, the orchestrator uses Postgres instead of JSON files for all configuration and work item persistence. |
| `Database__MigrateOnStartup` | Apply EF Core migrations on startup (default: `true`). Disable if using the Helm migration job. |

### Config Import/Export

In DB mode, pipeline configuration is managed via **Settings → Data Management**:

- **Export** — Downloads the full configuration as a single JSON bundle (providers, profiles, quality gates, reviewers, projects, templates)
- **Import** — Uploads a JSON bundle, clears existing config, and inserts from the bundle. Cache is invalidated immediately; UI refreshes automatically.

The bundle format is a flat JSON object with arrays for each entity type. Provider configurations include their inner `configuration` JSON (serialized `ProviderConfig` with full settings including credentials).

API endpoints:
- `GET /api/config/export` — returns the bundle as `application/json`
- `POST /api/config/import` — accepts multipart form upload of the bundle file

### Orchestrator

| Variable | Description |
|----------|-------------|
| `AGENT_API_KEY` | Shared secret for authenticating agent connections. Each agent derives its actual auth key via HMAC(master_key, agent_id). Legacy agents without an ID fall back to raw key comparison. |
| `LOG_LEVEL` | Serilog log level (default: `Information`) — also applies to the orchestrator |
| `PIPELINE_LOOP_STARTUP_DELAY_SECONDS` | Seconds to wait before resuming the pipeline loop after pod restart (default: 90, range: 0–600). Prevents dispatching to agents mid-termination during rolling updates. |
| `READINESS_DRAIN_DELAY_SECONDS` | Seconds to wait after marking `/readyz` as 503 before shutting down (default: 15). Used for zero-downtime rolling updates. |

### Agent Containers

| Variable | Description |
|----------|-------------|
| `ORCHESTRATOR_URL` | URL of the orchestrator's SignalR hub (e.g., `http://orchestrator:8080`) |
| `AGENT_ID` | Unique identifier for this agent instance (falls back to machine hostname if unset) |
| `AGENT_LABELS` | Comma-separated labels for routing (e.g., `kiro,dotnet,dotnet10`) |
| `AGENT_API_KEY` | Must match the orchestrator's key |
| `AGENT_PROVIDER_TYPE` | Agent backend type: `KiroCli` (default) or `OpenCode` |
| `OPENCODE_CONFIG_CONTENT` | JSON configuration for OpenCode agents (injected as environment variable, not needed for Kiro agents) |
| `OPENCODE_SERVER_PASSWORD` | Password for OpenCode server authentication (required for OpenCode agents) |
| `ANTHROPIC_API_KEY` | Anthropic API key for LLM access (required for OpenCode agents using Claude) |
| `OPENAI_API_KEY` | OpenAI API key for LLM access (optional, for OpenAI-backed agents) |
| `OPENROUTER_API_KEY` | OpenRouter API key for LLM access (optional, for OpenRouter-backed agents) |
| `LOG_LEVEL` | Serilog log level (default: `Information`) |

## Environment Setup Steps

Repository providers can define shell commands that run in the agent workspace after clone but before the agent starts. This is useful for package restore, private feed authentication, or tool installation.

Setup steps are configured on the **Repository Provider** in Settings → Providers → Repository → (select provider):

| Field | Type | Description |
|-------|------|-------------|
| `Secrets` | Dictionary | Key-value pairs injected as environment variables during setup step execution. Values are plaintext and masked in pipeline output (values ≥ 4 characters are redacted). |
| `SetupSteps` | List | Ordered shell commands executed sequentially via `/bin/bash -c`. Each step has a `Name` (display label) and a `Command` (the shell command). |

### Example Configuration

```json
{
  "providerType": "GitHub",
  "settings": { ... },
  "Secrets": {
    "NUGET_TOKEN": "ghp_xxxxxxxxxxxx",
    "PRIVATE_FEED_URL": "https://nuget.pkg.github.com/my-org/index.json"
  },
  "SetupSteps": [
    {
      "Name": "Configure NuGet feed",
      "Command": "dotnet nuget add source $PRIVATE_FEED_URL --name private --username bot --password $NUGET_TOKEN --store-password-in-clear-text"
    },
    {
      "Name": "Restore packages",
      "Command": "dotnet restore"
    }
  ]
}
```

### Behavior

- Steps execute in order; if any step returns a non-zero exit code, the run aborts with `Failed`
- Secrets are merged: project-level secrets as base, repo-level secrets overlay (repo wins on key collision)
- Secret values ≥ 4 characters are automatically masked in all subsequent pipeline output
- The step runs in the cloned workspace directory
- The pipeline step `RunningEnvironmentSetup` appears in the UI during execution

## Agent Steering Content

Repository providers can include custom markdown steering content that is written to the agent workspace before each run. This provides project-specific conventions, coding guidelines, or architectural context to the agent.

Configure via Settings → Providers → Repository → Steering Content field. The content is written to:
- `.kiro/steering/pipeline-repo.md` for Kiro agents (repository-level steering)
- `AGENTS.md` for OpenCode agents

Project-level steering (configured on the Project, not the provider) is written to `.kiro/steering/pipeline-project.md` for Kiro agents.

## MCP Server Support

The agent CLI supports [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) servers for extending agent capabilities. The Docker images include `uv`/`uvx` (Python) and `npm`/`npx` (Node.js) for running MCP servers.

Configure MCP servers in the agent's settings directory (written at runtime by `WriteMcpConfigStep` to `/home/ubuntu/.kiro/settings/mcp.json`):

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

The agent CLI automatically discovers and starts configured MCP servers during pipeline runs. The `.agent/` directory is in the pipeline's blacklisted paths, so MCP config and any credentials it contains are never committed.
