# Observability — Internal Details

Internal reference for telemetry implementation specifics. The full metric and span inventory is in [Observability](../observability.md).

## Internal-Only Metrics

These metrics are primarily useful for pipeline developers debugging infrastructure. All are recorded by the API.

| Metric | Type | Description |
|--------|------|-------------|
| `token_vending.failures` | Counter | Token vending operation failures |
| `agent.hub.auth_rejections` | Counter | Hub calls rejected by the auth checks (`reason`) |
| `workdistribution.progress_write_failures` | Counter | Failed `LastProgressAt` writes |

## Internal Trace Spans

These spans represent internal plumbing and are unlikely to be queried by operators:

| Span Name | Description |
|-----------|-------------|
| `Hub.ReportJobCompleted` | Hub business logic for job completion (API) |
| `TokenVending.GenerateToken` | Token generation HTTP call (API) |

## Resilience Retry Events

All resilience pipelines (`ResiliencePipelineFactory` and `TokenVendingService` internal pipeline) emit `ActivityEvent("retry")` on each retry attempt, attached to whatever parent span is active.

Event tags:

| Tag | Description |
|-----|-------------|
| `attempt` | Retry attempt number (1-based) |
| `exception_type` | Exception type name that triggered the retry |

## Background Service Spans

`Hub.ReportJobCompleted` is emitted within the Pipeline API process whenever hub completion logic runs. There are no root-span background service spans remaining after `JobQueueDrainService` was removed in Spec 041.

## Tag Value Casing

Metric `run_type` values are lowercased (`implementation`), while span `pipeline.run_type` values are PascalCase (`Implementation`). Use the appropriate casing when querying.

## Quality Gate Processes and Agent Stalls

Agent pods export no metrics, so quality gate processes and agent stalls reach Prometheus only through what the agent reports to the API:

- Each gate result (`ReportQualityGateResult`) → `pipeline.run.quality_gate.results`.
- A stall kill or process death detected by `AgentStallMonitor`, or a gate process timeout in `QualityGateValidator`, is reported as an `AgentStall` pipeline event → `pipeline.run.agent_stalls{phase, kind}`. The API maps the reported phase onto the closed `phase` set (`PipelineTelemetry.NormalizeRunPhase`) and drops events whose kind is not `stall_kill`, `process_death` or `process_timeout`.
- CI waits (`CiWait` events) → `pipeline.run.ci.wait{stage}`.

Per-invocation detail stays in the trace: `invoke_agent {phase}` spans carry `agent.stall_warning`, `agent.stall_kill` and `agent.process_death` events, and `QualityGate.Compilation` / `QualityGate.Tests` spans cover each gate process.

## Grafana Faro Frontend Observability

The Orchestrator (Blazor Server) emits frontend Real User Monitoring data to Grafana Faro. Faro is initialized in `wwwroot/js/faro-init.js` via an async CDN bundle load from `unpkg.com`. When `Faro__CollectorUrl` is absent or empty, Faro stays as a no-op stub — no errors, no impact on page load.

Data collected includes: page load timing, Blazor circuit errors, unhandled JS exceptions, and custom frontend log events. See [Configuration — Frontend Observability](../configuration.md#frontend-observability-grafana-faro) for setup.

## Work Distribution Metrics

The `CodingAgent.WorkDistribution` meter (defined in `WorkDistributionTelemetry.cs` in `CodingAgent.Infrastructure.Common`, namespace `CodingAgent.Pipeline.Telemetry`) emits metrics for Kubernetes dispatch. Ownership by process (issue #2980):

- **API** (`service.name=coding-agent-api`): `workdistribution.dispatch_latency_seconds`, `workdistribution.credential_pool_available`, `workdistribution.credential_pool_claimed`, `workdistribution.dispatch.attempts`, `workdistribution.pod_start_seconds`, `workdistribution.pvc_pool_exhaustions` — recorded when the API dispatches work items and agents fetch their assignment.
- **Scheduler** (`service.name=coding-agent-scheduler`): `workdistribution.dispatcher_last_poll_epoch_seconds`, `workdistribution.dispatcher_polls` (via `WorkItemDispatchLoop`), and `workdistribution.workitems_by_status` (via `WorkItemCountsService`). The `workitems_by_status` gauge is only emitted by the leader Scheduler replica.
- **Job Controller** (`service.name=coding-agent-jobcontroller`): `workdistribution.timeout_execution_age_seconds`, `workdistribution.timeout_canary_violations`, `workdistribution.agent_timeouts` — recorded by `ReconciliationLoop` when enforcing session timeouts.

Terminal transitions are counted by `pipeline.run.outcomes` (API) only.

The credential pool and dispatcher gauges use owner-only empty-measurement guards: non-owning processes emit no measurement, preventing spurious 0 series that would corrupt the `CredentialPoolExhausted` and `DispatcherStalled` Prometheus alerts.

See [Observability — Dispatch and work distribution](../observability.md#dispatch-and-work-distribution) for the full metric table.

## CriticalMessageBuffer (Chat Pod Agent-Side)

`CriticalMessageBuffer` buffers failed `ReportJobCompleted` messages on the agent side for replay after reconnection. It is used by **chat pods** (ephemeral K8s Jobs spawned without `--work-item-id`) which run `AgentWorkerService` and communicate with the orchestrator hub over SignalR. Failed deliveries are buffered silently and there is no metric for them (agent pods export no metrics); the agent logs each failed send and each reconnect.

Drain behavior:
- On reconnection, buffered messages are replayed (max 3 drain attempts per message)
- Successful replay releases the agent's job slot and signals readiness
- Messages exceeding max drain attempts are discarded with a warning log
