# Observability

Pipeline telemetry is built on [OpenTelemetry](https://opentelemetry.io/) for .NET, exporting metrics and distributed traces via OTLP.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for how pipeline steps relate to trace spans, and [Configuration](configuration.md) for general pipeline settings.

## Metrics

All metrics are emitted from the `CodingAgent.Pipeline` meter, defined in `PipelineTelemetry.cs`.

| Metric | Type | Unit | Tags | Description |
|--------|------|------|------|-------------|
| `pipeline.jobs.dispatched` | Counter | — | `run_type` | Incremented when a pipeline job starts |
| `pipeline.jobs.completed` | Counter | — | `run_type` | Incremented when a job completes successfully |
| `pipeline.jobs.failed` | Counter | — | `run_type` | Incremented when a job fails |
| `pipeline.jobs.duration` | Histogram | seconds | `run_type` | Duration of the entire pipeline job |
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
| `pipeline.step.duration` | Histogram | seconds | `step_name`, `run_type` | Duration of individual pipeline steps |
| `pipeline.step.count` | Counter | — | `step_name`, `run_type` | Pipeline step execution count |
| `agent.tokens.used` | Counter | — | `run_type`, `pipeline.project_id`, `pipeline.project_name` | Agent tokens consumed |
| `agent.cost.usd` | Counter | USD | `run_type`, `pipeline.project_id`, `pipeline.project_name` | LLM cost in USD |
| `quality_gate.retries` | Counter | — | `run_type` | Quality gate retry attempts |
| `quality_gate.duration` | Histogram | seconds | `run_type` | Total time in quality gate phase |
| `quality_gate.evaluations` | Counter | — | `gate_name`, `result` | Individual gate evaluation events |
| `quality_gate.external_ci.duration` | Histogram | seconds | — | Time waiting for external CI |
| `dispatch.queue.wait_time` | Histogram | seconds | — | Time a job spent waiting in the dispatch queue |
| `pipeline.decomposition.sub_issues.created` | Counter | — | — | Sub-issues created by decomposition |
| `pipeline.decomposition.sub_issues.failed` | Counter | — | — | Sub-issue creation failures |
| `pipeline.decomposition.duration` | Histogram | seconds | — | Duration of decomposition phases |

### Tag Schema

| Tag | Values | Description |
|-----|--------|-------------|
| `run_type` | `implementation`, `review`, `decomposition` | Pipeline run type (lowercased) |
| `result` | `success`, `failure` | Poll cycle outcome |
| `decision` | `dispatched`, `skipped_already_processing`, `skipped_dependency_blocked`, `skipped_no_agent`, `skipped_max_runs`, `skipped_filtered_by_label` | Dispatch decision reason |
| `reason` | `busy`, `shutting_down`, `unknown` | Agent job rejection reason |

### Histogram Bucket Boundaries

The `pipeline.jobs.duration` and other histogram metrics use the OpenTelemetry SDK's default bucket boundaries. No custom bucket views are configured.

## Traces

All spans are emitted from the `CodingAgent.Pipeline` ActivitySource. Spans marked with † are emitted from both the orchestrator (`PipelineOrchestrationService`) and the agent worker (`LocalPipelineExecutor`).

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
| `QualityGate.Coverage` | `gate_name` | Coverage report parsing (child of RunQualityGates) |
| `ReviewCode` | `pipeline.run_id`, `pipeline.issue` | Multi-agent code review |
| `CreatePullRequest` † | `pipeline.run_id`, `pipeline.issue`, `pipeline.pr.is_draft` | PR creation step |
| `GeneratePrDescription` | `pipeline.run_id`, `pipeline.issue` | Agent-generated PR description |
| `FinalizePullRequest` | `pipeline.run_id`, `pipeline.issue`, `pipeline.pr.is_draft` | PR finalization (when existing draft PR is promoted) |
| `PostReviewFindings` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting review findings to PR |
| `ExtractLinkedIssues` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Extracting linked issues from PR |
| `Decomposition` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Sub-issue generation (Phase 2) |
| `DecompositionAnalysis` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Epic analysis (Phase 1) |
| `PostDecompositionPlan` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting decomposition plan comment |
| `PostDecompositionSummary` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Posting creation summary comment |
| `CreateSubIssues` | `pipeline.run_id`, `pipeline.issue`, `pipeline.run_type` | Creating sub-issues on GitHub |
| `Reflection` | `pipeline.run_id` | Post-PR reflection prompt (child of FinalizePullRequest) |
| `BrainSyncPostRun` | `pipeline.run_id` | Brain repository sync after run (child of FinalizePullRequest) |
| `FeedbackCollection` | `pipeline.run_id` | Structured feedback collection (child of FinalizePullRequest) |
| `DrainCycle` | `jobs_dispatched` | Single drain cycle in JobQueueDrainService (root span) |
| `Hub.ReportJobCompleted` | `job_id`, `success` | Hub business logic for job completion |
| `TokenVending.GenerateToken` | — | Token generation HTTP call |
| `Agent.ReceiveJob` | `job_id`, `run_type` | Agent job receipt and acceptance/rejection decision |
| `Agent.ReportCompletion` | `job_id`, `success` | Reporting job completion to orchestrator |
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

> **Note on tag value casing**: Metric `run_type` values are lowercased (`implementation`), while span `pipeline.run_type` values are PascalCase (`Implementation`). Use the appropriate casing when querying your observability backend.

## Configuration

Telemetry is exported via OTLP. The OpenTelemetry SDK reads configuration from standard environment variables — no code changes are needed to point at a different backend.

### Environment Variables

| Variable | Default | Description |
|----------|---------|-------------|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://localhost:4317` | OTLP collector endpoint |
| `OTEL_EXPORTER_OTLP_HEADERS` | — | Auth headers (e.g., `Authorization=Basic <token>`) |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` | Transport protocol: `grpc` or `http/protobuf` |

### Service Names

| Service | `service.name` | Description |
|---------|---------------|-------------|
| Orchestrator | `coding-agent-orchestrator` | Web UI + pipeline orchestration |
| Agent Worker (.NET 1) | `coding-agent-worker-dotnet-1` | Kiro .NET agent container |
| Agent Worker (.NET 2) | `coding-agent-worker-dotnet-2` | Kiro .NET agent container |
| Agent Worker (Python) | `coding-agent-worker-python` | Kiro Python agent container |
| Agent Worker (Java) | `coding-agent-worker-java` | Kiro Java agent container |
| Agent Worker (OpenCode .NET) | `coding-agent-worker-opencode-dotnet` | OpenCode .NET agent container |
| Agent Worker (OpenCode Python) | `coding-agent-worker-opencode-python` | OpenCode Python agent container |
| Agent Worker (OpenCode Java) | `coding-agent-worker-opencode-java` | OpenCode Java agent container |

### Example: Grafana Cloud

OTEL variables are configured in `docker-compose.yml` and sourced from `.env`. To connect to Grafana Cloud, set these values in your `.env` file:

```env
OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-prod-us-east-0.grafana.net/otlp
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
OTEL_EXPORTER_OTLP_HEADERS=Authorization=Basic <base64-encoded-instance-id:token>
```

See `.env.example` for the full template.

## Verification

### Metrics

Use `dotnet-counters` to verify metrics are being recorded locally:

```bash
# Inside the orchestrator container
dotnet-counters monitor --counters CodingAgent.Pipeline
```

Expected output after dispatching a job:

```
[CodingAgent.Pipeline]
    pipeline.jobs.completed (Count / 1 sec)            0
    pipeline.jobs.dispatched (Count / 1 sec)           1
    pipeline.jobs.duration (s)
        Percentile=50                                  0
        Percentile=95                                  0
        Percentile=99                                  0
    pipeline.jobs.failed (Count / 1 sec)               0
```

### Traces

When connected to a backend (Grafana Tempo, Jaeger, etc.), search for traces with:

- Service: `coding-agent-orchestrator` or `coding-agent-worker`
- Operation: `ExecutePipeline`

Expected span hierarchy for an implementation run:

```
ExecutePipeline
├── AnalyzeIssue
├── GenerateCode
├── RunQualityGates
│   ├── QualityGate.Compilation
│   ├── QualityGate.Tests
│   ├── QualityGate.Coverage
│   └── ReviewCode
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

