# Observability

Pipeline telemetry is built on [OpenTelemetry](https://opentelemetry.io/) for .NET, exporting metrics and distributed traces via OTLP.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for how pipeline steps relate to trace spans, and [Configuration](configuration.md) for general pipeline settings.

## Metrics

All metrics are emitted from the `CodingAgent.Pipeline` meter, defined in `PipelineTelemetry.cs` (`src/CodingAgent.Infrastructure.Common/Telemetry/PipelineTelemetry.cs`), unless otherwise noted.

### GitHub API Metrics

GitHub-specific metrics are emitted on a dedicated `CodingAgent.GitHub` meter (`GitHubTelemetry.cs` in `src/CodingAgent.Infrastructure.Providers/GitHub/`). This meter is registered in the API, Scheduler, Web, and Job Controller processes, but **not** in agent pods — agent pods must not emit these series.

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `github.api.requests` | Counter | — | `operation`, `outcome` | GitHub API request attempt outcomes. Emitted **per attempt including retries** |
| `github.rate_limit.remaining` | ObservableGauge | — | `resource` | Remaining GitHub API rate-limit quota. Only emitted from processes that have made at least one GitHub API call |

**Tag values:**
- `operation`: provider method name (closed set defined in `GitHubTelemetry.AllOperationNames`)
- `outcome`: `success` | `not_found` | `rate_limited` | `error`
- `resource`: `core` (REST API calls) | `graphql` (GraphQL mutations)

### Pipeline Housekeeping Metrics (PR Outcomes)

The following metrics are added alongside the existing `pipeline.housekeeping.*` series:

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `pipeline.pull_requests.closed` | Counter | — | `outcome` | Agent PRs that were merged or closed. Emitted **once per PR** by the housekeeping service |
| `pipeline.pull_requests.time_to_merge` | Histogram | seconds | — | Time from PR creation to merge. Buckets: 3600, 14400, 43200, 86400, 172800, 604800 s (1h, 4h, 12h, 24h, 48h, 1 week) |

**Tag values:**
- `outcome`: `merged` | `closed_unmerged`

`pipeline.pull_requests.time_to_merge` is only emitted for `merged` PRs where `PullRequestSummary.CreatedAt` is set. It uses `UtcNow` as a proxy for merge time; the approximation error is bounded by the housekeeping poll interval (typically 1–5 minutes).

Deduplication: both instruments are emitted at most once per PR per leader instance. Leader changes may cause a re-emit for already-counted PRs (graceful handling would require persistent storage).

### All Pipeline Metrics

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `pipeline.run.outcomes` | Counter | `{run}` | `run_type`, `outcome`, `failure_reason`, `pipeline.project_name` | Terminal pipeline run outcomes — recorded exactly once per transition by the API (`WorkItemStatusTransitionService`). Pre-initialized at process start for all closed-tag combinations (excluding `pipeline.project_name`, which has unbounded cardinality). |
| `pipeline.run.duration` | Histogram | seconds | `run_type`, `outcome` | Total duration of a pipeline run from dispatch to terminal status |
| `pipeline.run.step.duration` | Histogram | seconds | `run_type`, `step` | Duration of each pipeline step — recorded by the API on every step transition in `HandleStepTransition`. One sample per step visit (revisited steps each produce their own sample). Not pre-initialized. |
| `pipeline.run.sub_issues` | Counter | `{issue}` | `result` | Sub-issue creation results per decomposition run — recorded once per run when `DecompositionSubIssuesAttempted` first becomes non-zero. Pre-initialized at process start for `result=created` and `result=failed`. |
| `pipeline.run.brain_updates` | Counter | `{run}` | `result` | Brain update result at run completion — recorded once per non-consolidation run. Pre-initialized at process start for `result=pushed` and `result=none`. |
| `pipeline.loop.polls` | Counter | — | `result` | Incremented on each poll cycle (`success` or `failure`) |
| `pipeline.loop.issues_found` | Counter | — | — | Incremented by the number of issues/PRs/epics discovered per poll cycle |
| `pipeline.loop.dispatch_decisions` | Counter | — | `decision` | Incremented for each dispatch decision made by the loop |
| `pipeline.loop.backoff_events` | Counter | — | — | Incremented when a template poll failure triggers backoff escalation |
| `pipeline.loop.circuit_breaker_trips` | Counter | — | — | Incremented when the circuit breaker trips (all templates failing) |
| `token_vending.failures` | Counter | — | — | Token vending operation failures |
| `agent.heartbeat.failures` | Counter | — | — | Agent heartbeat send failures |
| `agent.reconnections` | Counter | — | — | Agent reconnection events |
| `pipeline.run.tokens` | Counter | `{token}` | `run_type`, `phase`, `provider` | LLM tokens consumed per run, per phase — recorded **API-side** at terminal status time. Pre-initialized for all `run_type × phase × provider` combinations. |
| `pipeline.run.cost_usd` | Counter | `{usd}` | `run_type`, `phase`, `provider` | LLM cost in USD per run, per phase — recorded **API-side** at terminal status time. Pre-initialized for all `run_type × phase × provider` combinations. |
| `pipeline.run.agent_sessions` | Counter | `{session}` | `run_type`, `phase`, `provider`, `model` | Agent CLI invocations per run, per phase — recorded **API-side** at terminal status time. Pre-initialized with `model=unknown` for all `run_type × phase × provider` combinations. |
| `pipeline.run.agent_time` | Counter | `s` | `run_type`, `phase`, `provider` | Agent execution time (seconds) per run, per phase — recorded **API-side** at terminal status time. Pre-initialized for all `run_type × phase × provider` combinations. |
| `pipeline.run.token_usage` | Counter | `{token}` | `run_type`, `provider`, `token_type` | Tokens per run split by `token_type` (`input`, `output`, `reasoning`, `cache_read`, `cache_write`) — recorded **API-side** at terminal status time from the per-phase breakdown. No `phase` tag, to keep series bounded; per-phase totals stay on `pipeline.run.tokens`. Reported by OpenCode and Claude Code. Pre-initialized for all `run_type × provider × token_type` combinations. |
| `pipeline.run.billing_cost_usd` | Counter | `{usd}` | `run_type`, `provider`, `billing` | Provider-reported cost per run split by how it is paid for: `billing=api` is billed per token, `billing=subscription` is the CLI's estimate under a flat Claude plan (not a bill), `unknown` otherwise — recorded **API-side**. Pre-initialized for all combinations. |
| `pipeline.run.agent_turns` | Counter | `{turn}` | `run_type`, `provider` | Agent turns (model round trips) per run (Claude Code) — recorded **API-side**. Pre-initialized. |
| `pipeline.run.web_search_requests` | Counter | `{request}` | `run_type`, `provider` | Web searches the model made per run (Claude Code) — recorded **API-side**. Pre-initialized. |
| `pipeline.run.rate_limit_events` | Counter | `{event}` | `provider`, `window`, `status` | Subscription rate-limit readings per run: the latest reading per window the agent saw (Claude Code `rate_limit_event`). `window`: `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet`, `overage`, `other`. `status`: `allowed`, `allowed_warning`, `rejected`, `other`. Pre-initialized for `provider=claude`. |
| `pipeline.run.rate_limit_utilization` | Histogram | `1` | `provider`, `window`, `status` | Fraction (0–1) of the subscription window used, as last seen in a run — recorded **API-side** when the CLI reports a utilization. Buckets: 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 1.0. |
| `pipeline.run.quality_gate.results` | Counter | `{evaluation}` | `run_type`, `gate`, `result`, `infrastructure_failure` | Quality gate evaluation outcomes — recorded **API-side** in `ReportQualityGateResult` when the agent reports a gate result. `gate`: `compilation`, `tests`, `external_ci`. `result`: `pass`, `fail`. `infrastructure_failure`: `true`, `false`. Pre-initialized for all combinations (excluding `infrastructure_failure=true` for `compilation`). |
| `pipeline.run.ci.not_started_retriggers` | Counter | `{retrigger}` | `run_type` | CI re-trigger commits (empty push to restart CI that never started) — recorded **API-side** in `ReportPipelineRunEvent` each time the agent re-pushes an empty commit. Pre-initialized for all run types. |
| `pipeline.run.ci.wait` | Histogram | `s` | `run_type`, `stage`, `result` | Time from push to CI conclusion — recorded **API-side** in `ReportPipelineRunEvent` when CI polling concludes. `stage`: `pre_pr`, `post_pr`. `result`: `pass`, `fail`. Buckets: 60, 300, 600, 900, 1800, 3600, 7200, 14400. Not pre-initialized (histograms cannot be pre-initialized). |
| `pipeline.run.agent_stalls` | Counter | `{stall}` | `run_type`, `phase`, `kind` | Agent stall events — recorded **API-side** in `ReportPipelineRunEvent` when the agent detects a stall. `kind`: `stall_kill`, `process_death`, `process_timeout`. `phase`: see `quality_gate.stall.*` phases. Pre-initialized for all `run_type × phase × kind` combinations. |
| `quality_gate.retries` | Counter | — | `run_type`, `pipeline.project_name` | Quality gate retry attempts — recorded by the pipeline service |
| `quality_gate.duration` | Histogram | seconds | `run_type`, `pipeline.project_name` | Total time in quality gate phase — recorded by the pipeline service |
| `quality_gate.evaluations` | Counter | — | `gate_name`, `result` | Individual gate evaluation events — recorded by the pipeline service |
| `quality_gate.process.timeout` | Counter | — | `gate_name`, `qgc_name` | QGC process timeouts (compilation or test command exceeded `processTimeoutSeconds`) |
| `quality_gate.process.duration` | Histogram | seconds | `gate_name`, `qgc_name` | Duration of a single QGC process invocation (compilation or test command). Distinct from `quality_gate.duration` which covers the entire retry phase |
| `quality_gate.stall.warnings` | Counter | — | `phase` | Agent silence warnings by pipeline phase — fires after each `stallWarningInterval` with no output |
| `agent.connections.total` | ObservableGauge | — | — | Total registered agents |
| `agent.signalr.failures` | Counter | — | — | Failed or dropped SignalR messages from agent |
| `pipeline.decomposition.duration` | Histogram | seconds | `pipeline.project_name`, `phase` | Duration of decomposition phases (`phase`: `analysis` or `creation`) |
| `pipeline.housekeeping.triggered` | Counter | — | `repo_provider_id` | Server-side branch updates triggered |
| `pipeline.housekeeping.succeeded` | Counter | — | `repo_provider_id` | Server-side branch updates completed successfully |
| `pipeline.housekeeping.failed` | Counter | — | `repo_provider_id` | Server-side branch updates that threw an exception |
| `pipeline.housekeeping.skipped` | Counter | — | `repo_provider_id` | PRs skipped during candidate selection (not behind, null, draft, active rework, or already in-flight) |
| `pipeline.housekeeping.evicted` | Counter | — | `repo_provider_id` | In-flight entries removed (CI resolved or PR merged/label removed) |
| `pipeline.housekeeping.conflict_rework_triggered` | Counter | — | `repo_provider_id` | Issues re-queued for rework due to PR merge conflict |
| `pipeline.housekeeping.branch_deleted` | Counter | — | `repo_provider_id` | Stale agent branches deleted (no open PR, inactive issue label) |
| `pipeline.pull_requests.closed` | Counter | — | `outcome` | Agent PRs that were merged or closed. Emitted once per PR (deduplicated across poll cycles) |
| `pipeline.pull_requests.time_to_merge` | Histogram | seconds | — | Time from PR creation to merge. Buckets: 1h, 4h, 12h, 24h, 48h, 1 week. Only emitted for merged PRs |
| `pipeline.queue_sweep.cancelled` | Counter | — | — | WorkItems cancelled as stale by the queue sweep (issue no longer eligible) |
| `pipeline.queue_sweep.skipped` | Counter | — | — | WorkItems skipped by the queue sweep (provider not polled, rate-limited, or wrong task type) |
| `pipeline.queue_sweep.failed` | Counter | — | — | Unexpected failures during the queue sweep (`POST /api/work-items/{id}/status` errors) |

### Tag Schema

| Tag | Values | Description |
|-----|--------|-------------|
| `run_type` | `implementation`, `review`, `decomposition`, `decompositionanalysis`, `consolidation` | Pipeline run type (lowercase). Resolved from `PipelineRunEntity.RunType`; falls back to `WorkItemEntity.TaskType` for legacy rows without a `PipelineRun`. |
| `outcome` | `cancelled`, `conflict_restart`, `needs_refinement`, `wont_do`, `pr_created`, `draft_pr`, `succeeded`, `timeout`, `failed` | Terminal run outcome derived from `JobCompletionPayload` — see [Outcome mapping](#outcome-mapping) |
| `failure_reason` | `none`, `timeout`, `infrastructure_failure`, `agent_error`, `token_refresh_failure`, `exit_code_failure`, `quality_gate_exhausted`, `gate_rejected` | Failure classification in snake_case. `none` for all non-failure outcomes including `needs_refinement` and `wont_do`. |
| `step` | PipelineStep enum name (e.g., `Created`, `GeneratingCode`, `RunningQualityGates`) | Pipeline step name in PascalCase — only present on `pipeline.run.step.duration`. Matches `PipelineStep` C# enum member names. |
| `result` | `created` / `failed` (for `pipeline.run.sub_issues`); `pushed` / `none` (for `pipeline.run.brain_updates`) | Sub-issue creation or brain update result — only present on the respective counters. |
| `phase` | `analysis`, `analysis_review`, `codegen`, `review`, `acceptance_criteria`, `pr_description`, `reflection`, `decomposition`, `other` | Normalized pipeline phase for LLM usage counters. Per-reviewer names (e.g. `review_correctness`) collapse into `review`. |
| `provider` | `kiro`, `opencode`, `claude`, `unknown` | Agent provider for LLM usage counters. Values outside the set are recorded as `unknown`. |
| `token_type` | `input`, `output`, `reasoning`, `cache_read`, `cache_write` | Token category — only present on `pipeline.run.token_usage`. `output` excludes `reasoning`. |
| `billing` | `api`, `subscription`, `unknown` | How the LLM calls were paid for — only present on `pipeline.run.billing_cost_usd`. |
| `window` / `status` | see `pipeline.run.rate_limit_events` | Subscription rate-limit window and state — only present on the `pipeline.run.rate_limit_*` metrics. |
| `model` | provider-specific model name (e.g. `claude-sonnet-4-5`) | Model name for `pipeline.run.agent_sessions`. Value `unknown` is used in pre-initialization and when the provider doesn't report a model. |
| `result` | `success`, `failure` | Poll cycle outcome (for loop metrics) |
| `decision` | `dispatched`, `skipped_already_processing`, `skipped_dependency_blocked`, `skipped_no_agent`, `skipped_max_runs`, `skipped_filtered_by_label` | Dispatch decision reason |
| `reason` | `busy`, `shutting_down`, `unknown` | Agent job rejection reason |
| `repo_provider_id` | provider config UUID | Repository provider config ID — only present on `pipeline.housekeeping.*` metrics |
| `phase` | `qgc_retry_agent`, `codegen`, `analysis`, `code_review`, `decomposition`, `unknown` | Pipeline phase — only present on `quality_gate.stall.*` and `pipeline.run.agent_stalls` metrics |
| `qgc_name` | QGC display name | Quality gate config name — only present on `quality_gate.process.*` metrics |
| `gate` | `compilation`, `tests`, `external_ci` | Quality gate name — only present on `pipeline.run.quality_gate.results` |
| `infrastructure_failure` | `true`, `false` | Whether the gate failure was due to infrastructure (OOM, container kill) rather than a code failure — only present on `pipeline.run.quality_gate.results` |
| `stage` | `pre_pr`, `post_pr` | CI polling stage — only present on `pipeline.run.ci.wait` |
| `kind` | `stall_kill`, `process_death`, `process_timeout` | Stall event kind — only present on `pipeline.run.agent_stalls` |

#### Outcome mapping

`outcome` is derived in `WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync` by inspecting the deserialized `JobCompletionPayload` first, then falling back to `request.Status` and `FailureReason`:

| Priority | Condition | `outcome` | `failure_reason` |
|----------|-----------|-----------|-----------------|
| 1 | `status == Cancelled` | `cancelled` | `none` |
| 2 | `payload.FinalStep == ConflictRestart` | `conflict_restart` | `none` |
| 3 | `payload.AnalysisRecommendation == WontDo` | `wont_do` | `none` |
| 4 | `payload.AnalysisRecommendation == NotReady` | `needs_refinement` | `none` |
| 5 | `payload.PullRequestUrl` set and `!IsDraftPr` | `pr_created` | `none` |
| 6 | `payload.IsDraftPr == true` | `draft_pr` | `none` |
| 7 | `failureReason == Timeout` | `timeout` | `timeout` |
| 8 | `status == Succeeded` | `succeeded` | `none` |
| 9 | fallthrough | `failed` | snake_case `FailureReason` or `none` |

**Important:** `needs_refinement` and `wont_do` both arrive with `FailureReason.GateRejected` in the request (set by `AgentPhaseExecutor`). The outcome derivation checks `AnalysisRecommendation` before `FailureReason`, forcing `failure_reason=none` for these outcomes. The pre-initialized series also use `failure_reason=none` for these outcomes.

#### Counter pre-initialization

All counters with closed tag sets are pre-initialized to `0` at API process start, then `MeterProvider.ForceFlush()` is called once. This ensures that Prometheus `increase()` is visible from the very first increment after a deploy, eliminating the "first-series zero" problem that affected ephemeral agent pods.

**Rules:**
- Agent pods do **not** record run-level metrics (`pipeline.run.outcomes`, `pipeline.run.duration`, `pipeline.run.step.duration`, `pipeline.run.sub_issues`, `pipeline.run.brain_updates`). They are ephemeral and subject to the first-series problem. Only the long-lived API process records these metrics.
- `pipeline.project_name` is a tag on `pipeline.run.outcomes` but is **excluded from pre-initialization**. It has unbounded cardinality, so pre-initializing it would exceed the ≈100 series limit. Pre-initialized series have 3 tags; live series have 4 tags (including `pipeline.project_name`). This means the first event for a brand-new project name still shows 0 in `increase()` until a second event arrives, but the closed dimensions are pre-initialized correctly.
- Histograms (`pipeline.run.duration`, `pipeline.run.step.duration`, `workdistribution.job_execution_duration_seconds`) cannot be pre-initialized and are left as-is.
- `pipeline.run.outcomes` is pre-initialized with **75 series** (3-tag): 5 run_types × (7 non-failure outcomes + 1 timeout + 7 failed × 7 failure_reasons).
- `workdistribution.workitems_terminated` is pre-initialized with **24 series**: 3 statuses × (1 none + 7 failure_reasons).
- `pipeline.run.sub_issues` is pre-initialized with **2 series**: `result=created`, `result=failed`.
- `pipeline.run.brain_updates` is pre-initialized with **2 series**: `result=pushed`, `result=none`.
- `pipeline.run.tokens`, `pipeline.run.cost_usd`, `pipeline.run.agent_sessions`, and `pipeline.run.agent_time` are pre-initialized with **180 series each** (5 run_types × 9 phases × 4 providers). `model` is excluded from pre-init (unbounded cardinality).
- `pipeline.run.token_usage` is pre-initialized with **100 series** (5 run_types × 4 providers × 5 token types), `pipeline.run.billing_cost_usd` with **60** (× 3 billing modes), `pipeline.run.agent_turns` and `pipeline.run.web_search_requests` with **20 each**, and `pipeline.run.rate_limit_events` with **15** (`provider=claude` × 5 windows × 3 statuses).

### Prompt Cache and Per-Phase Token Data

Each `PipelineRun` accumulates token and cost data beyond the simple totals. This richer data is available on `PipelineRunSummary` objects returned by `GET /api/pipeline-runs` and `GET /api/export/runs.json`, and is visible in the UI sidebars.

#### Cache Token Fields

| Field | Description |
|-------|-------------|
| `CacheReadTokens` | Tokens served from the upstream LLM's prompt cache across all agent invocations in this run |
| `CacheWriteTokens` | Tokens written into the prompt cache across all agent invocations in this run |

**Provider support:** Cache token fields are populated for **OpenCode** and **Claude Code** agents. KiroCli agents always report 0 for both fields (the KiroCli provider does not expose cache token breakdowns).

**Claude Code usage:** the provider reads the `result` event of `claude -p --output-format stream-json` and reports, per call, input / output / reasoning / cache tokens, the CLI's cost estimate (`total_cost_usd`), turns, API time, web searches, a per-model breakdown and subscription rate-limit readings (`rate_limit_event`). A resumed session reports the whole conversation's totals, so the provider subtracts what it saw at the end of the previous call. The per-model breakdown and rate-limit readings are logged per call (`Claude Code model usage` / `Claude Code rate limit` log lines); the totals feed the counters above. With a subscription token the cost is an estimate, not a bill — `pipeline.run.billing_cost_usd` separates the two.

#### Per-Phase Breakdown

`PipelineRunSummary.PhaseBreakdown` is a dictionary keyed by phase name (e.g., `"Analysis"`, `"CodeGeneration"`, `"Review.Correctness"`). Each entry contains:

| Property | Description |
|----------|-------------|
| `Tokens` | Total tokens consumed during this phase |
| `Cost` | Cost in USD for this phase, or `null` if unavailable |

The breakdown is rendered on the Run page (`/runs/{id}`), in the pipeline-progress sidebar, as a collapsible "Cost Breakdown" table sorted by cost descending, for live and finished runs. It is `null` for runs recorded before this feature was introduced.

**API exposure:** `PhaseBreakdown` is included in the `GET /api/export/runs.json` export. Fields will be absent (`null`) for runs that pre-date the phase breakdown feature.

### Kiro CLI Usage Data Investigation

**Investigation result (2026-09-29): Kiro CLI does not expose token or cost data.**

During a code audit of `KiroCliAgentProvider` and `KiroCliLib`, the following was verified:

- `KiroCliOrchestrator` and `KiroCliLib.Core.OutputParser` parse CLI output line-by-line to extract test results (via `TestResults` property) and session IDs (via `kiro chat --list-sessions`), but do not parse any token count or credit data.
- `kiro-cli` output does not contain token counts, credit usage, or cost information in any format (JSON blocks, text lines, or metadata).
- `KiroCliAgentProvider.ExecuteAsync` returns `AgentResult.Usage = null` and `AgentResult.Cost = null`. This is correct and expected.
- As a result, `pipeline.run.tokens` and `pipeline.run.cost_usd` will be zero for all phases on Kiro-powered runs. `pipeline.run.agent_sessions` and `pipeline.run.agent_time` **will** be populated for Kiro runs (these are tracked by the agent harness, not the CLI).

**What to watch for:** If a future Kiro CLI version exposes usage data (e.g., in a structured JSON output line like `{"tokens": {"input": ..., "output": ...}}`), `KiroCliOrchestrator` will need to be updated to parse it and populate `AgentResult.Usage`. At that point, the `TokenUsage` fields would flow through to `PhaseBreakdown` automatically via `AccumulateTokenUsage`.

### LLM Usage Telemetry Architecture

The four `pipeline.run.*` usage counters (`tokens`, `cost_usd`, `agent_sessions`, `agent_time`) follow the API-at-terminal-time pattern introduced in issue #2967:

1. **Agent side** — `AgentStallMonitor.ExecuteWithMonitoringAsync` creates a per-session OTel span with GenAI semantic convention attributes (see "Session Spans" below) and accumulates session data into `PipelineRun.Metrics.PhaseBreakdown` via `AccumulateAgentSession`.
2. **Wire** — `LocalPipelineExecutor.BuildPayloadBase` converts `PhaseBreakdown` into `JobCompletionPayload.PhaseBreakdown` (`IReadOnlyDictionary<string, PhaseUsagePayload>`) for transmission to the orchestrator.
3. **API side** — `WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync` reads `PhaseBreakdown` from the deserialized payload and calls `RecordRunUsageMetrics`, which emits the 4 counters with normalized phase tags.

**Phase normalization** is performed by `PipelineTelemetry.NormalizeRunPhase(string?)`, which maps the raw phase keys (e.g., `"review_correctness"`, `"codegen"`, `"code generation"`) to a closed set:

| Raw phase key pattern | Normalized tag |
|-----------------------|---------------|
| `analysis` | `analysis` |
| `analysis_review` / `analysisreview` | `analysis_review` |
| `codegen` / `code_gen` / `code generation` | `codegen` |
| `review` | `review` |
| `review_*` / `review *` | `review` (per-reviewer names collapse) |
| `acceptance_criteria` / `acceptancecriteria` | `acceptance_criteria` |
| `pr_description` / `prdescription` | `pr_description` |
| `reflection` | `reflection` |
| `decomposition` / `decomposition_review` | `decomposition` |
| anything else / empty / null | `other` |

### Session Spans (GenAI Semantic Conventions)

Every agent CLI invocation creates a child span under the current `ExecutePipeline` span:

| Span name | `invoke_agent {phase}` (e.g. `invoke_agent analysis`) |
|-----------|------------------------------------------------------|
| `gen_ai.operation.name` | `invoke_agent` |
| `gen_ai.provider.name` | `kiro`, `opencode` or `claude` |
| `gen_ai.request.model` | model name if configured, omitted otherwise |
| `pipeline.phase` | normalized phase tag (same as metric `phase` tag) |
| `agent.session.resumed` | `true` if `UseResume=true` or `ResumeSessionId` is set |
| `agent.exit_code` | integer exit code from the CLI process |
| `gen_ai.usage.input_tokens` | input tokens (OpenCode and Claude Code; Kiro always omitted) |
| `gen_ai.usage.output_tokens` | output tokens (OpenCode and Claude Code; Kiro always omitted) |
| `gen_ai.usage.total_tokens` | total tokens when > 0 (OpenCode and Claude Code; Kiro always omitted) |
| `gen_ai.usage.reasoning_tokens` / `gen_ai.usage.cache_read_input_tokens` / `gen_ai.usage.cache_creation_input_tokens` | when > 0 |
| `agent.cost_usd` | provider-reported cost when known |
| `agent.billing` / `agent.turns` / `agent.api_duration_s` / `agent.web_search_requests` / `agent.error_category` | Claude Code usage details (`agent.error_category` only on provider-side failures) |

Stall events are recorded as span events:

| Event name | When | Tags |
|------------|------|------|
| `agent.stall_warning` | agent silence exceeds `stallWarningInterval` | `silence_minutes` |
| `agent.stall_kill` | agent killed due to silence timeout | `silence_minutes`, `kill_timeout_minutes` |
| `agent.process_death` | agent process died unexpectedly | `pid` |

Custom bucket boundaries are configured via `InstrumentAdvice<double>` at instrument creation time:

| Metric | Boundaries (seconds) |
|--------|---------------------|
| `pipeline.run.duration` | 60, 300, 600, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 21600, 28800, 43200 |
| `pipeline.run.step.duration` | 5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800 |
| `quality_gate.process.duration` | 5, 10, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600 |
| `quality_gate.post_pr_ci.duration` | 5, 10, 30, 60, 120, 300, 600, 1200, 1800, 3600 |
| `pipeline.run.ci.wait` | 60, 300, 600, 900, 1800, 3600, 7200, 14400 |
| `workdistribution.dispatch_latency_seconds` | 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400 |
| `workdistribution.job_execution_duration_seconds` | 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600 |
| `workdistribution.timeout_execution_age_seconds` | 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600 |

Other histograms (`quality_gate.duration`, etc.) use the OpenTelemetry SDK's default bucket boundaries.
<!-- TODO [WARNING]: The previous sentence incorrectly listed quality_gate.external_ci.duration as a
default-bucket histogram. It has explicit InstrumentAdvice boundaries [5,10,30,60,120,300,600,1200,1800,3600]
(verified by ExternalCiDuration_HasExpectedBucketBoundaries in HistogramBucketBoundaryTests). Additionally,
quality_gate.external_ci.duration and quality_gate.post_pr_ci.duration (in the custom-bucket table above)
are described as deprecated in this doc but still referenced here — remove them or clarify deprecated status. -->

### Step & Run-Level Metrics (API-only)

`pipeline.run.outcomes` (`pipeline_run_outcomes_total`), `pipeline.run.duration` (`pipeline_run_duration_seconds`), `pipeline.run.step.duration` (`pipeline_run_step_duration_seconds`), `pipeline.run.sub_issues` (`pipeline_run_sub_issues_total`), and `pipeline.run.brain_updates` (`pipeline_run_brain_updates_total`) are emitted **only by the API**.

**Why API-only?**
- Agent pods (ephemeral K8s Jobs) start fresh OTel series on every run; Prometheus `rate()`/`increase()` can't see the first increment of a series. Anything recorded once per run (or once per step) reads zero.
- The same root cause was fixed for run-outcome metrics in #2967. This issue (#2974) extends that fix to step durations, sub-issue counts, and brain-update results.
- Recording in the API solves both: the API is a long-lived process with pre-initialized counter series, and it receives all step transitions via `HandleStepTransition` and all completion payloads via `HandleJobCompletedAsync`.

**Recording points:**
- `pipeline.run.step.duration`: emitted by `AgentJobLifecycleService.HandleStepTransition` on every step change. Duration = difference between the new transition's timestamp and the previous `LastStepChangeAt` from the shared run store. Multi-replica safe (uses the stored value, not a process-local clock).
- `pipeline.run.sub_issues`: emitted by `AgentJobLifecycleService.HandleStepTransition` when `DecompositionSubIssuesAttempted` metadata first becomes non-zero. Once per run.
- `pipeline.run.brain_updates`: emitted by `AgentJobLifecycleService.HandleJobCompletedAsync` for every non-consolidation run. `BrainUpdatesPushed` is set by `JobCompletionMapper.Apply` from the completion payload.

**Reliable sources by use case:**

| Use case | Recommended metric |
|----------|--------------------|
| Count of terminal runs by outcome | `increase(pipeline_run_outcomes_total[24h])` |
| Run duration percentiles | `pipeline_run_duration_seconds` |
| Step duration percentiles | `pipeline_run_step_duration_seconds{step=...}` |
| Sub-issues created/failed | `increase(pipeline_run_sub_issues_total[24h])` |
| Brain update rates | `increase(pipeline_run_brain_updates_total[24h])` |
| Exact job counts (alerts) | `workdistribution_workitems_terminated_total` (exact, pre-initialized) |

**Breaking change in issue #2967:** `workdistribution_workitems_terminated_total{failure_reason=...}` values changed from PascalCase (e.g. `"Timeout"`) to snake_case (e.g. `"timeout"`). Update any Grafana panels or alert rules that filter on `failure_reason` labels.

### Work Distribution Metrics

The `CodingAgent.WorkDistribution` meter is defined in `WorkDistributionTelemetry.cs` (`src/CodingAgent.Infrastructure.Common/Telemetry/WorkDistributionTelemetry.cs`, namespace `CodingAgent.Pipeline.Telemetry`). Instruments are fed by multiple processes:
- **API** (`service.name=coding-agent-api` or `coding-agent-web`): `workdistribution.dispatch_latency_seconds`, `workdistribution.credential_pool_available/claimed`, `workdistribution.dispatch.attempts`, `workdistribution.workitems_terminated`
- **Scheduler** (`service.name=coding-agent-scheduler`): `workdistribution.dispatcher_last_poll_epoch_seconds`, `workdistribution.dispatcher_polls`, `workdistribution.workitems_by_status` (via `WorkItemCountsService`)
- **Job Controller** (`service.name=coding-agent-jobcontroller`): `workdistribution.timeout_execution_age_seconds`, `workdistribution.timeout_canary_violations`, `workdistribution.agent_timeouts` (via `ReconciliationLoop`)

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `workdistribution.dispatch_latency_seconds` | Histogram | s | — | Time from WorkItem creation (Pending) to Dispatched |
| `workdistribution.workitems_pending_duration_seconds` | Histogram | s | — | **Removed in issue #2976** — this entry is stale. TODO [WARNING]: delete this row. The instrument no longer exists in WorkDistributionTelemetry.cs; `RecordDispatchLatency` doc comment confirms removal. Operators querying this metric will find no data. |
| `workdistribution.job_execution_duration_seconds` | Histogram | s | — | Total execution duration (Dispatched → terminal) |
| `workdistribution.timeout_execution_age_seconds` | Histogram | s | — | Execution age at the moment a timeout is enforced. Canary: if p10 clusters near zero, the timeout anchor is wrong |
| `workdistribution.workitems_terminated` | Counter | {item} | `status`, `failure_reason` | Work items reaching a terminal state |
| `workdistribution.dispatcher_polls` | Counter | {poll} | — | Number of dispatch poll cycles executed |
| `workdistribution.dispatcher_last_poll_epoch_seconds` | ObservableGauge | s | — | Epoch seconds of the last dispatch poll cycle. Drives `DispatcherStalled` alert |
| `workdistribution.credential_pool_available` | ObservableGauge | {pvc} | `pool` | Available credential PVCs |
| `workdistribution.credential_pool_claimed` | ObservableGauge | {pvc} | `pool` | Claimed credential PVCs |
| `workdistribution.workitems_by_status` | ObservableGauge | {item} | `status`, `agent_selector` | Current count of WorkItems by status |
| `workdistribution.timeout_canary_violations` | Counter | {violation} | — | Timeouts skipped due to canary invariant violation — any non-zero value indicates a timestamp bug |
| `workdistribution.progress_write_failures` | Counter | {failure} | — | Failed `LastProgressAt` DB writes. Sustained non-zero rate means `ReconciliationService` sees stale values and may false-positive timeout agents |
| `pipeline.db_retention.pipeline_runs_deleted` | Counter | {row} | — | `PipelineRuns` rows deleted by the per-project retention sweep |
| `pipeline.db_retention.work_items_deleted` | Counter | {row} | — | `WorkItems` rows deleted by the per-project retention sweep |

## Traces

### Span Noise Filters

Three high-volume span categories are filtered out before export to avoid overwhelming the OTLP backend with low-signal data (~70% of raw daily span volume). The filters are wired in the **Scheduler** and **Job Controller** processes via `OtelNoiseFilter` (`src/CodingAgent.Infrastructure.Common/Telemetry/OtelNoiseFilter.cs`):

| Filter | Mechanism | What is dropped |
|--------|-----------|-----------------|
| Health-probe and polling endpoints | `AspNetCoreTraceInstrumentationOptions.Filter` | Exact paths `/healthz`, `/readyz`, `/loop/status` — sub-paths like `/healthz/detail` are kept |
| Kubernetes API server lease calls | `HttpClientTraceInstrumentationOptions.FilterHttpRequestMessage` | Outbound requests to `KUBERNETES_SERVICE_HOST` (in-cluster) or `kubernetes.default.svc` |
| Chatty SignalR / Blazor circuit spans | `OtelNoiseSpanDropProcessor` (added via `.AddProcessor(new OtelNoiseSpanDropProcessor())`) | Hub methods ending in `/Heartbeat`, `/ReportOutputLines`, `/OnRenderCompleted`; spans starting with `"Circuit "` (Blazor circuit lifecycle); event callbacks starting with `"Event "` and containing `" -> "` |

The K8s API server filter alone eliminates ~387k leader-election lease spans per day. Additionally, outbound HTTP span names are enriched to `"{METHOD} {host}"` (e.g., `"GET api.github.com"`) by `EnrichWithHttpRequestMessage` to keep cardinality bounded while still making host-level tracing useful.

These filters are applied at the tracer-provider level — dropped spans are marked `IsAllDataRequested = false` at start, so no attributes are collected and nothing is exported.

All spans are emitted from the `CodingAgent.Pipeline` ActivitySource. Spans marked with † are emitted from both the orchestrator (`PipelineOrchestrationService`) and the agent worker (`LocalPipelineExecutor`).

### Trace Hierarchy

Each agent run's spans are connected to the API request that created the WorkItem. The linkage is:

```
POST /api/work-items (API request span)
└── WorkItemAgent.Execute (K8s agent pod)
    └── ExecutePipeline
        ├── Step CloneRepository
        │   └── CloneRepository
        ├── Step VerifyBaseline
        │   └── VerifyBaseline
        ├── Step AnalyzeCode
        │   └── AnalyzeIssue
        ├── Step GenerateCode
        │   └── GenerateCode
        ├── Step RunQualityGates
        │   └── RunQualityGates
        │       ├── QualityGate.Compilation
        │       ├── QualityGate.Tests
        │       └── WaitForCi  (pre-PR external CI)
        ├── Step CreatePullRequest
        │   └── CreatePullRequest
        │       └── WaitForCi  (post-PR external CI, inside FinalizePullRequest)
        └── PrePrCleanup
```

**Span structure rules:**

- `PipelineStepRunner` creates one `Step {StepName}` span per step with `pipeline.step` and `pipeline.run_id` tags.
- Steps that start their own inner span (e.g. `CloneRepository`, `AnalyzeIssue`) nest that inner span inside the runner-created `Step` span — giving two span levels per step.
- Steps without an inner span (`VerifyBaseline`, `FetchIssue`, etc.) have only the runner-created span.
- `VerifyBaseline` is a named inner span with `pipeline.run_id` and `pipeline.issue` tags.
- `WaitForCi` is emitted for both the pre-PR CI path (`QualityGateExecutor.RunExternalCiPollAsync`) and the post-PR CI path (`CiPollingCoordinator.WaitForPostPrCiAsync`). Tags: `pipeline.run_id`, `pipeline.run_type`, `pipeline.ci_path` (`pre_pr` or `post_pr`), `pipeline.ci_status`, `pipeline.ci_infra_retries`.
- `PrePrCleanup` is emitted by `PipelineCleanup.RunAsync` as a child of `ExecutePipeline`.

**Error status rules:**

- A step span (`Step X`) has Error status only when that step operation failed (unhandled exception from `ExecuteAsync`, or `TryCriticalAsync` failure).
- Non-critical failures (`TryNonCriticalAsync`) add an `exception` event with `pipeline.non_critical=true` to the current step span. The span status remains Unset — no Error.
- `ExecutePipeline` has Error status only when the run ends `PipelineStep.Failed`. All other terminal states (`Completed`, `ConflictRestart`, `PrMerged`, `PrClosed`, `Cancelled`) leave it Ok or Unset.

This is achieved by capturing the W3C `traceparent` from the API request span at WorkItem creation time (`WorkItemDispatchEndpoints.cs`, `DispatchWorkItemService.cs`), storing it in `WorkItemEntity.TraceParent`, and injecting it as the `TRACEPARENT` environment variable in the K8s Job (`DispatchLifecycleService.CreateK8sJobAsync` → `JobSpecBuilder.Build`). The agent process restores this context in `WorkItemAgentService.ExecuteAsync` and starts `WorkItemAgent.Execute` as a child.

### Scheduler Spans

The Scheduler and Web (closed-loop) processes emit spans only when actual work occurs — idle ticks produce no spans. The `CodingAgent.Pipeline` ActivitySource is registered in both the Scheduler and JobController OTel tracing configuration via `.AddSource(PipelineTelemetry.SourceName)`.

| Span Name | Tags | Emitter |
|-----------|------|---------|
| `Dispatch.Attempt` | `work_item_id`, `agent_selector`, `result` | `WorkItemDispatchLoop.PollAndDispatchAsync` — one span per item dispatched; result is `Dispatched`, `PermanentRejection`, or `Transient` |
| `Loop.Enqueue` | `issue_identifier`, `template_name` | `DispatchScheduler.DispatchIssueRoundAsync` — one span per issue successfully dispatched by the closed-loop (fires in the Web process) |
| `Housekeeping.BranchUpdate` | `pr_number`, `repo_provider_id` | `HousekeepingService.UpdateAsync` — one span per PR branch update triggered |
| `Housekeeping.ConflictRework` | `issue_id`, `pr_number` | `IssueReworkService.TrySwapIssueToNextAsync` — one span per issue re-queued for rework due to merge conflict |
| `Housekeeping.BranchDelete` | `branch_name`, `issue_id` | `StaleBranchCleaner` — one span per stale agent branch deleted |

### JobController Spans

| Span Name | Tags | Emitter |
|-----------|------|---------|
| `Reconcile.JobFailed` | `work_item_id`, `failure_reason` | `ReconciliationLoop.HandleJobAsync` `case JobPhaseFailed:` branch — one span per K8s Job failure reconciled (NOT emitted for succeeded jobs) |
| `Reconcile.Timeout` | `work_item_id`, `agent_selector`, `timeout_seconds` | `ReconciliationLoop.EnforceTimeoutsAsync` — one span per Running WorkItem timed out |
| `Reconcile.DispatchedTimeout` | `work_item_id`, `agent_selector` | `ReconciliationLoop.EnforceDispatchedTimeoutAsync` — one span per Dispatched WorkItem with no live K8s Job timed out |
| `Reconcile.OrphanCleanup` | `job_name`, `orphan_reason`, `work_item_id`* | `ReconciliationLoop.CleanupOrphansAsync` — one span per orphaned K8s Job deleted |

\* `work_item_id` is only set when the `caa/work-item-id` label is present on the job.

### Pipeline Spans

| Span Name | Tags | Emitter |
|-----------|------|---------|
| `ExecutePipeline` † | `pipeline.run_id`, `pipeline.issue`, `pipeline.final_step`, `pipeline.agent_id`* | Top-level span wrapping the full pipeline execution. Error status set only when run ends `Failed`. |
| `Step {StepName}` | `pipeline.step`, `pipeline.run_id` | Runner-created span per step (emitted by `PipelineStepRunner`). Error status set on unhandled exception. Non-critical failures add exception event, no Error. |
| `CloneRepository` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.repository` | Repository clone into workspace (child of `Step CloneRepository`) |
| `VerifyBaseline` | `pipeline.run_id`, `pipeline.issue` | Agent health check + workspace baseline verification (child of `Step VerifyBaseline`). Error status set on fatal health-check failure only. |
| `CreateBranch` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.branch_name` | Branch creation or checkout |
| `SyncBrainPreRun` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.brain_sync.skipped` | Brain repository sync (pre-run) |
| `RunEnvironmentSetup` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Environment setup commands |
| `CloneProjectRepositories` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Additional project repo clones |
| `AnalyzeIssue` | `pipeline.run_id`, `pipeline.issue`, `pipeline.analysis.continue` | Issue analysis (confidence gate) |
| `GenerateCode` | `pipeline.run_id`, `pipeline.issue`, `pipeline.is_rework` | Code generation / rework |
| `RunQualityGates` | `pipeline.run_id`, `pipeline.issue` | Quality gate execution |
| `QualityGate.Compilation` | `gate_name` | Compilation command execution (child of RunQualityGates) |
| `QualityGate.Tests` | `gate_name` | Test command execution (child of RunQualityGates) |
| `WaitForCi` | `pipeline.run_id`, `pipeline.run_type`, `pipeline.ci_path`, `pipeline.ci_status`, `pipeline.ci_infra_retries` | External CI polling span. `pipeline.ci_path` is `pre_pr` (inside `RunExternalCiPollAsync`) or `post_pr` (inside `WaitForPostPrCiAsync`). |
| `ReviewCode` | `pipeline.run_id`, `pipeline.issue` | Multi-agent code review |
| `CodeReview.Iteration` | `pipeline.run_id`, `pipeline.issue`, `code_review.iteration`, `code_review.max_iterations`, `code_review.parallel` | Single code review iteration (child of ReviewCode) |
| `CodeReview.Agent` | `pipeline.run_id`, `pipeline.issue`, `pipeline.review_agent`, `pipeline.isolated` | Individual review agent execution (child of CodeReview.Iteration) |
| `CreatePullRequest` † | `pipeline.run_id`, `pipeline.issue`, `pipeline.pr.is_draft` | PR creation step |
| `GeneratePrDescription` | `pipeline.run_id`, `pipeline.issue` | Agent-generated PR description |
| `FinalizePullRequest` | `pipeline.run_id`, `pipeline.issue`, `pipeline.pr.is_draft` | PR finalization (when existing draft PR is promoted) |
| `PostReviewFindings` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting review findings to PR |
| `WritePrConversationContext` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Writing PR conversation context to workspace |
| `ExtractLinkedIssues` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Extracting linked issues from PR |
| `Decomposition` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Sub-issue generation (Phase 2) |
| `DecompositionAnalysis` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Epic analysis (Phase 1) |
| `PostDecompositionPlan` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting decomposition plan comment |
| `PostDecompositionSummary` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting creation summary comment |
| `CreateSubIssues` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Creating sub-issues on GitHub |
| `Reflection` | `pipeline.run_id` | Post-PR reflection prompt (child of FinalizePullRequest) |
| `BrainSyncPostRun` | `pipeline.run_id` | Brain repository sync after run (child of FinalizePullRequest) |
| `FeedbackCollection` | `pipeline.run_id` | Structured feedback collection (child of FinalizePullRequest) |
| `PrePrCleanup` | `pipeline.run_id` | Workspace deletion + reporter disposal (child of ExecutePipeline, emitted by `PipelineCleanup.RunAsync`) |
| `Hub.ReportJobCompleted` | `job_id`, `success` | Hub business logic for job completion |
| `TokenVending.GenerateToken` | — | Token generation HTTP call |
| `Agent.ReceiveJob` | `job_id`, `run_type` | Agent job receipt and acceptance/rejection decision |
| `Agent.ReportCompletion` | `job_id`, `success` | Reporting job completion to orchestrator |
| `WorkItemAgent.Execute` | `work_item_id`, `agent_id` | Top-level span for K8s work-item agent lifecycle (connect, execute, report). Parent is the API request that created the WorkItem (via TRACEPARENT env var). |
| `ExecuteConsolidation` | `pipeline.run_id`, `pipeline.consolidation_type` | Top-level span wrapping a consolidation run (brain, refactoring, or harness) |
| `BrainConsolidation.Clone` | `pipeline.run_id` | Brain repo clone during consolidation |
| `BrainConsolidation.AgentExecution` | `pipeline.run_id` | Main agent LLM call for brain consolidation |
| `BrainConsolidation.DiffGeneration` | `pipeline.run_id` | Diff summary agent call (LLM execution) |
| `BrainConsolidation.AdversarialReview` | `pipeline.run_id` | Adversarial review of brain consolidation |
| `BrainConsolidation.Commit` | `pipeline.run_id` | Committing brain consolidation changes |
| `BrainConsolidation.Push` | `pipeline.run_id` | Pushing brain consolidation changes |
| `RefactoringDetection.Clone` | `pipeline.run_id` | Code repo clone for refactoring detection |
| `RefactoringDetection.HotspotAnalysis` | `pipeline.run_id` | Git hotspot analysis |
| `RefactoringDetection.AgentExecution` | `pipeline.run_id` | Main agent LLM call for refactoring detection |
| `RefactoringDetection.AdversarialReview` | `pipeline.run_id` | Adversarial review of refactoring proposals |
| `RefactoringDetection.CreateIssues` | `pipeline.run_id`, `pipeline.proposal_count` | Creating GitHub issues for proposals |
| `HarnessSuggestion.AgentExecution` | `pipeline.run_id` | Main agent LLM call for harness suggestions |
| `HarnessSuggestion.WriteToFile` | `pipeline.run_id` | Write-to-file agent call (LLM execution) |
| `HarnessSuggestion.AdversarialReview` | `pipeline.run_id` | Adversarial review of harness suggestions |

\* `pipeline.agent_id` is only set on the agent-side `ExecutePipeline` span (set to the container hostname).

### Tag Schema

| Tag | Values | Description |
|-----|--------|-------------|
| `pipeline.run_id` | UUID | Unique identifier for the pipeline run |
| `pipeline.issue` | string | Issue or PR identifier (e.g., `42`) |
| `pipeline.run_type` | `Implementation`, `Review`, `Decomposition` | Run type (**PascalCase** — differs from metric tags) |
| `pipeline.final_step` | step name or `Cancelled` | Last step reached before completion |
| `pipeline.agent_id` | hostname | Agent container hostname (agent-side only) |
| `pipeline.repository` | string | Repository name (on `CloneRepository` span) |
| `pipeline.branch_name` | string | Branch name created or checked out (on `CreateBranch` span) |
| `pipeline.brain_sync.skipped` | `true`/`false` | Whether brain sync was skipped due to missing provider (on `SyncBrainPreRun` span) |
| `pipeline.analysis.continue` | `true`/`false` | Whether analysis passed the confidence gate |
| `pipeline.is_rework` | `true`/`false` | Whether this is a rework run (linked PR exists) |
| `pipeline.pr.is_draft` | `true`/`false` | Whether the PR was created as a draft |
| `pipeline.project_id` | UUID | Project identifier — set only on **spans** (not on metric tags; use `pipeline.project_name` for metrics since it is 1:1 with `pipeline.project_id`) |
| `pipeline.project_name` | string | Project display name |
| `pipeline.consolidation_type` | `BrainConsolidation`, `RefactoringDetection`, `HarnessSuggestions` | Consolidation run type (on `ExecuteConsolidation` span) |
| `code_review.iteration` | integer | Code review iteration index (1-based) |
| `code_review.max_iterations` | integer | Total configured review iterations |
| `code_review.parallel` | `true`/`false` | Whether review agents ran in parallel |
| `pipeline.review_agent` | string | Review agent name (on `CodeReview.Agent` span) |
| `pipeline.isolated` | `true`/`false` | Whether review agent ran in isolated session |

> **Note on tag value casing**: Metric `run_type` values are lowercased (`implementation`), while span `pipeline.run_type` values are PascalCase (`Implementation`). Use the appropriate casing when querying your observability backend.

## Configuration

Telemetry is exported via OTLP. The OpenTelemetry SDK reads configuration from standard environment variables — no code changes are needed to point at a different backend.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | — | OTLP collector endpoint. When absent or empty, OTLP export is disabled (no-op). |
| `OTEL_EXPORTER_OTLP_HEADERS` | — | Auth headers (e.g., `Authorization=Basic <token>`) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | Transport protocol: `grpc` or `http/protobuf` |

### Service Names

Agent pods emit telemetry with `service.name` derived from the agent image and labels.

| `service.name` | Component | Port | How configured |
|----------------|-----------|------|----------------|
| `coding-agent-web` | Web service (Blazor UI) | — | Hardcoded at compile time in `OpenTelemetryRegistration.cs`; not overridable via `OTEL_SERVICE_NAME` |
| `coding-agent-web` *(default)* or override | REST/WebSocket API | Port 8080 | Set via `otel.apiServiceName` in `values.yaml` (default: `coding-agent-web`). Override to `coding-agent-api` to separate API spans from Blazor spans in Tempo — then also update Grafana panel queries. |
| `coding-agent-jobcontroller` | Job Controller | Port 8080 | Fixed fallback; overridable via `OTEL_SERVICE_NAME` env var |
| `coding-agent-scheduler` | Scheduler | Port 8080 | Fixed fallback; overridable via `OTEL_SERVICE_NAME` env var |
| `coding-agent-worker` | Agent pods (K8s Jobs) | — | Set unconditionally by `JobSpecBuilder` via `OTEL_SERVICE_NAME` on each Job pod. Per-run identity exposed via `service.instance.id` = K8s Job name (e.g., `caa-agent-7f3a9b2e1c4`), set in `OTEL_RESOURCE_ATTRIBUTES` |

> **Why API defaults to `coding-agent-web`:** The Grafana "Recent Pipeline Traces" panel queries `rootServiceName="coding-agent-web"`. With the API emitting under the same service name, `ExecutePipeline` spans (started by the API when a WorkItem is created) appear in that panel automatically. Override `otel.apiServiceName` to `coding-agent-api` if you want to distinguish API-origin spans from Blazor UI spans; then update the panel query to `rootServiceName=~"coding-agent-web|coding-agent-api"`. See issue #2255.

> **⚠️ Breaking change notice (API service name):** The API's default `service.name` reverted from `coding-agent-api` back to `coding-agent-web`. If you previously updated Grafana dashboards or alerts to filter on `service.name="coding-agent-api"` based on an earlier migration notice, update those filters back to `coding-agent-web` (or use a regex: `service.name=~"coding-agent-web|coding-agent-api"`). Dashboards that never changed the filter are unaffected.

### Example: Grafana Cloud

OTEL variables are configured via Helm values or the `OTEL_*` environment variables. To connect to Grafana Cloud, set these values in your `values.yaml` or as Helm `--set` arguments:

```env
OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-prod-us-east-0.grafana.net/otlp
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <base64-encoded-instance-id:token>
```

See `.env.example` for a reference of available variables.

## Verification

### Metrics

Use `dotnet-counters` to verify metrics are being recorded. Exec into the orchestrator pod:

```bash
kubectl exec -it <orchestrator-pod> -n coding-agent -- dotnet-counters monitor --counters CodingAgent.Pipeline
```

Expected output after dispatching a job:

```
[CodingAgent.Pipeline]
    pipeline.run.outcomes ({run} / 1 sec)              0
    pipeline.run.duration (s)
        Percentile=50                                  0
        Percentile=95                                  0
        Percentile=99                                  0
```

### Traces

When connected to a backend (Grafana Tempo, Jaeger, etc.), search for traces with:

- Service: `coding-agent-web` or `coding-agent-worker`
- Operation: `ExecutePipeline`

Expected span hierarchy for an implementation run:

```
ExecutePipeline
├── Step CloneRepository
│   └── CloneRepository
├── Step VerifyBaseline
│   └── VerifyBaseline
├── Step AnalyzeCode
│   └── AnalyzeIssue
├── Step GenerateCode
│   └── GenerateCode
├── Step ReviewCode
│   └── ReviewCode
│       ├── CodeReview.Iteration
│       │   ├── CodeReview.Agent (per agent)
│       │   └── ...
│       └── ...
├── Step RunQualityGates
│   └── RunQualityGates
│       ├── QualityGate.Compilation
│       ├── QualityGate.Tests
│       └── WaitForCi  (pre-PR external CI, pipeline.ci_path=pre_pr)
├── Step CreatePullRequest
│   └── CreatePullRequest
│       ├── GeneratePrDescription
│       └── FinalizePullRequest
│           ├── WaitForCi  (post-PR external CI, pipeline.ci_path=post_pr)
│           ├── Reflection
│           ├── BrainSyncPostRun
│           └── FeedbackCollection
└── PrePrCleanup
```

For a review run:

```
ExecutePipeline
├── Step ExtractLinkedIssues
├── Step ReviewCode
│   └── ReviewCode
├── Step PostReviewFindings
│   └── PostReviewFindings
└── PrePrCleanup
```

For a decomposition run (Phase 1):

```
ExecutePipeline
└── Step DecompositionAnalysis
    └── PostDecompositionPlan
```

For a brain consolidation run:

```
ExecuteConsolidation
├── BrainConsolidation.Clone
├── BrainConsolidation.AgentExecution
├── BrainConsolidation.DiffGeneration
├── BrainConsolidation.AdversarialReview
├── BrainConsolidation.Commit
└── BrainConsolidation.Push
```

For a refactoring detection run:

```
ExecuteConsolidation
├── RefactoringDetection.Clone
├── RefactoringDetection.HotspotAnalysis
├── RefactoringDetection.AgentExecution
├── RefactoringDetection.AdversarialReview
└── RefactoringDetection.CreateIssues
```

For a harness suggestion run:

```
ExecuteConsolidation
├── HarnessSuggestion.AgentExecution
├── HarnessSuggestion.WriteToFile
└── HarnessSuggestion.AdversarialReview
```


## Frontend Observability (Grafana Faro)

In addition to backend OTLP telemetry, the Orchestrator (Blazor Server app) supports [Grafana Faro](https://grafana.com/oss/faro/) for frontend Real User Monitoring (RUM).

When `Faro__CollectorUrl` is set, `faro-init.js` (loaded in `App.razor`) asynchronously loads the Faro Web SDK from the unpkg.com CDN and starts collecting:
- Page load performance
- JavaScript errors and unhandled promise rejections
- User interactions

When the env var is absent or empty, Faro is disabled and the `faroApi` stub no-ops silently — no errors are thrown and there is no page-load impact.

See [Configuration — Frontend Observability](configuration.md#frontend-observability-grafana-faro) for the `Faro__CollectorUrl` env var and Helm setup.

### Air-Gapped Deployments

In environments without outbound internet access, the CDN bundles fail to load silently. To use Faro in firewalled deployments: download the pinned bundle files locally, place them in `wwwroot/js/faro/`, and update `SDK_URL` / `TRACING_URL` in `faro-init.js` to relative paths.
