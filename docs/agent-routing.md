# Agent Routing

Three settings decide how a piece of work runs:

- an **agent profile** says how an agent runs: the job template it runs on (and so the image and the tool), the tool's
  settings and the MCP servers;
- the **routing** says which profile, model and effort run each task type: implementation, PR review, decomposition,
  triage and consolidation;
- a repository's **stack labels** pick its quality gates and reviewers. They pick no agent.

See also: [Configuration](configuration.md) for pipeline-level settings, and [Pipeline Orchestration](pipeline-orchestration.md) for how quality gates fit into the pipeline flow.

## Agent Profiles

Settings → Agents → Agent Profiles. A profile has a name, a job template, the settings of that template's tool and MCP
servers. It has no model and no effort: the route names those, and no enabled flag: to stop work on a tool, pause its
credential (see [Credentials and Pauses](#credentials-and-pauses)). The page shows the credential each profile uses.

| Field | What it does |
|-------|--------------|
| Job template | A Helm `jobTemplates[]` entry, by its labels key (for example `dotnet,kiro`). The template names the image, `maxConcurrent` (its capacity ceiling), the resources and the tool (`providerType`). |
| Tool settings | The settings of that tool: Agent Name (Kiro), Base URL (OpenCode), Auth Mode (Claude Code: `subscription`, the default, or `apiKey`). |
| MCP servers | Added to the agent's MCP configuration, together with the project's. |

The tool is the job template's `providerType`; a profile has no tool of its own, so the two cannot disagree. Saving a
profile is refused when its job template is not in the Helm values or has no `providerType`, or when another profile has
the same name. The page flags profiles whose job template is missing, for example after a Helm change: work routed to
such a profile is not started until the template is back.

Several profiles can share one job template, and with it the image and the capacity pool, for example two Kiro profiles
with different MCP servers. The template's capacity is its Helm `maxConcurrent`, which Settings → Agents → Capacity can
lower; the runs of every profile on the template and the chats on it count toward it (see
[Configuration — Agent Capacity](configuration.md#agent-capacity)). A profile that a route names cannot be deleted; the
refusal lists the routes ("Global · Decomposition", "Repository X · Implementation").

## Routing

Settings → Agents → Routing. The table has a **Default** row and one row per task type: Implementation, PR review,
Decomposition, Triage and Consolidation. The Default row is required; every other row either uses the default or names
its own route. A route is a profile, a model and an effort:

| Field | Values |
|-------|--------|
| Profile | A profile whose job template exists. |
| Model | Required. `auto` lets the tool choose, for example Kiro's model router. Claude Code offers its built-in model list; Kiro offers the models Fetch Models lists (a short Kubernetes Job, which needs a credential PVC); OpenCode takes `provider/model`. Any model name of letters, digits, `.`, `_`, `-` and `/` can be typed. |
| Effort | `auto` (the tool's default) or an effort the tool takes: `low` to `max` for Claude Code, `low`, `medium`, `high`, `max` for Kiro. OpenCode has no effort. |

Saving checks every row and refuses the table with one line per invalid row. A change applies to queued work at its
next dispatch, and work waiting for a route starts once one resolves. Running work keeps its route.

## Repository Rows

A repository (its pipeline job template) can set its own route per task type in the Binding card of its page. There is
no repository default row: a task type without a row inherits the global table, and the card says what it inherits
(profile, model, effort, job template and where it comes from). "Use for all task types" puts one route on all five
rows. The rows are offered for all five task types, whatever the repository's switches say: a manual dispatch or an
operator triage does not depend on them.

Only the global admin edits the rows. Everyone else who can read the repository sees the resolved route per task type:
profile name, tool, model, effort and source, without the profile's settings or MCP servers. The Pipelines page shows
each repository's routes in one line, for example "Kiro · sonnet; Decomposition, Triage: Claude · opus".

## Resolution Order

Creating work never routes and never refuses it for routing: the work item is queued. Each time the Scheduler tries
to start a queued item, the API resolves its route and pins profile, model and effort on it:

1. the repository's row for the task type;
2. else the global row for the task type;
3. else the global default.

The first row found is used. If the route cannot run now, the item **waits** in the queue with the reason, and the next
dispatch tries again; the resolution does not fall through to the next level, and nothing picks some other profile:

| Waits because | Text on the Work page |
|---|---|
| No row resolves (for example a fresh install), or the stored request cannot be read | "No agent route for Implementation — set it in Settings → Agents → Routing" |
| The route names a profile that was deleted | "… names agent profile '…', which does not exist" |
| The profile's job template has no tool, or is not in the Helm values | the template and the profile |
| The route's credential is paused | "Kiro paused — quota used up, since … (until …)" |
| An agent limit is reached, or no Kiro credential PVC is free | the limit and how many agents run |

The Work page shows the reason in its Waiting column and counts the waiting items per reason. The issue, PR and epic
drawers, the run page's re-dispatch, the operator triage and the consolidation trigger queue the work and say why it
waits when it would wait now. A wait that only an operator ends (a paused credential, a missing route, profile or
template) moves the item behind the other queued work, so it never keeps other work from starting; resuming the
credential or saving the routing makes it eligible again at once.

When an agent claims the work, the API checks the pinned route again: a profile deleted since, or a job template that
changed, refuses the claim with the reason, and the work item fails instead of running on something else.

Epic decomposition runs both phases on the Decomposition route. A consolidation runs on the Consolidation route of the
repository that triggered it, or on the global rows when none did. Agent Chat offers every profile whose job template
exists, with the models of the routes that name them (and Claude Code's built-in list) and the tool's efforts; see
[Chat Pod Lifecycle](configuration.md#chat-pod-lifecycle).

## Credentials and Pauses

Every profile uses one agent credential, derived from its job template's tool and, for Claude Code, its Auth Mode:

| Credential | Profiles |
|---|---|
| Kiro | Kiro profiles (the credential PVC pool) |
| Claude subscription | Claude Code profiles with Auth Mode `subscription` (the default) |
| Claude API key | Claude Code profiles with Auth Mode `apiKey` |
| OpenCode | OpenCode profiles |

A credential is **paused** when an agent reports that its quota is used up or its login was rejected (the pause lasts
until the reset time the provider names, or until resumed), or when an operator pauses it. The run that hit the limit
ends, its issue goes back to its queue label, and the same work is queued again as a new work item that waits for the
credential; no draft PR is opened. While the credential is paused, work routed to it waits, chats on it are refused,
and work on other credentials runs as before. There is no automatic fallback to another tool: to move the work, change
the routing.

Work and Fleet show a banner per paused credential: what is paused, why, since and until when, and how much work
waits. Global operators resume a credential or pause it until a time (with a note) from the banner, from the
Credentials card on Fleet, or in Settings → Agents → Credentials, which also lists the profiles per credential. A pause
whose end time passed ends by itself at the next dispatch. An automatic pause never replaces an operator's pause.

## Agent Images

Each agent tool has one image, built from `dockerfiles/agent.Dockerfile` with `--target`. Every image carries every supported tech stack: .NET 10 SDK, JDK 21 + Maven, Python 3.12 (pip, venv, uv), Node.js and npm.

| Image tag | Target | Agent tool | Stacks |
|-----------|--------|------------|--------|
| `coding-agent-kiro-latest` | `kiro` | Kiro CLI | .NET 10, Java 21, Python 3.12 |
| `coding-agent-opencode-latest` | `opencode` | OpenCode | .NET 10, Java 21, Python 3.12 |
| `coding-agent-claude-latest` | `claude` | Claude Code CLI | .NET 10, Java 21, Python 3.12 |

The image does not route work: the route names a profile, the profile names its job template, and the template names
the image. One job template per tool is enough; add another template of the same tool only for different capacity or
resources. Each template keeps its own `maxConcurrent` and resources, shared by all profiles on it and by chats on it;
Settings → Agents → Capacity can set a lower limit per template.

The image's default `AGENT_LABELS` lists the agent tool and every stack. Kubernetes Jobs set `AGENT_LABELS` from their job template, so the default applies only to an agent started outside Kubernetes.

The per-stack tags of earlier releases (`coding-agent-kiro-dotnet10-latest`, `coding-agent-claude-java21-latest`, …) are no longer published. Point job templates that still use them at `coding-agent-<tool>-latest`.

The Claude image runs the Claude Code CLI. Its job templates use `providerType: claude`, and the profiles on them set
the Auth Mode: `subscription` (the default; the subscription token) or `apiKey` (the API key, billed per token). It
needs no credential PVC: the token and the key come from the agent Secret — see [Deployment](deployment.md). A profile
whose credential is missing from the Secret pauses that credential on its first run.

## Stack Labels: Quality Gates and Reviewers

A repository's stack labels are set in the Binding card ("Stack labels", for example `dotnet, dotnet10`). They pick the
quality gate and reviewer configs: a config applies when its match labels intersect the stack labels, and a config with
no match labels always applies. A repository without stack labels gets only the configs without match labels. The label
preview on the Pipelines page lists what a repository's labels pick.

### Quality Gate Configurations

QGCs define per-stack quality gates. Configured in Settings → Quality → Quality Gate Configs.

| QGC | Match Labels | Compilation | Tests |
|-----|-------------|-------------|-------|
| .NET Quality Gate | `dotnet` | `dotnet build --no-restore` | `dotnet test --no-restore --no-build --filter Category!=E2E` |
| Python Quality Gate | `python` | `python -m pytest --collect-only` | `python -m pytest --cov=. --cov-report=xml:coverage.xml` |
| Java Quality Gate | `java` | `mvn compile -q` | `mvn test -q` |

Resolution: all QGCs whose labels intersect with the repository's stack labels are applied sequentially. A polyglot repo with labels `["dotnet", "python"]` gets both the .NET and Python quality gates.

> **Coverage enforcement:** The built-in coverage threshold gate was retired. To enforce coverage minimums, add the appropriate flag to the `TestArguments` field on the QGC. For example, `--coverage-fail-below 80` for pytest-cov (Python) or a test runner argument like `--minimum-coverage 80` for .NET tools that support it. The test command will fail with a non-zero exit code if coverage is below the threshold, which the Tests gate will catch.

### Reviewer Configurations

Reviewer Configurations define per-stack code review agents. Configured in Settings → Quality → Reviewer Configs.

| Reviewer Config | Match Labels | Agents |
|----------------|-------------|--------|
| Default Reviewers | *(empty — global fallback)* | Correctness, SecurityReviewer, TestQualityReviewer |
| .NET Reviewers | `dotnet` | DotNetSpecialist |

The default reviewers do not depend on the stack, so they apply to every repository. A stack's specialist matches the stack label, like the stack's quality gate config: add one per stack you use (for example a Python reviewer with `python`).

Resolution: all Reviewer Configurations whose labels intersect with the repository's stack labels are applied sequentially (ANY match). Each configuration contains one or more review agents that run in order. A configuration with empty MatchLabels acts as a global fallback (applies to all jobs). When no reviewer config matches (or all are disabled), or the matching configs define no review agents, the review phase is **skipped**. A warning is logged (`Pipeline {RunId} no reviewer configurations matched — review phase skipped`). An implementation run then opens its PR without a review; a PR review run posts a comment on the pull request that says why no review ran. To ensure review always runs, keep the default reviewer configuration (`MatchLabels = []`) enabled in Settings → Quality → Reviewer Configs.

The other code review settings (iterations, fix prompt, inline comments, acceptance criteria) are on Settings → Global Defaults → Code Review; see [Configuration — Code Review](configuration.md#code-review).

Stack labels are set per repository, so a project cannot choose which of these reviewers or quality gates apply. What a project can do is add its own reviewer, which checks a change against the whole project: see [PR Review — Project Review](pr-review.md#project-review).

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
| `agent:triage` | Problem report queued for a triage run (label triage) | No |
| `agent:triage-review` | Root cause analysis posted; the issue drafts wait for review in the app | Yes |
| `agent:generated` | Issue was created by the pipeline (a decomposition sub-issue or a refactoring issue from a consolidation run); not a status label, so label swaps keep it | No |

**Terminal labels** (`agent:done`, `agent:error`, `agent:needs-refinement`, `agent:wont-do`, `agent:cancelled`, `agent:epic-review`, `agent:triage-review`) mark states that require human action to re-queue. Remove the terminal label and add `agent:next` to retry; for `agent:epic-review`, add `agent:epic-approved` to approve the plan or `agent:epic` to request a new one; for `agent:triage-review`, re-run or dismiss from the Triage page, or replace the label with `agent:triage` for another attempt. `agent:needs-refinement` specifically indicates the issue body needs more detail — review the analysis confidence output before re-queuing.

**`agent:epic-approved`** is a gated label: agents may not set it via `RequestLabelChange`. Only humans can approve decomposition.


## Setting Up a New Stack

1. **Add a job template** if the tool has none yet — In `values.yaml`, add an entry to `jobTemplates[]` with its `labels`, `image` and `providerType`
2. **Create an Agent Profile** — In Settings → Agents → Agent Profiles, choose the job template and fill in the tool's settings and MCP servers
3. **Route work to it** — In Settings → Agents → Routing, name the profile in the Default row or in the rows of the task types it should run, with a model and an effort; or set a repository's own rows in its Binding card
4. **Create a QGC** — In Settings → Quality → Quality Gate Configs, define the build/test commands for the stack and match them to its stack label
5. **Create a Reviewer Config** (optional) — In Settings → Quality → Reviewer Configs, define stack-specific review agents
6. **Configure the repository** — On the Pipelines page, add the pipeline job template (issue provider, repo provider, optional brain/CI providers), then set its stack labels in the Binding card of its page
