# Agent Feedback Loops & Consolidation

## Agent Feedback Loops

After every pipeline run, the agent provides structured feedback about what went well and what didn't. This feedback is grounded in external signals (compiler errors, test failures, retry history) rather than pure introspection.

See also: [Pipeline Orchestration](pipeline-orchestration.md) for where feedback collection fits in the pipeline flow (ReflectingOnRun step).

### How It Works

- **Success path** — Feedback questions are appended to the existing reflection prompt (no extra agent call). The agent reports what caused retries, what context was missing, and what could be improved.
- **Failure path** — A dedicated 60-second agent call collects feedback after max retries are exhausted, before creating the draft PR.

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
- **Read-only brains:** brain consolidation writes to the brain, so it does not run from a template whose brain is read-only (the template's `BrainReadOnly`, or the global `BrainReadOnly` with the project's override). The button is disabled with the reason, and the trigger refuses it. Trigger it from a template that writes to the brain.

It runs a 5-phase process:

1. **Orient** — Scan all files, build inventory
2. **Gather Signal** — Identify drift, duplicates, contradictions
3. **Research & Verify** — Check if referenced tools/libraries/versions are still current, validate external links, update outdated information
4. **Consolidate** — Merge duplicates, resolve contradictions, convert relative dates to absolute
5. **Prune** — Remove stale entries, clean up empty files

After the agent produces changes, an **adversarial review** pass evaluates the diff summary (`.agent/brain-consolidation-diff.md`). The discriminator checks for incorrectly removed entries, bad merges, contradictions, and inaccurate factual updates. If CRITICAL or WARNING findings are found, a refinement pass revises the `.brain/` files.

Changes are committed and pushed to the brain's base branch automatically. Git history provides rollback. Runs of the other repositories keep pushing their lessons to the brain while a consolidation works on its clone. When the push is rejected because the brain moved on, the consolidation is merged on top of it: the consolidated files are kept, and the lines the other runs added to them are appended, so nothing they learned is lost. A file the consolidation deleted comes back with only those lines. The next consolidation folds them in. The push is tried up to `BrainPushMaxRetries` times (default 3), like the runs' own brain pushes; no force push is used.

Configuration: `BrainConsolidationReviewEnabled` (default: `true`) controls whether the adversarial review runs.

### Refactoring Detection (per template)

Dispatches agents to analyze the codebase holistically for architectural drift using a multi-phase, multi-agent pipeline. Produces up to `MaxRefactoringProposals` (default: 3) GitHub issues with bounded refactoring proposals. Each issue includes:

- Summary of the problem
- Affected files with evidence
- Suggested approach with named refactoring technique
- Estimated effort and risk level
- Prerequisites (e.g., "add characterization tests before refactoring")
- Labels: `agent:generated`

**Execution flow (phased):**

1. Clone code repo (+ brain repo for architectural context if configured)
2. Run git hotspot analysis (frequently-changed files within `HotspotAnalysisLookback` window, default 90 days)
3. Query open `agent:generated` issues and recent open issues for deduplication context
4. Query closed refactoring issues (within `RefactoringOutcomeLookback`, default 90 days) for outcome feedback — categorizes past proposals as implemented (`agent:done`) or rejected (`agent:wont-do`/`agent:cancelled`) so the agent learns from history
5. **Phase 0: Context Extraction** — Agent extracts project conventions, layer rules, intentional patterns, and known debt into `.agent/refactoring-conventions.json`. This grounds subsequent phases and prevents flagging idiomatic patterns as smells.
6. **Phase 1: Parallel Focused Detection** — Three sub-agents run concurrently:
   - **Agent A (Structural Debt)** — Duplicated logic, structural drift, complexity, over-engineering
   - **Agent B (Correctness & Hygiene)** — TODOs/FIXMEs, dead code, obvious bugs, stale documentation
   - **Agent C (Design Consistency)** — Naming inconsistencies, primitive obsession
7. **Phase 2: Aggregation** — Synthesizes findings from all three agents: deduplicates, filters against project conventions, ranks by hotspot frequency × evidence strength × scope feasibility, and produces final ranked proposals (capped at `MaxRefactoringProposals`)
8. **Adversarial review** (if `RefactoringReviewEnabled`) — Evaluates proposals for non-existent file paths, unsupported claims, scope exceeding single-agent capacity (>30 files), and bundled concerns. If CRITICAL/WARNING found, refinement re-generates proposals.
9. Create GitHub issues (capped at `MaxRefactoringProposals`)

**Partial failure handling:** If some Phase 1 agents fail but at least one succeeds, the pipeline continues with partial results. If ALL Phase 1 agents fail, the run fails.

Configuration: `RefactoringReviewEnabled` (default: `true`) controls the adversarial review step. `MaxRefactoringProposals` (default: 3, per-project overridable) caps both the prompt instruction and issue creation count. `HotspotAnalysisLookback` (default: 90 days) controls the git history window for hotspot analysis. `RefactoringOutcomeLookback` (default: 90 days) controls the window for querying past proposal outcomes.

### Harness Suggestions (global)

Analyzes accumulated `RunFeedback` from all pipeline runs to identify recurring patterns. Produces a JSON file (`config/pipeline/harness-suggestions.json`) with the top 3-5 improvement opportunities ranked by frequency and impact.

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
7. Parse final suggestions and persist to `config/pipeline/harness-suggestions.json`

Configuration: `HarnessSuggestionsReviewEnabled` (default: `true`) controls the adversarial review step.

### Consolidation Dispatch

Consolidation jobs are dispatched via `IConsolidationDispatchService`. In K8s mode, dispatch originates from the Orchestrator (Web) via `ConsolidationJobPreparationService` and routes through the Pipeline API's synchronous dispatch endpoint, using the `caa-{release}-dispatch-lock` lease for deduplication. (`ConsolidationDispatchService`, a standalone background loop that previously ran in the Job Controller, was removed in #2323.) The job naming format `caa-cons-{12 hex chars}` is preserved for compatibility with any in-flight Jobs created before that change. The service enforces:

- **Deduplication:** Each consolidation work item has a fixed key `{type}:{scope}`. The scope is what the run works on: the brain for brain consolidation, the template (and so its repository) for a refactoring scan, `global` for harness suggestions. While a work item with that key is live, another trigger is rejected as already running.
- **Timeout:** The job's timeout is the `AgentTimeout` its agent runs with: the current global value with the template's project override. Harness suggestions have no template and use the global value.

### Consolidation Page

The page is reached through the "Consolidation" item in the sidebar navigation. It displays:

- Per-template cards with trigger buttons and last-run status
- Global harness suggestions section
- Run history table across all consolidation types

### Retention

Consolidation run history is automatically pruned by `DatabaseMaintenanceService`. Two retention mechanisms apply:

- **PipelineRun records (backfilled consolidation history):** Consolidation run history is migrated into `PipelineRuns` by `BackfillConsolidationRunsAsync` and pruned by the standard `PipelineRunRetentionDays` (default: `30` days, age-based) and `PipelineRunRetentionCount` (count-based per-project, default disabled) sweeps.
- **WorkItem rows (K8s mode):** Terminal consolidation `WorkItems` are deleted by `WorkDistribution:Reconciliation:StaleRetentionDays` (default: `7` days).
