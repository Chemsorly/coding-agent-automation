# Coding Agent Automation

An automated development pipeline that uses AI coding agents to implement issues end-to-end: analyze the issue, generate code, run quality gates, and create a pull request — all orchestrated through a web UI running in Docker.

## How It Works

1. **Pick an issue** — Select an issue from the web UI (or let closed-loop mode pick the next one automatically)
2. **Analysis** — The agent reads the issue, explores the codebase, and writes an analysis
3. **Implementation** — The agent implements the changes, guided by the analysis
4. **Quality gates** — Automated checks run: build, tests, code review (multi-agent), external CI
5. **Retry loop** — If quality gates fail, the agent gets feedback and retries (configurable max retries)
6. **Pull request** — On success, a PR is created with the changes, linked to the original issue

## Key Concepts

- **Brain repository** — A `.brain/` folder in the target repo containing markdown files (lessons learned, architecture decisions, project context). Agents read it before starting and write to it after completing a run, accumulating knowledge across runs.
- **Confidence gate** — After analysis, the pipeline evaluates whether the issue is clear enough to implement. Vague or blocked issues are rejected with specific feedback rather than producing bad code.
- **Quality gates** — Automated checks that must pass before a PR is created: compilation, tests, code coverage, and optionally external CI pipelines.
- **Closed-loop mode** — The pipeline polls for labeled issues and processes them autonomously without manual dispatch. Configurable poll interval and backoff.
- **PR review pipeline** — A parallel workflow that picks up pull requests labeled `agent:next` and runs automated multi-agent code review, posting findings as a PR review comment. Uses the same dispatch mechanism and label lifecycle as the implementation pipeline.
- **Epic decomposition pipeline** — A two-phase workflow that breaks down high-level epics into implementation-ready sub-issues. Phase 1 produces a validated plan for human review; Phase 2 creates sub-issues after approval. See [Epic Decomposition Pipeline](#epic-decomposition-pipeline) below.
- **Label routing** — Repository labels determine which agent container handles the job, which quality gates run, and which review agents are used.
- **Harness suggestions** — Automated improvement recommendations for the pipeline itself, derived from accumulated run feedback patterns.

## PR Review Pipeline

The pipeline can also perform automated code review on pull requests. This uses the same `agent:next` label mechanism as the implementation pipeline but runs a shorter workflow focused on review.

### How to Use

1. Add the `agent:next` label to any open pull request
2. The pipeline picks up the PR on the next poll cycle
3. The agent clones the repo, checks out the PR branch, and runs multi-agent code review
4. Review findings are posted as a PR review comment
5. The label transitions: `agent:next` → `agent:in-progress` → `agent:done` (or `agent:error`)

### Expected Workflow

```
Label PR with agent:next → Pipeline picks up PR → Clone → Checkout PR branch
  → [Brain sync] → Extract linked issues → Code review → Post findings → Done
```

Draft PRs are included in review dispatch (a warning is shown in the UI). To re-review after changes, remove `agent:done` and re-add `agent:next`.

### Inline Review Comments

The pipeline can post code review findings as native inline comments on specific file:line positions in the diff, giving PR authors precise feedback at the exact location of each issue — in addition to the summary body comment.

#### How to Enable

Inline comments are enabled by default. To disable them, set `InlineComments.Enabled` to `false` in the Code Review configuration (via the web UI under Settings → Quality Gates → Code Review → Inline Review Comments, or directly in the pipeline config JSON):

```json
{
  "CodeReview": {
    "MaxIterations": 2,
    "ReviewIsolation": "Isolated",
    "InlineComments": {
      "Enabled": true,
      "SeverityThreshold": "Warning",
      "MaxInlineComments": 15,
      "OrderBySeverity": true,
      "MaxRetries": 1
    }
  }
}
```

#### Configuration Options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `Enabled` | bool | `true` | Master switch. When false, body-only reviews are posted (existing behavior) |
| `SeverityThreshold` | enum | `Warning` | Minimum severity for inline posting. Options: `Suggestion`, `Warning`, `Critical` |
| `MaxInlineComments` | int | `15` | Maximum inline comments per review (range: 1–50). Highest-severity findings are prioritized |
| `OrderBySeverity` | bool | `true` | Sort findings by severity (Critical first) when selecting which to post inline |
| `MaxRetries` | int | `1` | Times to re-ask the agent for structured output if it doesn't include file:line references (range: 0–5). Each retry adds one LLM API call per agent |

#### Behavior

- **When enabled**: Review agents are instructed to output findings in `[SEVERITY] path/to/file.ext:LINE — message` format. The pipeline parses these, filters by severity threshold, caps at the configured limit, and posts them as inline comments via the GitHub Pull Request Reviews API.
- **When disabled**: The pipeline posts a single body-level review comment (existing behavior). No parsing or prompt enhancement occurs.
- **Graceful degradation**: If structured output parsing fails, the GitHub API rejects inline comments (HTTP 422), or any other error occurs in the inline path, the pipeline falls back to body-only submission. Inline comments never fail the pipeline.
- **Findings without location**: Findings that don't reference a specific file:line appear only in the body summary, regardless of inline settings.

### Configuration

Each pipeline job template has three independent toggles:

| Property | Default | Effect |
|----------|---------|--------|
| `ImplementationEnabled` | `true` | Template processes issues for implementation |
| `ReviewEnabled` | `true` | Template processes PRs for code review |
| `DecompositionEnabled` | `false` | Template processes epics for decomposition |

Set `ReviewEnabled: false` to disable PR review for a template, or `ImplementationEnabled: false` to create a review-only template. See [Pipeline Orchestration](docs/pipeline-orchestration.md) for details.

## Epic Decomposition Pipeline

The pipeline can decompose high-level epics into implementation-ready sub-issues. This bridges the gap between broad goals and the atomic issues the implementation pipeline requires.

### How to Use

1. **Label an epic** — Add the `agent:epic` label to a GitHub issue describing a high-level feature or goal
2. **Phase 1 (Analysis)** — The pipeline picks up the epic, explores the codebase, and posts a decomposition plan as a comment on the epic
3. **Review the plan** — The epic transitions to `agent:epic-review`. Review the proposed sub-issues on GitHub
4. **Approve or reject** — To approve, swap the label to `agent:epic-approved`. To request changes, post a comment with feedback and swap back to `agent:epic`
5. **Phase 2 (Creation)** — After approval, the pipeline creates implementation-ready sub-issues with dependencies resolved

### Two-Phase Workflow

```
Label epic with agent:epic → Phase 1: Clone → Brain sync → Download open issues
  → Agent explores codebase → Adversarial review → Post plan comment
  → Label: agent:epic-review (awaiting human approval)

Approve: swap to agent:epic-approved → Phase 2: Clone → Brain sync
  → Agent generates sub-issue JSON → Parse & validate → Create issues sequentially
  → Post summary comment → Label: agent:done
```

### Label State Machine

| Current Label | Trigger | Next Label |
|---------------|---------|------------|
| `agent:epic` | Phase 1 dispatched | `agent:in-progress` |
| `agent:in-progress` | Phase 1 success | `agent:epic-review` |
| `agent:in-progress` | Phase 1/2 failure | `agent:error` |
| `agent:epic-review` | User approves | `agent:epic-approved` |
| `agent:epic-review` | User requests re-analysis | `agent:epic` |
| `agent:epic-approved` | Phase 2 dispatched | `agent:in-progress` |
| `agent:in-progress` | Phase 2 success | `agent:done` |
| `agent:error` | User retries Phase 1 | `agent:epic` |
| `agent:error` | User retries Phase 2 | `agent:epic-approved` |

### Approval Process

After Phase 1 posts the decomposition plan:

- **Approve**: Remove `agent:epic-review`, add `agent:epic-approved` → Phase 2 runs automatically
- **Request changes**: Post a comment on the epic with feedback, then remove `agent:epic-review` and add `agent:epic` → Phase 1 re-runs with your feedback as context
- **The plan comment is updated** (not duplicated) on re-runs, identified by the `<!-- agent:decomposition-plan -->` marker

### Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DecompositionEnabled` | `bool` | `false` | Enable decomposition polling for this template |
| `MaxDecompositionSubIssues` | `int` | `5` | Maximum sub-issues per epic (range: 1–20) |
| `MaxConcurrentDecompositions` | `int` | `2` | Maximum simultaneous decomposition runs |
| `DecompositionTimeout` | `TimeSpan` | `15 min` | Timeout for each decomposition phase |
| `MaxOpenIssuesForContext` | `int` | `50` | Open issues downloaded for deduplication context |

Example template configuration:
```json
{
  "Name": "Full Pipeline with Decomposition",
  "Enabled": true,
  "ImplementationEnabled": true,
  "ReviewEnabled": true,
  "DecompositionEnabled": true
}
```

See [Pipeline Orchestration — Epic Decomposition](docs/pipeline-orchestration.md#epic-decomposition-pipeline) for the full technical reference.

## Documentation

Detailed documentation lives in the [`docs/`](docs/) folder. Suggested reading order:

1. [Pipeline Orchestration](docs/pipeline-orchestration.md) — Core state machine, step descriptions, retry logic, error handling
2. [Issue Workflows](docs/github-issue-workflows.md) — Label system, user flows, closed-loop mode
3. [Label Routing](docs/label-routing.md) — Label hierarchy, agent selection, quality gate configs, setting up new stacks
4. [Configuration](docs/configuration.md) — Pipeline settings, job templates, MCP server support
5. [Feedback & Consolidation](docs/feedback-and-consolidation.md) — Agent feedback loops, brain consolidation, refactoring detection
6. [Observability](docs/observability.md) — Metrics, traces, OTLP configuration, verification

## Features

- **Multi-agent architecture** — Multiple agent containers run in parallel, picking jobs from a shared queue
- **Multi-stack support** — Label-based routing dispatches jobs to the right agent (dotnet, python, java) with stack-specific quality gates and review agents
- **PR review pipeline** — Automated code review for pull requests using the same multi-agent review infrastructure, triggered by labeling PRs with `agent:next`
- **Epic decomposition pipeline** — Two-phase workflow that breaks epics into implementation-ready sub-issues with human approval checkpoint, triggered by labeling issues with `agent:epic`
- **Brain repository** — Shared knowledge repo that agents read/write across runs
- **Multi-agent code review** — Specialized review agents (Correctness, Security, AcceptanceCriteria, etc.) analyze changes sequentially
- **Confidence gate** — Rejects vague issues with specific feedback before attempting implementation
- **Issue dependency tracking** — Issues referencing `Blocked by #N`, `Depends on #N`, `Requires #N`, or `After #N` in their body are automatically held until all referenced issues are closed
- **Closed-loop automation** — Polls for labeled issues and PRs, processing them autonomously
- **PR rework** — Re-queue an issue with an open PR to incorporate review feedback
- **External CI integration** — Optionally waits for CI pipelines to pass before creating the final PR
- **Agent feedback loops** — Structured feedback collected after every run for continuous improvement
- **Consolidation loops** — Brain pruning, refactoring detection, and harness suggestions
- **Real-time web UI** — Live output streaming, pipeline step sidebar, agent monitoring

## Quick Start

### Prerequisites

- **Docker** — For building and running the application
- **.NET 10 SDK** — For local development (optional if only running via Docker)
- **Issue tracker credentials** — App credentials for issue/repository access and PR creation (e.g., a GitHub App with Issues + Contents + Pull Requests permissions)
- **Agent CLI authentication** — Each agent container needs CLI auth tokens (see First-Time Setup below)

### Run with Docker Compose

```bash
# 1. Create a .env file with a shared secret for orchestrator↔agent authentication
#    (any random string works — it's a symmetric key for internal API auth)
echo "AGENT_API_KEY=$(openssl rand -hex 32)" > .env

# 2. Start the orchestrator and all agent containers
docker compose up --build
```

Open `http://localhost:8080` in your browser.

The `docker-compose.yml` defines 5 services: 1 orchestrator + 2 .NET agents + 1 Python agent + 1 Java agent. To add more agents, copy a service definition with a new name and volume — don't use `--scale` (each agent needs its own named volume to avoid SQLite corruption).

### First-Time Setup

1. **Authenticate the agent CLI** — Exec into each agent container and run the login flow:
   ```bash
   docker exec -it coding-agent-automation-agent-dotnet-1-1 kiro-cli login
   ```
   Follow the device code flow in your browser. Auth tokens persist via the volume mount, so this is a one-time step per agent.

2. **Configure providers** — Go to Settings → Providers in the web UI and set up:
   - **Issue Provider** — Connects to your issue tracker (requires app credentials)
   - **Repository Provider** — Connects to your code host for clone/push operations
   - **Agent Provider** — Points to the agent CLI binary (pre-configured in Docker)
   - **Pipeline Provider** (optional) — Connects to your CI system for external checks

3. **Configure label routing** — Go to Settings → Label Routing and set up Agent Profiles, Quality Gate Configs, and Reviewer Configs for your stack.

4. **Create a pipeline job template** — Go to Agent Coding and add a template linking your providers.

5. **Start a run** — Select a template, browse issues, and dispatch. Or enable closed-loop mode to process `agent:next` issues automatically.

## Provider Configuration

The pipeline supports multiple provider backends. Each provider type requires specific settings.

### GitHub Configuration

```json
{
  "providerType": "GitHub",
  "settings": {
    "owner": "my-org",
    "repo": "my-repo",
    "appId": "123456",
    "privateKeyBase64": "base64-encoded-pem-key",
    "installationId": "78901234"
  }
}
```

### GitLab Configuration

```json
{
  "providerType": "GitLab",
  "settings": {
    "apiUrl": "https://gitlab.com",
    "accessToken": "glpat-xxxxxxxxxxxxxxxxxxxx",
    "projectId": "12345",
    "baseBranch": "main"
  }
}
```

## Project Structure

```
src/
  CodingAgentWebUI/                — Blazor Server app (UI, DI wiring, entry point)
  CodingAgentWebUI.Pipeline/       — Core library (interfaces, models, orchestration)
  CodingAgentWebUI.Infrastructure/ — Provider implementations (see table below)
  CodingAgentWebUI.Orchestration/  — Agent registry, job dispatch, run tracking
  CodingAgentWebUI.Agent/          — Agent worker container (SignalR client, CLI invocation)
  KiroCliLib/                      — Shared library (agent CLI process management)
tests/
  CodingAgentWebUI.UnitTests/              — WebUI unit tests
  CodingAgentWebUI.Pipeline.UnitTests/     — Pipeline core unit + property tests
  CodingAgentWebUI.Infrastructure.UnitTests/ — Infrastructure unit tests
  CodingAgentWebUI.Agent.UnitTests/        — Agent unit tests
  CodingAgentWebUI.IntegrationTests/       — Integration tests (bUnit)
  CodingAgentWebUI.E2ETests/               — End-to-end tests
  KiroCliLib.UnitTests/                    — KiroCliLib unit tests
dockerfiles/
  webui.Dockerfile                    — Orchestrator (web UI)
  kiro/
    agent-kiro-dotnet10.Dockerfile      — Kiro CLI .NET 10 agent container
    agent-kiro-python312.Dockerfile     — Kiro CLI Python 3.12 agent container
    agent-kiro-java21.Dockerfile        — Kiro CLI Java 21 agent container
  opencode/
    agent-opencode-dotnet10.Dockerfile  — OpenCode .NET 10 agent container
    agent-opencode-java21.Dockerfile    — OpenCode Java 21 agent container
    agent-opencode-python312.Dockerfile — OpenCode Python 3.12 agent container
    entrypoint.sh                       — OpenCode agent entrypoint script
  e2e-tests.Dockerfile                — E2E test runner
config/
  pipeline/          — Provider configs, quality gates, profiles, run history
  appsettings.json   — Application configuration
```

## Volume Mounts

### Orchestrator

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Pipeline config | `/app/config/pipeline` | Provider configs, quality gates, profiles, run history (persists across restarts) |

### Agent Containers

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Agent CLI auth | `/home/ubuntu/.local/share/kiro-cli` | Agent CLI login tokens |
| SSO cache | `/home/ubuntu/.aws` | SSO cache for agent CLI auth (mounted read-only) |

Each agent container needs its own CLI data volume to avoid SQLite corruption from concurrent access. Workspaces are created inside the container at `/app/workspaces/` — no volume mount needed.

## Architecture

The application follows Clean Architecture with a multi-container deployment:

```mermaid
graph TB
    Browser[Web Browser]
    
    subgraph Orchestrator["Orchestrator Container"]
        WebUI[WebUI<br/>Blazor Server]
        Orch[Orchestration<br/>dispatch, tracking]
        Infra[Infrastructure<br/>providers, config store]
        Core[Pipeline Core<br/>interfaces, models]
    end
    
    subgraph Agents["Agent Containers"]
        A1[Agent .NET<br/>KiroCliLib]
        A2[Agent Python<br/>KiroCliLib]
        A3[Agent Java<br/>KiroCliLib]
    end
    
    Browser -->|HTTP/SignalR| WebUI
    WebUI --> Orch
    Orch --> Infra
    Orch --> Core
    Infra --> Core
    Orch -->|SignalR<br/>job dispatch| A1
    Orch -->|SignalR<br/>job dispatch| A2
    Orch -->|SignalR<br/>job dispatch| A3
```

- **Pipeline (Core)** — Interfaces, models, orchestration services. Zero infrastructure dependencies.
- **Infrastructure** — Provider implementations (see table below).
- **Orchestration** — Agent registry, job dispatch, run tracking, token vending (issues short-lived auth tokens to agent containers for API calls).
- **WebUI** — Blazor Server components, SignalR hub for agent communication.
- **Agent** — Standalone worker container connecting via SignalR, executes coding agent CLI.
- **KiroCliLib** — Shared library for agent CLI process management and output parsing.

### Provider Implementations

The pipeline defines abstract provider interfaces in the core layer. Concrete implementations live in the Infrastructure and Agent projects.

| Provider | Interface | Purpose | Implementations |
|----------|-----------|---------|----------------|
| Issue | `IIssueProvider` | Fetch issues, manage labels, post comments, create issues | GitHub (Octokit), GitLab (NGitLab) |
| Repository | `IRepositoryProvider` | Clone repos, create branches, commit/push, create MRs/PRs | GitHub (LibGit2Sharp + Octokit), GitLab (LibGit2Sharp + NGitLab) |
| Agent | `IAgentProvider` | Execute coding agent for analysis/implementation/review | Kiro CLI (process wrapper) |
| Pipeline/CI | `IPipelineProvider` | Check external CI status for a branch/commit | GitHub Actions (Octokit), GitLab CI (NGitLab) |

Adding a new implementation requires implementing the corresponding interface and registering it in the provider factory. GitLab is a working example of this extensibility — see the `src/CodingAgentWebUI.Infrastructure/GitLab/` directory for the full implementation pattern.

## Testing

```bash
# Run all tests
dotnet test

# Run in Docker (Linux)
docker run --rm -v "${PWD}:/app" -w /app mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
```

## Development

```bash
dotnet build
dotnet run --project src/CodingAgentWebUI
```

### Code Conventions

- Microsoft C# coding conventions, SOLID principles
- Immutability patterns (`init`-only properties, `IReadOnlyList<T>`)
- Input validation with `ArgumentNullException.ThrowIfNull`
- Async I/O with `CancellationToken` propagation

## License

This project is for internal use.
