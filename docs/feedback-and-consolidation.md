# Agent Feedback Loops & Consolidation

## Agent Feedback Loops

After every pipeline run, the agent provides structured feedback about what went well and what didn't. This feedback is grounded in external signals (compiler errors, test failures, retry history) rather than pure introspection.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for where feedback collection fits in the pipeline flow (ReflectingOnRun step).

### How It Works

- **Success path** — After the PR is marked ready, a separate agent call (`CollectFeedbackAsync`, resuming the run's session) collects the feedback with its own prompt. It runs after the reflection and brain sync, and also when the template has no brain. The agent reports what caused retries, what context was missing, and what could be improved.
- **Failure path** — A dedicated agent call collects feedback after max retries are exhausted, before creating the draft PR. Both paths time out after `FeedbackTimeoutSeconds` (default 60, range 10-600, overridable per project).

### Feedback Schema

Each run produces a `RunFeedback` record with two sections:

- **Harness Feedback** — For the pipeline team: category label, stuck reason, missing context, missing capabilities, prompt issues, suggestions
- **Issue Feedback** — For the issue author: category label, description of what's wrong, affected files, human action needed

### Where Feedback Appears

- **Run page** — Collapsible "Feedback (n)" section at the bottom of the run's page (`/runs/{id}`), showing all fields. The Runs list has a "Feedback only" filter
- **GitHub issue** — If issue feedback has a description, a comment is posted with the `<!-- agent:issue-feedback -->` marker
- **Harness suggestions** — Accumulated feedback feeds into the consolidation loops (see below)

### Category Reuse

The feedback prompt includes previously-used category labels from the last 50 runs, encouraging the agent to reuse existing labels for clustering rather than inventing new ones each time.

## Consolidation Loops

Three maintenance loops that review accumulated state and produce improvements. All are manually triggered from the **Consolidation** page in the sidebar.

### Brain Consolidation (per brain)

Dispatches an agent to prune, deduplicate, and organize the `.brain/` knowledge repository. Several repositories usually feed one brain, so brain consolidation works on the brain, not on a template:

- **Trigger:** from the card of any template that uses the brain. The run uses that template's settings (its project's overrides and its repository's agent labels).
- **One run per brain at a time:** templates that share a brain share its running state and its last run on the Consolidation page, and a second trigger for the same brain is rejected as already running.
- **Read-only brains:** brain consolidation writes to the brain, so it does not run from a template whose brain is read-only (the template's `BrainReadOnly`, or the global `BrainReadOnly` with the project's override). The button is disabled with the reason, and the trigger refuses it. Trigger it from a template that writes to the brain. For GitHub App brain providers, runs get a read-only token (`contents: read`) when the brain is read-only, so a misbehaving agent cannot push to it even if it tries.

It runs a six-step process:

1. **Orient** — Scan all files, build inventory
2. **Gather Signal** — Identify drift, duplicates, contradictions
3. **Research & Verify** — Check if referenced tools/libraries/versions are still current, validate external links, update outdated information
4. **Consolidate** — Merge duplicates, resolve contradictions, convert relative dates to absolute
5. **Prune** — Remove stale entries, clean up empty files
6. **Generate Project SKILL.md** — Regenerate, from scratch, a `SKILL.md` of about 1500 words for each project folder under `.brain/projects/`, as the distilled context agents get for that project

After the agent produces changes, an **adversarial review** pass evaluates the diff summary (`.agent/brain-consolidation-diff.md`). The discriminator checks for incorrectly removed entries, bad merges, contradictions, and inaccurate factual updates. If CRITICAL or WARNING findings are found, a refinement pass revises the `.brain/` files.

Changes are committed and pushed to the brain's base branch automatically. Git history provides rollback. Runs of the other repositories keep pushing their lessons to the brain while a consolidation works on its clone. When the push is rejected because the brain moved on, the consolidation is merged on top of it: the consolidated files are kept, and the lines the other runs added to them are appended, so nothing they learned is lost. A file the consolidation deleted comes back with only those lines. The next consolidation folds them in. The push is tried up to `BrainPushMaxRetries` times (default 3), like the runs' own brain pushes; no force push is used.

Configuration: `BrainConsolidationReviewEnabled` (default: `true`) controls whether the adversarial review runs.

### Refactoring Detection (per template)

Dispatches agents to analyze the codebase holistically for architectural drift using a multi-phase, multi-agent pipeline. Produces up to `MaxRefactoringProposals` (default: 3) GitHub issues with bounded refactoring proposals. Each issue includes:

- **Problem** — what goes wrong or what it costs (for a bug: the failure scenario)
- **Before You Start** — how to check that the problem still exists (the Scope search or the quoted evidence), and to report `wont_do` when it is already fixed
- Category, estimated effort, risk level and named refactoring technique
- **Suggested Approach** — one concrete change
- Prerequisites (e.g., "add characterization tests before refactoring")
- Affected files
- **Scope** — the search that lists every instance the change must cover, when the problem is a repeated pattern
- **Evidence** — the decisive code or tool output, quoted verbatim, and how it was found
- Acceptance criteria (a bug without criteria defaults to a reproduction test)
- The commit the analysis ran against, which the line numbers refer to
- Labels: `agent:generated`, plus `agent:next` when the scan is started with the "auto-dispatch created issues" option in the Refactoring Scan dialog, so the pipeline picks the issues up without further approval

The finding categories are one list from detection to issue (`RefactoringCategories`): `duplication`, `structural-drift`, `complexity`, `over-engineering`, `todo`, `dead-code`, `bug`, `stale-documentation`, `naming-inconsistency`, `primitive-obsession`.

**Execution flow (phased):**

1. Clone code repo (+ brain repo for architectural context if configured)
2. Run git hotspot analysis (frequently-changed files within `HotspotAnalysisLookback` window, default 90 days). Pipeline scratch space (`.agent/`, `.brain/`), Markdown files, lock files and deleted files are left out.
3. Query the open issues (up to 100, any label or age) for deduplication context. The scan's own issues are recognized by the footer sentence every one of them carries, not by a label.
4. Query closed refactoring issues (within `RefactoringOutcomeLookback`, default 90 days) for outcome feedback — categorizes past proposals as implemented (`agent:done`) or rejected (`agent:wont-do`/`agent:cancelled`). The query uses `agent:generated`; the footer keeps decomposition sub-issues, which carry the same label, out. `agent:done` only means an agent completed the issue, so implemented items are not re-proposed rather than encouraged. For each of these issues, the latest issue-quality feedback comment (`<!-- agent:issue-feedback -->`) that the implementing agent posted is added as "Implementer Feedback", e.g. "partial scope: the outer catch was missed". Both contexts are also written to `.agent/refactoring-issue-context.md` for the review step.
5. **Phase 0: Context Extraction** — Agent extracts project conventions, layer rules, intentional patterns, and known debt into `.agent/refactoring-conventions.json`. This grounds subsequent phases and prevents flagging idiomatic patterns as smells. Inline TODO comments are not known debt: Agent B checks each of them. Phase 0 also prepares the tool evidence: it checks its available MCP tools for additional data sources, runs the project's own build, lint and analysis commands once (saving the output in `.agent/refactoring-tool-output/`), and lists both under `availableTools`. Consolidation runs get the MCP servers of their agent profile and project, like pipeline runs; no particular tool or service is required.
6. **Phase 1: Parallel Focused Detection** — Three sub-agents run concurrently. They use the tool output and MCP tools Phase 0 found, and do not run builds themselves, since they share the workspace. Each writes its `findings` and the areas it did not check (`notChecked`):
   - **Agent A (Structural Debt)** — Duplicated logic, structural drift, complexity, over-engineering
   - **Agent B (Correctness & Hygiene)** — TODOs/FIXMEs, dead code, obvious bugs, stale documentation
   - **Agent C (Design Consistency)** — Naming inconsistencies, primitive obsession
7. **Phase 2: Aggregation** — Synthesizes findings from all three agents: deduplicates, filters against project conventions, applies a per-category evidence gate (a hotspot rank is never evidence), ranks bugs first and the rest by hotspot frequency × evidence strength × scope feasibility, keeps at most one proposal per primary file, and produces final ranked proposals (capped at `MaxRefactoringProposals`). The analysis log goes to `.agent/refactoring-analysis.md`.
8. **Adversarial review** (if `RefactoringReviewEnabled`) — Evaluates proposals for non-existent file paths, unsupported or unquoted evidence, incomplete scope (instances of the pattern the proposal misses), bugs without a reproduction, overlap with existing issues, scope exceeding single-agent capacity (>30 files), and bundled concerns. If CRITICAL/WARNING found, refinement re-generates proposals.
9. **Validation** — Deterministic checks drop a proposal that names a file in `.agent/`, `.brain/` or `.git/`, names no file that exists, names more than 30 files, has an unknown category or an empty title, or has the title of an open issue, a recently closed one, or an earlier proposal of the batch. Dropped proposals are logged and counted in the run summary.
10. Create GitHub issues (capped at `MaxRefactoringProposals`)

**Partial failure handling:** If some Phase 1 agents fail but at least one succeeds, the pipeline continues with partial results. If ALL Phase 1 agents fail, the run fails.

Configuration: `RefactoringReviewEnabled` (default: `true`) controls the adversarial review step. `MaxRefactoringProposals` (default: 3, per-project overridable) caps both the prompt instruction and issue creation count. `HotspotAnalysisLookback` (default: 90 days) controls the git history window for hotspot analysis. `RefactoringOutcomeLookback` (default: 90 days) controls the window for querying past proposal outcomes.

### Harness Suggestions (global)

Analyzes accumulated `RunFeedback` from all pipeline runs to identify recurring patterns. Produces top 3-5 improvement opportunities ranked by frequency and impact, persisted to the database via `IHarnessSuggestionStore`.

Each suggestion includes:
- Concrete, actionable text (what to change)
- Rationale (why, with references to specific feedback patterns)
- Frequency (how many runs contributed to this observation)

**Execution flow:**
1. Early exit if no feedback data
2. Write feedback JSON to workspace (`feedback-data.json`)
3. Calculate feedback count and success rate from the data
4. Execute agent to generate suggestions
5. **Write-to-file step** — a follow-up agent call serializes suggestions to `.agent/harness-suggestions-output.json` (enables stable file for review)
6. **Adversarial review** — evaluates suggestions against original feedback data. Checks for ungrounded suggestions, implausible frequency counts, and non-actionable advice.
7. Parse final suggestions and persist to the database via `IHarnessSuggestionStore` (Postgres)

Configuration: `HarnessSuggestionsReviewEnabled` (default: `true`) controls the adversarial review step.

### Consolidation Dispatch

Consolidation jobs are triggered via `ConsolidationService.TriggerAsync`, which creates a pending `WorkItem` through the standard `IWorkDistributor` path. The Scheduler's `WorkItemDispatchLoop` dispatches consolidation `WorkItem` rows in the lowest-priority tier (4th, after Review, Decomposition, and Implementation). Like every work item, a consolidation Job is named `caa-{8 hex chars}`.

- **Deduplication:** Each consolidation work item has a fixed key `{type}:{scope}`. The scope is what the run works on: the brain (by brain provider ID) for brain consolidation, the template (and so its repository) for a refactoring scan, `global` for harness suggestions. A partial unique index on `(IssueIdentifier, IssueProviderConfigId)` for non-terminal WorkItem statuses ensures that a second trigger for the same key is rejected as already running.
- **Timeout:** The job's timeout is the `AgentTimeout` its agent runs with: the current global value with the template's project override. Harness suggestions have no template and use the global value.
- **Dispatch retries:** There are no dispatch retries for consolidation jobs. If the Scheduler's dispatch loop fails to dispatch a consolidation `WorkItem`, the item remains in `Pending` status and will be picked up on the next poll cycle.

### Consolidation Page

The page is reached through the "Consolidation" item in the sidebar navigation. It displays:

- Per-template cards with trigger buttons and last-run status
- Global harness suggestions section
- Run history table across all consolidation types

A refactoring scan opens a dialog that shows the run parameters (max issues, hotspot lookback, adversarial review) and the auto-dispatch option before it starts.

### Retention

Consolidation run history is automatically pruned by `DatabaseMaintenanceService`. Two retention mechanisms apply:

- **PipelineRun records:** Consolidation runs are recorded as `PipelineRuns` when they are dispatched (`PipelineRunFactory.CreateFromWorkItem`) and pruned by the standard `PipelineRunRetentionDays` (default: `30` days, age-based) and `PipelineRunRetentionCount` (count-based per-project, default disabled) sweeps. The last successful run of a scope and the feedback for harness suggestions are read from these records, so what is pruned no longer counts.
- **WorkItem rows (K8s mode):** Terminal consolidation `WorkItems` are deleted by `WorkDistribution:Reconciliation:StaleRetentionDays` (default: `7` days).
