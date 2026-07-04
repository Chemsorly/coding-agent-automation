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
| `DrainCycle` | Single drain cycle in JobQueueDrainService (root span, no parent) |
| `Hub.ReportJobCompleted` | Hub business logic for job completion |
| `TokenVending.GenerateToken` | Token generation HTTP call |
| `Agent.ReceiveJob` | Agent job receipt and acceptance/rejection decision |
| `Agent.ReportCompletion` | Reporting job completion to orchestrator |

## Resilience Retry Events

All resilience pipelines (`ResiliencePipelineFactory` and `TokenVendingService` internal pipeline) emit `ActivityEvent("retry")` on each retry attempt, attached to whatever parent span is active.

Event tags:

| Tag | Description |
|-----|-------------|
| `attempt` | Retry attempt number (1-based) |
| `exception_type` | Exception type name that triggered the retry |

## Background Service Spans

`DrainCycle` spans are root spans (no parent) because `JobQueueDrainService` runs as a `BackgroundService` with no ambient `Activity.Current`. They appear as independent traces in observability backends.

## Tag Value Casing

Metric `run_type` values are lowercased (`implementation`), while span `pipeline.run_type` values are PascalCase (`Implementation`). Use the appropriate casing when querying.

## Work Distribution Metrics

The `CodingAgent.WorkDistribution` meter (defined in `WorkDistributionTelemetry.cs`) emits metrics specific to DB+SignalR and DB+Kubernetes dispatch modes. These metrics are only active when the orchestrator is running with `Database__Host` configured.

| Metric | Type | Unit | Description |
|--------|------|------|-------------|
| `workdistribution.dispatch_latency_seconds` | Histogram | s | Time from WorkItem creation (Pending) to Dispatched |
| `workdistribution.workitems_pending_duration_seconds` | Histogram | s | Duration work items spend in Pending status |
| `workdistribution.job_execution_duration_seconds` | Histogram | s | Total execution duration of dispatched jobs |
| `workdistribution.workitems_terminated` | Counter | — | Work items reaching terminal status |
| `workdistribution.dispatcher_polls` | Counter | — | Number of dispatch poll cycles executed |
| `workdistribution.dispatcher_last_poll_epoch_seconds` | ObservableGauge | s | Epoch seconds of the last DispatchService poll cycle (for stale-poll alerting) |
| `workdistribution.credential_pool_available` | ObservableGauge | — | Available credential PVCs in the kiro pool (K8s mode) |
| `workdistribution.credential_pool_claimed` | ObservableGauge | — | Claimed credential PVCs in the kiro pool (K8s mode) |

## CriticalMessageBuffer (Agent-Side)

`CriticalMessageBuffer` buffers failed `ReportJobCompleted` messages on the agent side for replay after reconnection. This is invisible to external telemetry backends but affects the `agent.signalr.failures` counter — each failed delivery increments it before the message is buffered.

Drain behavior:
- On reconnection, buffered messages are replayed (max 3 drain attempts per message)
- Successful replay releases the agent's job slot and signals readiness
- Messages exceeding max drain attempts are discarded with a warning log
