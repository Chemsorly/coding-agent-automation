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
| **Profile Resolution** | Job's RequiredLabels (from repo) | Profile MatchLabels ⊇ job labels (the profile covers every repo label); the profile with the most labels wins, then Priority, then Id | Pick the agent provider config; the profile's labels become the job's agent selector |
| **Job Template Selection** | Matched profile's MatchLabels | Template labels = profile labels | Pick the image, `maxConcurrent` and resources |
| **QGC Resolution** | Matched profile's MatchLabels | QGC MatchLabels ∩ profile labels ≠ ∅ (ANY match); empty MatchLabels always applies | Determine which quality gates to run |
| **Reviewer Resolution** | Matched profile's MatchLabels | Reviewer MatchLabels ∩ profile labels ≠ ∅ (ANY match); empty MatchLabels always applies | Determine which review agents to run |

Consolidation runs resolve the profile from the agent's labels instead (Profile MatchLabels ⊆ agent labels).

Chat sessions start from a profile: Agent Chat lists the enabled profiles whose labels name a job template, and the chosen profile supplies the provider config (model, effort, auth mode), the MCP servers and the template. A job template without a profile is not offered, and a profile whose provider config no longer exists cannot be launched.

## Agent Images

Each agent tool has one image, built from `dockerfiles/agent.Dockerfile` with `--target`. Every image carries every supported tech stack: .NET 10 SDK, JDK 21 + Maven, Python 3.12 (pip, venv, uv), Node.js and npm.

| Image tag | Target | Agent tool | Stacks |
|-----------|--------|------------|--------|
| `coding-agent-kiro-latest` | `kiro` | Kiro CLI | .NET 10, Java 21, Python 3.12 |
| `coding-agent-opencode-latest` | `opencode` | OpenCode | .NET 10, Java 21, Python 3.12 |
| `coding-agent-claude-latest` | `claude` | Claude Code CLI | .NET 10, Java 21, Python 3.12 |

The image does not route jobs. A job's labels pick an agent profile, and the profile's labels pick the job template, which names the image. Keep one profile and one job template per stack, and point the templates of one agent tool at the same image:

| Job template labels | Image |
|---------------------|-------|
| `kiro, dotnet, dotnet10` | `coding-agent-kiro-latest` |
| `kiro, java, java21` | `coding-agent-kiro-latest` |
| `kiro, python, python312` | `coding-agent-kiro-latest` |

Each label set keeps its own `maxConcurrent` and resources. Quality gates and reviewers are resolved against the matched profile's labels, so don't give a profile every stack label for convenience: a Java repository matched to `kiro, dotnet, dotnet10, java, java21` would also run the .NET quality gate. For a polyglot repository, add a profile and template with exactly its stacks (for example `kiro, dotnet, dotnet10, python, python312`) on the same image.

The image's default `AGENT_LABELS` lists the agent tool and every stack. Kubernetes Jobs set `AGENT_LABELS` from their job template, so the default applies only to an agent started outside Kubernetes.

The per-stack tags of earlier releases (`coding-agent-kiro-dotnet10-latest`, `coding-agent-claude-java21-latest`, …) are no longer published. Point job templates that still use them at `coding-agent-<tool>-latest`.

The Claude image runs the Claude Code CLI. Its job templates use `providerType: claude`, and their agent profiles point to an agent provider config of type `ClaudeCode` (model, effort, auth mode). It needs no credential PVC: the API key and/or subscription token come from the agent Secret — see [Deployment](deployment.md).

## Agent Profiles

Agent Profiles map label sets to agent provider configs (model, effort, CLI path). Configured in Settings → Label Routing → Agent Profiles.

| Profile | Match Labels | Effect |
|---------|-------------|--------|
| Kiro .NET 10 Agent | `kiro, dotnet, dotnet10` | Uses Opus model |
| Kiro Python 3.12 Agent | `kiro, python, python312` | Uses Opus model |
| Kiro Java 21 Agent | `kiro, java, java21` | Uses Opus model |

Resolution: the profile must contain every label of the repository; among those, the profile with the most labels wins, then the higher Priority, then the Id. A profile with empty MatchLabels matches only a repository with no required labels; it is a catch-all only for consolidation runs, which match by agent labels. Agent Chat does not offer it: a chat pod needs labels to pick its job template.

## Quality Gate Configurations

QGCs define per-stack quality gates. Configured in Settings → Label Routing → Quality Gate Configs.

| QGC | Match Labels | Compilation | Tests |
|-----|-------------|-------------|-------|
| .NET Quality Gate | `dotnet` | `dotnet build --no-restore` | `dotnet test --no-restore --no-build --filter Category!=E2E` |
| Python Quality Gate | `python` | `python -m pytest --collect-only` | `python -m pytest --cov=. --cov-report=xml:coverage.xml` |
| Java Quality Gate | `java` | `mvn compile -q` | `mvn test -q` |

Resolution: all QGCs whose labels intersect with the job's labels are applied sequentially. A polyglot repo with labels `["dotnet", "python"]` gets both the .NET and Python quality gates.

> **Coverage enforcement:** The built-in coverage threshold gate was retired. To enforce coverage minimums, add the appropriate flag to the `TestArguments` field on the QGC. For example, `--coverage-fail-below 80` for pytest-cov (Python) or a test runner argument like `--minimum-coverage 80` for .NET tools that support it. The test command will fail with a non-zero exit code if coverage is below the threshold, which the Tests gate will catch.

## Reviewer Configurations

Reviewer Configurations define per-stack code review agents. Configured in Settings → Label Routing → Reviewer Configs.

| Reviewer Config | Match Labels | Agents |
|----------------|-------------|--------|
| Default Reviewers | *(empty — global fallback)* | Correctness, SecurityReviewer, TestQualityReviewer |
| .NET Reviewers | `dotnet` | DotNetSpecialist |

The default reviewers do not depend on the stack, so they apply to every repository. A stack's specialist matches the stack label, like the stack's quality gate config: add one per stack you use (for example a Python reviewer with `python`).

Resolution: all Reviewer Configurations whose labels intersect with the job's labels are applied sequentially (ANY match). Each configuration contains one or more review agents that run in order. A configuration with empty MatchLabels acts as a global fallback (applies to all jobs). When no reviewer config matches (or all are disabled), or the matching configs define no review agents, the review phase is **skipped**. A warning is logged (`Pipeline {RunId} no reviewer configurations matched — review phase skipped`). An implementation run then opens its PR without a review; a PR review run posts a comment on the pull request that says why no review ran. To ensure review always runs, keep the default reviewer configuration (`MatchLabels = []`) enabled in Settings → Label Routing → Reviewer Configs.

The other code review settings (iterations, fix prompt, inline comments, acceptance criteria) are on Settings → Global Defaults → Code Review; see [Configuration — Code Review](configuration.md#code-review).

Labels are set per repository, and the same labels also pick the agent profile, which must contain every one of them. A project therefore cannot choose which of these reviewers or quality gates apply, and adding a label to a repository only to select a reviewer also changes which agent profile, and so which agent image, the repository needs. What a project can do is add its own reviewer, which checks a change against the whole project: see [PR Review — Project Review](pr-review.md#project-review).

## Agent Lifecycle Labels

The pipeline applies `agent:*` labels to GitHub issues and PRs to communicate pipeline state. Only one status label (every `agent:*` label except `agent:generated`) should be present on an issue at a time; `agent:generated` stays next to it. These labels are managed by the pipeline — setting them manually can interfere with dispatch logic.

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
| `agent:epic-review` | Decomposition plan is posted and awaiting human review | Yes |
| `agent:epic-approved` | Human approved the decomposition plan (required before sub-issues are created) | No |
| `agent:generated` | Issue was created by the pipeline (a decomposition sub-issue or a refactoring issue from a consolidation run); not a status label, so label swaps keep it | No |

**Terminal labels** (`agent:done`, `agent:error`, `agent:needs-refinement`, `agent:wont-do`, `agent:cancelled`, `agent:epic-review`) mark states that require human action to re-queue. Remove the terminal label and add `agent:next` to retry; for `agent:epic-review`, add `agent:epic-approved` to approve the plan or `agent:epic` to request a new one. `agent:needs-refinement` specifically indicates the issue body needs more detail — review the analysis confidence output before re-queuing.

**`agent:epic-approved`** is a gated label: agents may not set it via `RequestLabelChange`. Only humans can approve decomposition.

## Setting Up a New Stack

1. **Add a job template** — In `values.yaml`, add an entry to `jobTemplates[]` with the appropriate `labels`, `image`, and `providerType`
2. **Create an Agent Profile** — In Settings → Label Routing → Agent Profiles, map the labels to a provider config
3. **Create a QGC** — In Settings → Label Routing → Quality Gate Configs, define the build/test commands for the stack
4. **Create a Reviewer Config** (optional) — In Settings → Label Routing → Reviewer Configs, define stack-specific review agents
5. **Configure the repository** — Set `requiredLabels` on the repository provider config (include both stack and version labels)
6. **Create a Pipeline Job Template** — On the Pipelines page (Pipeline Job Templates section), link the issue provider, repo provider, and optional brain/CI providers
