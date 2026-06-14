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
| `exception.type` | Exception type name that triggered the retry |
| `exception.message` | Exception message (truncated to 200 chars) |

## Background Service Spans

`DrainCycle` spans are root spans (no parent) because `JobQueueDrainService` runs as a `BackgroundService` with no ambient `Activity.Current`. They appear as independent traces in observability backends.

## Tag Value Casing

Metric `run_type` values are lowercased (`implementation`), while span `pipeline.run_type` values are PascalCase (`Implementation`). Use the appropriate casing when querying.
