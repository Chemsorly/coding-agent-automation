# Pipeline Configuration

Pipeline behavior is configured via the web UI (Settings page) or the database. All configuration is persisted to PostgreSQL.

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
| `externalCiTimeout` | 00:15:00 | Max wait time for external CI completion (CI runs automatically when a Pipeline Provider is configured on the job template) |
| `externalCiPollInterval` | 00:00:30 | How often to poll external CI for status updates |
| `ciNotStartedTimeout` | 00:05:00 | How long to wait for CI runs to appear before concluding CI never started. Triggers re-push instead of burning the full `externalCiTimeout` |
| `ciNotStartedMaxRetries` | 5 | Max re-push retries when CI never starts (range: 0–20). Each retry creates an empty commit and force-pushes to re-trigger CI |
| `acceptanceCriteriaEnabled` | true | Enable acceptance criteria compliance check (runs in parallel with code reviewers, produces structured JSON report) |
| `blacklistedPaths` | .agent, .brain | Paths excluded from agent commits |
| `orphanedLabelSweepIntervalMinutes` | 30 | Minutes between orphaned label recovery sweeps (periodic background check for issues stuck with `agent:in-progress` label when no active run exists) |
| `failedWorkspaceRetentionDays` | 7 | Days to keep failed workspaces before cleanup |
| `stallWarningInterval` | 00:02:00 | Time without agent output before a stall warning is logged |
| `stallPollInterval` | 00:00:30 | How often to check for agent silence |
| `brainReadOnly` | false | If true, brain repo is synced pre-run but not written to post-run |
| `brainPushMaxRetries` | 3 | Max retries for pushing brain repo changes (handles concurrent push conflicts) |
| `outputBufferCapacity` | 10000 | Max lines of agent output kept in memory for the UI |
| `agentDisconnectGracePeriod` | 00:05:00 | How long to wait for a disconnected agent to reconnect before failing the run |
| `agentBusyProgressTimeout` | 01:00:00 | How long a busy agent can go without reporting progress before being marked stuck |
| `maxInfrastructureRetries` | 5 | Max retries for transient infrastructure failures (range: 0–10). These retries don't consume the agent's quality gate retry budget. |
| `transientRetryDelay` | 00:00:30 | Delay between retry loop iterations when a transient provider error (`ProviderRateLimit` or `ProviderOverload`) is encountered. Default: 30 seconds. Set to zero in tests for faster execution. |
| `heartbeatSweepIntervalSeconds` | 60 | Seconds between heartbeat monitor sweeps |
| `heartbeatTimeoutSeconds` | 90 | Seconds without a heartbeat before an agent is considered stale |
| `feedbackTimeoutSeconds` | 60 | Timeout in seconds for the agent call during feedback collection (both post-PR success path and post-retry-exhaustion failure path). Increase for slow models or large repositories. Configurable per project. |
| `analysisCommitThreshold` | 30 | Number of commits on the default branch since last analysis that triggers automatic analysis refresh. Set to 0 to disable commit-count staleness detection |

### Feature Toggles

| Setting | Default | Description |
|---------|---------|-------------|
| `analysisReviewEnabled` | true | Enable adversarial analysis review — a second agent reviews the analysis and feeds findings back for refinement before implementation begins |
| `baselineHealthCheckEnabled` | true | Run baseline health check (build + tests) on the default branch after branch creation and before code analysis. Catches broken base branches early |
| `refactoringReviewEnabled` | true | Enable discriminator review of refactoring proposals before issues are created |
| `brainConsolidationReviewEnabled` | true | Enable discriminator review of brain consolidation changes before they are committed |
| `harnessSuggestionsReviewEnabled` | true | Enable discriminator review of harness suggestions before they are persisted |

### Refactoring

| Setting | Default | Description |
|---------|---------|-------------|
| `maxRefactoringProposals` | 3 | Maximum refactoring proposals the agent produces per run. Controls both the prompt instruction and the issue creation cap |
| `hotspotAnalysisLookback` | 90.00:00:00 | Time window for git hotspot analysis in refactoring detection. Only commits within this window are counted |
| `refactoringOutcomeLookback` | 90.00:00:00 | Time window for querying past refactoring proposal outcomes. Only closed issues within this window are included in feedback context |

### Buffer Capacities

These control in-memory bounded data structures for each pipeline run. Rarely need adjustment unless running on constrained memory or needing deeper history.

| Setting | Default | Description |
|---------|---------|-------------|
| `outputLinesCapacity` | 5000 | Max lines in the `PipelineRun.OutputLines` bounded queue (UI live output) |
| `chatHistoryCapacity` | 200 | Max entries in the `PipelineRun.ChatHistory` bounded queue |
| `qualityGateHistoryCapacity` | 50 | Max entries in the `PipelineRun.QualityGateHistory` bounded queue |
| `retryErrorsCapacity` | 100 | Max entries in the `PipelineRun.RetryErrors` bounded queue |

### Decomposition

| Setting | Default | Description |
|---------|---------|-------------|
| `maxDecompositionSubIssues` | 10 | Maximum sub-issues the decomposition agent may propose per epic (range: 1–20) |
| `maxDecompositionSubIssueFiles` | 12 | Maximum files a single decomposition sub-issue may create or modify (range: 1–30). Controls scope per sub-issue to keep each one within single-agent capacity |
| `maxConcurrentDecompositions` | 2 | Maximum decomposition runs (across both phases) executing simultaneously |
| `decompositionTimeout` | 00:15:00 | Timeout for decomposition phases (separate from `agentTimeout`) |
| `maxOpenIssuesForContext` | 50 | Maximum open issues downloaded for deduplication context |

### Consolidation Dispatch

| Setting | Default | Description |
|---------|---------|-------------|
| `maxConsolidationDispatchRetries` | 5 | Maximum attempts the drain service will make to dispatch a consolidation job to an agent before marking the run as `Failed`. Consolidation jobs are not subject to the standard quality gate retry budget. Configurable per project. |

### Kubernetes

| Setting | Default | Description |
|---------|---------|-------------|
| `modelFetchTimeoutSeconds` | 120 | Timeout in seconds for the model-fetch K8s Job (`caa-models-*`). Increase on slow setups where image pull or pod scheduling takes longer than the default. Range: 30–600. |

### Chat Pod Lifecycle

These settings control the lifetime of ephemeral chat session pods dispatched by `ChatJobDispatcher`. They map to `workDistribution.dispatch.*` in `values.yaml` and are bound via `WorkDistribution:Dispatch:*` environment variables on the Pipeline API and Job Controller.

| values.yaml key / env var | Default | Description |
|---------------------------|---------|-------------|
| `workDistribution.dispatch.chatJobMaxDurationSeconds` | 7200 | Maximum lifetime (seconds) of a **chat session** K8s Job pod. Sets `activeDeadlineSeconds` on the chat pod spec — the pod is forcibly terminated by Kubernetes when this deadline passes. Minimum: 60s. **Note:** this setting does NOT apply to work-item agent jobs or consolidation jobs; those derive their `activeDeadlineSeconds` from `PipelineConfiguration.AgentTimeout` (per-project overridable, default 30 min). See [Configuration — Pipeline Settings](configuration.md#pipeline-settings). |
| `workDistribution.dispatch.chatPodConnectTimeoutSeconds` | 120 | Maximum time (seconds) the dispatcher waits for a chat pod to connect to the hub after the Job is created before aborting and returning an error to the caller. Minimum: 5s. |
| `workDistribution.dispatch.chatTerminationGracePeriodSeconds` | 120 | `terminationGracePeriodSeconds` on the chat pod spec — time Kubernetes allows for graceful shutdown before SIGKILL. Minimum: 5s. |
| `workDistribution.dispatch.chatIdleTimeoutSeconds` | 90 | Seconds a chat pod may remain idle (no client keepalive heartbeat) before the watcher terminates it automatically. The Blazor UI sends a heartbeat while the chat window is open; closed or crashed windows are cleaned up within this window. Minimum: 10s. |

> **Note on `api.replicas`:** The `WorkDistribution:Dispatch:ChatReplicaCount` env var is automatically derived from `api.replicas` by the Helm chart — it is not a standalone `workDistribution.dispatch.*` key. When Redis is absent and `api.replicas > 1`, `ChatJobDispatcher` emits a startup warning that keepalive heartbeats may be silently lost on non-watcher replicas.

## Quality Gate Settings

Quality gates are configured per-stack via Quality Gate Configurations (see [Label Routing](label-routing.md#quality-gate-configurations)). Each QGC has these fields:

| Field | Description |
|-------|-------------|
| `compilationCommand` / `compilationArguments` | Build command that must exit 0 |
| `testCommand` / `testArguments` | Test command that must have 0 failures |
| `coverageThreshold` | Minimum code coverage percentage (0-100). Set to `null` or `0` to disable coverage checks. |
| `coverageReportFormat` | `cobertura` or `jacoco` — determines how coverage reports are parsed |
| `coverageReportPaths` | Explicit file globs for coverage reports. When not specified, convention-based discovery is used. |
| `processTimeoutSeconds` | Maximum execution time in seconds for quality gate processes (compilation, tests). Default: `600` (10 minutes). Processes exceeding this limit are killed (entire process tree) and the gate is reported as failed. |

### Provider Error Handling in the Retry Loop

The retry loop classifies agent failures into categories to distinguish provider-side transient errors from code-level problems. This affects how retry budget is consumed:

| Error Category | HTTP Status | Retry Budget Consumed? | Behavior |
|----------------|-------------|------------------------|----------|
| `ProviderRateLimit` | 429 | **No** | `RetryCount` is rolled back. Loop waits `TransientRetryDelay` (default: 30 seconds) then retries from the same position without burning a fix attempt. No cap on consecutive transient retries — only the overall job timeout (`agentTimeout`) bounds this. |
| `ProviderOverload` | 503 | **No** | Same as `ProviderRateLimit` — waits `TransientRetryDelay`, no budget consumed. |
| `PermanentAuthFailure` | 401/403 | Yes (1 attempt counted) | Loop aborts immediately — credentials cannot be fixed by retrying. |
| `None` (default) | — | **Yes** | Normal code-fix attempt: `RetryCount` incremented, QG re-run after fix. |

> **Operator note:** A sustained 429/503 storm from the upstream LLM provider causes the retry loop to spin indefinitely until the job's `agentTimeout` fires. If you observe stalled runs with no code changes, check agent logs for repeated `ProviderRateLimit` or `ProviderOverload` classifications and investigate your LLM provider's rate limits or quota.

## Code Review Settings

Code review behavior is configured via the `codeReview` sub-object on the pipeline configuration.

| Setting | Default | Description |
|---------|---------|-------------|
| `codeReview.maxIterations` | 2 | Max review → fix cycles |
| `codeReview.fixPrompt` | *(null)* | When set, review splits into find-then-fix: review agents report findings with severity markers, then this fix prompt runs only if `[CRITICAL]` findings exist. When null, falls back to single-pass behavior |
| `codeReview.reviewIsolation` | Isolated | Controls whether review agents share the code-generation session or run isolated. Values: `Isolated` (default, no shared context — prevents bias) or `Shared` |

### Inline Comments

Inline comments post review findings directly on PR diff lines. Configured via `codeReview.inlineComments`:

| Setting | Default | Description |
|---------|---------|-------------|
| `inlineComments.enabled` | true | Master switch for inline comment posting. When false, posts body-only reviews |
| `inlineComments.maxInlineComments` | 15 | Maximum inline comments per review submission (range: 1–50). Excess findings appear only in the body summary |
| `inlineComments.maxRetries` | 1 | Retry attempts when the review agent doesn't produce structured file:line output (range: 0–5). Each retry is an additional LLM API call per agent |
| `inlineComments.orderBySeverity` | true | Sort inline comments by severity (Critical → Warning → Suggestion) when selecting within the limit |
| `inlineComments.severityThreshold` | `Warning` | Minimum severity for inline posting. Findings below this threshold appear only in the body summary |

### Image Extraction

Issue and PR bodies can contain embedded images (screenshots, diagrams). The pipeline extracts and downloads these images, then provides them to agents as native image parts for vision-capable models. Configured via the top-level pipeline settings:

| Setting | Default | Description |
|---------|---------|-------------|
| `enableIssueImageExtraction` | true | Master switch for image extraction from issue/PR bodies |
| `enableNativeImageParts` | true | Send downloaded images as native image parts to the agent API (requires vision-capable model) |
| `maxIssueImages` | 10 | Maximum images extracted per issue/PR |
| `maxImageSizeBytes` | 5242880 | Maximum size in bytes for a single downloaded image (5 MB) |
| `maxTotalImageSizeBytes` | 20971520 | Maximum total bytes for all downloaded images combined (20 MB) |
| `imageDownloadTimeoutSeconds` | 30 | Timeout in seconds for downloading a single image |
| `totalImageDownloadTimeoutSeconds` | 60 | Total time budget in seconds for downloading all images |

### Housekeeping

Controls automated PR branch management for templates with `HousekeepingEnabled: true`. On each poll cycle, the housekeeping service evaluates `agent:done` PRs: triggers server-side branch updates for PRs that are behind base (fire-and-forget, respects concurrency limit), and re-queues conflicted PRs for rework by swapping the linked issue label back to `agent:next`. Optionally runs stale branch cleanup on a configurable interval.

| Setting | Default | Description |
|---------|---------|-------------|
| `housekeepingConcurrencyLimit` | `1` | Max PRs simultaneously in "update triggered, CI running" state per repository. Enforced per `RepoProviderId`, not per template. Minimum effective value: 1 (values ≤ 0 are clamped). Can be overridden per template via `HousekeepingConcurrencyLimit` on the `PipelineJobTemplate`. |
| `housekeepingBranchCleanupIntervalMinutes` | `60` | How often (in minutes) stale agent branch cleanup runs per repository. Set to `0` to run every poll cycle. Only active when the template has `HousekeepingBranchCleanupEnabled: true`. |

Per-template controls (on `PipelineJobTemplate`):

| Field | Default | Description |
|-------|---------|-------------|
| `HousekeepingEnabled` | `false` | Master switch — enables PR mergeability polling and conflict rework for this template |
| `HousekeepingConcurrencyLimit` | `null` | Per-template override for concurrency limit. When `null`, falls back to the global `housekeepingConcurrencyLimit` |
| `HousekeepingBranchCleanupEnabled` | `false` | When `true`, deletes remote agent branches that have no open PR and whose linked issue carries no active label |



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
| HousekeepingEnabled | No | Whether this template manages agent:done PRs for branch updates and stale cleanup (default: false) |
| BrainReadOnly | No | When `true`, forces brain read-only mode for this template regardless of global and project-level settings. **One-directional override** — can only be set to `true`; a template cannot re-enable brain writes if the project has disabled them. Default: `false`. |

## Environment Variables

These environment variables are used by the Kubernetes deployment.

### Database

| Variable | Description |
|----------|-------------|
| `Database__Host` | PostgreSQL hostname (required — startup fails if not configured). |
| `Database__Port` | PostgreSQL port (default: `5432`) |
| `Database__Username` | PostgreSQL username |
| `Database__Password` | PostgreSQL password |
| `Database__Name` | PostgreSQL database name (default: `coding_agent_automation`). |
| `Database__SslMode` | Npgsql SSL mode: `Disable`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. The application normalizes `Prefer` to `Require` in production environments when no explicit value is set. Use `Disable` for local/in-cluster Postgres without TLS. |
| `Database__MigrateOnStartup` | Apply EF Core migrations on Pipeline API startup. The Helm default is `true` (applied automatically). Set `false` only for blue/green deployments where you apply migrations manually via `kubectl exec` into the API pod before cutover. |

### Config Import/Export

Pipeline configuration is managed via **Settings → Data Management**:

- **Export** — Downloads the full configuration as a single JSON bundle (providers, profiles, quality gates, reviewers, projects, templates)
- **Import** — Uploads a JSON bundle, clears existing config, and inserts from the bundle. Cache is invalidated immediately; UI refreshes automatically.

The bundle format is a flat JSON object with arrays for each entity type. Provider configurations include their inner `configuration` JSON (serialized `ProviderConfig` with full settings including credentials).

API endpoints:
- `GET /api/config/export` — returns the bundle as `application/json`
- `POST /api/config/import` — accepts `multipart/form-data` upload with field `file`

For full request/response examples, authentication details, and query parameters, see the [HTTP API Reference](api-reference.md). For migration scenarios, see [Bootstrap](bootstrap.md).

### Frontend Observability (Grafana Faro)

| Variable | Description |
|----------|-------------|
| `Faro__CollectorUrl` | Grafana Faro collector endpoint for frontend RUM data. Obtain from Grafana Cloud → Frontend Observability → Add App → copy the collector URL. When absent or empty, Faro is disabled and the `faroApi` stub no-ops silently — no errors thrown, no impact on the app. Example: `https://faro-collector-prod-eu-west-0.grafana.net/collect/<your-app-id>`. The URL contains a per-app token and is designed to be public-facing (safe to expose in the browser). Must be an `https://` URL; HTTP values are not validated at startup but will route data to an unintended destination. |

> **Free tier:** Grafana Cloud free tier includes 50,000 frontend sessions/month, which covers this feature at no cost.

> **Air-gapped / firewalled deployments:** When `Faro__CollectorUrl` is set, `faro-init.js` asynchronously loads two bundles from `unpkg.com` (CDN). In environments without outbound internet access, these loads fail silently — the app continues normally, Faro stays as a no-op stub, and there is **no page-load stall** (loading is async, not blocking). If you need Faro in a firewalled environment: download the pinned bundle files locally, copy them to `wwwroot/js/faro/`, and update `SDK_URL` / `TRACING_URL` in `faro-init.js` to relative paths (`js/faro/faro-web-sdk.iife.js`, etc.).

### Orchestrator

| Variable | Description |
|----------|-------------|
| `AGENT_API_KEY` | Shared secret for authenticating agent connections. Each agent derives its actual auth key via HMAC(master_key, agent_id). |
| `LOG_LEVEL` | Serilog log level (default: `Information`) |
| `PIPELINE_LOOP_STARTUP_DELAY_SECONDS` | Seconds to wait before resuming the pipeline loop after pod restart (default: 0, range: 0–300). The API now owns `IOrchestratorRunService` and rehydrates independently, so the Orchestrator no longer needs a startup delay. Increase only when a rolling-restart race condition is observed. |
| `READINESS_DRAIN_DELAY_SECONDS` | Seconds to wait after marking `/readyz` as 503 before shutting down (default: 15, range: 0–120). Used for zero-downtime rolling updates. |
| `PipelineApi__BaseUrl` | Base URL of the Pipeline API (e.g., `http://my-release-api.coding-agent.svc.cluster.local:8080`). **Required.** Used by `IPipelineApiConfigClient` to load pipeline configuration and by `IAgentHubConnection` as the fallback hub URL base. Set automatically by the Helm chart; override via `api.baseUrl` in `values.yaml` when the API is deployed externally or in a different namespace. |
| `PipelineApi__HubUrl` | Full URL of the Pipeline API SignalR hub (default: `{PipelineApi__BaseUrl}/hubs/agent`). The Orchestrator's `IAgentHubConnection` subscribes to this hub for live run streaming. Override via `api.hubUrl` in `values.yaml` only when the hub path differs from the default. |

### Pipeline API

| Variable | Description |
|----------|-------------|
| `DB_LOG_LEVEL` | EF Core SQL command log level (default: `Warning`). Set to `Information` or `Debug` for SQL query diagnostics. Only consumed by the Pipeline API process, which owns the database connection. |

### SignalR Backplane (multi-replica)

| Variable | Description |
|----------|-------------|
| `SignalR__Redis__ConnectionString` | Redis connection string for SignalR backplane (required when running multiple orchestrator replicas). Format: `host:port,password=xxx` |

### Database Maintenance

A background `DatabaseMaintenanceService` periodically deletes terminal records to prevent unbounded table growth. Configuration is via `PipelineConfiguration` properties (set in the pipeline config JSON in the database, not as environment variables):

| Setting | Default | Description |
|---------|---------|-------------|
| `PipelineRunRetentionCount` | `-1` (disabled) | Max `PipelineRuns` rows to retain per project. `-1` disables count-based retention. |
| `WorkItemRetentionCount` | `-1` (disabled) | Max terminal `WorkItems` rows to retain per project. `-1` disables count-based retention. |
| `DbRetentionSweepInterval` | `24h` | Interval between maintenance cycles. Minimum 1 minute. |
| `WorkDistribution:Reconciliation:StaleRetentionDays` | `7` | Days to retain terminal `WorkItems` (`Succeeded`, `Failed`, `Cancelled`) before deletion. Set via env var. |

> **Note:** Two retention mechanisms coexist for `PipelineRuns`:
> - `PipelineRunRetentionCount` (in `PipelineConfiguration`) — count-based cap per project; default `-1` (disabled)
> - `WorkDistribution:Reconciliation:PipelineRunRetentionDays` (on `DatabaseMaintenanceOptions`) — age-based deletion; default `30` days
>
> Both run on each maintenance sweep. Set `PipelineRunRetentionCount` to limit row count; set `PipelineRunRetentionDays` to limit row age. The `MaintenanceIntervalHours` config key no longer exists — it was replaced by `DbRetentionSweepInterval` in `PipelineConfiguration`. `ConsolidationRunRetentionDays` still exists on `DatabaseMaintenanceOptions` (default: **30 days**) and controls how long consolidation run history is kept.

The maintenance service is triggered by the Scheduler via `POST /api/scheduler/maintenance/retention-sweep`. In multi-replica Scheduler deployments, the Scheduler's leader election (`caa-{release}-scheduler-lock`) ensures only one Scheduler replica triggers sweeps.

### OpenTelemetry

| Variable | Description |
|----------|-------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP collector endpoint (e.g., `https://otlp-gateway.grafana.net/otlp`) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | OTLP protocol: `grpc` (default) or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Authentication headers for OTLP endpoint (e.g., `Authorization=Basic xxx`) |
| `OTEL_SERVICE_NAME` | Service name for telemetry (set per process — `coding-agent-orchestrator`, `coding-agent-api`, `coding-agent-jobcontroller`, `coding-agent-scheduler`). For the Orchestrator, configure via `otel.orchestratorServiceName` in `values.yaml`. Other processes use fixed names set in their own deployment templates. |
| `OTEL_RESOURCE_ATTRIBUTES` | Additional resource attributes (e.g., `deployment.environment=production`) |

### Agent Containers

| Variable | Description |
|----------|-------------|
| `ORCHESTRATOR_URL` | URL of the orchestrator's SignalR hub (e.g., `http://orchestrator:8080`) |
| `AGENT_ID` | Unique identifier for this agent instance (falls back to machine hostname if unset) |
| `AGENT_LABELS` | Comma-separated labels for routing (e.g., `kiro,dotnet,dotnet10`) |
| `AGENT_API_KEY` | Must match the orchestrator's key |
| `AGENT_API_KEY_FILE` | File path containing the API key (K8s Secret mount alternative to `AGENT_API_KEY` env var) |
| `AGENT_PROVIDER_TYPE` | Agent backend type: `KiroCli` (default) or `OpenCode` |
| `KIRO_CLI_PATH` | Override path for the Kiro CLI executable (default: `/home/ubuntu/.local/bin/kiro-cli`) |
| `OPENCODE_BASE_URL` | Override base URL for the OpenCode HTTP API (default: `http://127.0.0.1:4096`) |
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

### HTTP-Type MCP Servers

For HTTP-based MCP servers, use `"type": "http"` with a `url` field instead of `command`/`args`:

```json
{
  "mcpServers": {
    "my-remote-mcp": {
      "type": "http",
      "url": "https://mcp.example.com/mcp",
      "headers": {
        "Authorization": "Bearer <token>"
      },
      "disabled": false
    }
  }
}
```

The `headers` field passes HTTP request headers to the remote server (e.g., `Authorization` for authenticated endpoints). It is only used for `http` transport — ignored for `stdio` servers.

### Project-Level MCP Servers

MCP servers can be configured at the **agent profile level** (global) or at the **project level** (per-project override). Both are managed in the Settings UI.

**Merge semantics**: At dispatch time, project-level MCP servers are merged with the resolved agent profile's MCP servers:
- A project server with the same `Name` (case-insensitive) as a profile server **replaces** it
- Project servers with new names are **appended** to the profile list
- `null` project servers = inherit profile list unchanged

This allows projects to selectively override or augment the profile's MCP configuration without having to redefine the entire list.

**Chat session isolation**: MCP servers, project secrets, and steering content are **not** passed to interactive chat sessions. Chat sessions use the agent's own runtime environment. This is by design and is not configurable.

