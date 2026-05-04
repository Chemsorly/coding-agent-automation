# Coding Agent Automation

Hello, World! 👋

An automated development pipeline that uses AI coding agents (Kiro CLI) to implement GitHub issues end-to-end: analyze the issue, generate code, run quality gates, and create a pull request — all orchestrated through a Blazor Server web UI running in Docker.

## How It Works

1. **Pick an issue** — Select a GitHub issue from the web UI (or let the closed-loop mode pick the next `agent:next` issue automatically)
2. **Analysis** — The agent reads the issue, explores the codebase, and writes an analysis with a planned approach
3. **Implementation** — The agent implements the changes, guided by the analysis
4. **Quality gates** — Automated checks run: build, tests, code review (multi-agent), external CI
5. **Retry loop** — If quality gates fail, the agent gets feedback and retries (configurable max retries)
6. **Pull request** — On success, a PR is created with the changes, linked to the original issue

The pipeline runs inside a Docker container with Kiro CLI installed. The web UI provides real-time visibility into each step.

## Features

- **Multi-agent architecture** — Multiple agent containers run in parallel, each picking up jobs from a shared queue. Scale horizontally by adding more agent services to `docker-compose.yml`.
- **Multi-stack support** — Label-based routing dispatches jobs to the right agent (dotnet, python, java) and applies stack-specific quality gates, review agents, and build commands. Coverage reporting supports Cobertura XML and JaCoCo XML formats with configurable file discovery.
- **Brain repository** — A shared knowledge repo (`.brain/`) that agents read before starting and write to after completing a run. Accumulates lessons learned, architecture decisions, and project context across runs.
- **Multi-agent code review** — After implementation, specialized review agents (Correctness, SecurityReviewer) analyze the changes sequentially. Critical findings are auto-fixed; warnings become TODO comments. Language-specific reviewers (e.g., DotNetSpecialist) can be added via label-matched Reviewer Configurations.
- **Confidence gate** — Before writing code, the agent assesses whether the issue is clear enough. Vague issues get labeled `agent:needs-refinement` with specific feedback posted to GitHub.
- **Pipeline job templates** — Define provider combinations for each repository. The UI shows a live preview of which quality gates, reviewers, and profiles will be assigned based on the repo's labels.
- **Closed-loop automation** — The pipeline polls for `agent:next` issues and processes them autonomously with configurable rate limits and backoff.
- **PR rework** — Re-queue an issue that already has an open PR, and the agent merges from main, incorporates review feedback, and pushes to the existing branch.
- **External CI integration** — Optionally waits for GitHub Actions to pass before creating the final PR. Failures trigger the retry loop.
- **Real-time web UI** — Blazor Server dashboard with live output streaming, pipeline step sidebar, agent monitoring, and manual dispatch drawer.
- **Label-based routing** — Repository labels determine agent selection (superset match), quality gate configs (any-match), reviewer configs (any-match), and agent profiles (all-match).

## Prerequisites

- **Docker** — For building and running the application
- **.NET 10 SDK** — For local development (optional if only running via Docker)
- **GitHub App** — For issue/repository access and PR creation (configured in Settings)
- **Kiro CLI authentication** — The container needs Kiro CLI auth tokens (see First-Time Setup)

## Quick Start

### Run with Docker Compose (recommended)

```bash
# 1. Create a .env file with a shared API key for agent authentication
echo "AGENT_API_KEY=your-secret-key-here" > .env

# 2. Start the orchestrator and all agent containers
docker compose up --build
```

Open `http://localhost:8080` in your browser.

### First-time setup

1. **Authenticate Kiro CLI** — On first run, exec into each agent container and run `kiro-cli login`. The auth tokens are persisted via the `kiro-cli-data` volume mounts, so you only need to do this once per agent.
2. **Configure providers** — Go to **Settings → Providers** in the web UI and set up your Issue Provider (GitHub App), Repository Provider, Agent Provider, and Pipeline Provider.
3. **Configure label routing** — Go to **Settings → Label Routing** and set up Agent Profiles, Quality Gate Configs, and Reviewer Configs for your stack.
4. **Create a pipeline job template** — Go to **Agent Coding** and add a template linking your providers.
5. **Start a run** — Select a template, browse issues, and dispatch — or start the closed-loop mode.

## Volume Mounts

### Orchestrator

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Pipeline config | `/app/config/pipeline` | Provider configs, quality gates, profiles, reviewers, run history (persists across restarts) |

### Agent Containers

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Kiro CLI auth | `/home/ubuntu/.local/share/kiro-cli` | Kiro CLI login tokens (persists across container restarts) |
| AWS SSO | `/home/ubuntu/.aws` | AWS SSO cache and config for Kiro CLI auth (read-only) |

Each agent container needs its own Kiro CLI data volume (e.g., `kiro-cli-data-1`, `kiro-cli-data-2`) to avoid SQLite corruption from concurrent access.

Without the pipeline config mount on the orchestrator, any providers configured in Settings will be lost when the container restarts.

Workspaces are created inside the container at `/app/workspaces/` (configurable via `workspaceBaseDirectory` in pipeline config). The pipeline clones a fresh copy of the repository for each run, so no workspace volume mount is needed. Successful workspaces are cleaned up automatically; failed ones are retained based on the `failedWorkspaceRetentionDays` setting.

## Project Structure

```
src/
  CodingAgentWebUI/              — Blazor Server app: UI components, DI wiring, entry point
  CodingAgentWebUI.Pipeline/     — Core library: interfaces, models, orchestration services
  CodingAgentWebUI.Infrastructure/ — Provider implementations (GitHub, Git, JSON persistence)
  CodingAgentWebUI.Agent/        — Agent container: SignalR client, job execution, Kiro CLI invocation
  KiroCliLib/                    — Shared library: Kiro CLI process management, output parsing
tests/
  CodingAgentWebUI.UnitTests/              — WebUI unit tests
  CodingAgentWebUI.Pipeline.UnitTests/     — Pipeline core unit + property tests
  CodingAgentWebUI.Infrastructure.UnitTests/ — Infrastructure unit tests
  CodingAgentWebUI.Agent.UnitTests/        — Agent unit tests
  CodingAgentWebUI.IntegrationTests/       — Integration tests (bUnit, full-stack)
  KiroCliLib.UnitTests/                    — KiroCliLib unit tests
config/
  pipeline/            — Provider configs, quality gates, profiles, reviewers, run history
  appsettings.json     — Application configuration
dockerfiles/
  webui.Dockerfile           — Orchestrator (Blazor Server web UI)
  agent-dotnet10.Dockerfile  — .NET 10 agent container
  agent-python312.Dockerfile — Python 3.12 agent container
  agent-java21.Dockerfile    — Java 21 agent container
```

## User Interaction via GitHub Issues

The pipeline is driven entirely through GitHub issue labels. Users never interact with the pipeline directly — they manage issues on GitHub, and the pipeline reacts to label changes.

### Labels

The pipeline uses these `agent:*` labels (created automatically on first run):

| Label | Color | Meaning |
|-------|-------|---------|
| `agent:next` | 🟢 Green | Issue is queued for the pipeline to pick up |
| `agent:in-progress` | 🔵 Blue | Pipeline is actively working on this issue |
| `agent:error` | 🔴 Red | Pipeline failed (build errors, timeout, etc.) |
| `agent:needs-refinement` | 🟡 Yellow | Confidence gate rejected — issue needs more detail |
| `agent:wont-do` | ⚪ Gray | Agent determined no code changes are needed |
| `agent:done` | 🔵 Blue | Pipeline completed, PR awaiting review |
| `agent:cancelled` | 🟣 Light blue | Pipeline run was cancelled by user |

Only one `agent:*` label should be present on an issue at a time. The pipeline swaps labels atomically (removes all, then adds the new one).

### Flow 1: Happy Path

1. **User** adds `agent:next` label to a GitHub issue
2. **Pipeline** picks it up (manually via the web UI, or automatically in closed-loop mode)
3. **Pipeline** swaps label to `agent:in-progress`
4. **Pipeline** analyzes the issue, generates code, runs quality gates, creates a PR
5. **Pipeline** adds `agent:done` label on success
6. **User** reviews and merges the PR

### Flow 2: Confidence Gate Rejection (Needs Refinement)

1. **User** adds `agent:next` label
2. **Pipeline** runs the analysis agent, which determines the issue is too vague or has blockers
3. **Pipeline** posts two comments to the issue:
   - `## 🤖 Agent Analysis` — the agent's analysis of the codebase
   - `## ⚠️ Analysis Gate: Needs Refinement` — blocking issues and concerns
4. **Pipeline** swaps label to `agent:needs-refinement`
5. **User** reads the feedback, edits the issue description to address blocking issues
6. **User** removes `agent:needs-refinement` and re-adds `agent:next`
7. **Pipeline** detects the re-queue (gate rejection comment is newer than analysis comment), forces a fresh analysis

### Flow 3: Confidence Gate — Won't Do

1. **User** adds `agent:next` label
2. **Pipeline** analyzes the issue and determines no code changes are needed (bug already fixed, feature already exists, working as designed)
3. **Pipeline** posts analysis + `## 🚫 Analysis Gate: Won't Do` comment with reasoning
4. **Pipeline** swaps label to `agent:wont-do`, marks run as Completed
5. **User** can disagree: remove `agent:wont-do`, re-add `agent:next` to force re-analysis

### Flow 4: Quality Gate Failure with Draft PR

1. **Pipeline** generates code but quality gates fail (build errors, test failures, etc.)
2. **Pipeline** retries up to `maxRetries` times, giving the agent error feedback each time
3. If retries are exhausted, **pipeline** creates a **draft PR** with the failing code
4. **Pipeline** swaps label to `agent:error`
5. **User** can review the draft PR, fix issues manually, or close it

### Flow 5: Pipeline Error

1. **Pipeline** encounters an unrecoverable error (clone failure, timeout, provider error)
2. **Pipeline** swaps label to `agent:error`, records the failure reason
3. **User** can investigate via the web UI output log, fix the underlying issue, then remove `agent:error` and re-add `agent:next`

### Flow 6: PR Rework

1. **User** adds `agent:next` label to an issue that already has an open agent-created PR
2. **Pipeline** detects the existing PR by matching the branch name pattern (`feature/auto-{issueNumber}-*`)
3. **Pipeline** swaps label to `agent:in-progress`, enters rework mode
4. **Pipeline** checks out the existing PR branch and merges from main
5. **Pipeline** builds a rework prompt containing merge conflict info (if any) and/or PR review feedback
6. **Pipeline** re-runs code generation and quality gates using the rework prompt
7. **Pipeline** pushes to the existing branch (updates the PR automatically) and refreshes the PR body with current quality gate results
8. **Pipeline** adds `agent:done` label on success

If the user wants a fresh run instead of rework, they close the existing PR first, then add `agent:next`. The pipeline only enters rework mode when an open agent PR exists for the issue.

### Closed-Loop Mode

When the pipeline loop is active, it polls for `agent:next` issues automatically:

- Issues are processed FIFO (oldest `CreatedAt` first)
- Issues with `agent:error` or `agent:needs-refinement` are **skipped** (even if they also have `agent:next`)
- One issue is processed at a time; the loop waits for the current run to finish before starting the next
- Configurable poll interval, max runs per cycle, and backoff on failures

## Pipeline Orchestration

The pipeline is a state machine that progresses through a fixed sequence of steps, with decision points that can branch to terminal states.

```mermaid
stateDiagram-v2
    direction TB

    [*] --> Created
    Created --> CloningRepository
    CloningRepository --> SyncingBrainRepoPreRun
    SyncingBrainRepoPreRun --> CreatingBranch
    CreatingBranch --> AnalyzingCode
    AnalyzingCode --> PostingAnalysis

    state ConfidenceGate <<choice>>
    PostingAnalysis --> ConfidenceGate
    ConfidenceGate --> GeneratingCode : ready
    ConfidenceGate --> Failed : not_ready (needs-refinement)
    ConfidenceGate --> Completed : wont_do (wont-do)

    GeneratingCode --> ReviewingCode
    ReviewingCode --> RunningQualityGates

    state QualityGateDecision <<choice>>
    RunningQualityGates --> QualityGateDecision
    QualityGateDecision --> PreparingForPullRequest : all passed
    QualityGateDecision --> GeneratingCode : failed, retries remaining
    QualityGateDecision --> CreatingPullRequest : failed, retries exhausted (draft PR)

    PreparingForPullRequest --> RunningFinalQualityGates
    state RunningFinalQualityGates <<choice>>
    RunningFinalQualityGates --> CreatingPullRequest : all passed
    RunningFinalQualityGates --> GeneratingCode : failed, retries remaining
    RunningFinalQualityGates --> CreatingPullRequest : failed, retries exhausted (draft PR)

    CreatingPullRequest --> ReflectingOnRun
    ReflectingOnRun --> SyncingBrainRepoPostRun
    SyncingBrainRepoPostRun --> Completed

    Completed --> [*]
    Failed --> [*]
    Cancelled --> [*]

    note right of CloningRepository
        Label swapped to agent in-progress
    end note
    note right of ConfidenceGate
        blockingIssues non-empty forces not_ready
    end note
    note right of QualityGateDecision
        Agent gets error feedback and fixes before re-check
    end note
    note left of CreatingPullRequest
        Draft PR sets agent error label. Normal PR adds agent done label.
    end note
    note left of ReflectingOnRun
        Only if brain repo configured and not read-only
    end note
```

### Pipeline Steps

```
Created → CloningRepository → SyncingBrainRepoPreRun → CreatingBranch
  → AnalyzingCode → PostingAnalysis → [Confidence Gate]
  → GeneratingCode → ReviewingCode → RunningQualityGates → [Quality Gate Decision]
  → PreparingForPullRequest → [Final Quality Gate]
  → CreatingPullRequest → ReflectingOnRun → SyncingBrainRepoPostRun → Completed
```

Each step is represented by the `PipelineStep` enum. The pipeline tracks both the current step and a `HighWaterMark` (highest step ever reached), which the UI uses to show revisited steps during retries.

### State Descriptions

| Step | What Happens |
|------|-------------|
| **Created** | Run initialized, providers resolved and validated |
| **CloningRepository** | Repository cloned to a fresh workspace directory. Label swapped to `agent:in-progress` |
| **SyncingBrainRepoPreRun** | Brain repository synced into workspace (if configured). Non-fatal on failure |
| **CreatingBranch** | Feature branch created from default branch (format: `agent/{issue}-{slug}-{runid}`) |
| **AnalyzingCode** | Agent analyzes the issue and codebase, writes `analysis.md` and `analysis-assessment.json` |
| **PostingAnalysis** | Analysis comment posted to the GitHub issue |
| **GeneratingCode** | Agent implements the changes. Also used during quality gate retries |
| **ReviewingCode** | Multi-agent code review: each review agent writes findings, then a fix agent addresses `[CRITICAL]` items |
| **RunningQualityGates** | Build, tests, coverage, and external CI checks run |
| **PreparingForPullRequest** | Agent cleans up the working directory (removes debug artifacts, unused code, formatting). Quality gates run one final time after cleanup |
| **CreatingPullRequest** | PR created (normal or draft). Blacklisted file detection happens here |
| **ReflectingOnRun** | Agent reviews the entire run and enriches `.brain/` knowledge (if brain repo configured) |
| **SyncingBrainRepoPostRun** | Brain updates committed and pushed to brain repository |
| **Completed** | Terminal state — run succeeded (or `wont_do` assessment) |
| **Failed** | Terminal state — unrecoverable error or retries exhausted |
| **Cancelled** | Terminal state — user cancelled the run |

### Confidence Gate

After the analysis phase, the pipeline evaluates the agent's structured assessment (`analysis-assessment.json`):

```mermaid
flowchart TD
    PA[PostingAnalysis] --> CG{Confidence Gate}
    CG -->|ready| GC[GeneratingCode]
    CG -->|not_ready| F[Failed\nagent needs-refinement]
    CG -->|wont_do| C[Completed\nagent wont-do]
```

- **`ready`** — proceed to code generation
- **`not_ready`** — abort, label `agent:needs-refinement`, post blocking issues to GitHub
- **`wont_do`** — mark Completed, label `agent:wont-do`, post reasoning to GitHub

Override rule: if `blockingIssues` is non-empty, the gate forces `not_ready` regardless of the recommendation value. Unknown recommendation values (e.g. typos) fall through as `ready` (fail-open design).

### Quality Gate Retry Loop

After code generation and review, quality gates run. If they fail, the pipeline enters a retry loop:

```mermaid
flowchart TD
    RQG[RunningQualityGates] --> GP{Gates Passed?}
    GP -->|yes| PREP[PreparingForPullRequest\nagent cleanup]
    GP -->|no| RL{retries remaining?}
    RL -->|yes| GC[GeneratingCode\nagent gets error feedback]
    RL -->|no| DPR[Draft PR\nagent error label]
    GC --> RQG2[RunningQualityGates\nre-validate]
    PREP --> FQG[RunningFinalQualityGates]
    FQG -->|pass| PR[CreatingPullRequest]
    FQG -->|fail| RL2{retries remaining?}
    RL2 -->|yes| GC
    RL2 -->|no| DPR
```

Quality gates checked (in order):
1. **Compilation** — Build command must succeed with 0 errors
2. **Tests** — Test command must have 0 failures
3. **Coverage** — Code coverage must meet `coverageThreshold` (if configured). Supports Cobertura XML (Python, .NET) and JaCoCo XML (Java) formats
4. **External CI** — GitHub Actions must pass (if enabled). Requires commit + push before checking

External CI is only evaluated after local gates (compilation, tests, coverage) pass. If external CI fails, it does not enter the agent retry loop — the failure goes straight to a draft PR. Only local gate failures trigger retries with agent error feedback.

The retry prompt includes the full gate failure details and points the agent to diagnostic output files. Each retry attempt is a `--resume` call, so the agent has full conversation history.

If all retries are exhausted, a **draft PR** is created with the failing code, and the issue is labeled `agent:error`.

### Label Transitions

```mermaid
stateDiagram-v2
    direction LR
    state "no label" as none
    state "agent next" as next
    state "agent in-progress" as ip
    state "agent done" as done
    state "agent needs-refinement" as nr
    state "agent wont-do" as wd
    state "agent error" as err
    state "agent cancelled" as cancel

    none --> next : user adds label
    next --> ip : pipeline starts
    ip --> done : success
    ip --> nr : not_ready
    ip --> wd : wont_do
    ip --> err : error / timeout
    ip --> cancel : user cancels
    done --> next : user requests rework
```

### Error Handling

Any step can transition to `Failed` on error. The pipeline catches exceptions at each phase boundary and records the failure reason. Specific behaviors:

- **Clone failure** — immediate fail, no retry
- **Analysis failure** — retries up to `maxAnalysisRetries` (assessment file missing, malformed JSON, analysis too short)
- **Agent timeout** — fail with exit code 124
- **Blacklisted files** — fail if `blacklistMode` is `Fail`, warn if `Warn`
- **External CI timeout** — treated as gate failure, enters retry loop
- **Cancellation** — `OperationCanceledException` caught at top level, label set to `agent:cancelled`

## Architecture

The application follows Clean Architecture principles with a multi-container deployment:

- **Pipeline (Core)** — `CodingAgentWebUI.Pipeline` — Interfaces, models, and orchestration services. Defines the pipeline steps, provider contracts, and data models. Zero infrastructure dependencies.
- **Infrastructure** — `CodingAgentWebUI.Infrastructure` — Provider implementations (GitHub API via Octokit, JSON config store, Git operations via LibGit2Sharp). Implements the interfaces defined in Pipeline.
- **WebUI (Presentation)** — `CodingAgentWebUI` — Blazor Server components, DI wiring, SignalR hub for agent communication, and the application entry point.
- **Agent** — `CodingAgentWebUI.Agent` — Standalone container that connects to the orchestrator via SignalR, receives job assignments, and executes Kiro CLI to implement issues.
- **KiroCliLib** — Shared library for Kiro CLI process management, output parsing, and configuration. Used by the agent to invoke Kiro CLI.

## Label-Based Configuration System

The pipeline uses a hierarchical label system to route jobs to agents and determine which quality gates to run. Labels are the glue between repositories, agents, profiles, and quality gate configurations.

### Label Hierarchy

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

### How Labels Are Used

| System | Label Source | Matching Logic | Purpose |
|--------|-------------|----------------|---------|
| **Agent Selection** | Job's RequiredLabels (from repo) | Agent labels ⊇ job labels (superset) | Route job to capable agent |
| **Profile Resolution** | Agent's labels | Profile MatchLabels ⊆ agent labels (ALL match) | Determine which provider config to send |
| **QGC Resolution** | Job's RequiredLabels (from repo) | QGC MatchLabels ∩ job labels ≠ ∅ (ANY match) | Determine which quality gates to run |
| **Reviewer Resolution** | Job's RequiredLabels (from repo) | Reviewer MatchLabels ∩ job labels ≠ ∅ (ANY match) | Determine which review agents to run |

### Configured Agent Types

| Agent Type | Labels | Docker Image | SDK |
|-----------|--------|--------------|-----|
| `kiro-dotnet10` | `kiro, dotnet, dotnet10` | `dockerfiles/agent-dotnet10.Dockerfile` | .NET 10 |
| `kiro-python312` | `kiro, python, python312` | `dockerfiles/agent-python312.Dockerfile` | Python 3.12 |
| `kiro-java21` | `kiro, java, java21` | `dockerfiles/agent-java21.Dockerfile` | Java 21 |

### Agent Profiles

Agent Profiles map label sets to agent provider configs (model, timeout, CLI path). Configured in Settings → Agent Profiles.

| Profile | Match Labels | Effect |
|---------|-------------|--------|
| Kiro .NET 10 Agent | `kiro, dotnet, dotnet10` | Uses Opus model, 30min timeout |
| Kiro Python 3.12 Agent | `kiro, python, python312` | Uses Opus model, 20min timeout |
| Kiro Java 21 Agent | `kiro, java, java21` | Uses Opus model, 30min timeout |

Resolution: most specific match wins (highest label count). A profile with empty MatchLabels acts as a default/catch-all.

### Quality Gate Configurations

QGCs define per-stack quality gates. Configured in Settings → Quality Gate Configs.

| QGC | Match Labels | Compilation | Tests | Coverage |
|-----|-------------|-------------|-------|----------|
| .NET Quality Gate | `dotnet` | `dotnet build --no-restore` | `dotnet test --no-restore --no-build` | Cobertura (auto-collected via coverlet) |
| Python Quality Gate | `python` | `python -m pytest --collect-only` | `python -m pytest --cov=. --cov-report=xml` | Cobertura (`coverage.xml`) |
| Java Quality Gate | `java` | `mvn compile -q` | `mvn test -q` | JaCoCo (`target/site/jacoco/jacoco.xml`) |

Resolution: all QGCs whose labels intersect with the job's labels are applied sequentially. A polyglot repo with labels `["dotnet", "python"]` gets both the .NET and Python quality gates.

Coverage report format and file paths are configurable per QGC via `coverageReportFormat` ("cobertura" or "jacoco") and `coverageReportPaths` (explicit file globs). When not specified, convention-based discovery is used.

### Reviewer Configurations

Reviewer Configurations define per-stack code review agents. Configured in Settings → Label Routing → Reviewer Configs.

| Reviewer Config | Match Labels | Agents |
|----------------|-------------|--------|
| Default Reviewers (dotnet) | `dotnet` | Correctness, DotNetSpecialist, SecurityReviewer |

Resolution: all Reviewer Configurations whose labels intersect with the job's labels are applied sequentially (ANY match). Each configuration contains one or more review agents that run in order. A configuration with empty MatchLabels acts as a global fallback (applies to all jobs). When no reviewer config matches, the default agents (Correctness, SecurityReviewer) are used as a fallback.

### Setting Up a New Stack

1. **Create an agent container** — Add a service to `docker-compose.yml` with the appropriate `AGENT_TYPE` and `AGENT_LABELS`
2. **Create an Agent Profile** — In Settings → Label Routing → Agent Profiles, map the labels to a provider config
3. **Create a QGC** — In Settings → Label Routing → Quality Gate Configs, define the build/test commands for the stack
4. **Create a Reviewer Config** (optional) — In Settings → Label Routing → Reviewer Configs, define stack-specific review agents
5. **Configure the repository** — Set `requiredLabels` on the repository provider config (include both stack and version labels)
6. **Create a Pipeline Job Template** — In Agent Coding → Pipeline Job Templates, link the issue provider, repo provider, and optional brain/CI providers

## Pipeline Configuration

Pipeline behavior is configured in `config/pipeline/pipeline-config.json`:

| Setting | Default | Description |
|---------|---------|-------------|
| `maxRetries` | 3 | Max retry attempts when quality gates fail |
| `agentTimeout` | 02:00:00 | Maximum time for a single agent invocation |
| `codeReview.enabled` | true | Enable multi-agent code review |
| `codeReview.maxIterations` | 2 | Max review → fix cycles |
| `externalCiEnabled` | true | Wait for GitHub Actions CI to pass |
| `externalCiTimeout` | 00:15:00 | Max wait time for external CI |
| `blacklistedPaths` | .kiro, .github, .brain | Paths excluded from agent commits |
| `failedWorkspaceRetentionDays` | 7 | Days to keep failed workspaces |

### Closed-loop mode

The pipeline can run autonomously, polling for `agent:next` labeled issues and processing them sequentially:

| Setting | Default | Description |
|---------|---------|-------------|
| `closedLoopPollInterval` | 00:01:00 | How often to check for new issues |
| `closedLoopMaxRunsPerCycle` | 0 | Max issues per cycle (0 = unlimited) |
| `closedLoopMaxConsecutivePollFailures` | 5 | Failures before backing off |
| `closedLoopMaxBackoffInterval` | 00:15:00 | Max backoff between poll attempts |

### Pipeline Job Templates

Pipeline Job Templates define which provider combination to use when polling for issues. Each template links an issue provider, repository provider, and optional brain/CI providers. Multiple templates enable round-robin polling across repositories.

Templates are managed in the **Agent Coding** page. When creating or viewing a template, the UI shows a preview of which label-mapped resources (quality gates, reviewers, agent profiles) will be assigned based on the repository's labels.

| Field | Required | Description |
|-------|----------|-------------|
| Name | Yes | Display name for the template |
| Issue Provider | Yes | Which GitHub repo to poll for `agent:next` issues |
| Repo Provider | Yes | Which repository to clone and create PRs in |
| Brain Provider | No | Brain repository for knowledge persistence |
| Pipeline/CI Provider | No | External CI provider for GitHub Actions checks |

## MCP Server Support

Kiro CLI supports [MCP (Model Context Protocol)](https://modelcontextprotocol.io/) servers for extending agent capabilities. The Docker image includes `uv`/`uvx` (Python) and `npm`/`npx` (Node.js) for running MCP servers.

Configure MCP servers in your Kiro settings directory (mounted at `/home/ubuntu/.kiro/settings/mcp.json`):

```json
{
  "mcpServers": {
    "context7": {
      "command": "uvx",
      "args": ["context7-mcp@latest"],
      "env": {},
      "disabled": false,
      "autoApprove": []
    }
  }
}
```

Kiro CLI automatically discovers and starts configured MCP servers during pipeline runs. The `.kiro/` directory is in the pipeline's blacklisted paths, so MCP config and any credentials it contains are never committed.

## Testing

### Run all tests

```bash
dotnet test
```

### Run tests in Docker (Linux)

```bash
docker run --rm -v "${PWD}:/app" -w /app mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

## Development

### Local development

```bash
dotnet build
dotnet run --project src/CodingAgentWebUI
```

### Code conventions

- Microsoft C# coding conventions
- SOLID principles
- Immutability patterns (`init`-only properties, `IReadOnlyList<T>`)
- Input validation with `ArgumentNullException.ThrowIfNull`
- Async I/O with `CancellationToken` propagation

## Roadmap

See [open issues](https://github.com/Chemsorly/coding-agent-automation/issues) for planned features.

## License

This project is for internal use.
