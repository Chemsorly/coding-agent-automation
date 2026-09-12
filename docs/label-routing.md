# Label-Based Configuration System

The pipeline uses a hierarchical label system to route jobs to agents and determine which quality gates to run. Labels are the glue between repositories, agents, profiles, and quality gate configurations.

See also: [Configuration](configuration.md) for pipeline-level settings, and [Pipeline Orchestration](pipeline-orchestration.md) for how quality gates fit into the pipeline flow.

## Label Hierarchy

Labels follow a hierarchical convention: general stack → specific version. Both levels should be present on repositories and agents:

```
kiro          — coding agent tool
dotnet        — technology stack (determines quality gates)
dotnet10      — specific SDK version (determines agent routing)
```

**Example: A .NET 10 repository**
```
Repository requiredLabels: ["kiro", "dotnet", "dotnet10"]
Agent labels:              ["kiro", "dotnet", "dotnet10"]
```

## How Labels Are Used

| System | Label Source | Matching Logic | Purpose |
|--------|-------------|----------------|---------|
| **Agent Selection** | Job's RequiredLabels (from repo) | Agent labels ⊇ job labels (superset) | Route job to capable agent |
| **Profile Resolution** | Agent's labels | Profile MatchLabels ⊆ agent labels (ALL match) | Determine which provider config to send |
| **QGC Resolution** | Job's RequiredLabels (from repo) | QGC MatchLabels ∩ job labels ≠ ∅ (ANY match) | Determine which quality gates to run |
| **Reviewer Resolution** | Job's RequiredLabels (from repo) | Reviewer MatchLabels ∩ job labels ≠ ∅ (ANY match) | Determine which review agents to run |

## Configured Agent Types

| Agent Type | Labels | Docker Image | SDK |
|-----------|--------|--------------|-----|
| `kiro-dotnet10` | `kiro, dotnet, dotnet10` | `dockerfiles/kiro/agent-kiro-dotnet10.Dockerfile` | .NET 10 |
| `kiro-python312` | `kiro, python, python312` | `dockerfiles/kiro/agent-kiro-python312.Dockerfile` | Python 3.12 |
| `kiro-java21` | `kiro, java, java21` | `dockerfiles/kiro/agent-kiro-java21.Dockerfile` | Java 21 |
| `opencode-dotnet10` | `opencode, dotnet, dotnet10` | `dockerfiles/opencode/agent-opencode-dotnet10.Dockerfile` | .NET 10 |
| `opencode-python312` | `opencode, python, python312` | `dockerfiles/opencode/agent-opencode-python312.Dockerfile` | Python 3.12 |
| `opencode-java21` | `opencode, java, java21` | `dockerfiles/opencode/agent-opencode-java21.Dockerfile` | Java 21 |

## Agent Profiles

Agent Profiles map label sets to agent provider configs (model, timeout, CLI path). Configured in Settings → Agent Profiles.

| Profile | Match Labels | Effect |
|---------|-------------|--------|
| Kiro .NET 10 Agent | `kiro, dotnet, dotnet10` | Uses Opus model, 30min timeout |
| Kiro Python 3.12 Agent | `kiro, python, python312` | Uses Opus model, 20min timeout |
| Kiro Java 21 Agent | `kiro, java, java21` | Uses Opus model, 30min timeout |

Resolution: most specific match wins (highest label count). A profile with empty MatchLabels acts as a default/catch-all.

## Quality Gate Configurations

QGCs define per-stack quality gates. Configured in Settings → Quality Gate Configs.

| QGC | Match Labels | Compilation | Tests |
|-----|-------------|-------------|-------|
| .NET Quality Gate | `dotnet` | `dotnet build --no-restore` | `dotnet test --no-restore --no-build --filter Category!=E2E` |
| Python Quality Gate | `python` | `python -m pytest --collect-only` | `python -m pytest --cov=. --cov-report=xml:coverage.xml` |
| Java Quality Gate | `java` | `mvn compile -q` | `mvn test -q` |

Resolution: all QGCs whose labels intersect with the job's labels are applied sequentially. A polyglot repo with labels `["dotnet", "python"]` gets both the .NET and Python quality gates.

> **Coverage enforcement:** The built-in coverage threshold gate was retired (`coverageThreshold`, `coverageReportFormat`, and `coverageReportPaths` are tombstoned fields — configuring them has no effect). To enforce coverage minimums, add the appropriate flag to the `TestArguments` field on the QGC. For example, `--coverage-fail-below 80` for pytest-cov (Python) or a test runner argument like `--minimum-coverage 80` for .NET tools that support it. The test command will fail with a non-zero exit code if coverage is below the threshold, which the Tests gate will catch.

## Reviewer Configurations

Reviewer Configurations define per-stack code review agents. Configured in Settings → Label Routing → Reviewer Configs.

| Reviewer Config | Match Labels | Agents |
|----------------|-------------|--------|
| Default Reviewers | *(empty — global fallback)* | Correctness, DotNetSpecialist, SecurityReviewer, TestQualityReviewer |

Resolution: all Reviewer Configurations whose labels intersect with the job's labels are applied sequentially (ANY match). Each configuration contains one or more review agents that run in order. A configuration with empty MatchLabels acts as a global fallback (applies to all jobs). When no reviewer config matches (or all are disabled), the review phase is **skipped**. A warning is logged (`Pipeline {RunId} no reviewer configurations matched — review phase skipped`) and a `pipeline.review.skipped` telemetry counter is incremented. No review agents execute and the run completes as `agent:done` without a review comment. To ensure review always runs, keep the default reviewer configuration (`MatchLabels = []`) enabled in Settings → Reviewers.

<!-- TODO [WARNING]: Verify that `pipeline.review.skipped` matches the externally observable OTel counter name
     if the telemetry backend or metric export configuration is ever changed. The counter is defined in
     PipelineTelemetry.cs as Meter.CreateCounter<long>("pipeline.review.skipped", ...) and referenced in
     AgentPhaseExecutor.CodeReview.cs. If the name changes, update the counter name referenced above. -->

## Agent Lifecycle Labels

The pipeline applies `agent:*` labels to GitHub issues and PRs to communicate pipeline state. Only one `agent:*` label should be present on an issue at a time. These labels are managed by the pipeline — setting them manually can interfere with dispatch logic.

| Label | Description | Terminal? |
|-------|-------------|-----------|
| `agent:next` | Issue is queued for the pipeline (dispatch trigger) | No |
| `agent:in-progress` | Pipeline is actively working on this issue | No |
| `agent:done` | Pipeline completed successfully (PR created or review posted) | Yes |
| `agent:error` | Pipeline failed with an unrecoverable error | Yes |
| `agent:needs-refinement` | Analysis confidence gate rejected the issue — needs clearer requirements before dispatch | Yes |
| `agent:wont-do` | Issue explicitly marked out of scope for automation | Yes |
| `agent:cancelled` | Run was cancelled by a user or system event | Yes |
| `agent:epic` | Issue is an epic awaiting decomposition | No |
| `agent:epic-review` | Decomposition plan is posted and awaiting human review | No |
| `agent:epic-approved` | Human approved the decomposition plan (required before sub-issues are created) | No |
| `agent:generated` | Issue was auto-generated by decomposition | No |

**Terminal labels** (`agent:done`, `agent:error`, `agent:needs-refinement`, `agent:wont-do`, `agent:cancelled`) mark states that require human action to re-queue. Remove the terminal label and add `agent:next` to retry. `agent:needs-refinement` specifically indicates the issue body needs more detail — review the analysis confidence output before re-queuing.

**`agent:epic-approved`** is a gated label: agents may not set it via `RequestLabelChange`. Only humans can approve decomposition.

## Setting Up a New Stack

1. **Add a job template** — In `values.yaml`, add an entry to `jobTemplates[]` with the appropriate `labels`, `image`, and `providerType`
2. **Create an Agent Profile** — In Settings → Label Routing → Agent Profiles, map the labels to a provider config
3. **Create a QGC** — In Settings → Label Routing → Quality Gate Configs, define the build/test commands for the stack
4. **Create a Reviewer Config** (optional) — In Settings → Label Routing → Reviewer Configs, define stack-specific review agents
5. **Configure the repository** — Set `requiredLabels` on the repository provider config (include both stack and version labels)
6. **Create a Pipeline Job Template** — In Agent Coding → Pipeline Job Templates, link the issue provider, repo provider, and optional brain/CI providers
