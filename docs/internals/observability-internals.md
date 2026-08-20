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

## Work Distribution Metrics

The `CodingAgent.WorkDistribution` meter (defined in `WorkDistributionTelemetry.cs` in `CodingAgentWebUI.Pipeline`, namespace `CodingAgentWebUI.Pipeline.Telemetry`) emits metrics for Kubernetes dispatch. The instruments are fed by `DispatchService` and `ReconciliationService` in the **Job Controller** (`service.name=coding-agent-jobcontroller`), and `workitems_by_status` is fed by `WorkItemMetricsBackgroundService` in the **Pipeline API** (`service.name=coding-agent-api`).

See [Observability — Work Distribution Metrics](../observability.md#work-distribution-metrics) for the full metric table including all 14 instruments.

## CriticalMessageBuffer (Chat Pod Agent-Side)

`CriticalMessageBuffer` buffers failed `ReportJobCompleted` messages on the agent side for replay after reconnection. It is used by **chat pods** (ephemeral K8s Jobs spawned without `--work-item-id`) which run `AgentWorkerService` and communicate with the orchestrator hub over SignalR. Failed deliveries are buffered silently — there is no dedicated metric counter for individual send failures; instead monitor `agent.reconnections` for connection instability.

Drain behavior:
- On reconnection, buffered messages are replayed (max 3 drain attempts per message)
- Successful replay releases the agent's job slot and signals readiness
- Messages exceeding max drain attempts are discarded with a warning log
