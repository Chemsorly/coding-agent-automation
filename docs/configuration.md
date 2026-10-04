# Pipeline Configuration

Pipeline behavior is configured in the web UI (Settings page) and stored in PostgreSQL. Deployment details (images, replicas, chat pods, age-based retention) come from the Helm chart instead.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for how these settings affect the state machine, [Label Routing](label-routing.md) for per-stack quality gate and reviewer configuration, and [Projects](projects.md) for per-project settings inheritance.

## Where Settings Live

Each setting has one home:

| Scope | What it holds | Where to edit it |
|-------|---------------|------------------|
| Global settings | Every setting in the [reference](#settings-reference) below: the pipeline's policy defaults, and the loop, retention and delivery settings | Settings → Global Defaults (the model-fetch timeout is under Settings → Providers → Agent) |
| Project | Overrides of the settings marked **Project** in the reference, plus secrets, steering and MCP servers | Settings → Projects → (project) |
| Pipeline job template | The issue tracker and repository it binds, brain and CI providers, the workflow switches (implementation, review, decomposition, housekeeping), `BrainReadOnly` and the housekeeping limit | Pipelines page |
| Repository provider | Labels (which pick the agent profile, quality gates and reviewers), commit blacklist, secrets, setup steps, steering | Settings → Providers → Repository |
| Label catalogs | Agent profiles, quality gate configs and reviewer configs, each chosen by the repository's labels | Settings → Label Routing |
| Deployment | Images, concurrency per label set, chat pod lifetimes, age-based retention | Helm `values.yaml` and environment variables |

How the layers combine:

- A project override replaces the global value; an empty override inherits it. `codeReview` is merged field by field.
- A template can turn `BrainReadOnly` on, never off.
- Secrets merge by name, and the repository's win over the project's. MCP servers merge by name, and the project's win over the agent profile's. Project and repository steering are both written.
- A repository's blacklist replaces the global or project blacklist. `.agent` and `.brain` are always excluded.
- A repository's labels replace `defaultRequiredAgentLabels`. Every quality gate config and reviewer config whose labels match applies.

A setting that applies to a whole product is set once on its project. Reviewers and quality gates are chosen by repository labels (see [Label Routing](label-routing.md)); a project can add its own reviewer, the [project review](pr-review.md#project-review).

### Limits

Every number and duration has a range, listed in the reference. Saving global settings, a project or an import with a value outside its range is refused with a message that names the setting. A project override stored outside its range (saved before this check existed) is skipped when the configuration is resolved: that setting keeps the global value, the project's other overrides still apply, and the log gets a warning.

<!-- settings-reference:start -->
## Settings Reference

Settings are listed by their page under Settings → Global Defaults. The **Project** column marks the settings a project can override. Durations are written as `[d.]hh:mm:ss`.

### General

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `maxRetries` | 3 | 0–10 | ✓ | Max retry attempts when quality gates fail |
| `agentTimeout` | 00:30:00 | 1 min–1 day | ✓ | Limit for each agent call, in every run type including decomposition. Also the job deadline: Kubernetes stops the job after this value plus 60 seconds. The minimum is the reconciliation canary minimum |
| `maxInfrastructureRetries` | 5 | 0–10 | ✓ | Max retries for transient infrastructure failures; they don't consume the quality gate retry budget |
| `housekeepingConcurrencyLimit` | 1 | 1–20 | | Max PRs per repository in the "update triggered, CI running" state. A template can override it (see [Housekeeping](#housekeeping)) |
| `housekeepingBranchCleanupIntervalMinutes` | 60 | 0–10080 | | How often stale agent branch cleanup runs per repository; 0 runs every poll cycle |
| `housekeepingTriggerCooldownMinutes` | 25 | 1–1440 | | Minimum minutes between branch-update triggers for the same PR |
| `maxAnalysisRetries` | 2 | 0–10 | ✓ | Max retry attempts for the analysis phase (assessment file missing, malformed JSON, or analysis too short) |
| `analysisCommitThreshold` | 30 | 0–1000 | ✓ | Commits on the default branch since the last analysis that trigger a fresh analysis; 0 turns this off |
| `stallWarningInterval` | 00:02:00 | 30 s–1 h | ✓ | Time without agent output before a stall warning is logged |
| `stallPollInterval` | 00:00:30 | 5 s–2 min | | How often to check for agent silence |
| `feedbackTimeoutSeconds` | 60 | 10–600 | ✓ | Limit for the agent call that collects feedback after the PR is opened or after the retries are used up |
| `baselineHealthCheckEnabled` | true | | ✓ | Run a baseline health check (build + tests) after branch creation and before analysis; catches broken base branches early |
| `blacklistedPaths` | .agent, .brain | | ✓ | Paths excluded from agent commits; a repository provider's list replaces it |

### Pipeline Loop

The pipeline loop polls for `agent:next` issues, PRs and epics and dispatches them. Start and stop it with the loop controls on the Pipelines page. See also: [Issue Workflows — Closed-Loop Mode](github-issue-workflows.md#closed-loop-mode).

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `closedLoopPollInterval` | 00:01:00 | 10 s–10 min | | How often the loop checks for new work when idle |
| `closedLoopMaxRunsPerCycle` | 0 | 0–1000 | | Max dispatches per cycle; 0 = unlimited |
| `minIssueSlots` | 1 | 0–100 | | Slots per cycle held back for implementation issues when PRs and other higher-priority work would take them all. Applies when `closedLoopMaxRunsPerCycle` is 0 or at least 2; 0 = strict priority |
| `closedLoopMaxPagesToFetch` | 10 | 1–100 | | Max pages of issues fetched per poll (100 issues per page) |
| `closedLoopMaxConsecutivePollFailures` | 5 | 1–50 | | Consecutive poll failures before the circuit breaker pauses the loop |
| `closedLoopCircuitBreakerCooldown` | 00:05:00 | 30 s–1 h | | Pause before the circuit breaker resumes polling |
| `orphanedLabelSweepIntervalMinutes` | 30 | 5–1440 | | Minutes between sweeps for issues stuck with an agent label and no active run |
| `queueSweepEnabled` | true | | | After each dispatch pass, cancel Pending work items whose issue or PR is no longer eligible (closed, label removed, or terminal) |

### Prompts

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `analysisPrompt` | *(built in)* | | ✓ | Prompt for the analysis phase |
| `implementationPrompt` | *(built in)* | | ✓ | Prompt for the implementation phase |
| `analysisReviewEnabled` | true | | ✓ | Adversarial analysis review: a second agent reviews the analysis in an isolated session and feeds its findings back before implementation begins |
| `analysisReviewPrompt` | *(built in)* | | ✓ | Prompt for the analysis reviewer |
| `analysisRefinementPrompt` | *(built in)* | | ✓ | Prompt that asks the analysis agent to refine the analysis from the review |

### Decomposition

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `maxDecompositionSubIssues` | 10 | 1–20 | ✓ | Max sub-issues the decomposition agent may propose per epic |
| `maxDecompositionSubIssueFiles` | 12 | 1–30 | ✓ | Max files one sub-issue may create or modify, to keep each within one agent's capacity |
| `maxConcurrentDecompositions` | 2 | 1–10 | | Max decomposition runs at the same time, across all projects |
| `maxOpenIssuesForContext` | 50 | 1–200 | ✓ | Max open issues downloaded as de-duplication context |

### External CI

External CI runs when a Pipeline/CI provider is set on the pipeline job template.

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `externalCiTimeout` | 00:15:00 | 1 min–1 day | ✓ | Max wait for external CI to complete |
| `externalCiPollInterval` | 00:00:30 | 5 s–5 min | ✓ | How often CI status is polled |
| `ciNotStartedTimeout` | 00:10:00 | 1–30 min | ✓ | How long to wait for CI runs to appear before re-pushing, instead of waiting out `externalCiTimeout` |
| `ciNotStartedMaxRetries` | 15 | 0–20 | ✓ | Max re-pushes when CI never starts; each pushes an empty commit |
| `ciCancelledMoveMaxRetries` | 3 | 0–10 | ✓ | Re-polls when CI is cancelled because the branch moved to a new commit, instead of counting a failed gate. The whole wait stays within `externalCiTimeout` |

### Code Review

Implementation runs review their changes before the pull request is opened. PR review runs review a pull request once and never change code. Which reviewers run is set per repository label in [Reviewer Configs](label-routing.md), plus the project's reviewer when its [project review](pr-review.md#project-review) is on; whether a repository's pull requests are reviewed is the pipeline job template's Review switch.

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `codeReview.maxIterations` | 2 | 0–5 | ✓ | Review → fix cycles in implementation runs; 0 turns their review step off. PR reviews are not affected |
| `codeReview.fixPrompt` | *(empty)* | | ✓ | When set, implementation-run review splits into find-then-fix: this prompt runs only if `[CRITICAL]` findings exist. Empty = single pass |
| `codeReview.inlineComments.enabled` | true | | ✓ | PR reviews post findings as comments on the changed lines, in addition to the review summary |
| `codeReview.inlineComments.severityThreshold` | `Warning` | | ✓ | Minimum severity for inline comments; the other findings appear only in the summary |
| `codeReview.inlineComments.maxInlineComments` | 15 | 1–50 | ✓ | Max inline comments per review; the rest appear only in the summary |
| `codeReview.inlineComments.orderBySeverity` | true | | ✓ | Choose inline comments by severity (Critical → Warning → Suggestion) when there are more than the limit |
| `codeReview.inlineComments.maxRetries` | 1 | 0–5 | ✓ | Re-asks when a reviewer's output lacks file:line references; each is one more LLM call per reviewer |
| `acceptanceCriteriaEnabled` | true | | ✓ | Acceptance criteria check next to the reviewers, reporting criterion by criterion |
| `acceptanceCriteriaPrompt` | *(built in)* | | | Prompt for the acceptance criteria check |

### Consolidation

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `maxRefactoringProposals` | 3 | 1–10 | ✓ | Max refactoring proposals per scan; caps both the prompt and the issues created |
| `refactoringReviewEnabled` | true | | ✓ | Discriminator review of refactoring proposals before issues are created |
| `brainConsolidationReviewEnabled` | true | | ✓ | Discriminator review of brain consolidation changes before they are committed |
| `harnessSuggestionsReviewEnabled` | true | | | Discriminator review of harness suggestions before they are stored; global, because harness suggestions belong to no project |
| `hotspotAnalysisLookback` | 90.00:00:00 | 7–365 days | | Window of commits counted by the hotspot analysis of refactoring scans |
| `refactoringOutcomeLookback` | 90.00:00:00 | 7–365 days | | Window of closed refactoring issues used as feedback |

### Advanced

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `defaultRequiredAgentLabels` | *(empty)* | | | Agent labels for repositories whose provider sets none; empty = any agent |
| `brainReadOnly` | false | | ✓ | Runs read the brain but never write to it, and brain consolidation does not run. A project or template can also turn it on (see [Brain Consolidation](feedback-and-consolidation.md#brain-consolidation-per-brain)) |
| `brainPushMaxRetries` | 3 | 0–10 | | Attempts to push brain changes, by runs and by brain consolidation |
| `enableIssueImageExtraction` | true | | | Download images from issue and PR bodies and give them to the agent |
| `enableNativeImageParts` | true | | | Send downloaded images to the agent as images; when off, the prompt still references the downloaded files |
| `maxIssueImages` | 10 | 0–50 | | Max images per issue or PR |
| `maxImageSizeBytes` | 5242880 | 1–50 MB | | Larger images are skipped (default 5 MB) |
| `maxTotalImageSizeBytes` | 20971520 | 1–200 MB | | Download stops once one issue's images reach this size (default 20 MB) |
| `totalImageDownloadTimeoutSeconds` | 60 | 5–600 | | Time budget for downloading one issue's images |
| `pipelineRunRetentionCount` | -1 | -1–1000000 | | Completed pipeline runs kept per project by the hourly retention sweep; 0 or -1 keeps all (see [Database Maintenance](#database-maintenance)) |
| `workItemRetentionCount` | -1 | -1–1000000 | | Finished work items kept per project; 0 or -1 keeps all |
| `feedbackCommentOutboxMaxAttempts` | 5 | 1–100 | | Attempts to post a run's feedback comment before giving up on it |

### Agent Provider

Edited under Settings → Providers → Agent, in the Kiro provider form in Kubernetes mode.

| Setting | Default | Range | Project | Description |
|---------|---------|-------|---------|-------------|
| `modelFetchTimeoutSeconds` | 120 | 30–600 | | Limit for the model-fetch Kubernetes Job (`caa-models-*`); increase where image pulls or pod scheduling are slow |

<!-- settings-reference:end -->

### Internal Fields

The stored configuration has four fields that are not settings, and no page offers them:

- `closedLoopAutoStart` records whether the loop runs; the loop controls set it.
- `pipelineInjectedPaths` is filled in at runtime from the agent provider.
- `transientRetryDelay` is the wait after a provider rate limit or overload (30 seconds); tests set it to zero.
- `workspaceBaseDirectory` is where agents create run workspaces: `./workspaces`, which is `/app/workspaces` in the agent images.

### Removed Settings

These settings had no effect and were removed: `issuePageSize`, `lastUsedProviderIds`, `failedWorkspaceRetentionDays`, `agentDisconnectGracePeriod`, `agentBusyProgressTimeout`, `heartbeatSweepIntervalSeconds`, `heartbeatTimeoutSeconds`, `outputBufferCapacity`, `outputLinesCapacity`, `chatHistoryCapacity`, `qualityGateHistoryCapacity`, `retryErrorsCapacity`, `closedLoopMaxBackoffInterval`, `dbRetentionSweepInterval`, `imageDownloadTimeoutSeconds`, `maxConsolidationDispatchRetries` and `codeReview.reviewIsolation`, as well as the agent provider's timeout. Stored configurations and exports that still contain them load normally, and the values are ignored.

## Chat Pod Lifecycle

These settings control the lifetime of ephemeral chat session pods. Pod dispatch is handled by `ChatJobDispatcher`; the per-session idle-kill loop and K8s job polling run in `ChatSessionWatcher`; cross-replica heartbeat storage uses `ChatHeartbeatTracker` (only when Redis is configured). Settings map to `workDistribution.dispatch.*` in `values.yaml` and are bound via `WorkDistribution:Dispatch:*` environment variables on the Pipeline API and Job Controller.

| values.yaml key / env var | Default | Description |
|---------------------------|---------|-------------|
| `workDistribution.dispatch.chatJobMaxDurationSeconds` | 7200 | Maximum lifetime (seconds) of a **chat session** K8s Job pod. Sets `activeDeadlineSeconds` on the chat pod spec — the pod is forcibly terminated by Kubernetes when this deadline passes. Minimum: 60s. **Note:** this setting does NOT apply to work-item agent jobs or consolidation jobs; those derive their `activeDeadlineSeconds` from `agentTimeout` (per-project overridable, default 30 min). See [General](#general). |
| `workDistribution.dispatch.chatPodConnectTimeoutSeconds` | 120 | Maximum time (seconds) the dispatcher waits for a chat pod to connect to the hub after the Job is created before aborting and returning an error to the caller. Minimum: 5s. |
| `workDistribution.dispatch.chatTerminationGracePeriodSeconds` | 120 | `terminationGracePeriodSeconds` on the chat pod spec — time Kubernetes allows for graceful shutdown before SIGKILL. Minimum: 5s. |
| `workDistribution.dispatch.chatIdleTimeoutSeconds` | 90 | Seconds a chat pod may remain idle (no client keepalive heartbeat) before `ChatSessionWatcher` terminates it automatically. The Blazor UI sends a heartbeat while the chat window is open; closed or crashed windows are cleaned up within this window. Minimum: 10s. |

> **Note on `api.replicas`:** The `WorkDistribution:Dispatch:ChatReplicaCount` env var is automatically derived from `api.replicas` by the Helm chart — it is not a standalone `workDistribution.dispatch.*` key. When Redis is absent and `api.replicas > 1`, `ChatJobDispatcher` emits a startup warning that keepalive heartbeats may be silently lost on non-watcher replicas (since `ChatHeartbeatTracker` is only instantiated when Redis is configured).

## Quality Gate Settings

Quality gates are configured per-stack via Quality Gate Configurations (see [Label Routing](label-routing.md#quality-gate-configurations)). Each QGC has these fields:

| Field | Description |
|-------|-------------|
| `compilationCommand` / `compilationArguments` | Build command that must exit 0 |
| `testCommand` / `testArguments` | Test command that must have 0 failures |
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

## Housekeeping

Housekeeping manages the agent's pull requests for pipeline job templates with `HousekeepingEnabled: true`. On each poll cycle it evaluates `agent:done` PRs: it triggers server-side branch updates for PRs that are behind base (fire-and-forget, within the concurrency limit), and re-queues conflicted PRs for rework by swapping the linked issue label back to `agent:next`. It can also delete stale agent branches at an interval. The global settings are `housekeepingConcurrencyLimit`, `housekeepingBranchCleanupIntervalMinutes` and `housekeepingTriggerCooldownMinutes` (see [General](#general)).

Per-template controls (on `PipelineJobTemplate`):

| Field | Default | Description |
|-------|---------|-------------|
| `HousekeepingEnabled` | `false` | Master switch — enables PR mergeability polling and conflict rework for this template |
| `HousekeepingConcurrencyLimit` | `null` | Per-template override for the concurrency limit, within the global setting's range. When `null`, the global `housekeepingConcurrencyLimit` applies |
| `HousekeepingBranchCleanupEnabled` | `false` | When `true`, deletes remote agent branches that have no open PR and whose linked issue carries no active label |

## Pipeline Job Templates

Pipeline Job Templates define which provider combination to use when polling for issues. Each template links an issue provider, repository provider, and optional brain/CI providers. Multiple templates enable round-robin polling across repositories.

Templates are managed on the **Pipelines** page (route `/pipelines`; `/agent-coding` still works as an alias). When creating or viewing a template, the UI shows a preview of which label-mapped resources (quality gates, reviewers, agent profiles) will be assigned based on the repository's labels. **Edit** changes a template in place; it keeps its id, and with it its run and consolidation history. A template keeps its issue tracker and repository: to use another one, add a new template. **Move to…** changes its project.

| Field | Required | Description |
|-------|----------|-------------|
| Name | Yes | Display name for the template |
| Issue Provider | Yes | Which issue tracker to poll for `agent:next` issues. Fixed once the template is saved |
| Repository Provider | Yes | Which repository to clone and push changes to. Fixed once the template is saved |
| Brain Provider | No | Brain repository for knowledge persistence |
| Pipeline/CI Provider | No | External CI provider for pipeline status checks |
| ImplementationEnabled | No | Whether this template processes issues for implementation (default: true) |
| ReviewEnabled | No | Whether this template processes PRs for code review (default: true) |
| DecompositionEnabled | No | Whether this template processes epics for decomposition (default: false) |
| HousekeepingEnabled | No | Whether this template manages agent:done PRs for branch updates and stale cleanup (default: false) |
| BrainReadOnly | No | When `true`, forces brain read-only mode for this template regardless of global and project-level settings: its runs do not write to the brain, and brain consolidation does not run from it. **One-directional override** — can only be set to `true`; a template cannot re-enable brain writes if the project has disabled them. Default: `false`. |

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
| `AGENT_API_KEY` | Master key for authenticating agent connections. Each agent Job receives its own key, HMAC(master_key, agent_id), from a per-Job Secret; agents never see the master key. |
| `LOG_LEVEL` | Serilog log level (default: `Information`) |
| `PIPELINE_LOOP_STARTUP_DELAY_SECONDS` | Seconds to wait before resuming the pipeline loop after pod restart (default: 0, range: 0–300). The API now owns `IOrchestratorRunService` and rehydrates independently, so the Orchestrator no longer needs a startup delay. Increase only when a rolling-restart race condition is observed. **Note:** `CodingAgent.Web` reads this via the IConfiguration keys `Orchestrator:PipelineLoopStartupDelaySeconds` or `Env:PipelineLoopStartupDelaySeconds`; the Helm-injected flat env var `PIPELINE_LOOP_STARTUP_DELAY_SECONDS` does not map to either of those paths and is effectively ignored at present (the value is always 0 in Kubernetes). |
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

The Scheduler triggers a retention sweep every hour (`POST /api/scheduler/maintenance/retention-sweep`); in multi-replica Scheduler deployments, its leader election (`caa-{release}-scheduler-lock`) ensures only one replica triggers it. The sweep deletes old rows in two ways:

| Setting | Default | Where | Description |
|---------|---------|-------|-------------|
| `pipelineRunRetentionCount` | `-1` (keep all) | Settings → Global Defaults → Advanced | Completed `PipelineRuns` kept per project; 0 or -1 keeps all |
| `workItemRetentionCount` | `-1` (keep all) | Settings → Global Defaults → Advanced | Terminal `WorkItems` kept per project; 0 or -1 keeps all |
| `WorkDistribution:Reconciliation:StaleRetentionDays` | `7` | Helm `workDistribution.reconciliation.staleRetentionDays` | Days to keep terminal `WorkItems` (`Succeeded`, `Failed`, `Cancelled`) |
| `WorkDistribution:Reconciliation:PipelineRunRetentionDays` | `30` | environment variable | Days to keep completed `PipelineRuns`, which includes consolidation run history |

Both limits apply on each sweep: the counts cap the rows per project, the days cap their age.

### OpenTelemetry

| Variable | Description |
|----------|-------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | OTLP collector endpoint (e.g., `https://otlp-gateway.grafana.net/otlp`) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | OTLP protocol: `grpc` (default) or `http/protobuf` |
| `OTEL_EXPORTER_OTLP_HEADERS` | Authentication headers for OTLP endpoint (e.g., `Authorization=Basic xxx`) |
| `OTEL_SERVICE_NAME` | Service name for telemetry (set per process — `coding-agent-web`, `coding-agent-api`, `coding-agent-jobcontroller`, `coding-agent-scheduler`, `coding-agent-worker`). For the web service, configure via `otel.webServiceName` in `values.yaml` (legacy alias `otel.orchestratorServiceName` still honored). For agent pods, `JobSpecBuilder` sets this to `coding-agent-worker` unconditionally. Other processes use fixed names set in their own deployment templates. |
| `OTEL_RESOURCE_ATTRIBUTES` | Additional resource attributes (e.g., `deployment.environment=production`) |

### Agent Containers

| Variable | Description |
|----------|-------------|
| `ORCHESTRATOR_URL` | URL of the orchestrator's SignalR hub (e.g., `http://orchestrator:8080`) |
| `AGENT_ID` | Unique identifier for this agent instance (falls back to machine hostname if unset) |
| `AGENT_LABELS` | Comma-separated labels for routing (e.g., `kiro,dotnet,dotnet10`) |
| `AGENT_API_KEY` | The agent's own key, `HMAC-SHA256(master key, AGENT_ID)`, used as-is. Every dispatched agent Job (work item, consolidation, chat, model fetch) receives it from its per-Job Secret `caa-key-{job name}`; agent pods never receive the master key. An agent started by hand needs the same derived key. |
| `AGENT_PROVIDER_TYPE` | Agent backend type: `KiroCli` or `OpenCode`. When absent or empty, defaults to `KiroCli`. |
| `KIRO_CLI_PATH` | Override path for the Kiro CLI executable (default: `/root/.local/bin/kiro-cli`) |
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

## Runtime System Packages

Agent pods run as the non-root `ubuntu` user with all Linux capabilities dropped, so `apt-get install` and `sudo` fail there. The agent images include `user-apt`, which installs Ubuntu packages without root:

```bash
user-apt install libgbm1 libxkbcommon0
. ~/.user-apt/env && <command that needs the packages>
```

`user-apt` resolves the packages and their missing dependencies with apt and downloads them. It unpacks them under `~/.user-apt` with `dpkg -x` and writes `~/.user-apt/env`, which puts their programs and libraries on `PATH` and `LD_LIBRARY_PATH`. Maintainer scripts do not run, so it suits libraries and plain command-line tools, not services. The packages last only as long as the pod.

The images stay free of project-specific tools and versions; a repository's own docs say what its tasks need. To make agents aware of `user-apt`, add a line like this to the project steering content:

> You run as a non-root user without sudo. To install an Ubuntu package (a library or command-line tool), run `user-apt install <package>...`, then prefix the command that needs it with `. ~/.user-apt/env &&`.

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

### SSE-Type MCP Servers

For Server-Sent Events (SSE) based MCP servers, use `"type": "sse"` with a `url` field:

```json
{
  "mcpServers": {
    "my-sse-mcp": {
      "type": "sse",
      "url": "https://mcp.example.com/sse",
      "headers": {
        "Authorization": "Bearer <token>"
      },
      "disabled": false
    }
  }
}
```

SSE transport uses the same `url` and `headers` fields as `http` transport. Use `sse` when the remote server streams events over Server-Sent Events rather than responding to standard HTTP requests.

### Project-Level MCP Servers

MCP servers can be configured at the **agent profile level** (global) or at the **project level** (per-project override). Both are managed in the Settings UI.

**Merge semantics**: At dispatch time, project-level MCP servers are merged with the resolved agent profile's MCP servers:
- A project server with the same `Name` (case-insensitive) as a profile server **replaces** it
- Project servers with new names are **appended** to the profile list
- `null` project servers = inherit profile list unchanged

This allows projects to selectively override or augment the profile's MCP configuration without having to redefine the entire list.

**Chat session isolation**: When a project is selected, its MCP servers (merged with the profile's), secrets, steering content, and project identity are sent on the first prompt of the chat session. Subsequent prompts do not re-send secrets or steering content. Repository-level steering and repo secrets are not sent to chat sessions.

