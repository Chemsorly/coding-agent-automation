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

The Orchestrator (Blazor Server) emits frontend Real User Monitoring data to Grafana Faro. Faro is initialized in `wwwroot/js/faro-init.js` via an async CDN bundle load from `unpkg.com`. When `Faro__CollectorUrl` is absent or empty, Faro stays as a no-op stub — no errors, no impact on page load.

Data collected includes: page load timing, Blazor circuit errors, unhandled JS exceptions, and custom frontend log events. See [Configuration — Frontend Observability](../configuration.md#frontend-observability-grafana-faro) for setup.

## Work Distribution Metrics

The `CodingAgent.WorkDistribution` meter (defined in `WorkDistributionTelemetry.cs` in `CodingAgent.Infrastructure.Common`, namespace `CodingAgent.Pipeline.Telemetry`) emits metrics for Kubernetes dispatch. The process that records each metric is listed in the "Recorded by" column of [Observability — Dispatch and work distribution](../observability.md#dispatch-and-work-distribution).

The `workdistribution.workitems_by_status` gauge is only emitted by the leader Scheduler replica.

Terminal transitions are counted by `pipeline.run.outcomes` (API) only.

The credential pool and dispatcher gauges use owner-only empty-measurement guards: non-owning processes emit no measurement, preventing spurious 0 series that would corrupt the `CredentialPoolExhausted` and `DispatcherStalled` Prometheus alerts.
