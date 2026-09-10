# Observability — Internal Details

Internal reference for telemetry implementation specifics.

## Internal-Only Metrics

These metrics are primarily useful for pipeline developers debugging infrastructure:

| Metric | Type | Description |
|--------|------|-------------|
| `token_vending.failures` | Counter | Token vending operation failures |
| `token_vending.duration` | Histogram | Duration of token vending operations |
| `agent.heartbeat.failures` | Counter | Agent heartbeat send failures |
| `agent.reconnections` | Counter | Agent reconnection events |

## Internal Trace Spans

These spans represent internal plumbing and are unlikely to be queried by operators:

| Span Name | Description |
|-----------|-------------|
| `Hub.ReportJobCompleted` | Hub business logic for job completion |
| `TokenVending.GenerateToken` | Token generation HTTP call |
| `Agent.ReceiveJob` | Agent job receipt and acceptance/rejection decision |
| `Agent.ReportCompletion` | Reporting job completion to Pipeline API |

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

## QGC Process and Stall Metrics

These metrics cover individual QGC process invocations and agent silence detection. All defined in `PipelineTelemetry` (`CodingAgent.Pipeline` meter).

| Metric | Type | Tags | Description |
|--------|------|------|-------------|
| `quality_gate.process.timeout` | Counter | `gate_name`, `qgc_name` | QGC process (compilation or test command) killed for exceeding `processTimeoutSeconds` |
| `quality_gate.process.duration` | Histogram | `gate_name`, `qgc_name` | Single-invocation duration. Distinct from `quality_gate.duration` (entire retry phase) |
| `quality_gate.stall.warnings` | Counter | `phase` | Agent silence warning — fires after each `stallWarningInterval` with no output. `phase` uses a closed-set constant from `PipelineTelemetry.StallPhases` |
| `quality_gate.stall.kills` | Counter | `phase` | Agent process killed due to stall timeout |
| `quality_gate.stall.process_deaths` | Counter | `phase` | Agent process exited unexpectedly (not stall-killed) |
| `quality_gate.post_pr_ci.duration` | Histogram | — | Time waiting for post-PR CI to complete. Recorded by `QualityGateExecutor` on the post-PR finalization path |

The `phase` tag uses a closed set to prevent unbounded cardinality:
- `qgc_retry_agent` — quality gate retry, pre-PR cleanup, final QG, post-PR CI
- `codegen` — code generation / rework
- `analysis` — analysis agent
- `code_review` — review agents, acceptance criteria, review summary
- `decomposition` — decomposition phases
- `unknown` — unmapped phases

## Grafana Faro Frontend Observability

The Orchestrator (Blazor Server) emits frontend Real User Monitoring data to Grafana Faro. Faro is initialized in `wwwroot/js/faro-init.js` via an async CDN bundle load from `unpkg.com`. When `Faro__CollectorUrl` is absent or empty, Faro stays as a no-op stub — no errors, no impact on page load.

Data collected includes: page load timing, Blazor circuit errors, unhandled JS exceptions, and custom frontend log events. See [Configuration — Frontend Observability](../configuration.md#frontend-observability-grafana-faro) for setup.

## Work Distribution Metrics

The `CodingAgent.WorkDistribution` meter (defined in `WorkDistributionTelemetry.cs` in `CodingAgent.Pipeline`, namespace `CodingAgent.Pipeline.Telemetry`) emits metrics for Kubernetes dispatch. The instruments are fed by `DispatchService` and `ReconciliationService` in the **Job Controller** (`service.name=coding-agent-jobcontroller`), and `workitems_by_status` is fed by `WorkItemCountsPoller` in the **Scheduler** (`service.name=coding-agent-scheduler`), which polls `GET /api/work-items/counts-by-status`.

See [Observability — Work Distribution Metrics](../observability.md#work-distribution-metrics) for the full metric table.

## CriticalMessageBuffer (Chat Pod Agent-Side)

`CriticalMessageBuffer` buffers failed `ReportJobCompleted` messages on the agent side for replay after reconnection. It is used by **chat pods** (ephemeral K8s Jobs spawned without `--work-item-id`) which run `AgentWorkerService` and communicate with the orchestrator hub over SignalR. Failed deliveries are buffered silently — there is no dedicated metric counter for individual send failures; instead monitor `agent.reconnections` for connection instability.

Drain behavior:
- On reconnection, buffered messages are replayed (max 3 drain attempts per message)
- Successful replay releases the agent's job slot and signals readiness
- Messages exceeding max drain attempts are discarded with a warning log
