# Observability — Internal Details

Internal reference for telemetry implementation specifics. The full metric and span inventory is in [Observability](../observability.md).

## Internal-Only Metrics

These metrics are primarily useful for pipeline developers debugging infrastructure. All are recorded by the API.

| Metric | Type | Description |
|--------|------|-------------|
| `token_vending.failures` | Counter | Token vending operation failures |
| `token_vending.scope_refused` | Counter | Token refreshes granted without the issue permission they asked for (`permission`) |
| `agent.hub.auth_rejections` | Counter | Hub calls rejected by the auth checks (`reason`) |
| `workdistribution.progress_write_failures` | Counter | Failed `LastProgressAt` writes |

## Internal Trace Spans

These spans represent internal plumbing and are unlikely to be queried by operators:

| Span Name | Description |
|-----------|-------------|
| `Hub.ReportJobCompleted` | Hub business logic for job completion (API) |
| `TokenVending.GenerateToken` | Token generation HTTP call (API) |

## Resilience Retry Events

All resilience pipelines built by `ResiliencePipelineFactory` emit `ActivityEvent("retry")` on each retry attempt, attached to whatever parent span is active.

Event tags:

| Tag | Description |
|-----|-------------|
| `attempt` | Retry attempt number (1-based) |
| `exception.type` | Exception type name that triggered the retry |
| `exception.message` | Truncated exception message |

## Quality Gate Processes and Agent Stalls

Agent pods export no metrics, so quality gate processes and agent stalls reach Prometheus only through what the agent reports to the API:

- Each gate result (`ReportQualityGateResult`) → `pipeline.run.quality_gate.results`.
- A stall kill or process death detected by `AgentStallMonitor`, or a gate process timeout in `QualityGateValidator`, is reported as an `AgentStall` pipeline event → `pipeline.run.agent_stalls{phase, kind}`. The API maps the reported phase onto the closed `phase` set (`PipelineTelemetry.NormalizeRunPhase`) and drops events whose kind is not `stall_kill`, `process_death` or `process_timeout`.
- CI waits (`CiWait` events) → `pipeline.run.ci.wait{stage}`.

Per-invocation detail stays in the trace: `invoke_agent {phase}` spans carry `agent.stall_warning`, `agent.stall_kill` and `agent.process_death` events, and `QualityGate.Compilation` / `QualityGate.Tests` spans cover each gate process.

## Grafana Faro Frontend Observability

The browser UI (Blazor WebAssembly) emits frontend Real User Monitoring data to Grafana Faro. Faro is initialized in `wwwroot/js/faro-init.js` via an async CDN bundle load from `unpkg.com`. When `Faro__CollectorUrl` is absent or empty, Faro stays as a no-op stub — no errors, no impact on page load.

Data collected includes: page load timing and web vitals, unhandled JS exceptions, console warnings and errors (`consoleInstrumentation.disabledLevels` keeps Trace to Info in the browser), fetch spans that start the browser → web host → API trace (the polls are in `ignoreUrls`), and the UI's notifications. There are no circuit events; Faro's session tracking marks a browser session. See [Observability — Frontend Observability](../observability.md#frontend-observability-grafana-faro) and [Configuration — Frontend Observability](../configuration.md#frontend-observability-grafana-faro) for setup.

## Work Distribution Metrics

The `CodingAgent.WorkDistribution` meter (defined in `WorkDistributionTelemetry.cs` in `CodingAgent.Infrastructure.Common`, namespace `CodingAgent.Pipeline.Telemetry`) emits metrics for Kubernetes dispatch. The process that records each metric is listed in the "Recorded by" column of [Observability — Dispatch and work distribution](../observability.md#dispatch-and-work-distribution).

The `workdistribution.workitems_by_status` gauge is only emitted by the leader Scheduler replica.

Terminal transitions are counted by `pipeline.run.outcomes` (API) only, tagged `run_type`, `outcome`, `failure_reason`, `pipeline.project_name`, `provider` and `model`. `RunOutcomeTelemetry` reads `provider` and `model` from the route pinned in the work item's payload (the job template's tool and the route's model), not from the agent's report, so their cardinality is bounded by what the admin routes.

Routing refusals are counted by `pipeline.routing.refusals` (API) only, at the `claim` stage by `AssignmentEnricher` (spec 056 removed the `create` and `dispatch` stages). A route that cannot run at dispatch is a hold: `DispatchRouter` records it on the item and the dispatch handler counts it on `workdistribution.dispatch.attempts` (`deferred`, the hold reason).

The credential pool and dispatcher gauges use owner-only empty-measurement guards: non-owning processes emit no measurement, preventing spurious 0 series that would corrupt the `CredentialPoolExhausted` and `DispatcherStalled` Prometheus alerts.

The agent capacity gauges `workdistribution.agents.running` and `workdistribution.agents.limit` (spec 057) are emitted only by the leader Scheduler replica, with the same owner-only guard. `WorkItemCountsService` polls `GET /api/agents/capacity` every 10 s, in its own try/catch after the other counts, so a failed capacity poll resets only these two gauges. Their callbacks are registered like the credential pool's (`Volatile.Write`, the last registration wins). Nothing is emitted before the first successful poll, after a failed one, or on a non-leader; `agents.running` also emits nothing when the API could not list the agent Jobs. Spec 056 did not change them.

The credential pause instruments (spec 056) split the same way. The API's `CredentialPauseStore` records `pipeline.agent_credential.pauses` when a credential changes from available to paused and `pipeline.agent_credential.resumes` when it becomes available (a resume, or the expiry it notices on a read, recorded once because the expiry removes the entry under the store's advisory lock). The gauge `pipeline.agent_credential.paused` is the Scheduler leader's: `WorkItemCountsService` polls `GET /api/agents/credentials` every 10 s, in its own try/catch, with the same owner-only guard and registration as the capacity gauges. The requeue of a run that hit its credential's limit is counted once, by `ProviderFailureHandler`, as `pipeline.run.outcomes{outcome="requeued", failure_reason="provider_unavailable"}`; whichever completion path (HTTP status, hub job, hub consolidation) wins the compare-and-swap records it, and the losers record nothing.
