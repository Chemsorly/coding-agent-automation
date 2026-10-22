# Observability

Pipeline telemetry is built on [OpenTelemetry](https://opentelemetry.io/) for .NET, exporting metrics and distributed traces via OTLP.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for how pipeline steps relate to trace spans, and [Configuration](configuration.md) for general pipeline settings.

## Metrics

Custom metrics live on three meters. Agent pods (K8s Jobs) export **no metrics**: they are short-lived, and Prometheus `increase()` cannot see the first increment of a series that starts in a new process. Everything an agent measures is reported to the API over the hub (`ReportStepTransition`, `ReportQualityGateResult`, `ReportPipelineRunEvent`, the completion payload) and recorded there.

| Meter | Defined in | Registered in |
|-------|------------|---------------|
| `CodingAgent.Pipeline` | `PipelineTelemetry.cs` (`src/CodingAgent.Infrastructure.Common/Telemetry/`) | API, Scheduler, Job Controller, Web |
| `CodingAgent.WorkDistribution` | `WorkDistributionTelemetry.cs`, `ChatTelemetry.cs` (same folder) | API, Scheduler, Job Controller, Web |
| `CodingAgent.GitHub` | `GitHubTelemetry.cs` (`src/CodingAgent.Infrastructure.Providers/GitHub/`) | API, Scheduler, Job Controller |

Every host also exports the built-in ASP.NET Core, `HttpClient` and `System.Runtime` (.NET runtime) meters; the API also exports `Npgsql`. All metric readers use cumulative temporality (Grafana Cloud drops delta histograms).

### Run metrics (API)

Recorded by the API when an agent reports a step, a quality gate result, a pipeline event, or the run's terminal status (`WorkItemStatusTransitionService`, `AgentJobLifecycleService`, `AgentHub.Lifecycle`).

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `pipeline.run.outcomes` | Counter | `{run}` | `run_type`, `outcome`, `failure_reason`, `pipeline.project_name`, `provider`, `model` | Terminal run outcomes, once per terminal transition. `provider` is the tool of the work item's pinned route and `model` the route's model, not the model the agent reports: `auto` stays `auto`. Both are `unknown` for work created before agent routing. See [Outcome mapping](#outcome-mapping) |
| `pipeline.routing.refusals` | Counter | `{refusal}` | `task_type`, `stage`, `reason` | Agent routing refusals. `stage`: `create`, `dispatch`, `claim`; `reason`: `no_route`, `profile_unavailable`, `no_tool`, `template_missing`, `template_changed`, `config_collision`. A refusal at creation leaves no work item, so `pipeline.run.outcomes` never sees it. Not pre-initialized: the series appears with the first refusal. See [Routing](#routing) |
| `pipeline.run.duration` | Histogram | s | `run_type`, `outcome` | Dispatched → terminal |
| `pipeline.run.step.duration` | Histogram | s | `run_type`, `step` | Duration of each step visit, recorded on every step transition |
| `pipeline.run.sub_issues` | Counter | `{issue}` | `result` (`created`, `failed`) | Sub-issues per decomposition run, once per run |
| `pipeline.run.brain_updates` | Counter | `{run}` | `result` (`pushed`, `none`) | Brain update result per non-consolidation run |
| `pipeline.run.tokens` | Counter | `{token}` | `run_type`, `phase`, `provider` | LLM tokens per phase, at terminal time |
| `pipeline.run.cost_usd` | Counter | `{usd}` | `run_type`, `phase`, `provider` | LLM cost per phase, at terminal time |
| `pipeline.run.agent_sessions` | Counter | `{session}` | `run_type`, `phase`, `provider`, `model` | Agent CLI invocations per phase |
| `pipeline.run.agent_time` | Counter | s | `run_type`, `phase`, `provider` | Agent execution time per phase |
| `pipeline.run.token_usage` | Counter | `{token}` | `run_type`, `provider`, `token_type` | Tokens by type (`input`, `output`, `reasoning`, `cache_read`, `cache_write`); `output` excludes `reasoning`. No `phase` tag, to keep series bounded. Reported by OpenCode and Claude Code |
| `pipeline.run.billing_cost_usd` | Counter | `{usd}` | `run_type`, `provider`, `billing` | Provider-reported cost by how it is paid: `api` is billed per token, `subscription` is the CLI's estimate under a flat Claude plan (not a bill), `unknown` otherwise |
| `pipeline.run.agent_turns` | Counter | `{turn}` | `run_type`, `provider` | Model round trips per run (Claude Code) |
| `pipeline.run.web_search_requests` | Counter | `{request}` | `run_type`, `provider` | Web searches the model made per run (Claude Code) |
| `pipeline.run.rate_limit_events` | Counter | `{event}` | `provider`, `window`, `status` | Latest subscription rate-limit reading per window seen in a run (Claude Code `rate_limit_event`). `window`: `five_hour`, `seven_day`, `seven_day_opus`, `seven_day_sonnet`, `overage`, `other`. `status`: `allowed`, `allowed_warning`, `rejected`, `other` |
| `pipeline.run.rate_limit_utilization` | Histogram | `1` | `provider`, `window`, `status` | Fraction (0–1) of the window used, when the CLI reports it |
| `pipeline.run.quality_gate.results` | Counter | `{evaluation}` | `run_type`, `gate`, `result`, `infrastructure_failure` | Quality gate evaluations. `gate`: `compilation`, `tests`, `external_ci` |
| `pipeline.run.ci.not_started_retriggers` | Counter | `{retrigger}` | `run_type` | Empty commits pushed to restart CI that never started |
| `pipeline.run.ci.wait` | Histogram | s | `run_type`, `stage`, `result` | Push → CI conclusion. `stage`: `pre_pr`, `post_pr`; `result`: `pass`, `fail` |
| `pipeline.run.agent_stalls` | Counter | `{stall}` | `run_type`, `phase`, `kind` | Agent killed for silence (`stall_kill`), agent process died (`process_death`), or a quality gate process timed out (`process_timeout`). The API maps the agent-reported phase and kind onto the closed sets |

### Dispatch and work distribution

| Metric | Type | Unit | Tags | Recorded by | Description |
|--------|------|------|------|-------------|-------------|
| `workdistribution.dispatch_latency_seconds` | Histogram | s | `agent_selector` | API | WorkItem creation (or original enqueue) → dispatched |
| `workdistribution.dispatch.attempts` | Counter | `{attempt}` | `result`, `reason` | API | Outcome of every `POST /api/work-items/{id}/dispatch`. `result`: `dispatched`, `deferred`, `transient`; `reason`: `none`, `concurrency_limit`, `not_pending`, `no_template`, `no_route`, `pvc_unavailable`, `lock_timeout`, `k8s_error`. `no_template` drives `WorkItemNoJobTemplate` (see [Alerts](#alerts)). `no_route` is a work item a previous release queued without an agent route whose route is refused now; the dispatcher fails it |
| `workdistribution.pod_start_seconds` | Histogram | s | — | API | Dispatched → the agent's first `GET /assignment` |
| `workdistribution.credential_pool_available` / `_claimed` | ObservableGauge | `{pvc}` | `pool` | Scheduler leader | Kiro credential PVCs, polled every 10 s by the Scheduler leader. Emits nothing before the first successful poll and on non-leaders |
| `workdistribution.pvc_pool_exhaustions` | Counter | `{event}` | — | API | A dispatch found no free credential PVC |
| `workdistribution.progress_write_failures` | Counter | `{failure}` | — | API | Failed `LastProgressAt` writes; sustained failures risk false-positive timeouts |
| `workdistribution.dispatcher_polls` | Counter | `{poll}` | — | Scheduler | Dispatch poll cycles |
| `workdistribution.dispatcher_last_poll_epoch_seconds` | ObservableGauge | s | — | Scheduler | Epoch seconds of the last dispatch poll; drives `DispatcherStalled` |
| `workdistribution.workitems_by_status` | ObservableGauge | `{item}` | `status`, `agent_selector` | Scheduler | Current WorkItem counts |
| `workdistribution.pending.oldest_age_seconds` | ObservableGauge | s | — | Scheduler | Age of the oldest Pending WorkItem; absent when none is pending |
| `workdistribution.timeout_execution_age_seconds` | Histogram | s | `agent_selector` | Job Controller | Execution age when a timeout is enforced. If p10 clusters near zero, the timeout anchor is wrong |
| `workdistribution.timeout_canary_violations` | Counter | `{violation}` | `agent_selector` | Job Controller | Timeouts skipped by the canary invariant; any non-zero value is a timestamp bug |
| `workdistribution.agent_timeouts` | Counter | `{job}` | `agent_selector` | Job Controller | Agent jobs killed by the session timeout |
| `workdistribution.chat.*` | Histogram / UpDownCounter / Counter | — | `agent_selector`, `pool` | API | Chat pods: `dispatch_latency_seconds`, `sessions_active`, `session_duration_seconds`, `pod_connect_timeouts`, `pod_force_terminations`, `pvc_utilization` |
| `pipeline.db_retention.pipeline_runs_deleted` / `.work_items_deleted` | Counter | `{row}` | — | API | Rows removed by the per-project retention sweep |

### Scheduler loop, housekeeping and GitHub

| Metric | Type | Tags | Recorded by | Description |
|--------|------|------|-------------|-------------|
| `pipeline.loop.polls` | Counter | `result` (`success`, `partial_failure`, `failure`) | Scheduler | Closed-loop poll cycles. `partial_failure` means some but not all templates failed to poll |
| `pipeline.loop.issues_found` | Counter | — | Scheduler | Issues/PRs/epics discovered per poll |
| `pipeline.loop.dispatch_decisions` | Counter | `decision` | Scheduler | `dispatched`, `skipped_already_processing`, `skipped_dependency_blocked`, `skipped_no_agent`, `skipped_max_runs`, `skipped_filtered_by_label` |
| `pipeline.loop.backoff_events` | Counter | — | Scheduler | Template poll failures that escalated the backoff |
| `pipeline.loop.circuit_breaker_trips` | Counter | — | Scheduler | All templates failing |
| `pipeline.queue_sweep.cancelled` / `.skipped` / `.failed` | Counter | — | Scheduler | Stale WorkItems cancelled, skipped (fail-open), or unexpected sweep failures |
| `pipeline.dispatch.linked_issues_resolved` | Counter | `source` (`closing_keyword`, `issue_url`) | Scheduler | Linked issues resolved during dispatch enrichment |
| `pipeline.dispatch.linked_issue_fetch_failed` | Counter | — | Scheduler | Linked issue lookups that failed (non-fatal) |
| `consolidation.dispatch.permanent_failures` | Counter | `run.type` | Scheduler | Consolidation dispatches that cannot succeed (e.g. no job template) |
| `pipeline.housekeeping.triggered` / `.succeeded` / `.failed` | Counter | `repo_provider_id` | Scheduler | Server-side branch updates |
| `pipeline.housekeeping.skipped` | Counter | `repo_provider_id`, `skip_reason` | Scheduler | PRs skipped during candidate selection |
| `pipeline.housekeeping.evicted` | Counter | `repo_provider_id` | Scheduler | In-flight entries removed (CI resolved, PR merged or label removed) |
| `pipeline.housekeeping.slot_exhausted` | Counter | `repo_provider_id` | Scheduler | Cycles that hit the in-flight slot limit |
| `pipeline.housekeeping.pr_evaluated` | Counter | `repo_provider_id`, `mergeability_status` | Scheduler | PRs evaluated per mergeability status |
| `pipeline.housekeeping.reprobe_triggered` / `.reprobe_resolved` | Counter | `repo_provider_id` (+ `resolved_state`) | Scheduler | Re-probes for PRs whose mergeability came back unknown |
| `pipeline.housekeeping.conflict_rework_triggered` | Counter | `repo_provider_id` | Scheduler | Issues re-queued because their PR has a merge conflict |
| `pipeline.housekeeping.branch_deleted` | Counter | `repo_provider_id` | Scheduler | Stale agent branches deleted |
| `pipeline.pull_requests.closed` | Counter | `outcome` (`merged`, `closed_unmerged`) | Scheduler | Agent PRs merged or closed, once per PR per leader instance |
| `pipeline.pull_requests.time_to_merge` | Histogram (s) | — | Scheduler | PR creation → merge (merge time approximated by the poll time) |
| `label_swap_remove_exhausted_total` | Counter | `label`, `identifier` | API, Scheduler | Label swaps that could not remove the old `agent:*` label after 3 attempts (dual-label state) |
| `github.api.requests` | Counter | `operation`, `outcome` | API, Scheduler, Web | GitHub API attempts, retries included. `outcome`: `success`, `not_found`, `rate_limited`, `error` |
| `github.rate_limit.remaining` | ObservableGauge | `resource` (`core`, `graphql`) | API, Scheduler, Web | Remaining rate-limit quota; only emitted after the process made a GitHub call |

### Agent connections (Scheduler leader)

<!-- TODO [WARNING]: This heading attributes the whole table to the Scheduler leader, but only the two
     gauge rows (`agent.jobs.active`, `agent.connections.total`) are emitted there. `agent.hub.auth_rejections`
     and `token_vending.failures` are counters recorded by API-hosted code. An operator switching
     `service_name` after the deploy could query the wrong service for those counters. Fix: use a neutral
     heading (or split into two subsections) and name the Scheduler leader only on the two gauge rows. -->

| Metric | Type | Tags | Description |
|--------|------|------|-------------|
| `agent.jobs.active` | ObservableGauge | — | Agent jobs currently executing, polled from the agent registry by the Scheduler leader |
| `agent.connections.total` | ObservableGauge | — | Agents registered with the hub, polled from the agent registry by the Scheduler leader |
| `agent.hub.auth_rejections` | Counter | `reason` | Hub calls rejected: `reconnect_race`, `not_registered`, `job_mismatch`. A `DeregisterAgent` from a connection that is not registered is skipped, not rejected |
| `token_vending.failures` | Counter | — | Token vending failures |

### Tag Schema

| Tag | Values | Description |
|-----|--------|-------------|
| `run_type` | `implementation`, `review`, `decomposition`, `decompositionanalysis`, `consolidation`, `triage` | Lowercase run type, from `PipelineRunEntity.RunType` (falls back to `WorkItemEntity.TaskType`) |
| `outcome` | `cancelled`, `conflict_restart`, `needs_refinement`, `wont_do`, `pr_created`, `draft_pr`, `succeeded`, `timeout`, `failed` | Terminal run outcome — see [Outcome mapping](#outcome-mapping) |
| `failure_reason` | `none`, `timeout`, `infrastructure_failure`, `agent_error`, `token_refresh_failure`, `exit_code_failure`, `quality_gate_exhausted`, `gate_rejected` | Snake-case `FailureReason`; `none` for every non-failure outcome |
| `pipeline.project_name` | project display name | From the run's `PipelineRuns` row, or else the WorkItem's project. `unknown` only when neither resolves |
| `step` | `PipelineStep` enum name | PascalCase, only on `pipeline.run.step.duration` |
| `phase` | `analysis`, `analysis_review`, `codegen`, `review`, `quality_gate`, `acceptance_criteria`, `pr_description`, `reflection`, `decomposition`, `other` | One closed set for every `phase` tag and the `pipeline.phase` span attribute — see [Phase normalization](#phase-normalization) |
| `provider` | `kiro`, `opencode`, `claude`, `unknown` | Agent provider; anything else is recorded as `unknown`. On `pipeline.run.outcomes` it is the tool of the pinned route |
| `model` | model name, or `auto` | On `pipeline.run.agent_sessions` the model the agent reports (`unknown` when not reported); on `pipeline.run.outcomes` the pinned route's model (`unknown` for work created before agent routing) |
| `task_type` | `Implementation`, `Review`, `Decomposition`, `Triage`, `Consolidation` | Only on `pipeline.routing.refusals` |
| `reason` | `no_route`, `profile_unavailable`, `no_tool`, `template_missing`, `template_changed`, `config_collision` | On `pipeline.routing.refusals`. `workdistribution.dispatch.attempts` has its own `reason` set |
| `token_type` | `input`, `output`, `reasoning`, `cache_read`, `cache_write` | Only on `pipeline.run.token_usage` |
| `billing` | `api`, `subscription`, `unknown` | Only on `pipeline.run.billing_cost_usd` |
| `gate` / `infrastructure_failure` | `compilation`, `tests`, `external_ci` / `true`, `false` | Only on `pipeline.run.quality_gate.results`; an infrastructure failure is an OOM or container kill rather than a code failure |
| `stage` | `pre_pr`, `post_pr` / `create`, `dispatch`, `claim` | `pre_pr`, `post_pr` on `pipeline.run.ci.wait`; `create`, `dispatch`, `claim` on `pipeline.routing.refusals` |
| `kind` | `stall_kill`, `process_death`, `process_timeout` | Only on `pipeline.run.agent_stalls` |
| `repo_provider_id` | provider config UUID | Only on `pipeline.housekeeping.*` |

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

`needs_refinement` and `wont_do` both arrive with `FailureReason.GateRejected` (set by `AgentPhaseExecutor`); checking `AnalysisRecommendation` first forces `failure_reason=none` for them.

Hub-completed runs (where the agent's SignalR connection reports terminal status before the HTTP callback) emit only `succeeded`, `failed` (with a `failure_reason` tag), `timeout`, or `cancelled`; PR-specific outcomes (`pr_created`, `draft_pr`, `wont_do`, `needs_refinement`, `conflict_restart`) are available only via the HTTP completion path.

#### Phase normalization

`PipelineTelemetry.NormalizeRunPhase` maps the raw phase key of an agent invocation (the `PhaseBreakdown` key) onto the closed `phase` set. Invocations without a phase key are mapped from their description by `NormalizePhaseDescription`.

| Raw phase key | `phase` |
|---------------|---------|
| `analysis` | `analysis` |
| `analysis_review` | `analysis_review` |
| `codegen`, `code_gen`, `code generation` | `codegen` |
| `review`, `code_review`, `review_{reviewer}`, `review_summary`, `follow_up_{reviewer}`, `fix` (review fix iterations) | `review` |
| `quality_gate` (quality gate fix agent and gate processes), `qgc_retry_agent` (older agents) | `quality_gate` |
| `acceptance_criteria` | `acceptance_criteria` |
| `pr_description` | `pr_description` |
| `reflection` | `reflection` |
| `decomposition`, `decomposition_*` | `decomposition` |
| anything else, empty, null (incl. `unknown` from older agents, and the triage phases `triage`, `triage_review`, `triage_refinement`, which `run_type=triage` already separates) | `other` |

#### Counting events exactly

Counters are not seeded at startup. Seeding every closed-tag combination with `Add(0)` produced about 1,600 zero-value series per API replica and about 200 per Scheduler and Web replica. Every restart created them again under a new `service_instance_id`, and Grafana Cloud kept counting the old copies for 1–3 h, so the daily deploy pushed the stack over its in-memory series limit and metrics were dropped for hours. Do not add `Add(0)` seeding for new counters.

`increase()` and `rate()` miss the first increment of a series that starts in a new process. To count events exactly, take each instance's maximum in the window minus its value at the window start, which is 0 for a series that started inside the window:

```promql
sum by (outcome) (
  max by (run_type, outcome, failure_reason, pipeline_project_name, provider, model, service_instance_id) (max_over_time(pipeline_run_outcomes_total[$__range]))
  - (
    max by (run_type, outcome, failure_reason, pipeline_project_name, provider, model, service_instance_id) (last_over_time(pipeline_run_outcomes_total[6h] offset $__range))
    or
    max by (run_type, outcome, failure_reason, pipeline_project_name, provider, model, service_instance_id) (max_over_time(pipeline_run_outcomes_total[$__range])) * 0
  )
)
```

The inner `max by (…, service_instance_id)` must list every tag of the counter. It only merges the copies of one instance that arrive with and without `k8s_*` labels.

#### Histogram buckets

| Metric | Boundaries |
|--------|-----------|
| `pipeline.run.duration` (s) | 60, 300, 600, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 21600, 28800, 43200 |
| `pipeline.run.step.duration` (s) | 5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800 |
| `pipeline.run.ci.wait` (s) | 60, 300, 600, 900, 1800, 3600, 7200, 14400 |
| `pipeline.run.rate_limit_utilization` | 0.1, 0.25, 0.5, 0.75, 0.9, 0.95, 1.0 |
| `pipeline.pull_requests.time_to_merge` (s) | 300, 600, 1800, 3600, 7200, 14400, 28800, 43200, 86400, 172800, 345600, 604800 |
| `workdistribution.dispatch_latency_seconds` | 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400 |
| `workdistribution.timeout_execution_age_seconds` | 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600 |
| `workdistribution.pod_start_seconds` | 5, 10, 20, 30, 60, 120, 300, 600 |

The chat histograms use the SDK's default buckets.

### Prompt Cache and Per-Phase Token Data

Each `PipelineRun` accumulates token and cost data beyond the simple totals. This richer data is available on `PipelineRunSummary` objects returned by `GET /api/pipeline-runs` and `GET /api/export/runs.json`, and is visible in the UI sidebars.

| Field | Description |
|-------|-------------|
| `CacheReadTokens` | Tokens served from the upstream LLM's prompt cache across all agent invocations in this run |
| `CacheWriteTokens` | Tokens written into the prompt cache across all agent invocations in this run |

Cache token fields are populated for **OpenCode** and **Claude Code** agents. Kiro CLI does not report token or cost data at all (verified 2026-09-29: `KiroCliAgentProvider` returns `Usage = null` and `Cost = null`), so `pipeline.run.tokens` and `pipeline.run.cost_usd` stay at zero for Kiro runs while `agent_sessions` and `agent_time` (measured by the agent harness) are populated.

**Claude Code usage:** the provider reads the `result` event of `claude -p --output-format stream-json` and reports, per call, input / output / reasoning / cache tokens, the CLI's cost estimate (`total_cost_usd`), turns, API time, web searches, a per-model breakdown and subscription rate-limit readings (`rate_limit_event`). A resumed session reports the whole conversation's totals, so the provider subtracts what it saw at the end of the previous call. The per-model breakdown and rate-limit readings are logged per call (`Claude Code model usage` / `Claude Code rate limit` log lines); the totals feed the counters above.

`PipelineRunSummary.PhaseBreakdown` is keyed by the raw phase key (e.g. `analysis`, `codegen`, `review_correctness`, `quality_gate`). Each entry carries `Tokens` and `Cost`; the Run page (`/runs/{id}`) renders it as the "Cost & token breakdown" table (the pipeline sidebar shows the same data as "Cost Breakdown"). It is `null` for runs recorded before the feature existed. The run page also shows the tool calls per server ("MCP & web tools") and per phase (the "Tools" column).

### MCP tool calls

Each phase entry in `PipelineRunSummary.PhaseBreakdown` also carries:

- **`ToolCalls`** — a `Dictionary<string, McpCallCounts>` keyed by `server/tool` (e.g. `"sonarqube/get_issues"`, `"web/search"`). Each value holds the total call count and the count of failed calls split by cause: `FailedConnection`, `FailedAuth`, `FailedOther`, `FailedUnknown`. Built-in web tools use the `web` server prefix (`web/search`, `web/fetch`). Built-in file and shell tools are not counted.
- **`UnavailableMcpServers`** — a `Dictionary<string, string>` mapping server name to a `McpServerStatuses` value (`"failed"` or `"needs-auth"`).

`PipelineRunSummary.McpServers` holds the names of the MCP servers that were configured (and enabled) for the run, regardless of whether they were called. This lets the UI show a server that was configured but never called.

**Claude Code** populates these fields from `tool_use` blocks in `assistant` events (calls), `tool_result` blocks in `user` events (failures), and the `mcp_servers` array of the `init` system event (unavailable servers).

Two Warning log lines are emitted from agent pods (starting when each provider sub-issue lands):

| Event | Message template | Properties |
|---|---|---|
| Failed MCP call | `MCP call failed: {McpServer}/{McpTool} ({McpFailureCategory}): {McpError}` | `McpServer`, `McpTool`, `McpFailureCategory`, `McpError` |
| Unavailable server | `MCP server unavailable: {McpServer} ({McpServerStatus})` | `McpServer`, `McpServerStatus` |

`McpError` is masked (secrets replaced with `***`) and cut to 200 characters before logging.

**Kiro** populates `ToolCalls` from kiro-cli's stderr `[tool]` lines. Limitations: the CLI reports no failure reason (all failures count as `FailedUnknown`); status lines carry no call ID, so a failure is only attributed to a key when exactly one call was outstanding at the time (parallel calls are counted but not attributed); `UnavailableMcpServers` is always null for Kiro.

**LogQL example** — total failed calls by server and cause over 24 hours:

```logql
sum by (McpServer, McpFailureCategory) (count_over_time({service_name="coding-agent-worker"} |= "MCP call failed" [24h]))
```

There is no metric for tool calls, to keep active series low.

The **Attention page** (`/attention`) surfaces MCP server health automatically. For each server, the pipeline examines the runs loaded by the page (newest first). The first run that is not neutral (i.e., the server was either called or unavailable) determines the outcome: if that run shows the server as unavailable in any phase, or shows every call failing with a non-"other" cause (connection, auth, or unknown), the server appears in an "MCP servers" card with the reason and the count of consecutive problem runs. A server is removed from the card as soon as a newer OK run exists for it.

**OpenCode** sources tool-call data from SSE `message.part.updated` tool parts (call counts and error texts) and server statuses from `GET /mcp`; when `GET /mcp` is unavailable the provider falls back to the configured server names in `McpConfigPath` to perform tool-key matching.

### LLM Usage Telemetry Architecture

1. **Agent side** — `AgentStallMonitor.ExecuteWithMonitoringAsync` creates a per-invocation span (see "Session Spans" below) and adds the session count and elapsed time to `PipelineRun.Metrics.PhaseBreakdown` under the invocation's phase key. The calling phase executors add the tokens and cost with `AccumulateTokenUsage` under the same key.
2. **Wire** — `LocalPipelineExecutor.BuildPayloadBase` converts `PhaseBreakdown` into `JobCompletionPayload.PhaseBreakdown` for transmission to the API. The API copies it onto its own run in `JobCompletionMapper` and keeps it in the Redis run hash (`phaseBreakdown`), so it reaches `PipelineRunSummary.PhaseBreakdown`.
3. **API side** — `WorkItemStatusTransitionService.EmitTerminalStatusTelemetryAsync` reads the breakdown and records the `pipeline.run.*` usage counters with the normalized `phase` tag.

### Session Spans (GenAI Semantic Conventions)

Every agent CLI invocation creates a child span of the current step span:

| Attribute | Value |
|-----------|-------|
| span name | `invoke_agent {phase}` (e.g. `invoke_agent review`) |
| `gen_ai.operation.name` | `invoke_agent` |
| `gen_ai.provider.name` | `kiro`, `opencode` or `claude` |
| `gen_ai.request.model` | model name if configured, omitted otherwise |
| `pipeline.phase` | normalized phase (same value as the metric `phase` tag) |
| `pipeline.phase_key` | raw phase key (e.g. `review_correctness`), when the caller passes one |
| `agent.session.resumed` | `true` if `UseResume=true` or `ResumeSessionId` is set |
| `agent.exit_code` | integer exit code from the CLI process |
| `gen_ai.usage.input_tokens` / `gen_ai.usage.output_tokens` | OpenCode and Claude Code; omitted for Kiro |
| `gen_ai.usage.total_tokens`, `gen_ai.usage.reasoning_tokens`, `gen_ai.usage.cache_read_input_tokens`, `gen_ai.usage.cache_creation_input_tokens` | when > 0 |
| `agent.cost_usd` | provider-reported cost when known |
| `agent.billing` / `agent.turns` / `agent.api_duration_s` / `agent.web_search_requests` / `agent.error_category` | Claude Code usage details (`agent.error_category` only on provider-side failures) |

Stall events are recorded as span events:

| Event name | When | Tags |
|------------|------|------|
| `agent.stall_warning` | agent silence exceeds `stallWarningInterval` | `silence_minutes` |
| `agent.stall_kill` | agent killed due to silence timeout | `silence_minutes`, `kill_timeout_minutes` |
| `agent.process_death` | agent process died unexpectedly | `pid` |

Stall kills and process deaths are also reported to the API, which records `pipeline.run.agent_stalls`.

### Reliable sources by use case

| Use case | Query |
|----------|-------|
| Terminal runs by outcome | `sum by (outcome) (increase(pipeline_run_outcomes_total[24h]))` |
| Failed runs (alerts) | `sum(rate(pipeline_run_outcomes_total{outcome=~"failed\|timeout"}[5m]))` |
| Run duration percentiles | `pipeline_run_duration_seconds` |
| Step duration percentiles | `pipeline_run_step_duration_seconds{step=...}` |
| Sub-issues created/failed | `increase(pipeline_run_sub_issues_total[24h])` |
| Brain update rates | `increase(pipeline_run_brain_updates_total[24h])` |

**Removed metrics.** `workdistribution.workitems_terminated` and `workdistribution.job_execution_duration_seconds` recorded the same transition as `pipeline.run.outcomes` / `pipeline.run.duration` and were removed; use the `pipeline.run.*` pair. The agent-side `quality_gate.*` (`retries`, `duration`, `evaluations`, `process.*`, `stall.*`, `external_ci.duration`, `post_pr_ci.duration`), `agent.heartbeat.failures`, `agent.reconnections`, `agent.signalr.failures` and `brain.push.retries` instruments were removed because agent pods export no metrics, so none of them ever reached Prometheus after issue #2980; `pipeline.run.quality_gate.results`, `pipeline.run.agent_stalls` and `pipeline.run.ci.wait` are the API-side replacements.

### Alerts

The Helm chart defines its alert rules in `helm/coding-agent-automation/templates/prometheusrules.yaml`. They render only when `monitoring.prometheusRules.enabled` is `true` (default `false`) and need the Prometheus Operator, which provides the `PrometheusRule` resource. A deployment that sends its metrics to Grafana Cloud and alerts there must create the same rules in Grafana Cloud: that is outside this repository.

| Alert | Expression | For | Severity | Fires when |
|-------|------------|-----|----------|------------|
| `WorkItemNoJobTemplate` | `sum(increase(workdistribution_dispatch_attempts_total{reason="no_template"}[10m])) > 0` | 10m | `warning` | The dispatcher keeps deferring work because no job template matches an agent selector |

**`WorkItemNoJobTemplate` covers the permanent no-template case.** A work item's agent selector is the job template of the agent profile it was routed to. A work item whose selector names no job template is deferred on every dispatch cycle and stays Pending. The templates come from `jobTemplates[]` in the Helm values and change only with a redeploy, so this does not clear by itself. It is not one of the transient dispatch failures (no free credential PVC, a Kubernetes error) that the next cycle retries without an alert; the rule fires only after the deferrals have gone on for 10 minutes. It needs no threshold and no zero baseline: the dispatcher retries every cycle, so the series keeps incrementing after it first appears, and `increase()` over the window sees that. It counts items the dispatcher attempts in a cycle.

The counter has no selector label. The API logs `DispatchPendingWorkItem: no job template for selector <selector>` with the selector. To fix it, add a `jobTemplates[]` entry for that selector and redeploy. Pointing the agent profile at an existing job template (Settings → Agents → Agent Profiles) or routing the work to another profile (Settings → Agents → Routing) helps only work created afterwards: queued work keeps the route it was created with, so cancel the waiting items and queue them again. The Agent Profiles page and the repository pages flag a profile whose job template the Helm values do not define.

**`WorkItemsPendingTooLong` only sees items that dispatch.** It is a quantile of `workdistribution_dispatch_latency_seconds`, which the API records when an item moves from Pending to Dispatched. An item that never dispatches never enters that histogram, so the alert cannot fire for it.

**There is no pending-age alert.** A threshold on `workdistribution.pending.oldest_age_seconds` would fire on waits that are not a fault: the lowest dispatch tier can wait many hours and then dispatch. The `no_template` counter is the signal for the permanent case.

**There is no routing refusal alert.** `pipeline.routing.refusals` has no alert rule; the [Routing](#routing) section shows how to find refusals.

### Routing

The API resolves a work item's agent route when it creates the work item and checks it again when an agent claims it (see [Agent Routing](agent-routing.md)). Every refusal counts in `pipeline.routing.refusals` with its `stage`:

- `create`: `POST /api/work-items` answers `422` and no work item exists;
- `dispatch`: a work item a previous release queued without a route, which the dispatcher fails (`no_route` on `workdistribution.dispatch.attempts`);
- `claim`: `GET /api/work-items/{id}/assignment` answers `410` and the work item fails with the reason.

The span that creates a work item and the span of `GET /api/work-items/{id}/assignment` carry the route:

| Attribute | Value |
|-----------|-------|
| `pipeline.agent_profile` | The profile's display name |
| `pipeline.model` | The route's model; `auto` lets the tool choose |
| `pipeline.effort` | The route's effort; `auto` is the tool's default |
| `pipeline.route_source` | `repository`, `global task row`, `global default`; `claim` for work queued before agent routing and routed when an agent claimed it, `dispatch` for work the dispatcher routed |
| `pipeline.route_refusal` | Set on a refusal instead, to the `reason` of `pipeline.routing.refusals`. The span status is Error with the refusal text |

The routing log lines (the route or refusal of every work item creation, a refused claim, the claim-time route of work queued before agent routing, and the warnings and summary of the configuration move) carry structured properties, so Loki queries do not depend on the message text: `TaskType`, `TemplateId`, `TemplateName`, `AgentProfileId`, `AgentProfile`, `Model`, `Effort`, `RouteSource`, `RefusalReason` (the counter's `reason`) and `RefusalText` (the text the work item or the API answer shows). They never contain tool settings or MCP environment values. All routing refusals:

```logql
{service_name="coding-agent-api"} | RefusalReason != ""
```

## Traces

### Span Noise Filters

Four high-volume span categories are kept out of the export (~70% of raw daily span volume). `OtelNoiseFilter` and `OtelNoiseSpanProcessor` (`src/CodingAgent.Infrastructure.Common/Telemetry/OtelNoiseFilter.cs`) are wired into every host: **API**, **Web**, **Scheduler**, **Job Controller** and the **agent**.

| Noise | What is dropped |
|-------|-----------------|
| Health-probe and polling endpoints | Server spans for the exact paths `/healthz`, `/readyz` and the browser's polls: the loop status `/api/ui/loop/status`, the API health `/api/ui/health`, the sidebar's agent counts `/api/ui/agents` and the session heartbeat `/bff/session`; sub-paths like `/healthz/detail` and `/api/ui/agents/credential-pool` are kept |
| Static client files | Server spans for the web host's `/_framework/*` (WebAssembly runtime and assemblies) and `/_content/*` files, any `.css`, `.js`, `.wasm` or `.woff2` file (including the `CodingAgent.Web.styles.css` bundle and the fonts), and `/favicon.svg` |
| Kubernetes API server calls | Client spans to `KUBERNETES_SERVICE_HOST` (in-cluster) or `kubernetes.default.svc`, mostly leader-election leases (~387k spans per day) |
| Chatty agent-hub spans | Hub methods ending in `/Heartbeat` or `/ReportOutputLines` |

They are dropped at two points:

- **When the span starts:** the instrumentation filters (`AspNetCoreTraceInstrumentationOptions.Filter`, `HttpClientTraceInstrumentationOptions.FilterHttpRequestMessage`) and `OtelNoiseSpanProcessor.OnStart` (by name). A probe request dropped here also keeps the spans it creates underneath out of the trace. For a poll the web host forwards, that covers the forwarder's client span, and the API gets a `traceparent` without the sampled flag, so it records no span either (`BffTelemetryTests`).
- **When the span ends:** `OtelNoiseSpanProcessor.OnEnd` checks the finished span's name and tags (`url.path` on server spans, `server.address` on client spans) and clears its `Recorded` flag, so the batch exporter skips it. This check does not depend on the instrumentation callbacks having run. The processor must be added **before** `AddOtlpExporter`, because processors run in the order they are added.

`OnEnd` also renames kept outbound HTTP spans from the bare method to `"{METHOD} {host}"` (e.g. `"GET api.github.com"`): host-level detail without path cardinality.

Custom spans come from the `CodingAgent.Pipeline` ActivitySource.

### Web UI Requests

The UI runs in the browser and calls only the web host, which forwards `/api/ui/**` and `/hubs/ui` to the API ([Authentication](authentication.md#the-web-host-and-the-browser-client)).

- **Traces:** each forwarded request has a server span on the web host (`coding-agent-web`, route `/api/ui/{**rest}` or `/hubs/ui`) with the forwarder's client span under it, and the trace continues into the API's server span. With Faro tracing on, the browser's fetch span starts the trace, so one trace covers browser → web host → API. The polls produce no span anywhere (see the noise table above).
- **Metrics:** the web host has no meter of its own. Its `http.server.request.duration` covers the forwarded traffic under the routes `/api/ui/{**rest}` and `/hubs/ui`; a failed forward shows there as `502` (API unreachable) or `504` (5 minutes without activity). For latency per API route, use `coding-agent-api`: the web host's `/api/ui/{**rest}` row is the same calls plus one hop.
- **Logs:** the web host's own logs (sign-in, forwarding errors, startup) go to Loki through Serilog, like every host's. The forwarder (`Yarp`) logs from Warning up: a failed forward (`502`, `504`) is logged, a call that succeeds or that the browser cancels is not. On the API, a call the browser aborts (it navigated away) ends as `499` and its request log line is Debug, not a `500` Warning. The UI's component logs stay in the browser console; only warnings and errors reach Grafana, through Faro.

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
        │       ├── WaitForCi  (pre-PR external CI, pipeline.ci_path=pre_pr)
        │       ├── CreatePullRequest
        │       │   ├── GeneratePrDescription
        │       │   ├── Reflection
        │       │   ├── BrainSyncPostRun
        │       │   └── FeedbackCollection
        │       └── WaitForCi  (post-PR external CI, pipeline.ci_path=post_pr)
        └── PrePrCleanup
```

Every agent CLI call inside a step adds an `invoke_agent {phase}` span (see [Session Spans](#session-spans-genai-semantic-conventions)).

**Span structure rules:**

- `PipelineStepRunner` creates one `Step {StepName}` span per step with `pipeline.step` and `pipeline.run_id` tags.
- Steps that start their own inner span (e.g. `CloneRepository`, `AnalyzeIssue`) nest that inner span inside the runner-created `Step` span — giving two span levels per step.
- Steps without an inner span (`FetchIssue`, `DetectRework`, `DownloadIssueImages`, etc.) have only the runner-created span.
- `VerifyBaseline` is a named inner span with `pipeline.run_id` and `pipeline.issue` tags.
- `WaitForCi` is emitted for both the pre-PR CI path (`QualityGateExecutor.RunExternalCiPollAsync`) and the post-PR CI path (`CiPollingCoordinator.WaitForPostPrCiAsync`). Tags: `pipeline.run_id`, `pipeline.run_type`, `pipeline.ci_path` (`pre_pr` or `post_pr`), `pipeline.ci_status`, `pipeline.ci_infra_retries`.
- `PrePrCleanup` is emitted by `PipelineCleanup.RunAsync` as a child of `ExecutePipeline`.

**Error status rules:**

- A step span (`Step X`) has Error status only when that step operation failed (unhandled exception from `ExecuteAsync`, or `TryCriticalAsync` failure).
- Non-critical failures (`TryNonCriticalAsync`) add an `exception` event with `pipeline.non_critical=true` to the current step span. The span status remains Unset — no Error.
- `ExecutePipeline` has Error status only when the run ends `PipelineStep.Failed`. All other terminal states (`Completed`, `ConflictRestart`, `PrMerged`, `PrClosed`, `Cancelled`) leave it Ok or Unset.

This is achieved by capturing the W3C `traceparent` from the API request span at WorkItem creation time (`WorkItemDispatchEndpoints.cs`, `DispatchWorkItemService.cs`), storing it in `WorkItemEntity.TraceParent`, and injecting it as the `TRACEPARENT` environment variable in the K8s Job (`DispatchLifecycleService.CreateK8sJobAsync` → `JobSpecBuilder.Build`). The agent process restores this context in `WorkItemAgentService.ExecuteAsync` and starts `WorkItemAgent.Execute` as a child.

### Scheduler Spans

The Scheduler emits spans only when actual work occurs — idle ticks produce no spans.

| Span Name | Tags | Emitter |
|-----------|------|---------|
| `Dispatch.Attempt` | `work_item_id`, `agent_selector`, `result` | `WorkItemDispatchLoop.DispatchItemAsync` (called from `PollAndDispatchAsync`) — one span per item dispatched; result is `Dispatched`, `PermanentRejection`, `Transient`, plus `Cancelled` (shutdown) and `Exception` (unexpected client error) |
| `Loop.Enqueue` | `issue_identifier`, `template_name` | `DispatchScheduler.DispatchIssueRoundAsync` — one span per issue the closed loop enqueues |
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

### API Spans

| Span Name | Tags | Emitter |
|-----------|------|---------|
| `Hub.ReportJobCompleted` | `job_id`, `success` | `AgentJobLifecycleService.HandleJobCompletedAsync` — the agent's completion report |
| `TokenVending.GenerateToken` | — | `TokenVendingService` — GitHub App installation token minted for an agent |
| `Chat.Dispatch` | `agent_selector` | `ChatJobDispatcher.DispatchChatPodAsync` — chat pod start |
| `Chat.Terminate` | `agent_id` | `ChatJobDispatcher.TerminateChatSessionAsync` — chat pod stop |

The API creates no span for the run itself; the run's trace is the agent's `WorkItemAgent.Execute` → `ExecutePipeline` tree, parented to the API request that created the WorkItem. The request spans of `POST /api/work-items` and `GET /api/work-items/{id}/assignment` carry the route attributes (`pipeline.agent_profile`, `pipeline.model`, `pipeline.effort`, `pipeline.route_source`, or `pipeline.route_refusal`): see [Routing](#routing).

### Pipeline Spans

All emitted by the agent pod.

| Span Name | Tags | Emitter |
|-----------|------|---------|
| `ExecutePipeline` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.project_id`, `pipeline.project_name`, `pipeline.final_step`, `pipeline.agent_id`, `pipeline.failure_reason`, `pipeline.cancelled` | Top-level span wrapping the full pipeline execution (`LocalPipelineExecutor`). Error status set only when run ends `Failed`. |
| `invoke_agent {phase}` | see [Session Spans](#session-spans-genai-semantic-conventions) | One per agent CLI call (`AgentStallMonitor`) |
| `Step {StepName}` | `pipeline.step`, `pipeline.run_id` | Runner-created span per step (emitted by `PipelineStepRunner`). Error status set on unhandled exception. Non-critical failures add exception event, no Error. |
| `CloneRepository` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.repository` | Repository clone into workspace (child of `Step CloneRepository`) |
| `VerifyBaseline` | `pipeline.run_id`, `pipeline.issue` | Agent health check + workspace baseline verification (child of `Step VerifyBaseline`). Error status set on fatal health-check failure only. |
| `CreateBranch` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.branch_name` | Branch creation or checkout |
| `SyncBrainPreRun` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type`, `pipeline.brain_sync.skipped` | Brain repository sync (pre-run) |
| `RunEnvironmentSetup` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Environment setup commands |
| `CloneProjectRepositories` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Additional project repo clones |
| `CloneProjectReviewRepositories` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Additional project repo clones for review runs |
| `AnalyzeIssue` | `pipeline.run_id`, `pipeline.issue`, `pipeline.analysis.continue` | Issue analysis (confidence gate) |
| `GenerateCode` | `pipeline.run_id`, `pipeline.issue`, `pipeline.is_rework` | Code generation / rework |
| `RunQualityGates` | `pipeline.run_id`, `pipeline.issue` | Quality gate execution |
| `QualityGate.Compilation` | `gate_name` | Compilation command execution (child of RunQualityGates) |
| `QualityGate.Tests` | `gate_name` | Test command execution (child of RunQualityGates) |
| `WaitForCi` | `pipeline.run_id`, `pipeline.run_type`, `pipeline.ci_path`, `pipeline.ci_status`, `pipeline.ci_infra_retries` | External CI polling span. `pipeline.ci_path` is `pre_pr` (inside `RunExternalCiPollAsync`) or `post_pr` (inside `WaitForPostPrCiAsync`). |
| `ReviewCode` | `pipeline.run_id`, `pipeline.issue` | Multi-agent code review |
| `CodeReview.Iteration` | `pipeline.run_id`, `pipeline.issue`, `code_review.iteration`, `code_review.max_iterations`, `code_review.parallel` | Single code review iteration (child of ReviewCode) |
| `CodeReview.Agent` | `pipeline.run_id`, `pipeline.issue`, `code_review.agent_name`, `code_review.iteration`, `code_review.exit_code`, `code_review.findings_critical`, `code_review.findings_warning`, `code_review.findings_suggestion`, `code_review.has_findings_file` | Individual review agent execution (child of CodeReview.Iteration) |
| `CreatePullRequest` | `pipeline.run_id`, `pipeline.issue`, `pipeline.pr.is_draft` | PR creation, then the post-PR sequence (description, reflection, brain sync, feedback) |
| `GeneratePrDescription` | `pipeline.run_id`, `pipeline.issue` | Agent-generated PR description |
| `PostReviewFindings` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting review findings to PR |
| `WritePrConversationContext` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Writing PR conversation context to workspace |
| `ExtractLinkedIssues` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Extracting linked issues from PR |
| `Decomposition` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Sub-issue generation (Phase 2) |
| `DecompositionAnalysis` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Epic analysis (Phase 1) |
| `PostDecompositionPlan` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting decomposition plan comment |
| `PostDecompositionSummary` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting creation summary comment |
| `CreateSubIssues` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Creating sub-issues on GitHub |
| `Reflection` | `pipeline.run_id` | Post-PR reflection prompt |
| `BrainSyncPostRun` | `pipeline.run_id` | Brain repository sync after run |
| `FeedbackCollection` | `pipeline.run_id` | Structured feedback collection |
| `PrePrCleanup` | `pipeline.run_id` | Workspace deletion + reporter disposal (child of ExecutePipeline, emitted by `PipelineCleanup.RunAsync`) |
| `WorkItemAgent.Execute` | `work_item_id`, `agent_id` | Top-level span for K8s work-item agent lifecycle (connect, execute, report). Parent is the API request that created the WorkItem (via TRACEPARENT env var). |
| `ExecuteConsolidation` | `pipeline.run_id`, `pipeline.consolidation_type` | Top-level span wrapping a consolidation run (brain or refactoring) |
| `BrainConsolidation.Clone` | `pipeline.run_id` | Brain repo clone during consolidation |
| `BrainConsolidation.AgentExecution` | `pipeline.run_id` | Main agent LLM call for brain consolidation |
| `BrainConsolidation.DiffGeneration` | `pipeline.run_id` | Diff summary agent call (LLM execution) |
| `BrainConsolidation.AdversarialReview` | `pipeline.run_id` | Adversarial review of brain consolidation |
| `BrainConsolidation.Commit` | `pipeline.run_id` | Committing brain consolidation changes |
| `BrainConsolidation.Push` | `pipeline.run_id` | Pushing brain consolidation changes |
| `RefactoringDetection.Clone` | `pipeline.run_id` | Code repo clone for refactoring detection |
| `RefactoringDetection.HotspotAnalysis` | `pipeline.run_id` | Git hotspot analysis |
| `RefactoringDetection.Phase0.ContextExtraction` | `pipeline.run_id` | Project convention extraction |
| `RefactoringDetection.Phase1.StructuralDebt` | `pipeline.run_id` | Structural debt detection agent (one of three parallel detection agents) |
| `RefactoringDetection.Phase1.Correctness` | `pipeline.run_id` | Correctness detection agent (one of three parallel detection agents) |
| `RefactoringDetection.Phase1.DesignConsistency` | `pipeline.run_id` | Design consistency detection agent (one of three parallel detection agents) |
<!-- TODO: "one of three" is now stale (four Phase 1 agents since issue #3582). Update to "one of four parallel detection agents" in all three rows above. Also add a missing row: `RefactoringDetection.Phase1.TestGaps` | `pipeline.run_id` | Test gap detection agent (one of four parallel detection agents). Also update the span tree below (~line 602-604). See review finding #5. -->
| `RefactoringDetection.Phase2.Aggregation` | `pipeline.run_id` | Aggregation and prioritization of the detected proposals |
| `RefactoringDetection.AdversarialReview` | `pipeline.run_id` | Adversarial review of refactoring proposals |
| `RefactoringDetection.CreateIssues` | `pipeline.run_id`, `pipeline.proposal_count`, `pipeline.rejected_proposal_count` | Creating GitHub issues for proposals |

### Tag Schema

| Tag | Values | Description |
|-----|--------|-------------|
| `pipeline.run_id` | UUID | Unique identifier for the pipeline run |
| `pipeline.issue` | string | Issue or PR identifier (e.g., `42`) |
| `pipeline.run_type` | `Implementation`, `Review`, `DecompositionAnalysis`, `Decomposition` | Run type (**PascalCase** — differs from metric tags) |
| `pipeline.final_step` | step name or `Cancelled` | Last step reached before completion |
| `pipeline.agent_id` | Job name | Agent identity (`AGENT_ID`, the K8s Job name; the container hostname only when `AGENT_ID` is unset), on `ExecutePipeline` |
| `pipeline.failure_reason` | `FailureReason` member name (e.g. `QualityGateExhausted`) | Set when the run ends without completing and a category is known (on `ExecutePipeline`) |
| `pipeline.cancelled` | `true` | Set when the run was cancelled (on `ExecutePipeline`) |
| `pipeline.phase` | `analysis`, `analysis_review`, `codegen`, `review`, `quality_gate`, `acceptance_criteria`, `pr_description`, `reflection`, `decomposition`, `other` | Normalized phase on `invoke_agent` spans; same values as the metric `phase` tag |
| `pipeline.repository` | string | Repository name (on `CloneRepository` span) |
| `pipeline.branch_name` | string | Branch name created or checked out (on `CreateBranch` span) |
| `pipeline.brain_sync.skipped` | `true`/`false` | Whether brain sync was skipped due to missing provider (on `SyncBrainPreRun` span) |
| `pipeline.analysis.continue` | `true`/`false` | Whether analysis passed the confidence gate |
| `pipeline.is_rework` | `true`/`false` | Whether this is a rework run (linked PR exists) |
| `pipeline.pr.is_draft` | `true`/`false` | Whether the PR was created as a draft |
| `pipeline.project_id` | UUID | Project identifier — set only on **spans** (not on metric tags; use `pipeline.project_name` for metrics since it is 1:1 with `pipeline.project_id`) |
| `pipeline.project_name` | string | Project display name |
| `pipeline.consolidation_type` | `BrainConsolidation`, `RefactoringDetection` | Consolidation run type (on `ExecuteConsolidation` span) |
| `pipeline.agent_profile`, `pipeline.model`, `pipeline.effort`, `pipeline.route_source` | string | The work item's agent route, on the API's creation and claim request spans; see [Routing](#routing) |
| `pipeline.route_refusal` | `no_route`, `profile_unavailable`, `no_tool`, `template_missing`, `template_changed`, `config_collision` | Set instead of the route when it is refused; the span status is Error |
| `code_review.iteration` | integer | Code review iteration index (1-based) |
| `code_review.max_iterations` | integer | Total configured review iterations |
| `code_review.parallel` | `true`/`false` | Whether review agents ran in parallel |
| `code_review.agent_name` | string | Review agent name (on `CodeReview.Agent` span) |

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

Every host reads `OTEL_SERVICE_NAME` and falls back to a fixed name. `service.version` comes from `SERVICE_VERSION` (set in the Dockerfiles to the git commit SHA); when it is unset the web host uses the assembly version and the other hosts and the agent use `local`. Traces and metrics get `deployment.environment` only from `OTEL_RESOURCE_ATTRIBUTES` (`otel.resourceAttributes` in the Helm values). The log sink also falls back to the host environment name, then `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT`, then `Production`.

| `service.name` | Component | Port | How configured |
|----------------|-----------|------|----------------|
| `coding-agent-web` | Web host (BFF of the WebAssembly UI) | — | `otel.webServiceName` in `values.yaml` (default `coding-agent-web`); fallback when `OTEL_SERVICE_NAME` is unset |
| `coding-agent-api` *(default)* or override | REST/WebSocket API | Port 8080 | Set via `otel.apiServiceName` in `values.yaml` (default: `coding-agent-api`). Override if you need a different name. |
| `coding-agent-jobcontroller` | Job Controller | Port 8080 | Fixed fallback; overridable via `OTEL_SERVICE_NAME` env var |
| `coding-agent-scheduler` | Scheduler | Port 8080 | Fixed fallback; overridable via `OTEL_SERVICE_NAME` env var |
| `coding-agent-worker` | Agent pods (K8s Jobs) | — | Set unconditionally by `JobSpecBuilder` via `OTEL_SERVICE_NAME` on each Job pod. Per-run identity exposed via `service.instance.id` = K8s Job name (e.g., `caa-7f3a9b2e`), set in `OTEL_RESOURCE_ATTRIBUTES` |

> **Run identity in `service.instance.id`:** Agent pods all share the stable `service.name=coding-agent-worker`. The individual run is identified by `service.instance.id` (set to the Kubernetes Job name, e.g. `caa-abcdef12`) in `OTEL_RESOURCE_ATTRIBUTES`. This keeps service cardinality stable — queries no longer need regex to match per-run service names.

> **⚠️ Breaking change (upgrade from pre-2969):** The API's `service.name` changed from `coding-agent-web` to `coding-agent-api`. Update any Grafana dashboards or alerts that filter on `service.name="coding-agent-web"` for API traffic. The "Recent Pipeline Traces" panel is not affected (updated in #2966).

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

After a run finishes, its outcome appears in `pipeline_run_outcomes_total`:

```promql
count by (run_type, outcome) (pipeline_run_outcomes_total)
```

Right after a restart this returns nothing until the first run finishes, because counters are not seeded with zero series. For totals over a window, use the query in "Counting events exactly".

`ApiTelemetryExportTests` (`tests/CodingAgent.Api.IntegrationTests`) builds the real API host with an in-memory exporter and asserts that no counter series are exported before the first event and that the noise spans are not exported.

### Traces

When connected to a backend (Grafana Tempo, Jaeger, etc.), search for traces with:

- Service: `coding-agent-worker`
- Operation: `ExecutePipeline` (or `WorkItemAgent.Execute`, its parent)

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
│       ├── WaitForCi  (pre-PR external CI, pipeline.ci_path=pre_pr)
│       ├── CreatePullRequest
│       │   ├── GeneratePrDescription
│       │   ├── Reflection
│       │   ├── BrainSyncPostRun
│       │   └── FeedbackCollection
│       └── WaitForCi  (post-PR external CI, pipeline.ci_path=post_pr)
└── PrePrCleanup
```

For a review run:

```
ExecutePipeline
├── Step ExtractLinkedIssues
│   └── ExtractLinkedIssues
├── Step ReviewCode
│   └── ReviewCode
├── Step PostReviewFindings
│   └── PostReviewFindings
└── PrePrCleanup
```

For a decomposition run (Phase 1):

```
ExecutePipeline
├── Step DecompositionAnalysis
│   └── DecompositionAnalysis
├── Step PostDecompositionPlan
│   └── PostDecompositionPlan
└── PrePrCleanup
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
├── RefactoringDetection.Phase0.ContextExtraction
├── RefactoringDetection.Phase1.StructuralDebt
├── RefactoringDetection.Phase1.Correctness
├── RefactoringDetection.Phase1.DesignConsistency
├── RefactoringDetection.Phase2.Aggregation
├── RefactoringDetection.AdversarialReview
└── RefactoringDetection.CreateIssues
```


## Frontend Observability (Grafana Faro)

In addition to backend OTLP telemetry, the browser UI (Blazor WebAssembly) supports [Grafana Faro](https://grafana.com/oss/faro/) for frontend Real User Monitoring (RUM).

When `Faro__CollectorUrl` is set, `faro-init.js` (loaded in `App.razor`) asynchronously loads the Faro Web SDK and its tracing from the unpkg.com CDN and starts collecting:
- Page load performance and web vitals
- JavaScript errors and unhandled promise rejections
- Console warnings and errors. The lower levels stay in the browser (`consoleInstrumentation.disabledLevels`), so the UI's Information logs never leave it.
- Fetch spans for the UI's calls, which start the trace that continues into the web host and the API. The polls (`/bff/session`, `/api/ui/health`, `/api/ui/loop/status`, `/api/ui/agents`) are left out (`ignoreUrls`), as in the server's noise filter.
- The UI's notifications (`NotificationFaroBridge`): errors as Faro errors, the others as Faro logs

Faro's own session tracking marks when a browser session starts; there are no circuit events.

When the env var is absent or empty, Faro is disabled and the `faroApi` stub no-ops silently — no errors are thrown and there is no page-load impact.

See [Configuration — Frontend Observability](configuration.md#frontend-observability-grafana-faro) for the `Faro__CollectorUrl` env var and Helm setup.

### Air-Gapped Deployments

In environments without outbound internet access, the CDN bundles fail to load silently. To use Faro in firewalled deployments: download the pinned bundle files locally, place them in `wwwroot/js/faro/`, and update `SDK_URL` / `TRACING_URL` in `faro-init.js` to relative paths.
