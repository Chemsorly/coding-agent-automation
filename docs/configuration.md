# Pipeline Configuration

Pipeline behavior is configured in `config/pipeline/pipeline-config.json`.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for how these settings affect the state machine, and [Label Routing](label-routing.md) for per-stack quality gate and reviewer configuration.

## General Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `maxRetries` | 3 | Max retry attempts when quality gates fail |
| `maxAnalysisRetries` | 2 | Max retry attempts for the analysis phase (assessment file missing, malformed JSON, or analysis too short) |
| `agentTimeout` | 00:30:00 | Maximum time for a single agent invocation |
| `codeReview.enabled` | true | Enable multi-agent code review |
| `codeReview.maxIterations` | 2 | Max review → fix cycles |
| `externalCiTimeout` | 00:15:00 | Max wait time for external CI completion (CI runs automatically when a Pipeline Provider is configured on the job template) |
| `externalCiPollInterval` | 00:01:00 | How often to poll external CI for status updates |
| `blacklistedPaths` | .kiro, .github, .brain | Paths excluded from agent commits |
| `blacklistMode` | WarnAndExclude | How to handle blacklisted files. `WarnAndExclude` silently excludes them from commits. `Fail` aborts the run if the agent touches blacklisted paths. |
| `failedWorkspaceRetentionDays` | 7 | Days to keep failed workspaces before cleanup |
| `stallWarningInterval` | 00:02:00 | Time without agent output before a stall warning is logged |
| `stallPollInterval` | 00:00:30 | How often to check for agent silence |
| `brainReadOnly` | false | If true, brain repo is synced pre-run but not written to post-run |
| `brainPushMaxRetries` | 3 | Max retries for pushing brain repo changes (handles concurrent push conflicts) |
| `outputBufferCapacity` | 10000 | Max lines of agent output kept in memory for the UI |
| `agentDisconnectGracePeriod` | 00:05:00 | How long to wait for a disconnected agent to reconnect before failing the run |

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

## Environment Variables

These environment variables are used by the Docker containers:

### Orchestrator

| Variable | Description |
|----------|-------------|
| `AGENT_API_KEY` | Shared secret for authenticating agent connections |

### Agent Containers

| Variable | Description |
|----------|-------------|
| `ORCHESTRATOR_URL` | URL of the orchestrator's SignalR hub (e.g., `http://orchestrator:8080`) |
| `AGENT_ID` | Unique identifier for this agent instance |
| `AGENT_TYPE` | Agent type identifier (e.g., `kiro-dotnet10`) |
| `AGENT_LABELS` | Comma-separated labels for routing (e.g., `kiro,dotnet,dotnet10`) |
| `AGENT_API_KEY` | Must match the orchestrator's key |
| `LOG_LEVEL` | Serilog log level (default: `Information`) |

## MCP Server Support

The agent CLI supports [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) servers for extending agent capabilities. The Docker images include `uv`/`uvx` (Python) and `npm`/`npx` (Node.js) for running MCP servers.

Configure MCP servers in the agent's settings directory (mounted at `/home/ubuntu/.kiro/settings/mcp.json`):

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

The agent CLI automatically discovers and starts configured MCP servers during pipeline runs. The `.kiro/` directory is in the pipeline's blacklisted paths, so MCP config and any credentials it contains are never committed.
