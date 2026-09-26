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
| `pipeline.loop.polls` | Counter | — | `result` | Incremented on each poll cycle (`success` or `failure`) |
| `pipeline.loop.issues_found` | Counter | — | — | Incremented by the number of issues/PRs/epics discovered per poll cycle |
| `pipeline.loop.dispatch_decisions` | Counter | — | `decision` | Incremented for each dispatch decision made by the loop |
| `pipeline.loop.backoff_events` | Counter | — | — | Incremented when a template poll failure triggers backoff escalation |
| `pipeline.loop.circuit_breaker_trips` | Counter | — | — | Incremented when the circuit breaker trips (all templates failing) |
| `token_vending.failures` | Counter | — | — | Token vending operation failures |
| `token_vending.duration` | Histogram | seconds | — | Duration of token vending operations |
| `agent.jobs.received` | Counter | — | — | Jobs received by agent workers |
| `agent.jobs.rejected` | Counter | — | `reason` | Jobs rejected by agent workers |
| `agent.heartbeat.failures` | Counter | — | — | Agent heartbeat send failures |
| `agent.reconnections` | Counter | — | — | Agent reconnection events |
| `pipeline.step.duration` | Histogram | seconds | `step_name`, `run_type`, `pipeline.project_id`, `pipeline.project_name` | Duration of individual pipeline steps |
| `pipeline.step.count` | Counter | — | `step_name`, `run_type`, `pipeline.project_id`, `pipeline.project_name` | Pipeline step execution count |
| `agent.tokens.used` | Counter | — | `run_type`, `pipeline.project_id`, `pipeline.project_name` | Agent tokens consumed |
| `agent.cost.usd` | Counter | USD | `run_type`, `pipeline.project_id`, `pipeline.project_name` | LLM cost in USD |
| `quality_gate.retries` | Counter | — | `run_type`, `pipeline.project_id`, `pipeline.project_name` | Quality gate retry attempts |
| `quality_gate.duration` | Histogram | seconds | `run_type`, `pipeline.project_id`, `pipeline.project_name` | Total time in quality gate phase |
| `quality_gate.evaluations` | Counter | — | `gate_name`, `result` | Individual gate evaluation events |
| `quality_gate.external_ci.duration` | Histogram | seconds | — | Time waiting for external CI |
| `quality_gate.post_pr_ci.duration` | Histogram | seconds | — | Time waiting for post-PR CI to complete |
| `quality_gate.process.timeout` | Counter | — | `gate_name`, `qgc_name` | QGC process timeouts (compilation or test command exceeded `processTimeoutSeconds`) |
| `quality_gate.process.duration` | Histogram | seconds | `gate_name`, `qgc_name` | Duration of a single QGC process invocation (compilation or test command). Distinct from `quality_gate.duration` which covers the entire retry phase |
| `quality_gate.stall.warnings` | Counter | — | `phase` | Agent silence warnings by pipeline phase — fires after each `stallWarningInterval` with no output |
| `quality_gate.stall.kills` | Counter | — | `phase` | Agent processes killed due to stall timeout |
| `quality_gate.stall.process_deaths` | Counter | — | `phase` | Agent process death events (process exited unexpectedly) by phase |
| `dispatch.queue.wait_time` | Histogram | seconds | — | Time a job spent waiting in the dispatch queue |
| `agent.jobs.active` | ObservableGauge | — | — | Currently executing agent jobs |
| `agent.connections.total` | ObservableGauge | — | — | Total registered agents |
| `consolidation.jobs.expired` | Counter | — | — | Consolidation jobs expired from queue (not currently emitted) |
| `brain.syncs.completed` | Counter | — | — | Successful brain pre-run sync operations |
| `brain.updates.committed` | Counter | — | — | Brain post-run commits pushed |
| `brain.updates.empty` | Counter | — | — | Runs where agent produced no brain changes |
| `brain.files.written` | Counter | — | — | Total brain files committed across all runs |
| `brain.sync.duration` | Histogram | seconds | — | Duration of brain sync operations |
| `brain.sync.skipped` | Counter | — | `reason` | Brain post-run sync skipped (tagged by reason) |
| `agent.signalr.failures` | Counter | — | — | Failed or dropped SignalR messages from agent |
| `pipeline.decomposition.sub_issues.created` | Counter | — | — | Sub-issues created by decomposition |
| `pipeline.decomposition.sub_issues.failed` | Counter | — | — | Sub-issue creation failures |
| `pipeline.decomposition.duration` | Histogram | seconds | `pipeline.project_id`, `pipeline.project_name`, `phase` | Duration of decomposition phases (`phase`: `analysis` or `creation`) |
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
| `result` | `success`, `failure` | Poll cycle outcome |
| `decision` | `dispatched`, `skipped_already_processing`, `skipped_dependency_blocked`, `skipped_no_agent`, `skipped_max_runs`, `skipped_filtered_by_label` | Dispatch decision reason |
| `reason` | `busy`, `shutting_down`, `unknown` | Agent job rejection reason |
| `repo_provider_id` | provider config UUID | Repository provider config ID — only present on `pipeline.housekeeping.*` metrics |
| `phase` | `qgc_retry_agent`, `codegen`, `analysis`, `code_review`, `decomposition`, `unknown` | Pipeline phase — only present on `quality_gate.stall.*` metrics |
| `qgc_name` | QGC display name | Quality gate config name — only present on `quality_gate.process.*` metrics |

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
- Agent pods do **not** record run-level metrics (`pipeline.run.outcomes`, `pipeline.run.duration`). They are ephemeral and subject to the first-series problem. Only the long-lived API process records run outcomes.
- `pipeline.project_name` is a tag on `pipeline.run.outcomes` but is **excluded from pre-initialization**. It has unbounded cardinality, so pre-initializing it would exceed the ≈100 series limit. Pre-initialized series have 3 tags; live series have 4 tags (including `pipeline.project_name`). This means the first event for a brand-new project name still shows 0 in `increase()` until a second event arrives, but the closed dimensions are pre-initialized correctly.
- Histograms (`pipeline.run.duration`, `workdistribution.job_execution_duration_seconds`) cannot be pre-initialized and are left as-is.
- `pipeline.run.outcomes` is pre-initialized with **75 series** (3-tag): 5 run_types × (7 non-failure outcomes + 1 timeout + 7 failed × 7 failure_reasons).
- `workdistribution.workitems_terminated` is pre-initialized with **24 series**: 3 statuses × (1 none + 7 failure_reasons).

### Prompt Cache and Per-Phase Token Data

Each `PipelineRun` accumulates token and cost data beyond the simple totals exposed by `agent.tokens.used` and `agent.cost.usd`. This richer data is available on `PipelineRunSummary` objects returned by `GET /api/pipeline-runs` and `GET /api/export/runs.json`, and is visible in the UI sidebars.

#### Cache Token Fields

| Field | Description |
|-------|-------------|
| `CacheReadTokens` | Tokens served from the upstream LLM's prompt cache across all agent invocations in this run |
| `CacheWriteTokens` | Tokens written into the prompt cache across all agent invocations in this run |

**Provider support:** Cache token fields are populated only for **OpenCode** agents. KiroCli agents always report 0 for both fields (the KiroCli provider does not expose cache token breakdowns).

#### Per-Phase Breakdown

`PipelineRunSummary.PhaseBreakdown` is a dictionary keyed by phase name (e.g., `"Analysis"`, `"CodeGeneration"`, `"Review.Correctness"`). Each entry contains:

| Property | Description |
|----------|-------------|
| `Tokens` | Total tokens consumed during this phase |
| `Cost` | Cost in USD for this phase, or `null` if unavailable |

The breakdown is rendered in the active-run sidebar (collapsible "Cost Breakdown" table sorted by cost descending) and in the history run detail modal. It is `null` for runs recorded before this feature was introduced.

**API exposure:** `PhaseBreakdown` is included in the `GET /api/export/runs.json` export. Fields will be absent (`null`) for runs that pre-date the phase breakdown feature.

### Histogram Bucket Boundaries

Custom bucket boundaries are configured via `InstrumentAdvice<double>` at instrument creation time:

| Metric | Boundaries (seconds) |
|--------|---------------------|
| `pipeline.run.duration` | 60, 300, 600, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 21600, 28800, 43200 |
| `pipeline.step.duration` | 5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600 |
| `quality_gate.process.duration` | 5, 10, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600 |
| `quality_gate.post_pr_ci.duration` | 5, 10, 30, 60, 120, 300, 600, 1200, 1800, 3600 |
| `dispatch.queue.wait_time` | 5, 10, 30, 60, 120, 300, 600, 1200, 1800, 3600 |
| `workdistribution.dispatch_latency_seconds` | 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600 |
| `workdistribution.workitems_pending_duration_seconds` | 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600 |
| `workdistribution.job_execution_duration_seconds` | 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600 |
| `workdistribution.timeout_execution_age_seconds` | 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600 |

Other histograms (`token_vending.duration`, `quality_gate.duration`, etc.) use the OpenTelemetry SDK's default bucket boundaries.

### Run Outcome Metrics (API-only)

`pipeline.run.outcomes` (Prometheus: `pipeline_run_outcomes_total`) and `pipeline.run.duration` (`pipeline_run_duration_seconds`) are emitted **only by the API** (`WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync`), which receives every terminal status POST from both the agent pod and the Job Controller.

**Why API-only?**
- Agent pods (ephemeral K8s Jobs) start fresh OTel series on every run; Prometheus `rate()`/`increase()` can't see the first increment of a series. Anything recorded once per run reads zero.
- The Job Controller previously cross-emitted `pipeline.jobs.*` from `LogTerminalStatus()` after POSTing status to the API, causing double-counting (≈45% inflation in production).
- Recording in the API solves both: the API is a long-lived process with pre-initialized series, and it receives exactly one terminal POST per WorkItem transition.

**Reliable sources by use case:**

| Use case | Recommended metric |
|----------|--------------------|
| Count of terminal runs by outcome | `increase(pipeline_run_outcomes_total[24h])` |
| Run duration percentiles | `pipeline_run_duration_seconds` |
| Exact job counts (alerts) | `workdistribution_workitems_terminated_total` (exact, pre-initialized) |

**Breaking change in issue #2967:** `workdistribution_workitems_terminated_total{failure_reason=...}` values changed from PascalCase (e.g. `"Timeout"`) to snake_case (e.g. `"timeout"`). Update any Grafana panels or alert rules that filter on `failure_reason` labels.

### Work Distribution Metrics

The `CodingAgent.WorkDistribution` meter is defined in `WorkDistributionTelemetry.cs` (`src/CodingAgent.Infrastructure.Common/Telemetry/WorkDistributionTelemetry.cs`, namespace `CodingAgent.Pipeline.Telemetry`). Instruments are fed by `ReconciliationService` in the Job Controller, and by `WorkItemCountsService` in the Scheduler (`workitems_by_status` gauge only — `WorkItemMetricsBackgroundService` was removed from the Pipeline API in Spec 047/048).

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `workdistribution.dispatch_latency_seconds` | Histogram | s | — | Time from WorkItem creation (Pending) to Dispatched |
| `workdistribution.workitems_pending_duration_seconds` | Histogram | s | — | Time spent in Pending status before dispatch |
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

<!-- TODO: [WARNING] The "Span Noise Filters" section (covering OtelNoiseFilter wiring for AspNetCore
     request filtering, Kubernetes API server filtering, outbound span name enrichment, and
     OtelNoiseSpanDropProcessor) was removed in issue #2967. None of the OtelNoiseFilter source code
     was changed — the section was removed as unrelated cleanup. The section documented production code
     that reduces ~70% of raw daily span volume. Operators configuring OTLP or debugging high span
     volumes will no longer find this guidance. Restore the section (see git history of this file
     before the #2967 merge) or move it to a separate observability-internals doc. -->

All spans are emitted from the `CodingAgent.Pipeline` ActivitySource. Spans marked with † are emitted from both the orchestrator (`PipelineOrchestrationService`) and the agent worker (`LocalPipelineExecutor`).

### Trace Hierarchy

Each agent run's spans are connected to the API request that created the WorkItem. The linkage is:

```
POST /api/work-items (API request span)
└── WorkItemAgent.Execute (K8s agent pod)
    ├── CloneRepository
    ├── AnalyzeIssue
    ├── GenerateCode
    ├── RunQualityGates
    └── CreatePullRequest
```

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
| `ExecutePipeline` † | `pipeline.run_id`, `pipeline.issue`, `pipeline.final_step`, `pipeline.agent_id`* | Top-level span wrapping the full pipeline execution |
| `CloneRepository` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.repository` | Repository clone into workspace |
| `CreateBranch` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.branch_name` | Branch creation or checkout |
| `SyncBrainPreRun` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.brain_sync.skipped` | Brain repository sync (pre-run) |
| `RunEnvironmentSetup` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Environment setup commands |
| `CloneProjectRepositories` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Additional project repo clones |
| `AnalyzeIssue` | `pipeline.run_id`, `pipeline.issue`, `pipeline.analysis.continue` | Issue analysis (confidence gate) |
| `GenerateCode` | `pipeline.run_id`, `pipeline.issue`, `pipeline.is_rework` | Code generation / rework |
| `RunQualityGates` | `pipeline.run_id`, `pipeline.issue` | Quality gate execution |
| `QualityGate.Compilation` | `gate_name` | Compilation command execution (child of RunQualityGates) |
| `QualityGate.Tests` | `gate_name` | Test command execution (child of RunQualityGates) |
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
| `pipeline.project_id` | UUID | Project identifier (set on job/step metrics and agent-side spans) |
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

<!-- TODO: [WARNING] The coding-agent-worker row was removed from this table in issue #2967.
     Agent pods (K8s Jobs) still emit ExecutePipeline spans via PipelineRunInstrumentation.
     Operators querying Tempo for agent-pod spans by service.name or service.instance.id (set to the
     K8s Job name) will find the documented guidance gone. Re-add the row:
     | coding-agent-worker | Agent pods (K8s Jobs) | — | Set unconditionally by JobSpecBuilder; per-run identity in service.instance.id (= Job name) in OTEL_RESOURCE_ATTRIBUTES |
     Also restore the "Run identity in service.instance.id" callout that was removed. -->

<!-- TODO: [WARNING] The API service.name default was changed from coding-agent-api to coding-agent-web in this
     table without a breaking-change notice for operators who followed the earlier migration (issue pre-#2969)
     and updated their dashboards from coding-agent-web to coding-agent-api. Those operators will now get wrong
     results if they kept the coding-agent-api filter. Add a breaking-change callout here analogous to the one
     that was removed: "⚠️ Breaking change: the API's default service.name reverted from coding-agent-api to
     coding-agent-web. If you updated Grafana dashboards/alerts to coding-agent-api based on the previous
     notice, update them back to coding-agent-web (or use a regex that matches both)." -->

| `service.name` | Component | Port | How configured |
|----------------|-----------|------|----------------|
| `coding-agent-web` | Web service (Blazor UI) | — | Hardcoded at compile time in `OpenTelemetryRegistration.cs`; not overridable via `OTEL_SERVICE_NAME` |
| `coding-agent-web` *(default)* or override | REST/WebSocket API | Port 8080 | Set via `otel.apiServiceName` in `values.yaml` (default: `coding-agent-web`). Override to `coding-agent-api` to separate API spans from Blazor spans in Tempo — then also update Grafana panel queries. |
| `coding-agent-jobcontroller` | Job Controller | Port 8080 | Fixed fallback; overridable via `OTEL_SERVICE_NAME` env var |
| `coding-agent-scheduler` | Scheduler | Port 8080 | Fixed fallback; overridable via `OTEL_SERVICE_NAME` env var |

> **Why API defaults to `coding-agent-web`:** The Grafana "Recent Pipeline Traces" panel queries `rootServiceName="coding-agent-web"`. With the API emitting under the same service name, `ExecutePipeline` spans (started by the API when a WorkItem is created) appear in that panel automatically. Override `otel.apiServiceName` to `coding-agent-api` if you want to distinguish API-origin spans from Blazor UI spans; then update the panel query to `rootServiceName=~"coding-agent-web|coding-agent-api"`. See issue #2255.

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
├── AnalyzeIssue
├── GenerateCode
├── ReviewCode
│   ├── CodeReview.Iteration
│   │   ├── CodeReview.Agent (per agent)
│   │   └── ...
│   └── ...
├── RunQualityGates
│   ├── QualityGate.Compilation
│   └── QualityGate.Tests
├── CreatePullRequest
├── GeneratePrDescription
└── FinalizePullRequest
    ├── Reflection
    ├── BrainSyncPostRun
    └── FeedbackCollection
```

For a review run:

```
ExecutePipeline
├── ExtractLinkedIssues
├── ReviewCode
└── PostReviewFindings
```

For a decomposition run (Phase 1):

```
ExecutePipeline
└── DecompositionAnalysis
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
